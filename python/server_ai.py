"""Explicit server-AI backend for the existing atomic dispatcher/LuaRuntime.

No reconnection, retry, orchestration or fallback to the human endpoint.
"""
import json
import socket
import threading
import time
import uuid
from concurrent.futures import Future, TimeoutError as FutureTimeout

from control import BridgeError, NotAdmittedError, command_owner
import agent_trace as tracing


CONTAINER_READS = frozenset(("inspect_container",))
MAP_READS = frozenset(("query_map",))
COMBAT_READS = frozenset(("query_combat","query_combat_loadout","discover_bosses"))
CRAFT_READS = frozenset(("query_recipes",))
SCENE_OPERATIONS = frozenset(("observe_scene",))
PERCEPTION_OPERATIONS = frozenset(("observe_terrain", "scan_tiles", "scan_entities", "inspect_tile", "resolve_content"))


def operations_for_capabilities(capabilities):
    """Pure projection of the bind handshake; also used by model contract generation."""
    caps = set(capabilities)
    if 'server_ai_atomic_v1' not in caps:
        return frozenset()
    operations = {'select_hotbar_slot', 'stop_action', 'wait'}
    for capability, names in (
        ('server_ai_reads_v1', {'get_player_state', 'inspect_inventory'}),
        ('server_ai_perception_v1', PERCEPTION_OPERATIONS),
        ('server_ai_map_v1', MAP_READS),
        ('server_ai_containers_v1', CONTAINER_READS),
        ('server_ai_combat_v1', {'query_combat'}),
        ('server_ai_boss_strategy_v1', {'discover_bosses'}),
        ('server_ai_combat_primary_v1', {'query_combat_loadout'}),
        ('server_ai_crafting_v1', CRAFT_READS),
        ('server_ai_scene_v1', SCENE_OPERATIONS),
    ):
        if capability in caps:
            operations.update(names)
    if 'server_ai_actions_v1' in caps:
        operations.update(('navigate_to_x', 'use_item', 'mine_tile'))
        for capability, operation in (
            ('server_ai_direct_input_v1', 'move_input'),
            ('server_ai_tasks_v1', 'mine_target'),
            ('server_ai_collect_drops_v1', 'collect_drops'),
            ('server_ai_place_item_v1', 'place_item'),
            ('server_ai_navigation_v1', 'navigate_to'),
            ('server_ai_combat_v1', 'combat_attack'),
            ('server_ai_boss_encounter_v1', 'boss_encounter'),
            ('server_ai_combat_primary_v1', 'configure_combat_loadout'),
            ('server_ai_crafting_v1', 'craft_recipe'),
            ('server_ai_place_object_workbench_v1', 'place_object'),
            ('server_ai_place_tile_v1', 'place_tile'),
            ('server_ai_place_rope_v1', 'place_rope'),
            ('server_ai_move_on_rope_v1', 'move_on_rope'),
            ('landing_region_navigation_v1', 'navigate_to_landing_region'),
        ):
            if capability in caps:
                operations.add(operation)
    if {'server_ai_actions_v1', 'server_ai_inventory_v1'} <= caps:
        operations.update(('transfer_inventory_item', 'swap_inventory_slots', 'hold_inventory_item'))
    if {'server_ai_actions_v1', 'server_ai_equipment_v1'} <= caps:
        operations.update(('equip_item', 'unequip_item'))
    if {'server_ai_actions_v1', 'server_ai_containers_v1'} <= caps:
        operations.update(('open_container', 'close_container'))
        if 'server_ai_sorting_v1' in caps:
            operations.update(('sort_inventory', 'sort_container'))
        if 'server_ai_container_batch_v1' in caps:
            operations.update(('quick_stack_nearby', 'quick_stack_container', 'loot_all', 'deposit_all', 'restock'))
        if 'server_ai_container_transfer_v1' in caps:
            operations.update(('deposit_container_item', 'withdraw_container_item'))
    return frozenset(operations)


