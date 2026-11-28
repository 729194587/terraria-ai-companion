"""M2 ownership, backend projection and actual Python/C# parameter agreement."""
import copy
import asyncio
import json
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]/'python'))
import atomic_operations as atomic
from agent import Agent, TOOLS
from control import Bridge
from lua_agent import TOOL_NAME
from test_agent import Game, Model
from test_control import FakeServer

ROOT=Path(__file__).resolve().parents[1]
VECTORS=ROOT/'tests/fixtures/api-contract-vectors.json'


class ApiContractTests(unittest.TestCase):
    def test_client_hello_projects_direct_and_lua_tools_and_guards_dispatch(self):
        unavailable = {
            'query_map': {'mode': 'overview'},
            'plan_materials': {'typeId': 8, 'count': 12},
            'loot_all': {'containerToken': 'a' * 64},
        }
        rich = ['atomic_operations_v1', 'atomic_extensions_v1', 'server_confirmed_interactions_v1',
                'inventory_operations_v1', 'viewport_observation_v1', 'movement_loop_v1', 'bounded_movement_v1']
        for caps in (rich, ['atomic_operations_v1'], None):
            with self.subTest(caps=caps), FakeServer(capabilities=caps) as server, Bridge(server.port) as bridge:
                definitions = {t['function']['name']: t['function'] for t in Agent(bridge, Model()).tools}
                listed = definitions[TOOL_NAME]['description'].split('可调用的 API：', 1)[1].split('。', 1)[0]
                exposed = set(listed.split(', '))
                self.assertEqual(bridge.supported_atomic_operations, exposed)
                self.assertEqual(definitions.keys() & atomic.SCHEMAS.keys(), exposed)
                self.assertTrue(unavailable.keys().isdisjoint(exposed))
                for name in ('inspect_inventory', 'interact_tile', 'hold_inventory_item', 'move', 'get_observation'):
                    self.assertEqual(caps == rich, name in definitions, name)
                self.assertEqual(bool(caps), 'mine_tile' in exposed)
                for name, args in unavailable.items():
                    with self.assertRaises(atomic.PreDispatchRejected):
                        asyncio.run(atomic.dispatch(bridge, name, args))
                self.assertEqual([], server.requests)
                self.assertTrue(asyncio.run(atomic.dispatch(bridge, 'get_player_state', {}))['valid'])
                self.assertEqual(['get_terrain'], [r['cmd'] for r in server.requests])

    def test_shared_vectors_and_native_host_ownership(self):
        vectors=json.loads(VECTORS.read_text(encoding='utf-8'))
        self.assertEqual(set(vectors['hostOperations']),atomic.HOST_OPERATIONS)
        self.assertEqual(set(vectors['nativeOperations']),atomic.NATIVE_OPERATIONS)
        self.assertEqual(set(atomic.SCHEMAS),atomic.HOST_OPERATIONS|atomic.NATIVE_OPERATIONS)
        covered=set()
        for case in vectors['cases']:
            with self.subTest(name=case['name']):
                before=copy.deepcopy(case['arguments'])
                try:
                    normalized=atomic.validate(case['operation'],case['arguments'])
                    if case.get('backend')=='server_ai':
                        atomic.validate_backend(SimpleNamespace(hello={'capabilities':['server_ai_atomic_v1']}),case['operation'],normalized)
                    accepted=True
                except (ValueError,atomic.PreDispatchRejected): accepted=False
                self.assertEqual(case['accepted'],accepted)
                self.assertEqual(before,case['arguments'])
                if accepted:
                    covered.add(case['operation'])
                    self.assertEqual(json.dumps(case.get('wireArguments',case['arguments']),sort_keys=True),json.dumps(normalized,sort_keys=True))
        self.assertEqual(atomic.NATIVE_OPERATIONS,covered)

    def test_same_vectors_execute_actual_csharp_validator(self):
        with tempfile.TemporaryDirectory() as directory:
            output=Path(directory)/'accepted.json'
            run=subprocess.run(['pwsh','-NoProfile','-File',str(ROOT/'tests/run_api_contract_tests.ps1'),
                                '-OutputPath',str(output)],capture_output=True,text=True,encoding='utf-8',timeout=45)
            self.assertEqual(0,run.returncode,run.stdout+run.stderr)
            results=json.loads(output.read_text(encoding='utf-8'))
        expected=[dict(name=c['name'],accepted=c['accepted']) for c in json.loads(VECTORS.read_text(encoding='utf-8'))['cases']]
        self.assertEqual(expected,results)

    def test_reliable_direct_whitelist_filters_tools_without_mutating_global_contract(self):
        game=Game(); game.supported_atomic_operations={'wait','inspect_inventory','unknown_api'}
        before=copy.deepcopy(TOOLS)
        agent=Agent(game,Model())
        atomic_names={t['function']['name'] for t in agent.tools if t['function']['name'] in atomic.SCHEMAS}
        self.assertEqual({'wait','inspect_inventory'},atomic_names)
        lua=next(t for t in agent.tools if t['function']['name']==TOOL_NAME)
        listed=lua['function']['description'].split('可调用的 API：',1)[1].split('。',1)[0].split(', ')
        self.assertEqual({'wait','inspect_inventory'},set(listed))
        self.assertEqual(before,TOOLS)
        self.assertEqual(TOOLS,Agent(Game(),Model()).tools)
        game.supported_atomic_operations=frozenset()
        self.assertFalse(any(t['function']['name'] in atomic.SCHEMAS for t in Agent(game,Model()).tools))


if __name__=='__main__':unittest.main()
