"""Direct input through actual Lua worker + IPC + natural native Player.Update; no model."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from control import BridgeError
from server_ai import ServerAiBridge
from lua_runtime import LuaRuntime, Limits
import atomic_operations as atomic


async def wait_file(path, seconds=15):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        if path.exists():
            try:return json.loads(path.read_text(encoding='utf-8-sig'))
            except json.JSONDecodeError:pass
        await asyncio.sleep(.05)
    raise AssertionError(f'Timed out: {path}')


async def verify(args):
    profile=args.profile
    await wait_file(profile/'action-fixture.json',90)
    bridge=ServerAiBridge(args.port)
    rows=[]
    async def scene(name):
        ready=profile/'direct-input-ready.json';ready.unlink(missing_ok=True)
        temp=profile/'direct-input-request.tmp';temp.write_text(json.dumps(dict(scene=name)))
        temp.replace(profile/'direct-input-request.json')
        return await wait_file(ready)
    async def move(horizontal='none',vertical='none',jump=0,updates=30):
        inputs=dict(horizontal=horizontal,vertical=vertical,jumpHoldUpdates=jump,maxUpdates=updates)
        calls=[]
        r=await LuaRuntime(bridge,evidence_root=profile/'lua').execute(
            'return api("move_input", '+ '{horizontal="'+horizontal+'",vertical="'+vertical+'",jumpHoldUpdates='+str(jump)+',maxUpdates='+str(updates)+'})',
            limits=Limits(seconds=20,calls=2,actions=1),on_result=lambda n,a,r:calls.append(r))
        assert r['status']=='completed',r
        a=calls[-1];e=a['evidence']
        assert a['completed'] and e['updatesExecuted']==updates and e['inputReleased'],a
        assert e['lastInputGameUpdate']-e['firstInputGameUpdate']==updates-1,e
        trace=await wait_file(profile/'direct-input-frames.json')
        segment=trace[-updates:]
        assert len(segment)==updates and sum(f['jump'] for f in segment)==jump,segment
        assert all(f['left']==(horizontal=='left') and f['right']==(horizontal=='right') and f['down']==(vertical=='down') and f['up']==(vertical=='up') for f in segment)
        rows.append(dict(inputs=inputs,result=r,action=a,trace=segment))
        return e
    async def start_long():
        await asyncio.to_thread(bridge.command,'start_atomic',operation='move_input',
            arguments=dict(horizontal='right',vertical='none',jumpHoldUpdates=0,maxUpdates=600),expectedGeneration=bridge.binding['generation'])
        for _ in range(100):
            await asyncio.sleep(.02)
            state=await asyncio.to_thread(bridge.command,'get_movement')
            if ((state['movement']['action'] or {}).get('evidence') or {}).get('updatesExecuted',0)>=12:return
        raise AssertionError('Input did not start')
    try:
        valid=dict(horizontal='right',vertical='none',jumpHoldUpdates=8,maxUpdates=60)
        for change in ({'horizontal':'both'},{'vertical':'left'},{'jumpHoldUpdates':61},{'jumpHoldUpdates':True},
                       {'maxUpdates':0},{'maxUpdates':601},{'extra':1}):
            try:
                await asyncio.to_thread(bridge._request,'start',actionId=uuid.uuid4().hex,operation='move_input',
                    arguments={**valid,**change},expiresAtUnixMs=int(time.time()*1000)+1500)
            except BridgeError as e:assert e.status=='invalid_arguments',e
            else:raise AssertionError('Native parameter validation accepted invalid inputs')
        for direction in ('left','right'):
            await scene('flat');e=await move(direction)
            assert (e['endPosition']['X']-e['startPosition']['X'])*(1 if direction=='right' else -1)>20,e
        for direction in ('none','right'):
            await scene('flat');e=await move(direction,jump=8,updates=16)
            assert e['endPosition']['Y']<e['startPosition']['Y']-20,e
        # Set up the airborne diagnostic after Worker boot, immediately before the first start.
        # Both direction changes then share one Lua program; never re-seed state between them.
        original_request=bridge._request
        pending_air=True
        def request_with_air(op,**values):
            nonlocal pending_air
            if pending_air and op=='start':
                pending_air=False
                ready=profile/'direct-input-ready.json';ready.unlink(missing_ok=True)
                temp=profile/'direct-input-request.tmp';temp.write_text(json.dumps(dict(scene='air')))
                temp.replace(profile/'direct-input-request.json')
                until=time.monotonic()+5
                while not ready.exists() and time.monotonic()<until:time.sleep(.01)
                assert ready.exists(),'Air fixture not ready'
            return original_request(op,**values)
        bridge._request=request_with_air
        calls=[]
        r=await LuaRuntime(bridge,evidence_root=profile/'lua').execute(
            'local a=api("move_input",{horizontal="left",vertical="none",jumpHoldUpdates=0,maxUpdates=12}); '
            'local b=api("move_input",{horizontal="right",vertical="none",jumpHoldUpdates=0,maxUpdates=18}); return {a,b}',
            limits=Limits(seconds=20,calls=2,actions=2),on_result=lambda n,a,r:calls.append(r))
        bridge._request=original_request
        air=await wait_file(profile/'direct-input-ready.json')
        assert r['status']=='completed' and len(calls)==2,r
        for a in calls:
            assert a['completed'] and a['evidence']['startPosition']['Y']<air['floorY']-42,a
        assert calls[0]['evidence']['endVelocity']['X']<0 and calls[1]['evidence']['endVelocity']['X']>0,calls
        trace=await wait_file(profile/'direct-input-frames.json')
        assert len(trace)==30 and all(f['left'] for f in trace[:12]) and all(f['right'] for f in trace[12:]),trace
        rows.append(dict(airDirectionChange=r,actions=calls,trace=trace))
        await scene('edge');e=await move('right',updates=50)
        assert e['endPosition']['Y']>e['startPosition']['Y']+30,e
        await scene('wall');e=await move('right',updates=90)
        assert e['blockedHorizontalUpdates']>30,e
        await scene('wall');await move('right',updates=40);e=await move('right',updates=20)
        assert e['noProgress'],e
        await scene('platform');e=await move(vertical='down',updates=25)
        assert e['endPosition']['Y']>e['startPosition']['Y']+16,e
        await scene('platform');e=await move(vertical='down',jump=8,updates=60)
        # Record actual combined semantics; a jump may initially rise before down permits descent.
        assert e['endPosition']['Y']>e['startPosition']['Y']+16,e
        await scene('flat');await move(vertical='up',updates=2)
        task=asyncio.create_task(atomic.dispatch(bridge,'move_input',dict(horizontal='right',vertical='none',jumpHoldUpdates=0,maxUpdates=600)))
        for _ in range(100):
            await asyncio.sleep(.02)
            state=await asyncio.to_thread(bridge.command,'get_movement')
            a=state['movement']['action']
            if ((a or {}).get('evidence') or {}).get('updatesExecuted',0)>=12:break
        else:raise AssertionError('Input did not start')
        task.cancel()
        try:await task
        except asyncio.CancelledError:pass
        state=await asyncio.to_thread(bridge.command,'get_movement');a=state['movement']['action']
        assert a['status']=='cancelled' and a['endEvidence']['inputsReleased'],a
        assert a['evidence']['endVelocity']['X']>0,a
        rows.append(dict(cancel=a))
        await scene('flat');await start_long()
        temp=profile/'direct-input-event.tmp';temp.write_text(json.dumps(dict(cause='retire')));temp.replace(profile/'direct-input-event.json')
        for _ in range(100):
            await asyncio.sleep(.02)
            a=(await asyncio.to_thread(bridge.command,'get_movement'))['movement']['action']
            if a['status']!='running':break
        assert a['status']=='cancelled' and a['endEvidence']['inputsReleased'],a
        rows.append(dict(retire=a))
        await scene('flat');await start_long()
        terminal=profile/'direct-input-terminal.json';terminal.unlink(missing_ok=True)
        bridge.close()
        a=await wait_file(terminal)
        assert a['Status']=='cancelled' and a['evidence']['inputReleased'],a
        rows.append(dict(disconnect=a))
        # Explicit new test session after confirmed prior cleanup; production never reconnects automatically.
        bridge=ServerAiBridge(args.port)
        await scene('flat');await start_long()
        temp=profile/'direct-input-event.tmp';temp.write_text(json.dumps(dict(cause='death')));temp.replace(profile/'direct-input-event.json')
        for _ in range(100):
            await asyncio.sleep(.02)
            a=(await asyncio.to_thread(bridge.command,'get_movement'))['movement']['action']
            if a['status']!='running':break
        assert a['status']=='cancelled' and a['endEvidence']['inputsReleased'] and a['evidence']['dead'],a
        rows.append(dict(death=a))
    finally:
        bridge.close()
        (profile/'direct-input-results.json').write_text(json.dumps(dict(scenarios=rows),indent=2))
    print(f'PASS native direct input: {len(rows)} scenarios',flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--port',type=int,required=True);parser.add_argument('--profile',type=Path,required=True)
    asyncio.run(verify(parser.parse_args()))
