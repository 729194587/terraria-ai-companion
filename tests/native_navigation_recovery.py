"""Real native Player.Update with opt-in diagnostic displacement/dynamic walls.

Only the fixture injects the disturbance; the navigation must use native inputs.
One external action per scene, no retry/reconnect after unknown.
"""
import argparse
import asyncio
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from server_ai import ServerAiBridge
from lua_runtime import LuaRuntime, Limits
from native_direct_input import wait_file


async def verify(args):
    p=args.profile
    await wait_file(p/'action-fixture.json',90)
    rows=[]
    for scene in (args.scenes or (['offset'] if args.baseline else ['offset','low_wall','high_wall','air','cancel_air'])):
        ready=p/'terrain-ready.json';ready.unlink(missing_ok=True)
        (p/'terrain-recovery-fired.json').unlink(missing_ok=True)
        temp=p/'terrain-request.tmp';temp.write_text(json.dumps(dict(scene='flat',direction=1,recovery=scene)))
        temp.replace(p/'terrain-request.json')
        fixture=await wait_file(ready)
        bridge=ServerAiBridge(args.port)
        target=fixture['targetX']
        source=f"return api('navigate_to_x',{{targetX={target}}})"
        try:
            if scene=='cancel_air':
                await asyncio.to_thread(bridge.command,'start_atomic',operation='navigate_to_x',
                    arguments=dict(targetX=target),expectedGeneration=bridge.binding['generation'])
                for _ in range(150):
                    state=await asyncio.to_thread(bridge.command,'get_movement')
                    action=state['movement']['action']
                    nav=(action.get('evidence') or {}).get('navigation') or {}
                    if nav.get('Status')=='recovering':break
                    assert action['status'] in ('accepted','running'),action
                    await asyncio.sleep(.01)
                else:raise AssertionError('Did not observe active recovery before cancellation')
                await asyncio.to_thread(bridge.command,'cancel_move',actionId=action['actionId'])
                state=await asyncio.to_thread(bridge.command,'get_movement')
                action=state['movement']['action']
                receipt=dict(cancelledDuringRecovery=nav,action=action)
                assert action['status']=='cancelled',action
                action_id=action['actionId']
                await asyncio.sleep(.2)
                after=await asyncio.to_thread(bridge.command,'get_movement')
                assert after['movement']['action']['actionId']==action_id
                assert after['movement']['action']['status']=='cancelled'
            else:
                receipt=await LuaRuntime(bridge,evidence_root=p/'recovery-lua').execute(source,
                    limits=Limits(seconds=40,calls=1,actions=1))
                action=receipt.get('value') or receipt.get('lastAction') or {}
        finally:bridge.close()
        (p/f'recovery-{scene}-receipt.json').write_text(json.dumps(dict(source=source,fixture=fixture,receipt=receipt),indent=2))
        trace=await wait_file(p/f'terrain-motion-flat-1-{scene}.json')
        evidence=action.get('evidence') or {};end=action.get('endEvidence') or {}
        nav=evidence.get('navigation') or {}
        assert action['endEvidenceStatus']=='known' and end['inputsReleased'],action
        assert end['worldSessionId']==fixture['world'] and end['actorGeneration']==fixture['generation']
        assert end['actorSlot']==fixture['slot']
        updates=[f['Updates'] for f in trace]
        # IPC cancellation can add a terminal observation without another
        # movement update. Only that final cleanup sample may repeat its count.
        if scene=='cancel_air' and len(updates)>1 and updates[-1]==updates[-2]:updates=updates[:-1]
        assert updates==sorted(set(updates)),updates
        if args.baseline:
            assert action['status']=='failed' and not action.get('completed'),action
        elif scene in ('offset','low_wall','air'):
            assert action['completed'],action
            if scene=='low_wall':
                # A fresh checkpoint plan can see the new wall before any edge
                # diverges. Successful native bypass need not spend recovery budget.
                assert nav['PlanNumber']>=2 and nav['CompletedJumps']>=1,action
            else:
                assert nav['RecoveryCount']>=1 and nav['RecoveryReplans']>=1,action
            assert all(evidence.get(k) for k in ('arrived','grounded','safeSupport','settled'))
            assert evidence['x']>=target-12 and abs(evidence['vx'])<=.1
        elif scene=='high_wall':
            assert action['status']=='failed' and 0<=nav['RecoveryCount']<=3 and nav['PlanNumber']>=2,action
        if not args.baseline:
            recovering=[f for f in trace if f['status']=='recovering']
            if nav['RecoveryCount']:
                assert recovering,'No actual recovery trace'
                for f in recovering[1:]:
                    if f['injectedInput'] is None:
                        # Cancellation after the movement update adds a final
                        # physical release observation, not another injected input.
                        assert scene=='cancel_air' and f is trace[-1] and action['status']=='cancelled'
                        assert not any(f[k] for k in ('left','right','jump'))
                    else:
                        assert not any(f['injectedInput'][k] for k in ('left','right','jump'))
            for i,f in enumerate(trace):
                if f.get('RecoveryReplans',0) and not (trace[i-1].get('RecoveryReplans',0) if i else 0):
                    assert i>=2 and trace[i-1]['Settled'] and trace[i-2]['Settled'] and f['RecoveryInputsReleased'],f
        row=dict(scene=scene,status=action['status'],reason=action['reason'],detail=action.get('reasonDetail'),
            recoveryCount=nav.get('RecoveryCount'),replans=nav.get('RecoveryReplans'),plans=nav.get('PlanNumber'),x=evidence.get('x'),
            targetX=target,frames=len(trace),inputsReleased=end['inputsReleased'])
        rows.append(row)
        (p/'recovery-results.json').write_text(json.dumps(rows,indent=2))
        print(json.dumps(row),flush=True)
        assert action['status']!='unknown','Unknown forbids another scene/action'


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port',type=int,required=True)
    parser.add_argument('--profile',type=Path,required=True)
    parser.add_argument('--baseline',action='store_true')
    parser.add_argument('--scenes',nargs='+',choices=['offset','low_wall','high_wall','air','cancel_air'])
    asyncio.run(verify(parser.parse_args()))
