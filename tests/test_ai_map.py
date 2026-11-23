import sys
import tempfile
import threading
import unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from test_server_ai_backend import Host
from server_ai import ServerAiBridge
from lua_runtime import LuaRuntime, Limits
import atomic_operations as atomic

class MapIntegration(unittest.IsolatedAsyncioTestCase):
    async def test_lua_map_read_only_contract(self):
        host=Host();host.maps=True;host.perception=True
        def query(op,args):
            self.assertEqual('query_map',op)
            return dict(available=True,worldSessionId=host.binding['world'],actorGeneration=1,actorSlot=254,
                        gameUpdate=33,observedAtUnixMs=1793070000123,clock='server_utc_at_projection',
                        data=dict(rows=['#:', '? '],originX=10,originY=20,cellSize=2,exploredRatio=.5,legend='fixture',knowledge='last_known_state'))
        host.query=query
        thread=threading.Thread(target=host.serve_forever,daemon=True);thread.start()
        bridge=ServerAiBridge(host.server_address[1])
        try:
            self.assertIn('query_map',bridge.supported_atomic_operations)
            with tempfile.TemporaryDirectory() as directory:
                result=await LuaRuntime(bridge,evidence_root=directory).execute("""
                    local r=api('query_map',{mode='region',cellSize=2,centerX=12,centerY=22,width=4,height=4})
                    assert(r.valid and r.data.rows[1]=='#:' and r.data.cellSize==2)
                    return r.data.knowledge
                """,limits=Limits(seconds=8,actions=0))
            self.assertEqual('completed',result['status'],result)
            self.assertEqual(0,result['actions'])
            self.assertFalse(any(r['op']=='start' for r in host.sent))
            bridge.hello['capabilities'].remove('server_ai_map_v1')
            with self.assertRaises(atomic.PreDispatchRejected):
                await atomic.dispatch(bridge,'query_map',{'mode':'overview'})
        finally:
            bridge.close();host.shutdown();host.server_close();thread.join()

    def test_schema_limits(self):
        for args in ({'mode':'bad'},{'mode':'region','cellSize':3},{'mode':'region','width':129},{'mode':'region','height':97},
                     {'mode':'overview','includeChunks':True}):
            with self.assertRaises(ValueError):atomic.validate('query_map',args)

    def test_chunk_query_is_capability_gated(self):
        args = {'mode':'region','includeChunks':True}
        self.assertEqual(args,atomic.validate('query_map',args))
        def properties(enabled):
            tools = atomic.server_tools({'query_map'},map_chunks=enabled)
            return tools[0]['function']['parameters']['properties']
        self.assertIn('includeChunks',properties(True))
        self.assertNotIn('includeChunks',properties(False))

if __name__=='__main__':unittest.main()
