"""Real loopback + existing Lua worker with an offline server fixture, not Terraria."""
import asyncio
import copy
import json
from pathlib import Path
import socketserver
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import atomic_operations as atomic
from control import BridgeError
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


class Host(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self):
        super().__init__(('127.0.0.1', 0), Handler)
        self.mode = 'complete'
        self.sent = []
        self.binding = dict(actor='actor-1', world='world-1', generation=1, slot=254)
        self.selected = 0
        self.action = None
        self.polls = 0
        self.release = threading.Event()
        self.reads = True
        self.perception = False
        self.scene = False
        self.maps = False
        self.crafting = False
        self.crafting_override = {}
        self.direct_input = False
        self.actions = False
        self.object_placement = False
        self.placement = False
        self.placement_override = {}
        self.action_transform = None
        self.landing = False
        self.landing_override = {}
        self.chat = False
        self.published = []
        self.say_queued = threading.Event()

    def query(self, operation, args):
        player = dict(inWorld=True, id=254, source='server_ai_player', position=dict(x=100, y=200),
                      velocity=dict(x=0, y=0), life=73, lifeMax=100, mana=20, manaMax=20,
                      selectedSlot=self.selected, selectedHotbarKey=(self.selected+1) % 10)
        if operation == 'get_player_state':
            data = dict(player=player, motion=dict(grounded=False, supported=True, playerWidth=20, playerHeight=42))
        else:
            def item(slot):
                return dict(slot=slot, hotbarKey=(slot+1) % 10 if slot < 10 else None, empty=slot != 9,
                            typeId=3509 if slot == 9 else 0, stack=1 if slot == 9 else 0,
                            name='fixture pick' if slot == 9 else '', use=dict(pick=35 if slot == 9 and not self.placement else 0, createTile=30 if slot==9 and self.placement else -1), inventoryToken=None)
            mode = args.get('mode', 'selected')
            indices = args.get('slots', [self.selected]) if mode == 'slots' else range(10) if mode == 'hotbar' else range(59) if mode == 'full' else [self.selected]
            data = dict(status='ok', selectedSlot=self.selected, selectedHotbarKey=(self.selected+1) % 10,
                        inventory=[item(slot) for slot in indices], inventoryTokenStatus='unsupported_server_ai_inventory_mutation')
            if mode == 'item':
                data = dict(status='ok', count=1, slots=[dict(slot=9, stack=1)])
            elif mode == 'full':
                data.update(equipment=[], customState='unknown; fixture custom data not read')
        return dict(available=True, worldSessionId=self.binding['world'], actorGeneration=self.binding['generation'],
                    actorSlot=self.binding['slot'], gameUpdate=33, data=data)


