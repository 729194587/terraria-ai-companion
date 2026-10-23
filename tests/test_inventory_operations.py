"""Inventory schema/dispatcher/runtime contracts; game effects are simulated here.

Actual C# transfer/storage integration is tested separately in the TCP protocol suite.
"""
import asyncio
import multiprocessing as mp
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
import atomic_operations as atomic
from control import BridgeError
from lua_runtime import LuaRuntime
from test_lua_runtime import RuntimeGame

TOKEN = 'a' * 64


def args(name):
    value = dict(sourceSlot=10, sourceToken=TOKEN, destinationToken='b'*64)
    if name == 'hold_inventory_item':
        value['hotbarKey'] = 0
    else:
        value['destinationSlot'] = 9
    if name == 'transfer_inventory_item':
        value['count'] = 3
    return value


NAMES = ('transfer_inventory_item', 'swap_inventory_slots', 'hold_inventory_item')


class InventorySchemaTests(unittest.TestCase):
    def test_all_mutations_are_registered_as_actions_with_strict_guards(self):
        for name in NAMES:
            self.assertIn(name, atomic.ACTIONS)
            atomic.validate(name, args(name))
            for key in ('sourceToken', 'destinationToken'):
                for token in ('', '0'*63, 'g'*64, 'a'*65, 1, None):
                    with self.subTest(name=name, key=key, token=token), self.assertRaises(ValueError):
                        atomic.validate(name, {**args(name), key: token})
            for slot in (-1, 50, 58, True, 1.5):
                with self.subTest(slot=slot), self.assertRaises(ValueError):
                    atomic.validate(name, {**args(name), 'sourceSlot': slot})
        for count in (0, -1, 10000, True, 1.5):
            with self.assertRaises(ValueError):
                atomic.validate('transfer_inventory_item', {**args('transfer_inventory_item'), 'count': count})
        for name in NAMES[:2]:
            with self.assertRaises(ValueError):
                atomic.validate(name, {**args(name), 'destinationSlot': 10})
        with self.assertRaises(ValueError):
            atomic.validate('hold_inventory_item', {**args('hold_inventory_item'), 'hotbarKey': 10})

    def test_slots_query_is_bounded_and_only_valid_in_slots_mode(self):
        atomic.validate('inspect_inventory', {'mode': 'slots', 'slots': [0, 49, 50, 58]})
        for value in ({'mode': 'slots'}, {'slots': [0]}, {'mode': 'slots', 'slots': []},
                      {'mode': 'slots', 'slots': [0, 0]}, {'mode': 'slots', 'slots': [59]},
                      {'mode': 'slots', 'slots': [True]}, {'mode': 'slots', 'slots': list(range(17))},
                      {'mode': 'full', 'slots': [0]}):
            with self.subTest(value=value), self.assertRaises(ValueError):
                atomic.validate('inspect_inventory', value)


class InventoryRuntimeTests(unittest.IsolatedAsyncioTestCase):
    async def asyncTearDown(self):
        self.assertFalse(mp.active_children())

    def game(self, *pos, **kw):
        game = RuntimeGame(*pos, **kw)
        game.hello['capabilities'].append('inventory_operations_v1')
        return game

    async def test_old_mod_is_rejected_before_start_or_query(self):
        game = RuntimeGame()
        for name, params in [(n, args(n)) for n in NAMES] + [('inspect_inventory', {'mode': 'slots', 'slots': [10, 0]})]:
            with self.assertRaisesRegex(BridgeError, 'inventory_operations_v1'):
                await atomic.dispatch(game, name, params)
        self.assertEqual([], game.sent)

    async def test_slot_query_arguments_are_forwarded_without_action(self):
        game = self.game()
        await atomic.dispatch(game, 'inspect_inventory', {'mode': 'slots', 'slots': [10, 9]})
        self.assertEqual([('query_atomic', dict(operation='inspect_inventory', arguments={'mode': 'slots', 'slots': [10, 9]}))], game.sent)

    async def test_action_receipts_and_guard_arguments_are_preserved(self):
        for name in NAMES:
            game = self.game()
            r = await atomic.dispatch(game, name, args(name), chat_id='inventory-turn')
            self.assertTrue(r['completed'])
            self.assertEqual('known', r['endEvidenceStatus'])
            sent = next(p for c, p in game.sent if c == 'start_atomic')
            self.assertEqual(args(name), sent['arguments'])
            self.assertEqual('inventory-turn', sent['chatId'])

    async def test_new_inventory_action_cancellation_uses_existing_cleanup(self):
        game = self.game(pending=True)
        cancel = asyncio.Event()
        task = asyncio.create_task(LuaRuntime(game).execute(
            "api('hold_inventory_item',{sourceSlot=10,hotbarKey=0,sourceToken='"+TOKEN+"',destinationToken='"+TOKEN+"'}); api('use_item')", cancel=cancel))
        async with asyncio.timeout(10):
            while game.action is None:
                await asyncio.sleep(.01)
        cancel.set()
        r = await task
        self.assertEqual(('cancelled', 'released'), (r['status'], r['actionState']))
        self.assertEqual('cancelled', r['cleanup']['status'])
        self.assertEqual(1, sum(c == 'start_atomic' for c, _ in game.sent))

    async def test_unknown_inventory_effect_fences_session(self):
        game = self.game('unknown')
        r = await LuaRuntime(game).execute(
            "return api('swap_inventory_slots',{sourceSlot=10,destinationSlot=9,sourceToken='"+TOKEN+"',destinationToken='"+TOKEN+"'})")
        self.assertEqual(('action_unknown', 'unknown'), (r['status'], r['actionState']))
        self.assertTrue(game.closed)
        self.assertTrue(game._lua_poisoned)


if __name__ == '__main__':
    unittest.main()
