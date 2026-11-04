"""Read-only assessment forwarding through loopback IPC and the real Lua worker."""
import sys
import unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
import test_server_ai_backend as fixture
from server_ai import ServerAiBridge
from lua_runtime import LuaRuntime, Limits

class MiningSupportProjection(unittest.IsolatedAsyncioTestCase):
    setUp=fixture.BackendTests.setUp
    tearDown=fixture.BackendTests.tearDown

    async def test_known_and_unknown_assessment_do_not_become_actions(self):
        self.bridge.close();self.host.perception=True
        self.bridge=ServerAiBridge(self.host.server_address[1])
        def query(operation,args):
            known=args['x']==1
            return dict(available=True,worldSessionId=self.host.binding['world'],actorGeneration=1,actorSlot=254,
                gameUpdate=33,observedAtUnixMs=1793344501327,clock='server_utc_at_projection',data=dict(
                cell=dict(hasTile=known),miningSupportImpact=dict(available=known,
                    current=dict(supportPixels=20),afterRemoval=dict(supportPixels=4) if known else None,
                    affectsCurrentStance=True if known else None,supportDeltaPixels=-16 if known else None,
                    reasons=[] if known else ['target_terrain_unknown'],scope='current fixed pose only; no falling prediction')))
        self.host.query=query
        result=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute("""
            local r=api('inspect_tile',{x=1,y=10})
            assert(r.valid and r.data.miningSupportImpact.available)
            assert(r.data.miningSupportImpact.current.supportPixels==20)
            assert(r.data.miningSupportImpact.afterRemoval.supportPixels==4)
            local u=api('inspect_tile',{x=2,y=10})
            assert(u.valid and not u.data.miningSupportImpact.available)
            assert(u.data.miningSupportImpact.reasons[1]=='target_terrain_unknown')
            return r.data.miningSupportImpact.supportDeltaPixels
        """,limits=Limits(seconds=8,actions=0))
        self.assertEqual('completed',result['status'],result)
        self.assertEqual(-16,result['value']);self.assertEqual(0,result['actions'])
        self.assertEqual(2,sum(r['op']=='query' for r in self.host.sent))
        self.assertFalse(any(r['op']=='start' for r in self.host.sent))

if __name__=='__main__':unittest.main()