class Handler(socketserver.StreamRequestHandler):
    def handle(self):
        h = self.server
        reply_turn = 0
        queued_say = None
        while line := self.rfile.readline():
            r = json.loads(line)
            h.sent.append(r)
            op = r['op']
            error = None
            data = None
            if op == 'bind':
                data = dict(capabilities=['server_ai_atomic_v1', 'atomic_operations_v1', 'hotbar_keys_v1'])
                if h.direct_input:
                    data['capabilities'].append('server_ai_direct_input_v1')
                if h.reads:
                    data['capabilities'].append('server_ai_reads_v1')
                if h.maps:
                    data['capabilities'].append('server_ai_map_v1')
                if h.crafting:
                    data['capabilities'].append('server_ai_crafting_v1')
                if h.scene:
                    data['capabilities'].append('server_ai_scene_v1')
                if h.perception:
                    data['capabilities'].append('server_ai_perception_v1')
                if h.actions:
                    data['capabilities'].append('server_ai_actions_v1')
                if h.object_placement:
                    data['capabilities'].append('server_ai_place_object_workbench_v1')
                if h.placement:
                    data['capabilities'].append('server_ai_place_tile_v1')
                if h.landing:
                    data['capabilities'].append('landing_region_navigation_v1')
                if h.chat:
                    data['capabilities'].append('server_ai_chat_v2')
            elif r['binding'] != h.binding:
                error = 'stale_binding'
            elif op == 'reply_turn' and h.chat:
                reply_turn=max(reply_turn,r['replyTurn'])
                data=dict(replyTurn=reply_turn)
                if queued_say is not None:
                    self.wfile.write((json.dumps(dict(v=1,id=queued_say['id'],ok=False,error='superseded'))+'\n').encode())
                    queued_say=None
            elif op == 'say' and r.get('replyTurn') != reply_turn:
                error='superseded'
            elif op == 'say' and h.chat:
                if h.mode=='queue_say':
                    queued_say=r;h.say_queued.set();continue
                h.published.append(r)
                if h.mode == 'drop_say':
                    return
                data=dict(status='published',authorSlot=h.binding['slot'],worldSessionId=h.binding['world'],
                          actorGeneration=h.binding['generation'],gameUpdate=33)
            elif op == 'query':
                data = h.query(r['operation'], r['arguments'])
                if h.mode == 'bad_query_identity':
                    data['actorGeneration'] += 1
                elif h.mode == 'unavailable_query':
                    data = dict(available=False, reason='fixture_state_unknown')
            elif op == 'start':
                if h.mode in ('reject_start', 'reject_without_marker', 'reject_wrong_binding', 'reject_start_delayed'):
                    if h.mode == 'reject_start_delayed': h.release.wait(3)
                    refused = dict(v=1, id=r['id'], ok=False, binding=dict(h.binding), error=getattr(h,'start_error','action_busy'))
                    if h.mode != 'reject_without_marker': refused['dispatchState'] = 'not_admitted'
                    if h.mode == 'reject_wrong_binding': refused['binding']['generation'] += 1
                    self.wfile.write((json.dumps(refused)+'\n').encode())
                    continue
                if h.mode == 'drop_start':
                    return
                if h.mode == 'stall_start':
                    h.release.wait(3)
                    return
                key = r['arguments'].get('slot', 1)
                h.action = dict(actionId=r['actionId'], operation=r['operation'], session='connection-1',
                                generation=1, worldSessionId='world-1', actorId='actor-1', actorSlot=254,
                                status='running', reason='accepted', executionState='accepted', durationMs=300,
                                evidence=dict(targetSlot=(key + 9) % 10, selectionApplied=False),
                                endEvidenceStatus='not_ended', endEvidence=None)
                if r['operation'] == 'move_input':
                    h.action['requestedInputs'] = copy.deepcopy(r['arguments'])
                if r['operation'] == 'craft_recipe':
                    h.action['targetRecipe'] = copy.deepcopy(r['arguments'])
                if r['operation'] in ('place_tile', 'place_object'):
                    h.action['targetTile'] = copy.deepcopy(r['arguments'])
                if r['operation'] == 'navigate_to_landing_region':
                    h.action['targetRegion'] = copy.deepcopy(r['arguments'])
                data = dict(action=copy.deepcopy(h.action))
            elif op in ('action', 'cancel'):
                if h.mode == 'change_on_poll':
                    h.binding['generation'] = 2
                    self.wfile.write((json.dumps(dict(v=1, id=r['id'], ok=False, error='stale_binding'))+'\n').encode())
                    continue
                h.polls += 1
                if op == 'cancel' or h.mode not in ('pending', 'cancel_unknown') and h.polls >= 2:
                    a = h.action
                    h.selected = a['evidence'].get('targetSlot', h.selected)
                    a.update(status='cancelled' if op == 'cancel' else 'failed' if h.mode == 'failed' else 'completed', reason='fixture_end',
                             executionState='terminal', endEvidenceStatus='known',
                             endEvidence=dict(worldSessionId='world-1', gameUpdate=33,
                                              phase='ServerAI_game_thread_no_input', inputsAcquired=False,
                                              actorId='actor-1', actorGeneration=1, actorSlot=254,
                                              selectedSlot=h.selected, selectionVerified=True))
                    if op == 'cancel' and h.mode == 'cancel_unknown':
                        a.update(status='unknown', reason='direct_input_observation_unconfirmed' if a['operation']=='move_input' else 'native_placement_consumption_unconfirmed')
                    a['evidence']['selectionApplied'] = True
                    if h.mode == 'bad_evidence':
                        a['endEvidence']['selectionVerified'] = False
                    if a['operation'] != 'select_hotbar_slot':
                        a['evidence'] = dict(arrived=True, cycleVerified=True, destroyedByActor=True,
                                             targetAbsent=True, targetChanged=False, effectStatus='unconfirmed')
                        a['endEvidence'].update(phase='ServerAI_game_thread_inputs_released', inputsAcquired=True,
                                                inputsReleased=True, effectVerified=True, operation=a['operation'])
                        if a['operation'] == 'move_input':
                            g=a['requestedInputs']
                            a['evidence'].update(requestedInputs=copy.deepcopy(g),updatesRequested=g['maxUpdates'],updatesExecuted=g['maxUpdates'],inputReleased=True)
                            if a['status']=='unknown':a['endEvidence']['effectVerified']=False
                        if a['operation'] == 'craft_recipe':
                            g=a['targetRecipe']
                            a['evidence'].update(recipeId=g['recipeId'],recipeToken=g['recipeToken'],requestedCrafts=g['crafts'],completedCrafts=g['crafts'],producedCount=g['crafts'],outputTypeId=36,outputCountPerCraft=1,inventoryVerified=True,mutationAttempted=True,batches=[{'consumed': [{'typeId':9,'count':10}]}]*g['crafts'])
                            a['evidence'].update(h.crafting_override)
                            if h.mode == 'changed_recipe':a['targetRecipe']={**g,'recipeId':999}
                        if a['operation'] in ('place_tile', 'place_object'):
                            a['evidence'].update(placedByActor=True,targetPresent=True,targetChanged=False,consumedCount=1,**a['targetTile'])
                            if a['operation'] == 'place_object':
                                a['evidence'].update(objectPlacement=True,fullFootprintVerified=True,nativePlacementEvents=1,uncertainNativeEffect=False,typeId=18,itemType=36,style=0,footprint=dict(**a['targetTile'],width=2,height=1))
                            a['evidence'].update(h.placement_override)
                            if op == 'cancel' and h.mode == 'cancel_unknown':
                                a['evidence']['consumedCount'] = 0
                                a['endEvidence']['effectVerified'] = False
                        if h.mode == 'changed_tile':
                            a['targetTile'] = {**a['targetTile'], 'x': 11}
                        if a['operation'] == 'navigate_to_landing_region':
                            g = a['targetRegion']
                            a['evidence'].update(targetRegion=copy.deepcopy(g), x=g['minPlayerLeftX'],
                                y=g['surfaceY']-42, vx=0, vy=0, grounded=True, safeSupport=True, settled=True)
                            a['evidence'].update(h.landing_override)
                        if h.mode == 'changed_region':
                            a['targetRegion'] = {**a['targetRegion'], 'surfaceY': 1600}
                        if h.mode == 'no_release':
                            a['endEvidence']['inputsReleased'] = False
                        if h.mode == 'no_effect':
                            a['endEvidence']['effectVerified'] = False
                        if h.mode == 'replaced_target':
                            a['evidence']['targetChanged'] = True
                        if h.mode == 'wrong_operation':
                            a['endEvidence']['operation'] = 'select_hotbar_slot'
                if h.action_transform is not None and h.action['status'] != 'running':
                    h.action_transform(h.action)
                data = dict(action=copy.deepcopy(h.action))
            reply = dict(v=1, id=r['id'], ok=error is None, binding=h.binding, data=data, error=error)
            self.wfile.write((json.dumps(reply) + '\n').encode())


