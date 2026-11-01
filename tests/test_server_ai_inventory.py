"""Server inventory capability/receipt guards over the existing real loopback fixture."""
import asyncio
import copy
import threading
import unittest

from test_server_ai_backend import Host
from server_ai import ServerAiBridge, operations_for_capabilities
import atomic_operations as atomic
from lua_runtime import LuaRuntime, Limits


TOKEN = 'a' * 64
ARGS = dict(sourceSlot=10, destinationSlot=0, count=3, sourceToken=TOKEN, destinationToken=TOKEN)


def item(slot, stack, token=TOKEN):
    return dict(slot=slot, typeId=9 if stack else 0, stack=stack, prefix=0,
                maxStack=9999 if stack else 0, favorited=False, inventoryToken=token)


def evidence(op, goal):
    dest = (goal['hotbarKey'] - 1) % 10 if op == 'hold_inventory_item' else goal['destinationSlot']
    source = goal['sourceSlot']
    before = dict(source=item(source, 10), destination=item(dest, 0), selectedSlot=1)
    if source == dest:
        before['destination'] = copy.deepcopy(before['source'])
    if op == 'transfer_inventory_item':
        after = dict(source=item(source, 7), destination=item(dest, 3), selectedSlot=1)
    else:
        after = dict(source={**before['destination'], 'slot': source}, destination={**before['source'], 'slot': dest},
                     selectedSlot=dest if op == 'hold_inventory_item' else 1)
    return dict(before=before, after=after, sourceSlot=source, destinationSlot=dest,
                selectedSlot=after['selectedSlot'], mutationAttempted=True, inventoryVerified=True,
                mutationGameUpdate=20, confirmedAtGameUpdate=21)


class InventoryBackendTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.host = Host()
        self.host.actions = True
        self.thread = threading.Thread(target=self.host.serve_forever, daemon=True)
        self.thread.start()
        self.bridge = ServerAiBridge(self.host.server_address[1])

    def tearDown(self):
        self.bridge.close()
        self.host.release.set()
        self.host.shutdown()
        self.host.server_close()
        self.thread.join()

    def enable(self, op, goal, alter=None):
        self.bridge.hello['capabilities'].append('server_ai_inventory_v1')
        self.bridge.supported_atomic_operations = operations_for_capabilities(self.bridge.hello['capabilities'])
        original = self.bridge._movement
        def movement(action, status='movement'):
            if action is not None:
                action['targetInventory'] = copy.deepcopy(goal)
                if action['status'] != 'running':
                    action['evidence'] = evidence(op, goal)
                    action['endEvidence']['inputsAcquired'] = False
                    if alter:
                        alter(action)
            return original(action, status)
        self.bridge._movement = movement

    async def test_old_server_refuses_without_sending_start(self):
        for op in atomic.INVENTORY_OPERATIONS:
            args = ARGS if op != 'hold_inventory_item' else dict(sourceSlot=10, hotbarKey=0, sourceToken=TOKEN, destinationToken=TOKEN)
            if op == 'swap_inventory_slots':
                args = {k: v for k, v in ARGS.items() if k != 'count'}
            with self.assertRaises(atomic.PreDispatchRejected):
                await atomic.dispatch(self.bridge, op, args)
        self.assertFalse(any(r['op'] == 'start' for r in self.host.sent))
        self.assertFalse(atomic.INVENTORY_OPERATIONS & operations_for_capabilities(['server_ai_atomic_v1', 'server_ai_inventory_v1']))
        definitions = atomic.server_tools(atomic.INVENTORY_OPERATIONS)
        self.assertEqual(3, len(definitions))
        self.assertTrue(all('单人' not in t['function']['description'] for t in definitions))

    async def test_complete_transfer_through_lua_worker(self):
        self.enable('transfer_inventory_item', ARGS)
        result = await LuaRuntime(self.bridge).execute("return api('transfer_inventory_item', {sourceSlot=10,destinationSlot=0,count=3,sourceToken='"+TOKEN+"',destinationToken='"+TOKEN+"'})")
        self.assertEqual('completed', result['status'], result)
        self.assertTrue(result['value']['completed'])
        self.assertEqual(1, len([r for r in self.host.sent if r['op'] == 'start']))

    async def test_unknown_effect_fences_further_inventory_actions(self):
        def unknown(action):
            action.update(status='unknown', reason='inventory_native_exception')
            action['endEvidence']['effectVerified'] = False
        self.enable('transfer_inventory_item', ARGS, unknown)
        call = "api('transfer_inventory_item', {sourceSlot=10,destinationSlot=0,count=3,sourceToken='"+TOKEN+"',destinationToken='"+TOKEN+"'})"
        result = await LuaRuntime(self.bridge).execute(call + '; ' + call, limits=Limits(actions=2))
        self.assertEqual('unknown', result['actionState'], result)
        self.assertEqual(1, len([r for r in self.host.sent if r['op'] == 'start']))

    async def test_swap_and_hold_receipts(self):
        for op, args in [('swap_inventory_slots', {k: v for k, v in ARGS.items() if k != 'count'}),
                         ('hold_inventory_item', dict(sourceSlot=10, hotbarKey=0, sourceToken=TOKEN, destinationToken=TOKEN))]:
            # Each bridge has its own action ownership; fixture target is immutable.
            self.host.polls = 0
            self.enable(op, args)
            out = await atomic.dispatch(self.bridge, op, args)
            self.assertTrue(out['valid'] and out['completed'], out)
            self.bridge._movement = ServerAiBridge._movement.__get__(self.bridge)

    async def test_incomplete_or_forged_receipts_are_unknown(self):
        changes = [lambda a: a['evidence'].update(inventoryVerified=False),
                   lambda a: a['evidence'].update(confirmedAtGameUpdate=20),
                   lambda a: a['evidence']['after']['source'].update(stack=10),
                   lambda a: a['evidence']['before']['destination'].update(inventoryToken='b'*64),
                   lambda a: a['endEvidence'].update(inputsReleased=False)]
        for change in changes:
            self.host.polls = 0
            self.enable('transfer_inventory_item', ARGS, change)
            out = await atomic.dispatch(self.bridge, 'transfer_inventory_item', ARGS)
            self.assertEqual('unknown', out['status'], out)
            self.assertFalse(out['completed'])
            self.bridge._movement = ServerAiBridge._movement.__get__(self.bridge)

    async def test_changed_inventory_target_stops_without_replay(self):
        goal = copy.deepcopy(ARGS)
        self.enable('transfer_inventory_item', goal)
        self.host.action_transform = lambda a: goal.update(count=4)
        out = await atomic.dispatch(self.bridge, 'transfer_inventory_item', ARGS)
        self.assertEqual('unknown', out['status'], out)
        self.assertEqual(1, len([r for r in self.host.sent if r['op'] == 'start']))


if __name__ == '__main__':
    unittest.main()
