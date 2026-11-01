import asyncio
import json
import unittest

from test_atomic_operations import Game
import atomic_operations as atomic
from agent import Agent
from control import BridgeError
from test_agent import Model, event, tool, final


class ExtensionValidationTests(unittest.TestCase):
    def test_scan_bounds_arrays_and_unknown_fields(self):
        base = dict(minX=0, minY=0, maxX=63, maxY=63, tileTypes=[0, 2], maxResults=256)
        atomic.validate('scan_tiles', base)
        for change in [dict(maxX=64), dict(minX=64), dict(tileTypes=[]), dict(tileTypes=[2, 2]),
                       dict(tileTypes=[True]), dict(tileTypes=[1.5]), dict(tileTypes=[65536]),
                       dict(tileTypes=list(range(33))), dict(tileTypes='2'), dict(maxResults=0),
                       dict(maxResults=257), dict(minX=True), dict(maxY=-1), dict(extra=1)]:
            with self.subTest(change=change), self.assertRaises(ValueError):
                atomic.validate('scan_tiles', {**base, **change})

    def test_inventory_modes_strict(self):
        for args in [{}, {'mode': 'selected'}, {'mode': 'hotbar'}, {'mode': 'full'},
                     {'mode': 'item', 'item': 'Terraria/StoneBlock'}, {'mode': 'item', 'item': '石块'}]:
            atomic.validate('inspect_inventory', args)
        for args in [{'mode': 'item'}, {'item': 'Stone'}, {'mode': 'full', 'item': 'Stone'},
                     {'mode': 'item', 'item': ''}, {'mode': 'all'}, {'mode': True}, {'full': True}]:
            with self.subTest(args=args), self.assertRaises(ValueError):
                atomic.validate('inspect_inventory', args)

    def test_actions_reject_bad_coordinates(self):
        for name in ('mine_wall', 'place_wall', 'hammer_tile', 'interact_tile'):
            atomic.validate(name, dict(x=5, y=5))
            for args in [dict(x=True, y=1), dict(x=1.5, y=2), dict(x=0, y=-1),
                         dict(x=100001, y=1), dict(x=1), dict(x=1, y=1, force=True)]:
                with self.subTest(name=name, args=args), self.assertRaises(ValueError):
                    atomic.validate(name, args)


class ExtensionDispatchTests(unittest.IsolatedAsyncioTestCase):
    async def test_authority_capability_prevents_old_action_semantics(self):
        for name in ('mine_wall', 'place_wall', 'hammer_tile', 'interact_tile'):
            game = Game()
            game.hello['capabilities'].remove('server_confirmed_interactions_v1')
            with self.subTest(name=name), self.assertRaisesRegex(BridgeError, 'server_confirmed_interactions_v1'):
                await atomic.dispatch(game, name, dict(x=5, y=5))
            self.assertEqual([], game.sent)

    async def test_authority_evidence_and_uncertainty_preserved(self):
        for status in ('completed', 'failed', 'timeout', 'cancelled', 'unknown'):
            game = Game(status)
            evidence = dict(authority='server_native_handler' if status == 'completed' else 'pending_server',
                            mutationSent=True, nonce='nonce', serverWorldId='server-world',
                            clientStateVerified=status == 'completed')
            original = game.command
            def command(cmd, **args):
                reply = original(cmd, **args)
                if reply.get('movement', {}).get('action'):
                    reply['movement']['action']['evidence'] = evidence
                return reply
            game.command = command
            out = await atomic.dispatch(game, 'place_wall', dict(x=5, y=5))
            self.assertEqual(status, out['status'])
            self.assertEqual(evidence, out['evidence'])
            self.assertEqual(status == 'completed', out['completed'])

    async def test_extension_capability_required(self):
        for name, args in [('scan_tiles', dict(minX=0, minY=0, maxX=1, maxY=1, tileTypes=[2])),
                           ('inspect_inventory', {'mode': 'selected'}), ('mine_wall', dict(x=1, y=1)),
                           ('place_wall', dict(x=1, y=1)), ('hammer_tile', dict(x=1, y=1)),
                           ('interact_tile', dict(x=1, y=1))]:
            game = Game()
            game.hello['capabilities'].remove('atomic_extensions_v1')
            with self.subTest(name=name), self.assertRaisesRegex(BridgeError, 'atomic_extensions_v1'):
                await atomic.dispatch(game, name, args)
            self.assertEqual([], game.sent)

    async def test_queries_dispatch_without_action_and_preserve_unknown(self):
        class QueryGame(Game):
            def command(self, cmd, **args):
                self.sent.append((cmd, args))
                return {'data': {'available': True, 'data': {'status': 'partial_unloaded', 'returnedCount': 0, 'unknownCount': 4}}}
        game = QueryGame()
        out = await atomic.dispatch(game, 'scan_tiles', dict(minX=0, minY=0, maxX=1, maxY=1, tileTypes=[2]))
        self.assertEqual('partial_unloaded', out['data']['status'])
        self.assertEqual(['query_atomic'], [c for c, _ in game.sent])
        game = Game()
        await atomic.dispatch(game, 'inspect_inventory', {'mode': 'item', 'item': 'Terraria/StoneBlock'})
        self.assertEqual({'mode': 'item', 'item': 'Terraria/StoneBlock'}, game.sent[0][1]['arguments'])

    async def test_terminal_statuses_and_missing_release_for_each_action(self):
        for name in ('mine_wall', 'place_wall', 'hammer_tile', 'interact_tile'):
            for status in ('completed', 'failed', 'timeout', 'cancelled'):
                game = Game(status)
                out = await atomic.dispatch(game, name, dict(x=5, y=5))
                self.assertEqual(status, out['status'])
                self.assertEqual(status == 'completed', out['completed'])
                self.assertGreater(game.polls, 0)
            out = await atomic.terminal(Game(), dict(actionId=1, operation=name, status='completed', reason='shape', endEvidenceStatus='unknown'), 1)
            self.assertEqual('unknown', out['status'])

    async def test_each_new_action_cancels_owned_input(self):
        for name in ('mine_wall', 'place_wall', 'hammer_tile'):
            game = Game(pending=True)
            task = asyncio.create_task(atomic.dispatch(game, name, dict(x=5, y=5)))
            while game.action is None:
                await asyncio.sleep(.01)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
            self.assertIn(('cancel_move', {'actionId': game.action['actionId']}), game.sent)

    async def test_agent_extension_query_and_action_dispatch(self):
        game = Game()
        model = Model(tool('inspect_inventory', '{"mode":"hotbar"}'), tool('hammer_tile', '{"x":5,"y":5}'), final('完成'))
        agent = Agent(game, model)
        agent.receive(event(1))
        await agent.task
        self.assertEqual('hammer_tile', next(p['operation'] for c, p in game.sent if c == 'start_atomic'))
        queries = [p["arguments"]["mode"] for c, p in game.sent if c == "query_atomic"]
        self.assertEqual(["full", "hotbar", "full", "full"], queries)
