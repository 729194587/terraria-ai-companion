"""Controlled pcall with real Lua workers and production host supervision."""
import asyncio
import multiprocessing as mp
import tempfile
import unittest
from unittest.mock import patch

from test_lua_runtime import RuntimeGame
import test_lua_recovery as recovery
from control import BridgeError
from lua_runtime import LuaRuntime, Limits
from lua_worker import worker, encode, decode


class ControlledPcall(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.directory.cleanup()
        self.assertFalse(mp.active_children(), 'Lua worker leaked')
        self.assertEqual(0, LuaRuntime._active_runs)

    async def run_lua(self, source, game=None, **kwargs):
        game = game or RuntimeGame()
        runtime = LuaRuntime(game, evidence_root=self.directory.name)
        result = await runtime.execute(source, limits=kwargs.pop('limits', Limits(seconds=10)), **kwargs)
        return result, game, runtime

    async def wait_started(self, game):
        async with asyncio.timeout(10):
            while game.action is None:
                await asyncio.sleep(.01)

    async def test_ordinary_nested_errors_and_multiple_nil_returns(self):
        result, _, _ = await self.run_lua('''
            local ok,err=pcall(function() error('ordinary') end)
            assert(not ok and type(err)=='string' and string.len(err)<=512)
            local outer,inner,message=pcall(function() return pcall(function() error('nested') end) end)
            assert(outer and not inner and type(message)=='string')
            local values=table.pack(pcall(function(a,b) return a,nil,b,nil end,10,30))
            assert(values.n==5 and values[1] and values[2]==10 and values[3]==nil and values[4]==30)
            local zero=table.pack(pcall(function() end)); assert(zero.n==1 and zero[1])
            local bad=pcall(42); assert(not bad)
            local huge='x'; for i=1,12 do huge=huge..huge end
            local bounded,e=pcall(function() error(huge) end); assert(not bounded and #e<=512)
            return {ok=ok,nested=inner,multiple=values.n}
        ''')
        self.assertEqual(('completed',dict(ok=False,nested=False,multiple=5)),(result['status'],result['value']),result)

    async def test_local_read_schema_and_range_refusals_can_continue(self):
        result, game, _ = await self.run_lua('''
            local ok,err=pcall(function() return api('scan_tiles',{width=3}) end)
            assert(not ok and err.status=='bad_argument' and err.argumentOperation=='scan_tiles')
            assert(#err.diagnostics>0)
            local range,e=pcall(function() return api('scan_tiles',{minX=0,maxX=100,minY=0,maxY=100,tileTypes={1}}) end)
            assert(not range and e.status=='bad_argument')
            local resolved,r=pcall(function() return api('resolve_content',{kind='tile',id=1}) end)
            assert(not resolved and r.diagnostics[1].code=='missing_required')
            return api('inspect_inventory',{mode='selected'}).valid
        ''')
        self.assertEqual(('completed',True,4,0),(result['status'],result['value'],result['calls'],result['actions']),result)
        self.assertEqual(['inspect_inventory'],[a['operation'] for c,a in game.sent if c=='query_atomic'])
        self.assertEqual('inspect_inventory',result['lastObservation']['operation'])

    async def test_unknown_api_is_not_recoverable(self):
        for api in ("api('not_an_api',{})",):
            with self.subTest(api=api):
                result,game,_=await self.run_lua('pcall(function() '+api+" end); return api('inspect_inventory',{mode='selected'})")
                self.assertEqual('bad_argument',result['status'],result)
                self.assertEqual(1,result['calls'])
                self.assertFalse(any(c in ('start_atomic','query_atomic') for c,_ in game.sent))

    async def test_action_failure_terminates_before_pcall_can_resume(self):
        for status in ('failed','timeout','cancelled'):
            with self.subTest(status=status):
                result,game,_=await self.run_lua('''
                    api('inspect_inventory',{mode='selected'})
                    pcall(function()
                        pcall(function() return api('use_item') end)
                        log('inner escaped')
                    end)
                    log('outer escaped')
                    api('inspect_inventory',{mode='selected'}); api('use_item'); return true
                ''',RuntimeGame(status))
                self.assertEqual(status,result['status'],result)
                self.assertIsNone(result['value']); self.assertEqual([],result['logs'])
                self.assertEqual((2,1),(result['calls'],result['actions']))
                self.assertEqual(status,result['lastAction']['status'])
                self.assertFalse(result['lastAction']['continuationAllowed'])
                self.assertEqual('known',result['lastAction']['endEvidenceStatus'])
                self.assertEqual('released',result['actionState'])
                self.assertEqual('use_item',result['failure']['call']['operation'])
                self.assertEqual('inspect_inventory',result['lastObservation']['operation'])
                self.assertEqual(1,sum(c=='query_atomic' for c,_ in game.sent))
                self.assertEqual(1,sum(c=='start_atomic' for c,_ in game.sent))

    async def test_unknown_action_cannot_resume_or_replay(self):
        result,game,runtime=await self.run_lua("pcall(function() return api('use_item') end); log('escaped'); return api('use_item')",RuntimeGame('unknown'))
        self.assertEqual(('action_unknown','unknown'),(result['status'],result['actionState']),result)
        self.assertIsNone(result['value']); self.assertEqual([],result['logs'])
        self.assertEqual(1,sum(c=='start_atomic' for c,_ in game.sent))
        self.assertEqual('blocked',(await runtime.execute('return pcall(function() return 1 end)'))['status'])

    async def test_instruction_hook_cannot_be_swallowed_by_nested_pcall(self):
        sources=('pcall(function() while true do end end); log("escaped")',
                 'while true do pcall(function() pcall(function() while true do end end) end) end',
                 'pcall(function() table.sort({2,1},function() while true do end end) end); log("escaped")',
                 'local function f() pcall(f) end; pcall(f); log("escaped")')
        for source in sources:
            with self.subTest(source=source):
                result,_,_=await self.run_lua(source,limits=Limits(seconds=8,instructions=5000))
                self.assertEqual('instruction_limit',result['status'],result)
                self.assertIsNone(result['value']); self.assertEqual([],result['logs'])

    async def test_memory_limit_cannot_be_swallowed_by_nested_pcall(self):
        result,_,_=await self.run_lua('''
            pcall(function() pcall(function()
              local t={}; for i=1,100000 do t[i]={i,i} end
            end) end)
            log('escaped'); return true
        ''',limits=Limits(seconds=8,memory=100000))
        self.assertEqual('memory_limit',result['status'],result)
        self.assertIsNone(result['value']); self.assertEqual([],result['logs'])

    async def test_worker_host_errors_are_not_catchable(self):
        for source,expected in (("log(42)",'bad_argument'),
                ("api('scan_tiles',{1,width=3})",'serialization_error'),
                ("local t={};t.self=t;api('scan_tiles',t)",'serialization_error')):
            result,game,_=await self.run_lua('pcall(function() '+source+' end); log("escaped"); return true')
            self.assertEqual(expected,result['status'],result)
            self.assertIsNone(result['value']); self.assertEqual([],result['logs'])
            self.assertFalse(any(c in ('start_atomic','query_atomic') for c,_ in game.sent))

    async def test_read_communication_and_unexpected_errors_are_fatal(self):
        for error in (BridgeError('protocol mismatch'),OSError('disconnected'),ValueError('unexpected response')):
            game=RuntimeGame(); original=game.command
            def command(cmd,**params):
                if cmd=='query_atomic':
                    raise error
                return original(cmd,**params)
            game.command=command
            result,_,_=await self.run_lua("pcall(function() api('inspect_inventory',{mode='selected'}) end); log('escaped'); return true",game)
            self.assertNotEqual('completed',result['status'],result)
            self.assertIsNone(result['value']); self.assertEqual([],result['logs'])
            self.assertEqual(1,result['calls'])

    async def test_worker_protocol_errors_are_not_catchable(self):
        for reply in ({'type':'result','id':999,'value':{}}, {'type':'unexpected','id':1,'value':{}}):
            parent,child=mp.get_context('spawn').Pipe()
            process=mp.get_context('spawn').Process(target=worker,args=(child,
                "pcall(function() api('inspect_inventory',{mode='selected'}) end); log('escaped'); return true",Limits(seconds=8)),daemon=True)
            try:
                process.start(); child.close()
                self.assertTrue(await asyncio.to_thread(parent.poll,8))
                request=decode(parent.recv_bytes())
                self.assertTrue(request['protected'])
                parent.send_bytes(encode(reply))
                self.assertTrue(await asyncio.to_thread(parent.poll,8))
                terminal=decode(parent.recv_bytes())
                self.assertEqual('protocol_error',terminal['status'],terminal)
                self.assertEqual('done',terminal['type'])
                self.assertNotIn('value',terminal)
            finally:
                if process.pid:
                    await asyncio.to_thread(process.join,.2)
                    if process.is_alive(): process.terminate()
                    await asyncio.to_thread(process.join,1)
                    process.close()
                child.close(); parent.close()

    async def test_pcall_does_not_reset_call_or_action_budgets(self):
        cases=(("pcall(function() api('scan_tiles',{}) end); pcall(function() api('inspect_inventory',{mode='selected'}) end); log('escaped')",Limits(seconds=8,calls=1),'call_limit',0),
               ("pcall(function() api('use_item') end); pcall(function() api('use_item') end); log('escaped')",Limits(seconds=8,actions=1),'action_limit',1))
        for source,limits,expected,starts in cases:
            result,game,_=await self.run_lua(source,limits=limits)
            self.assertEqual(expected,result['status'],result)
            self.assertEqual([],result['logs'])
            self.assertEqual(starts,sum(c=='start_atomic' for c,_ in game.sent))

    async def test_cancellation_and_wall_clock_win_over_pcall(self):
        for kind in ('cancel','deadline','task_cancel'):
            game=RuntimeGame(pending=True); cancel=asyncio.Event()
            runtime=LuaRuntime(game,evidence_root=self.directory.name)
            original=__import__('time').monotonic; offset=0
            with patch('lua_runtime.monotonic',side_effect=lambda:original()+offset):
                task=asyncio.create_task(runtime.execute("pcall(function() api('use_item') end); log('escaped'); return true",cancel=cancel))
                await self.wait_started(game)
                if kind=='cancel': cancel.set()
                elif kind=='task_cancel': task.cancel()
                else: offset=100
                if kind=='task_cancel':
                    with self.assertRaises(asyncio.CancelledError):
                        await task
                    result=runtime.last_result
                else:
                    result=await task
            self.assertEqual('timeout' if kind=='deadline' else 'cancelled',result['status'],result)
            self.assertEqual('released',result['actionState'])
            self.assertEqual('known',result['cleanup']['endEvidenceStatus'])
            self.assertEqual('cancelled',game.action['status'])
            self.assertEqual([],result['logs']); self.assertIsNone(result['value'])
            self.assertEqual(1,sum(c=='start_atomic' for c,_ in game.sent))

    async def test_observation_survives_caught_then_uncaught_script_errors(self):
        result,_,_=await self.run_lua("api('inspect_inventory',{mode='selected'}); pcall(function() error('caught') end); pcall(function() api('scan_tiles',{}) end); error('uncaught')")
        self.assertEqual('script_error',result['status'],result)
        self.assertEqual('inspect_inventory',result['lastObservation']['operation'])
        self.assertIsNone(result['value'])

    async def test_log_budget_is_fatal_and_standard_libraries_stay_closed(self):
        result,_,_=await self.run_lua("pcall(function() for i=1,33 do log('entry') end end); return true")
        self.assertEqual('log_limit',result['status'],result)
        self.assertEqual(32,len(result['logs']))
        self.assertIsNone(result['value'])
        result,_,_=await self.run_lua('return {type(pcall),type(xpcall),type(print),type(debug),type(load),type(python)}')
        self.assertEqual(['function','nil','nil','nil','nil','nil'],result['value'],result)


class ServerPcall(unittest.IsolatedAsyncioTestCase):
    setUp = recovery.RecoveryTests.setUp
    tearDown = recovery.RecoveryTests.tearDown
    enable = recovery.RecoveryTests.enable
    reject_first = recovery.RecoveryTests.reject_first

    async def test_only_host_verified_preflight_can_continue(self):
        for unsafe in (False,True):
            self.enable()
            self.reject_first((lambda a:a['evidence'].update(mutationAttempted=True)) if unsafe else None)
            result=await LuaRuntime(self.bridge,evidence_root=self.directory.name).execute('''
                local ok,a=pcall(function() return api('place_object',{x=10,y=20}) end)
                assert(ok and a.status=='failed')
                log('safe continuation')
                local second,b=pcall(function() return api('place_object',{x=12,y=20}) end)
                return {allowed=a.continuationAllowed,completed=second and b.completed,blocked=b.blocked or false}
            ''',limits=Limits(seconds=8,actions=2))
            self.assertEqual('failed' if unsafe else 'completed',result['status'],result)
            if unsafe:
                self.assertIsNone(result['value']); self.assertEqual([],result['logs'])
                self.assertFalse(result['lastAction']['continuationAllowed'])
                self.assertTrue(result['lastAction']['evidence']['mutationAttempted'])
                self.assertEqual('known',result['lastAction']['endEvidenceStatus'])
            else:
                self.assertTrue(result['value']['allowed'])
                self.assertTrue(result['value']['completed'])
                self.assertEqual((2,2),(result['calls'],result['actions']))
            self.assertEqual(1 if unsafe else 2,sum(q['op']=='start' for q in self.host.sent))
            self.host.sent.clear()
            self.assertFalse(mp.active_children())


if __name__=='__main__':
    unittest.main()
