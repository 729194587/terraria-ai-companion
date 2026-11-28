"""Lua calls the shared directional explorer through the real worker and task mailbox."""
import asyncio
import base64
from unittest.mock import patch
import tempfile
import threading
import unittest

from exploration_fixture import ExplorerGame
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge
from program_tasks import coordinator


class ExplorationTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.host = ExplorerGame()
        self.thread = threading.Thread(target=self.host.serve_forever, daemon=True)
        self.thread.start()
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=2)
        self.directory = tempfile.TemporaryDirectory()
        self.runtime = LuaRuntime(self.bridge, evidence_root=self.directory.name)

    async def asyncTearDown(self):
        await coordinator(self.bridge).close()
        self.bridge.close()
        await asyncio.to_thread(self.host.shutdown)
        self.host.server_close()
        self.thread.join()
        self.directory.cleanup()

    def starts(self):
        return [r for r in self.host.sent if r['op'] == 'start']

    async def test_seek_reuses_observed_resource_and_released_navigation(self):
        tile = dict(x=20,y=12)
        self.host.hits = [dict(kind='chest', typeId=21, contentKey='Terraria/Containers', anchor=tile,
                               tiles=[tile], bounds=dict(**tile,width=1,height=1), status='complete')]
        r = await self.runtime.execute("return explore('right', 'chest')")
        self.assertEqual('completed', r['status'], r)
        self.assertEqual('discovery', r['value']['returnReason'])
        self.assertEqual('chest', r['value']['findings'][0]['kind'])
        self.assertEqual(512, r['value']['progress']['dx'])
        self.assertEqual(['navigate_to'], [s['operation'] for s in self.starts()])
        self.assertTrue(r['lastAction']['inputsReleased'])

    async def test_refused_investigation_keeps_lease_usable(self):
        self.host.mode='reject_start'
        refused=await self.runtime.execute("return api('mine_target',{x=20,y=12})")
        self.assertEqual(refused['status'],'failed')
        self.assertEqual(refused['actionState'],'idle')
        self.assertEqual(refused['lastAction']['dispatchState'],'not_admitted')
        self.host.mode='complete'
        self.host.hits=[dict(kind='chest',typeId=21,contentKey='chest',anchor=dict(x=20,y=12),
                             tiles=[],bounds=dict(x=20,y=12,width=1,height=1),status='complete')]
        self.assertEqual((await self.runtime.execute("return explore('right','chest')"))['status'],'completed')

    async def test_spatial_lua_omits_direction_and_resumes_after_seek(self):
        from exploration_session import ExplorationSession
        self.bridge.hello['capabilities'] += ['server_ai_exploration_v2','server_ai_map_v1','server_ai_passages_v1']
        self.bridge.supported_atomic_operations |= {'query_map'}
        self.host.hits=[dict(kind='chest',typeId=21,contentKey='chest',anchor=dict(x=20,y=12),
                             tiles=[],bounds=dict(x=20,y=12,width=1,height=1),status='complete')]
        query=self.host.query
        def observed(op,args):
            envelope=query('observe_scene',{}) if op=='query_map' else query(op,args)
            if op=='query_map':
                bits=bytearray(128*96//8)
                x=(self.host.x+10)//16
                bits[(12*128+x)//8]|=1<<(x%8)
                envelope['data']=dict(worldUid='a'*32,characterId='b'*32,originX=0,originY=0,width=128,
                                      height=96,cellSize=1,visitBits=base64.b64encode(bits).decode(),
                                      visitedCount=len(self.starts())+1,rows=[' '*128]*96)
            elif args.get('scale')!='region':
                data=envelope['data']
                data['landingRegions']['items'][0]['id']='test'
                data['landingRegions']['items'][0]['target']['surfaceY']=192
                data['passages']['connectedLandingIds']=['test']
            return envelope
        self.host.query=observed
        with patch('spatial_explorer.ExplorationSession',lambda ids:ExplorationSession(ids,self.directory.name)):
            first=await self.runtime.execute("return explore(nil,'chest')")
            self.assertEqual(first['value']['returnReason'],'discovery',first)
            self.assertFalse(self.starts())
            next_trip=await self.runtime.execute("return explore()",limits=Limits(actions=1))
            self.assertEqual(next_trip['value']['progress']['surveyedGoals'],1,next_trip)
            self.assertEqual(next_trip['value']['returnReason'],'budget_reached')
            self.assertEqual(next_trip['actionState'],'released')

    async def test_action_and_call_limits_stop_before_next_move(self):
        r = await self.runtime.execute("return explore('right')", limits=Limits(calls=2))
        self.assertEqual('call_limit', r['status'], r)
        self.assertFalse(self.starts())
        r = await self.runtime.execute("return explore('right')", limits=Limits(actions=1))
        self.assertEqual('action_limit', r['status'], r)
        self.assertEqual(1, len(self.starts()))
        self.assertEqual('released', r['actionState'])

    async def test_readonly_and_missing_capabilities_cannot_move(self):
        source = "return explore('down')"
        r = await self.runtime.execute(source, allow_actions=False)
        self.assertEqual('exploration_unavailable', r['status'])
        self.bridge.supported_atomic_operations -= {'observe_scene'}
        r = await self.runtime.execute(source)
        self.assertEqual('exploration_unavailable', r['status'])
        self.assertFalse(self.starts())

    async def test_old_options_rejected_and_cannot_restart_in_same_program(self):
        r = await self.runtime.execute("return explore({maxSteps=2})")
        self.assertEqual('script_error', r['status'], r)
        r = await self.runtime.execute("explore('up'); return explore('right')")
        self.assertEqual('exploration_unavailable', r['status'], r)
        self.assertFalse(self.starts())
