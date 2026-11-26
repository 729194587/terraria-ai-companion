"""Pinned native mobility observations/acceptance; one action, no LLM/retry."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from server_ai import ServerAiBridge
from lua_runtime import LuaRuntime, Limits
from native_direct_input import wait_file


def check_movement_trace(trace):
    checked = 0
    for frame in trace:
        profile = (frame.get('navigation') or {}).get('Mobility')
        if not profile or not frame.get('injectedInput'):
            continue
        movement = profile['Movement']
        assert profile['Source'] == 'native_PostUpdateRunSpeeds', profile
        assert profile['SourceTick'] == frame['inputPhysics']['sampleTick'], profile
        assert [j['Order'] for j in movement['ExtraJumps']] == list(range(len(movement['ExtraJumps'])))
        assert len({j['FullName'] for j in movement['ExtraJumps']}) == len(movement['ExtraJumps'])
        assert set(profile['AvailableModes']) <= {'ground_walk', 'ground_jump'}, profile
        checked += 1
    assert checked, 'Missing native boundary snapshots'
    return checked


async def verify(args):
    p=args.profile;await wait_file(p/'action-fixture.json',90);rows=[]
    cases=args.scenes or (['basic','swiftness'] if args.probe else ['basic','swiftness','basic_jump','swiftness_jump','basic_region','swiftness_region','swiftness_region_left','add','remove','rapid','expiry','unknown','equipment'])
    for mode in cases:
        ready=p/'terrain-ready.json';ready.unlink(missing_ok=True)
        variant=mode.split('_')[0] if mode.startswith(('basic_','swiftness_')) else mode
        scene='solid_up_three' if 'region' in mode else 'low_obstacle' if 'jump' in mode else 'flat'
        direction=-1 if mode.endswith('_left') else 1
        temp=p/'terrain-request.tmp';temp.write_text(json.dumps(dict(scene=scene,direction=direction,mobility=variant)))
        temp.replace(p/'terrain-request.json');fixture=await wait_file(ready)
        bridge=ServerAiBridge(args.port)
        try:
            if 'region' in mode:
                x=fixture['originTileX']*16;lo,hi=sorted((x+direction*6*16,x+direction*10*16))
                source=f"return api('navigate_to_landing_region',{{minPlayerLeftX={lo},maxPlayerLeftX={hi-4},surfaceY={fixture['floor']*16-48}}})"
            else:source=f"return api('navigate_to_x',{{targetX={fixture['startX']+direction*384}}})"
            result=await LuaRuntime(bridge).execute(source,limits=Limits(seconds=30,calls=1,actions=1))
        finally:bridge.close()
        action=result.get('value') or result.get('lastAction') or {}
        (p/f'mobility-{mode}-receipt.json').write_text(json.dumps(dict(source=source,fixture=fixture,result=result),indent=2))
        trace=await wait_file(p/f'terrain-motion-{scene}-{direction}-mobility-{variant}.json')
        check_movement_trace(trace)
        samples={json.dumps(f['inputPhysics'],sort_keys=True) for f in trace if f.get('inputPhysics') and f.get('injectedInput')}
        row=dict(mode=mode,status=action.get('status'),reason=action.get('reason'),detail=action.get('reasonDetail'),
            endX=(action.get('evidence') or {}).get('x'),samples=[json.loads(s) for s in sorted(samples)],frames=len(trace),maxPredictionError=max((f.get('maxError') or 0 for f in trace),default=0),navigation=(action.get('evidence') or {}).get('navigation'))
        rows.append(row);(p/'mobility-results.json').write_text(json.dumps(rows,indent=2))
        print(json.dumps({k:v for k,v in {**row,'samples':row['samples'][:1]}.items() if k!='navigation'}),flush=True)
        assert action.get('endEvidenceStatus')=='known' and action['endEvidence']['inputsReleased'],action
        if not args.probe:
            assert action.get('completed'),action
            e=action['evidence'];assert all(e.get(k) for k in ('arrived','grounded','safeSupport','settled'))
            assert all(abs(e['velocityBeforeRelease'][k])<=.1 for k in ('X','Y'))
            assert row['maxPredictionError']<.01, row
            if mode=='hermes':
                assert max(abs(f['vx']) for f in trace)>3.5, 'Boots never accelerated beyond ordinary running speed'
            if 'jump' in mode or 'region' in mode:assert e['navigation']['CompletedJumps']>=1
            if mode in ('add','remove','rapid','expiry'):
                assert e['navigation']['RecoveryCount']>=1 and e['navigation']['RecoveryReplans']>=1
                assert any(f.get('navigation',{}).get('Status')=='recovering' for f in trace if f.get('navigation'))
            if mode in ('rapid','expiry'):
                injected=[f for f in trace if f.get('injectedInput') and f.get('navigation',{}).get('Mobility')]
                changes=[]
                for f in injected:
                    profile=f['navigation']['Mobility']
                    if not changes or changes[-1]['Revision']!=profile['Revision']:changes.append(profile)
                assert len(changes)==(3 if mode=='rapid' else 2),changes
                assert all(a['Revision']<b['Revision'] for a,b in zip(changes,changes[1:])),changes
                assert e['navigation']['RecoveryCount']==e['navigation']['RecoveryReplans']==1,e
                if mode=='expiry':
                    tails=[f for f in injected if f['navigation']['Mobility']['SwiftnessExpiryTail']]
                    assert len(tails)==1 and not tails[0]['inputPhysics']['swiftness'],tails
                    assert tails[0]['inputPhysics']['maxRunSpeed']==3.75,tails
                    assert any(f['inputPhysics']['swiftnessTime']==1 for f in injected),injected
                assert e['navigation']['PlanMobilityRevision']==changes[-1]['Revision'],e
            for f in trace:
                n=f.get('navigation') or {};profile=n.get('Mobility')
                if profile and profile['Supported'] and f.get('injectedInput'):
                    if n['PlanMobilityRevision']!=profile['Revision']:
                        assert not any(f['injectedInput'][k] for k in ('left','right','jump')),f
                    assert n['MobilityRevision']==profile['Revision']
        assert action['status']!='unknown','Unknown forbids another scene'


if __name__=='__main__':
    a=argparse.ArgumentParser(description=__doc__)
    a.add_argument('--profile',type=Path,required=True);a.add_argument('--port',type=int,required=True)
    a.add_argument('--scenes',nargs='+');a.add_argument('--probe',action='store_true')
    asyncio.run(verify(a.parse_args()))
