"""Deterministic test programs on generated terrain; no LLM or production task helper."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
import time
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'python'))
import lua_runtime
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge

def completed_segments(mode, report, calls):
    # A worker may withhold its value after unknown; keep known earlier native
    # completions from the same navigation operation.
    if mode=='explore' and report:
        return report['progress']['completedSegments']
    return sum(c['operation']==('navigate_to' if mode=='explore' else 'navigate_to_x') and c['result'].get('completed') is True for c in calls)

async def wait_file(path, seconds=15):
    end=time.monotonic()+seconds
    while not path.exists():
        if time.monotonic()>=end: raise TimeoutError(str(path))
        await asyncio.sleep(.05)
    return json.loads(path.read_text(encoding='utf-8-sig'))

async def verify(args):
    profile=args.profile.resolve()
    assert (profile/'isolated-natural-world').is_file()
    if args.helper_source: lua_runtime.EXPLORATION_SOURCE=args.helper_source.read_text(encoding='utf-8')
    rows=[]
    for offset in args.offsets:
        for direction in (1,-1):
            for mode in ('explore','direct'):
                case=f"o{'p' if offset>=0 else 'm'}{abs(offset)}-{'r' if direction>0 else 'l'}-{mode}"
                ready=profile/'natural-ready.json'
                if ready.exists(): ready.unlink()
                (profile/'natural-request.tmp').write_text(json.dumps(dict(caseId=case,offsetTiles=offset)),encoding='utf-8')
                (profile/'natural-request.tmp').replace(profile/'natural-request.json')
                fixture=await wait_file(ready)
                if not fixture['Settled']:
                    rows.append(dict(case=case,offsetTiles=offset,direction=direction,mode=mode,fixture=fixture,completedSegments=0,stop='unsupported_natural_start'))
                    (profile/'natural-results.json').write_text(json.dumps(dict(scenarios=rows),indent=2),encoding='utf-8')
                    (profile/'natural-finish').write_text(case,encoding='utf-8')
                    await wait_file(profile/f'natural-motion-{case}.json')
                    print(json.dumps(dict(case=case,stop='unsupported_natural_start')),flush=True)
                    continue
                bridge=ServerAiBridge(port=args.port)
                assert fixture['fixture']['world']==bridge.binding['world']
                calls=[]
                def record(name,arguments,result):
                    calls.append(dict(operation=name,arguments=arguments,result=result))
                    if name=='navigate_to_x' and result.get('completed') is True:
                        e=result.get('evidence') or {};end=result.get('endEvidence') or {}
                        assert result['valid'] and result['status']=='completed' and result['endEvidenceStatus']=='known'
                        assert e.get('arrived') and e.get('grounded') and e.get('safeSupport') and e.get('settled')
                        assert end.get('inputsReleased'), 'completed natural navigation lacks release evidence'
                try:
                    if mode=='explore':
                        source="return {report=explore('"+('right' if direction>0 else 'left')+"')}"
                    else:
                        source="""local report={steps=json.array(),observations=json.array(),reason='step_limit'}
for step=1,6 do
    local before=api('get_player_state',{})
    assert(before.valid)
    report.observations[#report.observations+1]=api('observe_terrain',{})
    local action=api('navigate_to_x',{targetX=before.player.position.x+DIRECTION*128})
    report.steps[#report.steps+1]={action=action,before=before}
    if not action.valid or not action.completed or action.endEvidenceStatus~='known' then report.reason='action_'..tostring(action.status); break end
end
report.after=api('observe_terrain',{})
return {report=report}""".replace('DIRECTION',str(direction))
                    result=await LuaRuntime(bridge,evidence_root=profile/'natural-lua').execute(source,
                        limits=Limits(seconds=60,calls=64,actions=6),on_result=record)
                finally: bridge.close()
                (profile/f'natural-receipt-{case}.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
                (profile/'natural-finish').write_text(case,encoding='utf-8')
                trace_path=profile/f'natural-motion-{case}.json'
                trace=await wait_file(trace_path)
                report=(result.get('value') or {}).get('report') or {}
                action=result.get('lastAction') or {}
                e=action.get('evidence') or {}
                row=dict(case=case,offsetTiles=offset,direction=direction,mode=mode,fixture=fixture,result=result,
                    completedSegments=completed_segments(mode,report,calls),
                    stop=report.get('returnReason') or report.get('reason') or result['status'],nativeReason=action.get('reason'),nativeDetail=action.get('reasonDetail'),
                    settled=e.get('settled'),frames=len(trace),minY=min((f['y'] for f in trace),default=None),
                    maxY=max((f['y'] for f in trace),default=None),maxPredictionError=max((f.get('maxError') or 0 for f in trace),default=0))
                rows.append(row)
                (profile/f'natural-calls-{case}.json').write_text(json.dumps(calls,ensure_ascii=False,indent=2),encoding='utf-8')
                (profile/'natural-results.json').write_text(json.dumps(dict(scenarios=rows),ensure_ascii=False,indent=2),encoding='utf-8')
                print(json.dumps({k:row[k] for k in ('case','completedSegments','stop','nativeReason','nativeDetail','minY','maxY')}),flush=True)
                # Unknown outcomes cannot be followed by a reset/reconnect in this world.
                if result['actionState']=='unknown': return

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--port',type=int,required=True);p.add_argument('--profile',type=Path,required=True)
    p.add_argument('--helper-source',type=Path);p.add_argument('--offsets',type=int,nargs='+',default=[0,32,-32,64,-64])
    asyncio.run(verify(p.parse_args()))
