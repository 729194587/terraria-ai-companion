"""Explicit server-AI backend for the existing atomic dispatcher/LuaRuntime.

No reconnection, retry, orchestration or fallback to the human endpoint.
"""
import json
import socket
import threading
import time
import uuid

from control import BridgeError, command_owner


class ServerAiBridge:
    supported_atomic_operations = frozenset(("select_hotbar_slot", "stop_action", "wait"))
    def __init__(self, port=18779, timeout=5):
        self.timeout = timeout
        self._lock = threading.RLock()
        self._closed = False
        self._action_id = None
        self._request_id = 0
        self.reply_receipts = []
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        self.reader = self.sock.makefile("rb")
        try:
            bound = self._request("bind")
            self.binding = bound["binding"]
            self.hello = {"capabilities": (bound.get("data") or {}).get("capabilities", [])}
            if "server_ai_atomic_v1" not in self.hello["capabilities"]:
                raise BridgeError("Server AI execution capability unavailable")
            self.supported_atomic_operations = type(self).supported_atomic_operations
            if "server_ai_reads_v1" in self.hello["capabilities"]:
                self.supported_atomic_operations |= frozenset(("get_player_state", "inspect_inventory"))
            if "server_ai_actions_v1" in self.hello["capabilities"]:
                self.supported_atomic_operations |= frozenset(("navigate_to_x", "use_item", "mine_tile"))
        except BaseException:
            self.close()
            raise

    def _request(self, op, **values):
        if self._closed:
            raise BridgeError("Server AI connection closed; action outcome may be unknown")
        self._request_id += 1
        request_id = str(self._request_id)
        request = {"v": 1, "id": request_id, "op": op, **values}
        if op != "bind":
            request["binding"] = self.binding
        wire = (json.dumps(request, ensure_ascii=False) + "\n").encode("utf-8")
        if len(wire) > 4097:
            raise BridgeError("AI request exceeds 4096 bytes")
        try:
            self.sock.sendall(wire)
            line = self.reader.readline(1_048_577)
            if len(line) > 1_048_576 or not line.endswith(b"\n"):
                raise BridgeError("Missing/truncated server AI response; outcome unknown")
            reply = json.loads(line)
            if reply.get("v") != 1 or reply.get("id") != request_id:
                raise BridgeError("Server AI response identity mismatch")
        except (OSError, ValueError, BridgeError) as error:
            self.close()
            raise BridgeError(str(error)) from error
        if not reply.get("ok"):
            if self._action_id is not None and reply.get("error") in ("stale_binding", "identity_unavailable", "engine_unavailable", "unknown_action"):
                self.close()
                raise BridgeError("Action identity/outcome unresolved: " + reply["error"])
            raise BridgeError(reply.get("error", "AI request rejected"), status=reply.get("error"))
        if op != "bind" and reply.get("binding") != self.binding:
            self.close()
            raise BridgeError("Server AI binding changed; outcome unknown")
        return reply

    def command(self, cmd, **parameters):
        if getattr(self, "_lua_poisoned", False):
            raise BridgeError("Lua action state unresolved; reconnect required")
        if getattr(self, "_lua_owner", None) is not command_owner.get():
            raise BridgeError("Bridge session is owned by a Lua run")
        with self._lock:
            if cmd == 'say':
                if 'server_ai_chat_v1' not in self.hello['capabilities']:
                    raise BridgeError('Native AI chat author unavailable',status='unsupported_operation')
                text = parameters.get('text')
                if (not isinstance(text,str) or not text.strip() or len(text.encode('utf-16-le'))>800
                        or any(c in text for c in '\r\n\0')):
                    raise BridgeError('Invalid AI reply text',status='invalid_arguments')
                try:
                    reply=self._request('say',text=text,messageId=uuid.uuid4().hex,
                        expiresAtUnixMs=int(time.time()*1000)+1800)
                    data=reply.get('data') or {}
                    if (data.get('status')!='published' or data.get('authorSlot')!=self.binding['slot']
                            or data.get('worldSessionId')!=self.binding['world']
                            or data.get('actorGeneration')!=self.binding['generation']
                            or type(data.get('gameUpdate')) is not int):
                        raise BridgeError('Native chat publication evidence mismatch')
                    self.reply_receipts.append(dict(data,text=text,binding=dict(self.binding)))
                    self.reply_receipts[:]=self.reply_receipts[-32:]
                    return data
                except BaseException:
                    self.close()  # No retry or human fallback after an ambiguous broadcast.
                    raise
            if cmd == "query_atomic":
                operation = parameters.get("operation")
                if operation not in ("get_player_state", "inspect_inventory") or operation not in self.supported_atomic_operations:
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
                return {"status": "atomic_query", "data": {**data, "binding": dict(self.binding)}}
            if cmd == "get_movement":
                if self._action_id is None:
                    self._request("state")
                    return self._movement(None)
                return self._movement(self._request("action", actionId=self._action_id)["data"]["action"])
            if cmd == "start_atomic":
                operation = parameters.get("operation")
                if operation not in self.supported_atomic_operations - {"wait", "stop_action", "get_player_state", "inspect_inventory"}:
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

    def _movement(self, action, status="movement"):
        if action is not None and (action.get("actionId") != self._action_id
                                   or action.get("actorId") != self.binding["actor"]
                                   or action.get("actorSlot") != self.binding["slot"]
                                   or action.get("generation") != self.binding["generation"]
                                   or action.get("worldSessionId") != self.binding["world"]):
            self.close()
            raise BridgeError("Server AI action receipt identity mismatch")
        return {"status": status, "movement": {"generation": self.binding["generation"], "action": action}}

    def close(self):
        self._closed = True
        try:
            self.sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.sock.close()
        # A blocked request owns the reader; shutdown unblocks it. Avoid waiting
        # for its lock during existing Lua cancellation/unknown cleanup.
        if self._lock.acquire(blocking=False):
            try:
                self.reader.close()
            finally:
                self._lock.release()
