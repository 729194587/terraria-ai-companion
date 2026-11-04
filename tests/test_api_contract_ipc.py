"""Actual C# TCP/protocol + Python Bridge/dispatcher + real Lua worker."""
import asyncio
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import uuid
from unittest.mock import patch

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
import atomic_operations as atomic
from control import BridgeError
from lua_runtime import LuaRuntime
from server_ai import ServerAiBridge


class ActualProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def test_duplicate_running_and_completed_stays_unknown_without_replay(self):
        with tempfile.TemporaryDirectory() as directory:
            ready=Path(directory)/'ready';stop=Path(directory)/'stop'
            Path(str(ready)+'.live').touch()
            process=subprocess.Popen(['pwsh','-NoProfile','-File',str(Path(__file__).with_name('run_api_contract_host.ps1')),
                '-ReadyPath',str(ready),'-StopPath',str(stop)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,
                creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
            bridge=None
            try:
                async def started():
                    while not ready.exists():
                        if process.poll() is not None:
                            out,err=process.communicate();self.fail((out+err).decode('utf-8',errors='replace'))
                        await asyncio.sleep(.03)
                await asyncio.wait_for(started(),15)
                bridge=await asyncio.to_thread(ServerAiBridge,int(ready.read_text()),5)
                action_id=uuid.uuid4()
                with patch('server_ai.uuid.uuid4',return_value=action_id):
                    original=await asyncio.to_thread(bridge.command,'start_atomic',operation='use_item',arguments={},
                                                   expectedGeneration=bridge.binding['generation'])
                self.assertEqual(action_id.hex,original['movement']['action']['actionId'])
                async def action_with_status(status):
                    while True:
                        reply=await asyncio.to_thread(bridge.command,'get_movement')
                        state=await asyncio.to_thread(bridge._request,'state')
                        if reply['movement']['action']['status']==status and state['data']['starts']==1:
                            return reply['movement']['action']
                        await asyncio.sleep(.01)
                for status in ('running','completed'):
                    if status=='completed':Path(str(ready)+'.advance').touch()
                    before=await asyncio.wait_for(action_with_status(status),5)
                    # The actual wire marker is unknown, including after verified completion.
                    with self.assertRaises(BridgeError) as raised:
                        await asyncio.to_thread(bridge._request,'start',actionId=action_id.hex,
                            operation='use_item',arguments={},expiresAtUnixMs=0)
                    self.assertEqual('duplicate_action',raised.exception.status)
                    lifecycle={}
                    with patch('server_ai.uuid.uuid4',return_value=action_id):
                        result=await atomic.dispatch(bridge,'use_item',{},lifecycle=lifecycle)
                    self.assertEqual(('unknown','unknown','duplicate_action'),
                                     (result['status'],result['dispatchState'],result['reason']))
                    self.assertTrue(lifecycle['uncertain'])
                    self.assertNotEqual(False,result.get('inputsAcquired'))
                    self.assertEqual(action_id.hex,bridge._action_id)
                    after=await asyncio.wait_for(action_with_status(status),5)
                    self.assertEqual(before,after)
                    if status=='completed':
                        self.assertTrue(after['endEvidence']['effectVerified'])
                self.assertFalse(bridge._closed)
            finally:
                if bridge is not None:bridge.close()
                stop.write_text('stop')
                try:out,err=await asyncio.to_thread(process.communicate,timeout=5)
                except subprocess.TimeoutExpired:
                    process.terminate();out,err=await asyncio.to_thread(process.communicate,timeout=5)
                self.assertEqual(0,process.returncode,(out+err).decode('utf-8',errors='replace'))

    async def test_refusal_and_argument_engine_errors_across_actual_csharp_protocol(self):
        with tempfile.TemporaryDirectory() as directory:
            ready=Path(directory)/'ready';stop=Path(directory)/'stop'
            process=subprocess.Popen(['pwsh','-NoProfile','-File',str(Path(__file__).with_name('run_api_contract_host.ps1')),
                '-ReadyPath',str(ready),'-StopPath',str(stop)],stdout=subprocess.PIPE,stderr=subprocess.PIPE,
                creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
            bridge=None
            try:
                async def started():
                    while not ready.exists():
                        if process.poll() is not None:
                            out,err=process.communicate();self.fail((out+err).decode('utf-8',errors='replace'))
                        await asyncio.sleep(.03)
                await asyncio.wait_for(started(),15)
                bridge=await asyncio.to_thread(ServerAiBridge,int(ready.read_text()),5)
                lifecycle={}
                refusal=await atomic.dispatch(bridge,'select_hotbar_slot',{'slot':2},lifecycle=lifecycle)
                self.assertEqual(('failed','not_admitted','actor_not_alive'),
                    (refusal['status'],refusal['dispatchState'],refusal['reason']))
                self.assertFalse(lifecycle['uncertain']);self.assertIsNone(bridge._action_id)
                runtime=LuaRuntime(bridge,evidence_root=directory)
                result=await runtime.execute("api('select_hotbar_slot',{slot=2});log('escaped')")
                self.assertEqual(('failed','idle'),(result['status'],result['actionState']))
                self.assertFalse(result['lastAction']['continuationAllowed']);self.assertEqual([],result['logs'])
                read=await runtime.execute("return api('get_player_state',{}).player.life")
                self.assertEqual(('completed',73),(read['status'],read['value']))
                for args,expected in (({'mode':True},'invalid_request'),({'mode':'full'},'engine_unavailable')):
                    with self.assertRaises(BridgeError) as raised:
                        await asyncio.to_thread(bridge.command,'query_atomic',operation='inspect_inventory',arguments=args)
                    self.assertEqual(expected,raised.exception.status)
                self.assertFalse(bridge._closed)
            finally:
                if bridge is not None:bridge.close()
                stop.write_text('stop')
                try:out,err=await asyncio.to_thread(process.communicate,timeout=5)
                except subprocess.TimeoutExpired:
                    process.terminate();out,err=await asyncio.to_thread(process.communicate,timeout=5)
                self.assertEqual(0,process.returncode,(out+err).decode('utf-8',errors='replace'))


if __name__=='__main__':unittest.main()
