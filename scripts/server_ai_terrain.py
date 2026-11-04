"""Explicit isolated native terrain matrix; never run against a user profile."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
import time
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'python'))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge
SCENES=('flat','step_up','step_down','slope_up','slope_down','short_gap','low_obstacle','wide_gap','high_wall')
async def verify(args):
    profile=args.profile.resolve()
    assert (profile/'isolated-action-fixture').is_file()
    results=[]
    for scene in SCENES:
        for direction,mode in [(d,m) for d in (1,-1) for m in (('navigate','explore') if scene not in ('wide_gap','high_wall') else ('navigate',))]:
            ready=profile/'terrain-ready.json'
            if ready.exists(): ready.unlink()
            temp=profile/'terrain-request.tmp'
            temp.write_text(json.dumps(dict(scene=scene,direction=direction)),encoding='utf-8')
            temp.replace(profile/'terrain-request.json')
            deadline=time.monotonic()+10
            while not ready.exists() and time.monotonic()<deadline: await asyncio.sleep(.05)
            fixture=json.loads(ready.read_text())
            goal=fixture['startX']+direction*(128 if mode=='explore' else 192)
            bridge=ServerAiBridge(port=args.port)
            try:
                source=f"local before=api('get_player_state',{{}}); local action=api('navigate_to_x',{{targetX={fixture['targetX']}}}); return {{before=before,action=action,after=api('get_player_state',{{}})}}"
                if mode=='explore':
                    source='return explore({direction="'+('right' if direction==1 else 'left')+'",maxSteps=1,stepPixels=128})'
                r=await LuaRuntime(bridge,evidence_root=profile/'terrain-lua').execute(source,limits=Limits(seconds=35,calls=12,actions=1))
            finally: bridge.close()
            action=(r.get('value') or {}).get('action') or r.get('lastAction') or {}
            e=action.get('evidence') or {}
            expected=scene not in ('wide_gap','high_wall')
            passed=(action.get('completed') is True and e.get('settled') is True and direction*(e['x']-goal)>=-12) if expected else (action.get('status')=='failed' and e.get('settled') is True and any(reason in (action.get('reasonDetail') or '') for reason in ('no safe local route','navigation progress/action budget exhausted')))
            passed=passed and (action.get('endEvidence') or {}).get('inputsReleased') is True
            await asyncio.sleep(.1)
            trace_path=profile/f'terrain-motion-{scene}-{direction}.json'
            trace=json.loads(trace_path.read_text()) if trace_path.exists() else []
            motion=dict(frames=len(trace),minY=min((f['y'] for f in trace),default=None),maxY=max((f['y'] for f in trace),default=None),
                airborneFrames=sum(not f['Grounded'] for f in trace),maxPredictionError=max((f.get('maxError') or 0 for f in trace),default=0))
            if mode=='explore': passed=passed and (r.get('value') or {}).get('reason')=='step_limit'
            passed=passed and bool(trace) and trace[-1]['Settled'] and motion['maxPredictionError']<=16
            if scene in ('short_gap','low_obstacle'): passed=passed and motion['airborneFrames']>0
            saved_trace=profile/f'terrain-motion-{scene}-{direction}-{mode}.json'
            saved_trace.write_text(json.dumps(trace),encoding='utf-8')
            row=dict(scene=scene,direction=direction,mode=mode,motion=motion,trace=str(saved_trace),expectedPass=expected,passed=passed,fixture=fixture,result=r)
            results.append(row)
            (profile/'terrain-result.json').write_text(json.dumps(dict(passed=all(x['passed'] for x in results),scenarios=results),indent=2),encoding='utf-8')
            print(json.dumps(dict(scene=scene,direction=direction,mode=mode,passed=passed,status=action.get('status'),reason=action.get('reasonDetail'),x=e.get('x'),y=e.get('y'),maxError=(e.get('navigation') or {}).get('MaxError'))),flush=True)
    return all(r['passed'] for r in results)
def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--port',type=int,required=True);p.add_argument('--profile',type=Path,required=True);a=p.parse_args()
    return 0 if asyncio.run(verify(a)) else 1
if __name__=='__main__':raise SystemExit(main())