class BackendTests(unittest.IsolatedAsyncioTestCase):
    async def test_legacy_duplicate_not_admitted_remains_unknown_and_blocks_lua(self):
        self.host.mode='reject_start'
        self.host.start_error='duplicate_action'
        runtime=LuaRuntime(self.bridge,evidence_root=self.directory.name)
        result=await runtime.execute("api('select_hotbar_slot',{slot=2});api('select_hotbar_slot',{slot=3})")
        self.assertEqual('unknown',result['actionState'])
        self.assertEqual('unknown',result['lastAction']['dispatchState'])
        self.assertFalse(result['lastAction']['continuationAllowed'])
        self.assertTrue(self.bridge._closed)
        self.assertEqual(1,len([r for r in self.host.sent if r['op']=='start']))

    async def test_explicit_not_admitted_is_deterministic_and_never_replayed(self):
        self.host.mode = 'reject_start'
        for reason in ('action_busy','actor_not_alive','unsupported_operation','invalid_arguments','request_expired','invalid_deadline'):
            self.host.start_error = reason
            lifecycle = {}
            result = await atomic.dispatch(self.bridge,'select_hotbar_slot',{'slot':2},lifecycle=lifecycle)
            self.assertEqual(('failed','not_admitted'), (result['status'],result['dispatchState']))
            self.assertFalse(lifecycle['uncertain']); self.assertIsNone(self.host.action)
            self.assertFalse(self.bridge._closed)
        starts = [r['actionId'] for r in self.host.sent if r['op']=='start']
        self.assertEqual(6,len(set(starts)))
        runtime = LuaRuntime(self.bridge,evidence_root=self.directory.name)
        result = await runtime.execute("pcall(function() api('select_hotbar_slot',{slot=2}) end); log('escaped')")
        self.assertEqual(('failed','idle'),(result['status'],result['actionState']))
        self.assertEqual('not_admitted',result['lastAction']['dispatchState'])
        self.assertFalse(result['lastAction']['continuationAllowed']); self.assertEqual([],result['logs'])
        self.assertFalse(self.bridge._closed); self.assertFalse(getattr(self.bridge,'_lua_poisoned',False))
        self.assertEqual('completed',(await runtime.execute('return 1'))['status'])

    async def test_refusal_without_proof_or_with_wrong_binding_remains_unknown(self):
        for mode in ('reject_without_marker','reject_wrong_binding'):
            self.bridge.close(); self.host.mode=mode
            self.bridge=ServerAiBridge(self.host.server_address[1],timeout=1)
            result=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("api('select_hotbar_slot',{slot=2});api('select_hotbar_slot',{slot=3})")
            self.assertEqual('unknown',result['actionState']); self.assertTrue(self.bridge._closed)
            self.assertEqual('unknown',result['lastAction']['dispatchState'])
        self.assertEqual(2,len([r for r in self.host.sent if r['op']=='start']))

    async def test_cancel_waiting_on_explicit_refusal_does_not_poison(self):
        self.host.mode='reject_start_delayed'
        runtime=LuaRuntime(self.bridge,evidence_root=self.directory.name)
        task=asyncio.create_task(runtime.execute("api('select_hotbar_slot',{slot=2})"))
        async def started():
            while not any(r['op']=='start' for r in self.host.sent): await asyncio.sleep(.01)
        await asyncio.wait_for(started(),5)
        task.cancel(); await asyncio.sleep(.01); self.host.release.set()
        with self.assertRaises(asyncio.CancelledError): await task
        self.assertEqual('not_admitted',runtime.last_result['cleanup']['dispatchState'])
        self.assertEqual('idle',runtime.last_result['actionState']); self.assertFalse(self.bridge._closed)

    async def test_server_targeted_use_rejected_before_any_rpc(self):
        self.enable_native_actions()
        before=len(self.host.sent)
        with self.assertRaises(atomic.PreDispatchRejected):
            await atomic.dispatch(self.bridge,'use_item',{'target':{'x':10,'y':20}})
        self.assertEqual(before,len(self.host.sent))

    def enable_perception(self):
        self.bridge.close()
        self.host.perception = True
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)
        original = self.host.query
        def query(operation, args):
            result = original(operation, args)
            result.update(observedAtUnixMs=1793070000123, clock='server_utc_at_projection')
            if operation in ('observe_terrain','scan_tiles','scan_entities','inspect_tile','resolve_content'):
                result['data'] = dict(sample=operation, complete=False, unknownCount=2, truncated=True)
            return result
        self.host.query = query

    async def test_perception_lua_routes_all_five_reads_without_actions(self):
        self.enable_perception()
        source = """
            local terrain=api('observe_terrain',{})
            local tiles=api('scan_tiles',{minX=1,minY=2,maxX=4,maxY=5,tileTypes={0}})
            local entities=api('scan_entities',{category='item',radius=100,maxResults=1})
            local tile=api('inspect_tile',{x=1,y=2})
            local content=api('resolve_content',{kind='tile',query='Terraria/Dirt'})
            assert(terrain.valid and content.valid and tile.valid and entities.valid)
            assert(not tiles.data.complete and tiles.data.unknownCount==2 and tiles.data.truncated)
            return {time=tiles.observedAtUnixMs,tick=tiles.gameUpdate,binding=tiles.binding}
        """
        result = await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute(source,limits=Limits(seconds=8,actions=0))
        self.assertEqual('completed',result['status'],result)
        self.assertEqual(0,result['actions'])
        self.assertEqual(self.bridge.binding,result['value']['binding'])
        self.assertEqual(1793070000123,result['value']['time'])
        self.assertEqual(5,len([r for r in self.host.sent if r['op']=='query']))
        self.assertFalse(any(r['op']=='start' for r in self.host.sent))

    async def test_lua_float_tile_coordinates_are_json_integers_on_server_socket(self):
        self.enable_perception()
        source = """
            local s=api('get_player_state',{})
            local cx=(s.player.position.x+0.25)//16
            local cy=math.floor((s.player.position.y+0.5)/16)
            return api('scan_tiles',{minX=cx-1,minY=cy-1,maxX=cx+1,maxY=cy+1,
                tileTypes={5.0},maxResults=20.0})
        """
        result=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute(source,limits=Limits(seconds=8,actions=0))
        self.assertEqual('completed',result['status'],result)
        query=next(r for r in self.host.sent if r['op']=='query' and r['operation']=='scan_tiles')
        for field in ('minX','minY','maxX','maxY','maxResults'):
            self.assertIs(type(query['arguments'][field]),int)
        self.assertIs(type(query['arguments']['tileTypes'][0]),int)
        self.assertFalse(any(r['op']=='start' for r in self.host.sent))

    async def test_shipped_nearby_tree_program_uses_local_bounds_and_zero_actions(self):
        self.enable_perception()
        original=self.host.query
        def query(operation,args):
            result=original(operation,args)
            if operation=='observe_terrain':
                result['data']=dict(bounds=dict(minX=2090,minY=190,maxX=2134,maxY=218))
            elif operation=='resolve_content':
                result['data']=dict(status='ok',candidates=[dict(typeId=5,contentKey='Terraria/Trees')])
            elif operation=='scan_tiles':
                result['data']=dict(items=[dict(x=2100,y=200,typeId=5)],matchedCount=1,returnedCount=1,
                                    complete=True,truncated=False,unknownCount=0)
            return result
        self.host.query=query
        source=(Path(__file__).resolve().parents[1]/'examples/lua/nearby_trees.lua').read_text(encoding='utf-8')
        result=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute(source,limits=Limits(seconds=8,actions=0))
        self.assertEqual('completed',result['status'],result)
        self.assertEqual(1,result['value']['trees']['data']['matchedCount'])
        scan=next(r for r in self.host.sent if r['op']=='query' and r['operation']=='scan_tiles')
        self.assertEqual(dict(minX=2090,minY=190,maxX=2134,maxY=218,tileTypes=[5],maxResults=16),scan['arguments'])
        self.assertEqual(3,result['calls'])
        self.assertEqual(0,result['actions'])

    async def test_perception_missing_timestamp_closes_connection(self):
        self.enable_perception()
        original=self.host.query
        def query(operation,args):
            result=original(operation,args);result.pop('observedAtUnixMs');return result
        self.host.query=query
        with self.assertRaises(BridgeError):
            await atomic.dispatch(self.bridge,'observe_terrain',{})
        self.assertTrue(self.bridge._closed)

    async def test_perception_contract_is_capability_gated(self):
        from lua_agent import server_contract
        from server_ai_contract import exposed_operations
        self.assertNotIn('scan_tiles', exposed_operations(self.bridge))
        self.assertIn('Not exposed', server_contract(self.bridge)[0])
        self.enable_perception()
        instructions,_=server_contract(self.bridge)
        for name in ('observe_terrain','scan_tiles','scan_entities','inspect_tile','resolve_content'):
            self.assertIn(name,instructions)
        self.assertNotIn('没有地形/实体观察能力',instructions)
        self.assertNotIn('客户端已知NPC',instructions)
        with self.assertRaises(BridgeError):
            self.bridge.command('start_atomic',operation='scan_tiles')
        self.assertFalse(any(r['op']=='start' for r in self.host.sent))

    async def test_reply_fence_passes_say_waiting_on_same_tcp_connection(self):
        self.host.chat=True
        self.bridge.hello['capabilities'].append('server_ai_chat_v2')
        self.bridge.invalidate_replies(1)
        await asyncio.to_thread(self.bridge.advance_reply_turn,1)
        self.host.mode='queue_say'
        old=asyncio.create_task(asyncio.to_thread(self.bridge.command,'say',text='old',replyTurn=1))
        self.assertTrue(await asyncio.to_thread(self.host.say_queued.wait,2))
        self.bridge.invalidate_replies(2)
        await asyncio.wait_for(asyncio.to_thread(self.bridge.advance_reply_turn,2),2)
        with self.assertRaises(BridgeError) as error:await old
        self.assertEqual('superseded',error.exception.status)
        self.assertFalse(self.bridge._closed)
        self.assertEqual([],self.host.published)
        self.host.mode='complete'
        await asyncio.to_thread(self.bridge.command,'say',text='new',replyTurn=2)
        self.assertEqual(['new'],[r['text'] for r in self.host.published])
        self.assertEqual(1,sum(r['op']=='bind' for r in self.host.sent))

    async def test_old_worker_waiting_for_execution_lock_rechecks_before_send(self):
        self.host.chat=True;self.bridge.hello['capabilities'].append('server_ai_chat_v2')
        self.bridge.invalidate_replies(1);await asyncio.to_thread(self.bridge.advance_reply_turn,1)
        entered=threading.Event()
        def worker():
            entered.set()
            return self.bridge.command('say',text='old',replyTurn=1)
        with self.bridge._lock:
            old=asyncio.create_task(asyncio.to_thread(worker))
            self.assertTrue(await asyncio.to_thread(entered.wait,2))
            self.bridge.invalidate_replies(2)
            await asyncio.to_thread(self.bridge.advance_reply_turn,2)
        with self.assertRaises(BridgeError) as error:await old
        self.assertEqual('superseded',error.exception.status)
        self.assertFalse(any(r['op']=='say' for r in self.host.sent))
        self.assertFalse(self.bridge._closed)

    def setUp(self):
        self.host = Host()
        self.thread = threading.Thread(target=self.host.serve_forever, daemon=True)
        self.thread.start()
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)
        self.directory = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.bridge.close()
        self.host.release.set()
        self.host.shutdown()
        self.host.server_close()
        self.thread.join()
        self.directory.cleanup()

    def enable_native_actions(self):
        self.bridge.close()
        self.host.actions = True
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)

    async def test_direct_input_lua_multi_update_and_receipt_guards(self):
        self.host.actions = self.host.direct_input = True
        self.bridge.close()
        self.bridge = ServerAiBridge(self.host.server_address[1])
        self.assertIn('move_input', self.bridge.supported_atomic_operations)
        args = dict(horizontal='right', vertical='down', jumpHoldUpdates=8, maxUpdates=60)
        r = await LuaRuntime(self.bridge).execute('return api("move_input", {horizontal="right",vertical="down",jumpHoldUpdates=8,maxUpdates=60})')
        self.assertEqual('completed', r['status'], r)
        self.assertEqual(1, len([r for r in self.host.sent if r['op'] == 'start']))
        for mutate in (lambda a: a['evidence'].update(updatesExecuted=59),
                       lambda a: a['evidence'].update(inputReleased=False),
                       lambda a: a.update(requestedInputs={**args, 'maxUpdates': 59})):
            self.host.polls = 0
            self.host.action_transform = mutate
            r = await atomic.dispatch(self.bridge, 'move_input', args)
            self.assertFalse(r.get('completed', False))
            self.assertEqual('unknown', r['status'])

    async def test_direct_input_capability_and_duration_validation(self):
        args = dict(horizontal='none', vertical='none', jumpHoldUpdates=8, maxUpdates=4)
        with self.assertRaises(ValueError):
            atomic.validate('move_input', args)
        args['maxUpdates'] = 60
        for change in ({'horizontal': 'both'}, {'vertical': 'left'}, {'maxUpdates': 0}, {'maxUpdates': 601},
                       {'jumpHoldUpdates': True}, {'jumpHoldUpdates': -1}, {'extra': 1}):
            with self.assertRaises(ValueError):
                atomic.validate('move_input', {**args, **change})
        before = len(self.host.sent)
        with self.assertRaises(BridgeError):
            await atomic.dispatch(self.bridge, 'move_input', args)
        self.assertEqual(before, len(self.host.sent))

    async def test_existing_lua_worker_runs_navigation_use_mining_in_sequence(self):
        self.enable_native_actions()
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        result = await runtime.execute("""
            local move = api('navigate_to_x', {targetX=164})
            assert(move.valid and move.completed)
            local use = api('use_item', {})
            assert(use.valid and use.completed and use.evidence.effectStatus == 'unconfirmed')
            local mine = api('mine_tile', {x=10,y=20})
            assert(mine.valid and mine.completed)
            return mine
        """, limits=Limits(seconds=10, actions=3))
        self.assertEqual('completed', result['status'], result)
        self.assertEqual('released', result['actionState'])
        self.assertEqual(3, result['actions'])
        self.assertEqual(['navigate_to_x', 'use_item', 'mine_tile'],
                         [r['operation'] for r in self.host.sent if r['op'] == 'start'])

    async def test_native_completion_requires_release_effect_and_matching_operation(self):
        for mode in ('no_release', 'no_effect', 'replaced_target', 'wrong_operation'):
            with self.subTest(mode=mode):
                self.enable_native_actions()
                self.host.mode = mode
                result = await atomic.dispatch(self.bridge, 'mine_tile', {'x': 10, 'y': 20})
                self.assertEqual('unknown', result['status'], result)
                self.assertFalse(result['completed'])

    async def test_native_action_identity_change_never_retries(self):
        self.enable_native_actions()
        self.host.mode = 'change_on_poll'
        result = await atomic.dispatch(self.bridge, 'navigate_to_x', {'targetX': 164})
        self.assertEqual('unknown', result['status'], result)
        self.assertEqual(1, sum(r['op'] == 'start' for r in self.host.sent))

    async def test_existing_dispatch_verifies_key_zero_terminal_evidence(self):
        result = await atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 0})
        self.assertTrue(result['completed'], result)
        self.assertEqual(9, result['endEvidence']['selectedSlot'])
        self.assertEqual('known', result['endEvidenceStatus'])
        self.assertEqual(1, sum(r['op'] == 'start' for r in self.host.sent))

    async def test_actual_lua_worker_uses_existing_dispatcher_and_receipts(self):
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        result = await runtime.execute("return api('select_hotbar_slot', {slot=2})", limits=Limits(seconds=8))
        self.assertEqual('completed', result['status'], result)
        self.assertTrue(result['lastAction']['completed'])
        self.assertEqual('released', result['actionState'])
        self.assertEqual(1, result['actions'])

    async def test_unsupported_operations_reject_before_start(self):
        for name, arguments in [('mine_tile', dict(x=1, y=1)), ('observe_terrain', {}), ('get_player_status', {})]:
            with self.assertRaises(atomic.PreDispatchRejected):
                await atomic.dispatch(self.bridge, name, arguments)
        self.assertFalse(any(r['op'] == 'start' for r in self.host.sent))

    async def test_read_state_preserves_player_motion_interface_and_native_metadata(self):
        result = await atomic.dispatch(self.bridge, 'get_player_state', {})
        self.assertTrue(result['valid'], result)
        self.assertEqual(73, result['player']['life'])
        self.assertEqual(dict(x=100, y=200), result['player']['position'])
        self.assertEqual(20, result['motion']['playerWidth'])
        self.assertEqual(self.bridge.binding, result['binding'])
        self.assertFalse(any(r['op'] == 'start' for r in self.host.sent))

    async def test_all_inventory_modes_use_existing_query_dispatch(self):
        for args in (dict(mode='selected'), dict(mode='hotbar'), dict(mode='slots', slots=[58, 9, 0]), dict(mode='full'), dict(mode='item', item='Terraria/CopperPickaxe')):
            result = await atomic.dispatch(self.bridge, 'inspect_inventory', args)
            self.assertTrue(result['valid'], result)
            self.assertEqual('ok', result['data']['status'])
        result = await atomic.dispatch(self.bridge, 'inspect_inventory', dict(mode='slots', slots=[58, 9, 0]))
        self.assertEqual([58, 9, 0], [i['slot'] for i in result['data']['inventory']])
        self.assertIsNone(result['data']['inventory'][1]['inventoryToken'])
        self.assertFalse(any(r['op'] == 'start' for r in self.host.sent))

    async def test_workbench_capability_and_full_receipt_required(self):
        import agent, lua_agent
        self.assertNotIn('place_object', self.bridge.supported_atomic_operations)
        self.assertNotIn('place_object', [t['function']['name'] for t in agent.TOOLS])
        self.assertNotIn('place_object', lua_agent.TOOL['function']['description'])
        with self.assertRaises(atomic.PreDispatchRejected):
            await atomic.dispatch(self.bridge, 'place_object', {'x': 10, 'y': 20})
        self.bridge.close()
        self.host.actions = self.host.object_placement = True
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)
        self.assertIn('place_object', self.bridge.supported_atomic_operations)
        self.assertNotIn('place_tile', self.bridge.supported_atomic_operations)
        for override in ({}, {'nativePlacementEvents': 0}, {'uncertainNativeEffect': True}, {'fullFootprintVerified': False}, {'typeId': 19}, {'itemType': 34},
                         {'style': 1}, {'footprint': {'x': 10, 'y': 20, 'width': 2, 'height': True}}, {'footprint': {'x': 10, 'y': 20, 'width': 1, 'height': 1}},
                         {'consumedCount': 0}, {'placedByActor': False}, {'targetChanged': True}):
            self.host.polls = 0
            self.host.placement_override = override
            result = await atomic.dispatch(self.bridge, 'place_object', {'x': 10, 'y': 20})
            self.assertEqual('unknown' if override else 'completed', result['status'], result)
        self.host.placement_override = {}
        self.host.mode = 'no_release'
        self.host.polls = 0
        result = await atomic.dispatch(self.bridge, 'place_object', {'x': 10, 'y': 20})
        self.assertEqual('unknown', result['status'])

    async def test_workbench_lua_uses_existing_dispatch_and_unknown_barrier(self):
        self.bridge.close()
        self.host.actions = self.host.object_placement = True
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        result = await runtime.execute("local r=api('place_object',{x=10,y=20}); assert(r.completed); return r.evidence.footprint", limits=Limits(seconds=8))
        self.assertEqual('completed', result['status'], result)
        self.host.polls = 0
        self.host.placement_override = {'consumedCount': 0}
        result = await runtime.execute("api('place_object',{x=10,y=20}); api('place_object',{x=12,y=20})", limits=Limits(seconds=8))
        self.assertNotEqual('completed', result['status'], result)
        self.assertEqual(2, sum(r['op'] == 'start' for r in self.host.sent))

    async def test_direct_input_explicit_cancel_preserves_unknown_release(self):
        self.bridge.close()
        self.host.actions = self.host.direct_input = True
        self.host.mode = 'cancel_unknown'
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)
        lifecycle = {}
        args = dict(horizontal='right', vertical='none', jumpHoldUpdates=0, maxUpdates=60)
        task = asyncio.create_task(atomic.dispatch(self.bridge, 'move_input', args, lifecycle=lifecycle))
        while self.host.action is None:
            await asyncio.sleep(.01)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        cleanup = lifecycle['cleanup']
        self.assertEqual('unknown', cleanup['status'], cleanup)
        self.assertFalse(cleanup['completed'])
        self.assertTrue(cleanup['valid'])
        self.assertEqual('known', cleanup['endEvidenceStatus'])
        self.assertTrue(cleanup['endEvidence']['inputsReleased'])
        self.assertFalse(cleanup['endEvidence']['effectVerified'])
        self.assertEqual(1, sum(r['op'] == 'start' for r in self.host.sent))

    async def test_direct_input_deadline_unknown_poisoned_lua_without_retry(self):
        self.bridge.close()
        self.host.actions = self.host.direct_input = True
        self.host.mode = 'cancel_unknown'
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        source = 'api("move_input",{horizontal="right",vertical="none",jumpHoldUpdates=0,maxUpdates=60}); api("move_input",{horizontal="left",vertical="none",jumpHoldUpdates=0,maxUpdates=60})'
        result = await runtime.execute(source, limits=Limits(seconds=8))
        self.assertEqual('unknown', result['actionState'], result)
        self.assertEqual('unknown', result['lastAction']['status'], result)
        self.assertFalse(result['lastAction']['completed'])
        self.assertFalse(result['lastAction']['continuationAllowed'])
        self.assertEqual('known', result['lastAction']['endEvidenceStatus'])
        self.assertTrue(result['lastAction']['endEvidence']['inputsReleased'])
        self.assertTrue(self.bridge._lua_poisoned)
        blocked = await runtime.execute(source, limits=Limits(seconds=8))
        self.assertEqual('blocked', blocked['status'])
        self.assertEqual(1, sum(r['op'] == 'start' for r in self.host.sent))
        self.assertEqual(1, sum(r['op'] == 'cancel' for r in self.host.sent))

    async def test_workbench_explicit_cancel_preserves_unknown_release(self):
        self.bridge.close()
        self.host.actions = self.host.object_placement = True
        self.host.mode = 'cancel_unknown'
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)
        lifecycle = {}
        task = asyncio.create_task(atomic.dispatch(self.bridge, 'place_object', {'x': 10, 'y': 20}, lifecycle=lifecycle))
        while self.host.action is None:
            await asyncio.sleep(.01)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        cleanup = lifecycle['cleanup']
        self.assertEqual('unknown', cleanup['status'], cleanup)
        self.assertTrue(cleanup['valid'])
        self.assertTrue(cleanup['endEvidence']['inputsReleased'])
        self.assertEqual(0, cleanup['evidence']['consumedCount'])
        self.assertEqual(1, sum(r['op'] == 'start' for r in self.host.sent))

    async def test_workbench_deadline_cancel_unknown_poisoned_lua_without_retry(self):
        self.bridge.close()
        self.host.actions = self.host.object_placement = True
        self.host.mode = 'cancel_unknown'
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        source = "api('place_object',{x=10,y=20}); api('place_object',{x=12,y=20})"
        result = await runtime.execute(source, limits=Limits(seconds=8))
        self.assertEqual('unknown', result['actionState'], result)
        self.assertTrue(self.bridge._lua_poisoned)
        self.assertEqual('unknown', result['lastAction']['status'], result)
        blocked = await runtime.execute(source, limits=Limits(seconds=8))
        self.assertEqual('blocked', blocked['status'])
        self.assertEqual(1, sum(r['op'] == 'start' for r in self.host.sent))
        self.assertEqual(1, sum(r['op'] == 'cancel' for r in self.host.sent))

    async def test_lua_reads_before_and_after_existing_selection(self):
        source = """
        local before=api('get_player_state',{})
        local bag=api('inspect_inventory',{mode='hotbar'})
        local action=api('select_hotbar_slot',{slot=0})
        assert(action.completed)
        local selected=api('inspect_inventory',{mode='selected'})
        local after=api('get_player_state',{})
        return {before=before.player.selectedSlot, after=after.player.selectedSlot,
                key=selected.data.selectedHotbarKey, pick=selected.data.inventory[1].use.pick}
        """
        result = await LuaRuntime(self.bridge, evidence_root=self.directory.name).execute(source, limits=Limits(seconds=8))
        self.assertEqual('completed', result['status'], result)
        self.assertEqual(dict(before=0, after=9, key=0, pick=35), result['value'])
        self.assertEqual(1, result['actions'])

    async def test_committed_acceptance_lua_is_read_only_with_zero_action_budget(self):
        source = 'local testKey=nil\n' + (Path(__file__).resolve().parents[1] / 'scripts/lua/server_ai_reads.lua').read_text(encoding='utf-8')
        result = await LuaRuntime(self.bridge, evidence_root=self.directory.name).execute(source, limits=Limits(seconds=8, actions=0))
        self.assertEqual('completed', result['status'], result)
        self.assertEqual(0, result['actions'])
        self.assertEqual(59, result['value']['fullInventorySlots'])
        self.assertEqual(1, len(result['value']['inventoryAtFullQuery']))
        self.assertEqual(1, result['value']['occupiedInventorySlots'])
        self.assertFalse(any(r['op'] == 'start' for r in self.host.sent))

    async def test_read_identity_mismatch_fails_without_fallback(self):
        self.host.mode = 'bad_query_identity'
        with self.assertRaises(BridgeError):
            await atomic.dispatch(self.bridge, 'get_player_state', {})
        self.assertTrue(self.bridge._closed)
        self.assertFalse(any(r['op'] == 'start' for r in self.host.sent))

    async def test_unavailable_read_does_not_invent_player_or_inventory(self):
        self.host.mode = 'unavailable_query'
        for name in ('get_player_state', 'inspect_inventory'):
            result = await atomic.dispatch(self.bridge, name, {'mode': 'selected'} if name == 'inspect_inventory' else {})
            self.assertFalse(result['valid'])
            self.assertNotIn('player', result)
            self.assertNotIn('data', result)

    async def test_previous_server_without_read_capability_rejects_before_rpc(self):
        self.bridge.close()
        self.host.reads = False
        self.bridge = ServerAiBridge(self.host.server_address[1])
        self.host.sent.clear()
        for name in ('get_player_state', 'inspect_inventory'):
            with self.assertRaises(atomic.PreDispatchRejected):
                await atomic.dispatch(self.bridge, name, {'mode': 'selected'} if name == 'inspect_inventory' else {})
        self.assertEqual([], self.host.sent)

    async def test_missing_selection_evidence_preserves_unknown(self):
        self.host.mode = 'bad_evidence'
        result = await atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 2})
        self.assertEqual('unknown', result['status'])
        self.assertFalse(result['completed'])

    async def test_lost_start_receipt_is_unknown_and_never_retried(self):
        self.host.mode = 'drop_start'
        result = await atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 2})
        self.assertEqual('unknown', result['status'])
        self.assertEqual(1, sum(r['op'] == 'start' for r in self.host.sent))

    async def test_transport_timeout_after_start_is_unknown(self):
        self.host.mode = 'stall_start'
        self.bridge.sock.settimeout(.1)
        result = await atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 2})
        self.assertEqual('unknown', result['status'])
        self.assertTrue(self.bridge._closed)
        self.assertEqual(1, sum(r['op'] == 'start' for r in self.host.sent))

    async def test_generation_change_after_acceptance_is_unknown(self):
        self.host.mode = 'change_on_poll'
        result = await atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 2})
        self.assertEqual('unknown', result['status'])
        self.assertTrue(self.bridge._closed)

    async def test_server_unknown_evidence_poisoning_uses_existing_lua_runtime(self):
        self.host.mode = 'bad_evidence'
        runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)
        result = await runtime.execute("return api('select_hotbar_slot', {slot=2})", limits=Limits(seconds=8))
        self.assertEqual('unknown', result['actionState'], result)
        self.assertTrue(self.bridge._lua_poisoned)
        self.assertTrue(self.bridge._closed)

    async def test_cancellation_uses_existing_lifecycle(self):
        self.host.mode = 'pending'
        lifecycle = {}
        task = asyncio.create_task(atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 2}, lifecycle=lifecycle))
        while self.host.action is None:
            await asyncio.sleep(.01)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        self.assertEqual('cancelled', lifecycle['cleanup']['status'])
        self.assertTrue(lifecycle['cleanup']['valid'])

    async def test_generation_change_does_not_rebind_or_touch_human(self):
        self.host.binding['generation'] = 2
        with self.assertRaises(BridgeError):
            self.bridge.command('get_movement')
        self.assertFalse(any(r['op'] == 'start' for r in self.host.sent))

    async def test_end_phase_is_not_accepted_without_server_capability(self):
        result = await atomic.dispatch(self.bridge, 'select_hotbar_slot', {'slot': 2})
        action = self.host.action
        self.bridge.hello['capabilities'].remove('server_ai_atomic_v1')
        result = await atomic.terminal(self.bridge, action, 1)
        self.assertEqual('unknown', result['status'])


if __name__ == '__main__':
    unittest.main()