class ServerAiBridge:
    supported_atomic_operations = frozenset(("select_hotbar_slot", "stop_action", "wait"))
    def __init__(self, port=18779, timeout=5):
        self.timeout = timeout
        self.trace_connection_id = uuid.uuid4().hex
        self._lock = threading.RLock()
        self._closed = False
        self._action_id = None
        self._request_id = 0
        self._wire_lock = threading.Lock()
        self._pending_lock = threading.Lock()
        self._pending = {}
        self._reply_turn_lock = threading.Lock()
        self._reply_turn = 0
        self.reply_receipts = []
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        self.reader = self.sock.makefile("rb")
        self.sock.settimeout(None)
        self._receiver = threading.Thread(target=self._receive,daemon=True,name='server-ai-receive')
        self._receiver.start()
        try:
            bound = self._request("bind")
            self.binding = bound["binding"]
            self.hello = {"capabilities": (bound.get("data") or {}).get("capabilities", [])}
            if "server_ai_atomic_v1" not in self.hello["capabilities"]:
                raise BridgeError("Server AI execution capability unavailable")
            self.supported_atomic_operations = operations_for_capabilities(self.hello['capabilities'])
        except BaseException:
            self.close()
            raise

    def _receive(self):
        try:
            while not self._closed:
                line=self.reader.readline(1_048_577)
                if len(line)>1_048_576 or not line.endswith(b'\n'):
                    raise BridgeError('Missing/truncated server AI response; outcome unknown')
                reply=json.loads(line)
                if not isinstance(reply,dict) or reply.get('v')!=1:
                    raise BridgeError('Server AI response identity mismatch')
                with self._pending_lock:
                    pending=self._pending.pop(reply.get('id'),None)
                if pending is None:raise BridgeError('Server AI response identity mismatch')
                pending.set_result(reply)
        except (OSError,ValueError,BridgeError) as error:
            self.close()
        finally:
            self.reader.close()

    def _request(self, op, _guard=None, **values):
        # Only the write is serialized. A reply-turn fence must be able to pass
        # a say that is already awaiting its game-thread response on this socket.
        with self._wire_lock:
            if _guard is not None and not _guard():
                raise BridgeError('AI reply superseded before send',status='superseded')
            if self._closed:raise BridgeError('Server AI connection closed; action outcome may be unknown')
            self._request_id+=1;request_id=str(self._request_id)
            request={'v':1,'id':request_id,'op':op,**values}
            if op!='bind':request['binding']=self.binding
            wire=(json.dumps(request,ensure_ascii=False)+'\n').encode('utf-8')
            if len(wire)>4097:raise BridgeError('AI request exceeds 4096 bytes')
            pending=Future()
            with self._pending_lock:self._pending[request_id]=pending
            tracing.emit_full('server_ai_ipc_request', connection_id=self.trace_connection_id, request=request)
            try:self.sock.sendall(wire)
            except OSError as error:
                tracing.emit_full('server_ai_ipc_error', connection_id=self.trace_connection_id, request=request, category='network')
                self.close();raise BridgeError(str(error)) from error
        try:reply=pending.result(timeout=self.timeout)
        except FutureTimeout as error:
            tracing.emit_full('server_ai_ipc_error', connection_id=self.trace_connection_id, request=request, category='timeout')
            self.close();raise BridgeError('Server AI request timeout; outcome unknown') from error
        except BridgeError:
            tracing.emit_full('server_ai_ipc_error', connection_id=self.trace_connection_id, request=request, category='connection')
            raise
        tracing.emit_full('server_ai_ipc_reply', connection_id=self.trace_connection_id, request_id=request_id, reply=reply)
        if op != 'bind' and (reply.get('ok') or reply.get('binding') is not None
                             or op == 'start' and reply.get('dispatchState') == 'not_admitted') and reply.get('binding') != self.binding:
            self.close()
            raise BridgeError('Server AI binding changed; outcome unknown')
        if not reply.get("ok"):
            # Older peers incorrectly label duplicate IDs as not_admitted. The original
            # action may have executed; this error never proves absence of effects.
            if op=='retreat' and reply.get('error') in ('invalid_destination','boss_retreat_unsafe_destination','boss_not_started','boss_retreat_unavailable'):
                raise NotAdmittedError(reply['error'],status=reply['error'])
            if op == 'start' and reply.get('dispatchState') == 'not_admitted' and reply.get('error') != 'duplicate_action':
                raise NotAdmittedError(reply.get('error', 'AI start rejected'), status=reply.get('error'))
            if (reply.get("error") in ("stale_binding", "identity_unavailable")
                    or self._action_id is not None and reply.get("error") in ("engine_unavailable", "unknown_action")):
                self.close()
                raise BridgeError("Action identity/outcome unresolved: " + reply["error"])
            raise BridgeError(reply.get("error", "AI request rejected"), status=reply.get("error"))
        return reply

    def invalidate_replies(self, turn):
        with self._reply_turn_lock:
            self._reply_turn=max(self._reply_turn,turn)

    def reply_is_current(self, turn):
        with self._reply_turn_lock:return not self._closed and turn==self._reply_turn

    def advance_reply_turn(self, turn):
        """Host control metadata, not a Lua operation; bypass execution ownership/lock."""
        reply=self._request('reply_turn',replyTurn=turn,_guard=lambda:self.reply_is_current(turn))
        if (reply.get('data') or {}).get('replyTurn')!=turn:
            self.close();raise BridgeError('Reply turn acknowledgement mismatch')
        return reply['data']

    def command(self, cmd, **parameters):
        if getattr(self, "_lua_poisoned", False):
            raise BridgeError("Lua action state unresolved; reconnect required")
        if getattr(self, "_lua_owner", None) is not command_owner.get():
            raise BridgeError("Bridge session is owned by a Lua run")
        with self._lock:
            if cmd == 'say':
                if 'server_ai_chat_v2' not in self.hello['capabilities']:
                    raise BridgeError('Native AI chat author unavailable',status='unsupported_operation')
                text = parameters.get('text')
                turn=parameters.get('replyTurn');guard=parameters.get('is_current',lambda:True)
                def current():return self.reply_is_current(turn) and guard()
                if not current():raise BridgeError('AI reply superseded',status='superseded')
                if (not isinstance(text,str) or not text.strip() or len(text.encode('utf-16-le'))>800
                        or any(c in text for c in '\r\n\0')):
                    raise BridgeError('Invalid AI reply text',status='invalid_arguments')
                try:
                    reply=self._request('say',text=text,messageId=uuid.uuid4().hex,replyTurn=turn,
                        expiresAtUnixMs=int(time.time()*1000)+1800,_guard=current)
                    data=reply.get('data') or {}
                    if (data.get('status')!='published' or data.get('authorSlot')!=self.binding['slot']
                            or data.get('worldSessionId')!=self.binding['world']
                            or data.get('actorGeneration')!=self.binding['generation']
                            or type(data.get('gameUpdate')) is not int):
                        raise BridgeError('Native chat publication evidence mismatch')
                    self.reply_receipts.append(dict(data,text=text,binding=dict(self.binding)))
                    self.reply_receipts[:]=self.reply_receipts[-32:]
                    return data
                except BaseException as error:
                    if isinstance(error,BridgeError) and error.status=='superseded':raise
                    self.close()  # No retry or human fallback after an ambiguous broadcast.
                    raise
            if cmd == "query_atomic":
                operation = parameters.get("operation")
                if operation not in {"get_player_state", "inspect_inventory"} | PERCEPTION_OPERATIONS | SCENE_OPERATIONS | CRAFT_READS | MAP_READS | COMBAT_READS | CONTAINER_READS or operation not in self.supported_atomic_operations:
                    raise BridgeError("Query unsupported by server AI backend", status="unsupported_operation")
                reply = self._request("query", operation=operation, arguments=parameters.get("arguments", {}))
                data = reply.get("data")
                if not isinstance(data, dict):
                    self.close()
                    raise BridgeError("Missing AI query envelope")
                if data.get("available") is True and (data.get("worldSessionId") != self.binding["world"]
                        or data.get("actorGeneration") != self.binding["generation"]
                        or data.get("actorSlot") != self.binding["slot"]
                        or type(data.get("gameUpdate")) is not int or data["gameUpdate"] < 0
                        or not isinstance(data.get("data"), dict)):
                    self.close()
                    raise BridgeError("AI query evidence identity/shape mismatch")
                if operation in PERCEPTION_OPERATIONS | SCENE_OPERATIONS | CRAFT_READS | MAP_READS | COMBAT_READS | CONTAINER_READS and data.get("available") is True and (
                        type(data.get("observedAtUnixMs")) is not int or data["observedAtUnixMs"] <= 0
                        or data.get("clock") != "server_utc_at_projection"):
                    self.close()
                    raise BridgeError("Missing server AI observation time")
                return {"status": "atomic_query", "data": {**data, "binding": dict(self.binding)}}
            if cmd == "get_movement":
                if self._action_id is None:
                    self._request("state")
                    return self._movement(None)
                return self._movement(self._request("action", actionId=self._action_id)["data"]["action"])
            if cmd == "start_atomic":
                operation = parameters.get("operation")
                if operation not in self.supported_atomic_operations - ({"wait", "stop_action", "get_player_state", "inspect_inventory", "combat_attack", "boss_encounter", "collect_drops", "place_item"} | PERCEPTION_OPERATIONS | SCENE_OPERATIONS | CRAFT_READS | MAP_READS | COMBAT_READS | CONTAINER_READS):
                    raise BridgeError("Operation unsupported by server AI backend", status="unsupported_operation")
                if parameters.get("expectedGeneration") != self.binding["generation"]:
                    raise BridgeError("AI generation changed", status="stale_binding")
                action_id = uuid.uuid4().hex
                action = self._request("start", actionId=action_id, operation=operation,
                                       arguments=parameters.get("arguments", {}),
                                       expiresAtUnixMs=int(time.time() * 1000) + 1800)["data"]["action"]
                self._action_id = action_id
                return self._movement(action, "movement_started")
            if cmd in ("cancel_move", "renew_move"):
                if parameters.get("actionId") != self._action_id or self._action_id is None:
                    raise BridgeError("Action ownership changed", status="action_changed")
                op = "cancel" if cmd == "cancel_move" else "action"
                action = self._request(op, actionId=self._action_id)["data"]["action"]
                if cmd == "renew_move" and action["status"] != "running":
                    raise BridgeError("Action has ended", status="action_changed")
                return self._movement(action)
            raise BridgeError("Command unsupported by server AI backend", status="unsupported_operation")

    def task_request(self, op, task_id, **parameters):
        """Host task mailbox transport; independent of the current Program owner.

        No reconnect/replay or new execution state here. C# checks connection ownership.
        """
        if op=='retreat' and 'server_ai_boss_strategy_v1' not in self.hello.get('capabilities',()):raise ValueError('Retreat unavailable')
        if op not in ('start', 'action', 'cancel','retreat') or (op == 'start' and (
                parameters.get('operation') not in ('mine_target', 'combat_attack', 'boss_encounter', 'collect_drops', 'place_item')
                or parameters['operation'] not in self.supported_atomic_operations)):
            raise ValueError('Unsupported task request')
        if not {'server_ai_atomic_v1', 'server_ai_actions_v1', 'server_ai_tasks_v1', 'server_ai_program_tasks_v1'} <= set(self.hello.get('capabilities', ())):
            raise BridgeError('Task capability unavailable')
        action = self._request(op, actionId=task_id, **parameters)['data']['action']
        self._validate_action(action, task_id)
        return action

    def _validate_action(self, action, action_id):
        if not isinstance(action, dict) or (action.get("actionId") != action_id
                                   or action.get("actorId") != self.binding["actor"]
                                   or action.get("actorSlot") != self.binding["slot"]
                                   or action.get("generation") != self.binding["generation"]
                                   or action.get("worldSessionId") != self.binding["world"]):
            self.close()
            raise BridgeError("Server AI action receipt identity mismatch")

    def _movement(self, action, status="movement"):
        if action is not None:
            self._validate_action(action, self._action_id)
        return {"status": status, "movement": {"generation": self.binding["generation"], "action": action}}

    def close(self):
        self._closed = True
        with self._pending_lock:
            pending=list(self._pending.values());self._pending.clear()
        for future in pending:
            if not future.done():future.set_exception(BridgeError('Server AI connection closed; outcome unknown'))
        try:
            self.sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.sock.close()
        # The receiver owns and closes the reader; shutdown unblocks it without
        # waiting for the execution lock or joining the current receiver thread.
