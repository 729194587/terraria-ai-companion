"""Actual loopback, Worker and dispatcher; native world is an explicit substitute."""
import asyncio
import unittest
import test_server_ai_backend as backend
import atomic_operations as atomic
from control import BridgeError
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge
from lua_agent import server_contract


class PlacementBackendTests(unittest.IsolatedAsyncioTestCase):
    setUp = backend.BackendTests.setUp
    tearDown = backend.BackendTests.tearDown

    def connect(self, *, placement=True, actions=True):
        self.bridge.close()
        self.host.actions, self.host.placement = actions, placement
        self.bridge = ServerAiBridge(self.host.server_address[1], timeout=1)

    async def test_capability_requires_place_and_native_actions(self):
        for placement, actions in ((False, True), (True, False)):
            self.connect(placement=placement, actions=actions)
            self.assertNotIn('place_tile', self.bridge.supported_atomic_operations)
            with self.assertRaises(BridgeError):
                await atomic.dispatch(self.bridge, 'place_tile', dict(x=10, y=20))
        self.assertFalse(any(r['op'] == 'start' for r in self.host.sent))

    async def test_generated_lua_uses_observed_material_and_target(self):
        self.connect()
        instructions, _ = server_contract(self.bridge)
        self.assertIn('place_tile', instructions)
        self.assertIn('consumedCount=1', instructions)
        r = await LuaRuntime(self.bridge, evidence_root=self.directory.name).execute("""
local p=api('get_player_state',{})
local bag=api('inspect_inventory',{mode='hotbar'})
local material=nil
for _,i in ipairs(bag.data.inventory) do
  if i.use.createTile and i.use.createTile>=0 and i.stack>0 then material=i; break end
end
assert(material)
api('select_hotbar_slot',{slot=material.hotbarKey})
local target={x=math.floor(p.player.position.x/16)+3,y=math.floor((p.player.position.y+42)/16)-1}
local placed=api('place_tile',target)
assert(placed.valid and placed.completed and placed.evidence.consumedCount==1)
return placed
""", limits=Limits(seconds=8, calls=8, actions=2))
        self.assertTrue(r['value']['completed'], r)
        self.assertEqual(dict(x=9, y=14), r['value']['targetTile'])
        self.assertEqual('released', r['actionState'])
        self.assertEqual(['select_hotbar_slot', 'place_tile'], [r['operation'] for r in self.host.sent if r['op']=='start'])

    async def test_world_and_consumption_and_exact_target_are_required(self):
        for override in ({'placedByActor':False}, {'targetPresent':False}, {'targetChanged':True},
                         {'consumedCount':0}, {'consumedCount':2}, {'consumedCount':True}, {'x':11}):
            with self.subTest(override=override):
                self.connect()
                self.host.placement_override=override
                r=await atomic.dispatch(self.bridge,'place_tile',dict(x=10,y=20))
                self.assertEqual('unknown',r['status'],r)
                self.assertFalse(r['completed'])

    async def test_cancel_releases_same_fixed_tile_without_replay(self):
        self.connect();self.host.mode='pending'
        task=asyncio.create_task(atomic.dispatch(self.bridge,'place_tile',dict(x=10,y=20)))
        for _ in range(100):
            if self.host.action or task.done():break
            await asyncio.sleep(.01)
        self.assertIsNotNone(self.host.action)
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):await task
        self.assertEqual('cancelled',self.host.action['status'])
        self.assertTrue(self.host.action['endEvidence']['inputsReleased'])
        self.assertEqual(1,sum(r['op']=='start' for r in self.host.sent))

    async def test_changed_tile_receipt_is_unknown(self):
        self.connect();self.host.mode='changed_tile'
        r=await atomic.dispatch(self.bridge,'place_tile',dict(x=10,y=20))
        self.assertEqual('unknown',r['status'],r)
        self.assertEqual(1,sum(r['op']=='start' for r in self.host.sent))


if __name__=='__main__':unittest.main()
