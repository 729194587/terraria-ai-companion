"""One external goal per fixed natural-terrain scene; no helper/LLM/retries."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge
from native_natural_navigation import wait_file


def percentile(values,q):
    values=sorted(values)
    return values[min(len(values)-1,int((len(values)-1)*q))] if values else None


async def verify(args):
    p=args.profile.resolve();rows=[]
    assert (p/'isolated-natural-world').is_file()
    cases=[(0,1,768),(0,-1,768),(0,1,1536),(0,-1,1536)]
    cases += [(o,d,768) for o in args.offsets if o!=0 for d in (1,-1)]
    for offset,direction,distance in cases:
        case=f"trip-{'p' if offset>=0 else 'm'}{abs(offset)}-{'r' if direction>0 else 'l'}-{distance}"
        ready=p/'natural-ready.json';ready.unlink(missing_ok=True)
        temp=p/'natural-request.tmp';temp.write_text(json.dumps(dict(caseId=case,offsetTiles=offset)))
        temp.replace(p/'natural-request.json');fixture=await wait_file(ready)
        if not fixture['Settled']:
            (p/'natural-finish').write_text(case);await wait_file(p/f'natural-motion-{case}.json')
            rows.append(dict(case=case,status='unsupported_natural_start',fixture=fixture))
            (p/'natural-results.json').write_text(json.dumps(dict(scenarios=rows),indent=2));continue
        target=fixture['x']+direction*distance
        source=f"local a=api('navigate_to_x',{{targetX={target}}}); return a"
        calls=[];bridge=ServerAiBridge(args.port)
        try:
            receipt=await LuaRuntime(bridge,evidence_root=p/'trip-lua').execute(source,
                limits=Limits(seconds=35,calls=1,actions=1),
                on_result=lambda n,a,r:calls.append(dict(operation=n,arguments=a,result=r)))
        finally:bridge.close()
        action=receipt.get('value') or receipt.get('lastAction') or {}
        (p/f'trip-{case}-receipt.json').write_text(json.dumps(dict(source=source,fixture=fixture,receipt=receipt,calls=calls),indent=2))
        (p/'natural-finish').write_text(case)
        trace=await wait_file(p/f'natural-motion-{case}.json')
        e=action.get('evidence') or {};nav=e.get('navigation') or {};end=action.get('endEvidence') or {}
        assert len(calls)==1 and receipt['actions']==1,'Trip may submit exactly one external action'
        if action.get('completed'):
            assert all(e.get(k) for k in ('arrived','grounded','safeSupport','settled')),action
            assert end.get('inputsReleased') and action['endEvidenceStatus']=='known'
            assert direction*(target-e['x'])<=12 and abs(e['vx'])<=.1 and abs(e['vy'])<=.1
            assert end['worldSessionId']==fixture['fixture']['world']
            assert end['actorGeneration']==fixture['fixture']['generation'] and end['actorSlot']==fixture['fixture']['slot']
        active=[f for f in trace if f.get('navigation')]
        plans={f['navigation']['PlanNumber']:f['navigation'] for f in active if f['navigation']['PlanNumber']>0}
        times=[f['gameUpdateMilliseconds'] for f in active]
        statuses={}
        for n in plans.values():
            reason=n.get('SearchExit') or n.get('PlanKind');statuses[reason]=statuses.get(reason,0)+1
        row=dict(case=case,status=action.get('status'),reason=action.get('reason'),detail=action.get('reasonDetail'),
            completed=bool(action.get('completed')),startX=fixture['x'],targetX=target,endX=e.get('x'),endY=e.get('y'),
            displacement=abs(e['x']-fixture['x']) if 'x' in e else None,
            plans=len(plans),replans=max(0,len(plans)-1),continuedEdges=nav.get('ContinuedEdges'),
            recoveries=nav.get('RecoveryCount'),detourCheckpoints=nav.get('DetourCheckpoints'),
            searchExits=statuses,totalPlanMilliseconds=sum(n['PlanMilliseconds'] for n in plans.values()),
            activeTicks=len(active),tickMedianMs=percentile(times,.5),tickP95Ms=percentile(times,.95),tickMaxMs=max(times,default=None),
            finalNavigation=nav,endEvidence=end,endEvidenceStatus=action.get('endEvidenceStatus'))
        rows.append(row);(p/'natural-results.json').write_text(json.dumps(dict(scenarios=rows),indent=2))
        print(json.dumps({k:v for k,v in row.items() if k not in ('finalNavigation','endEvidence')}),flush=True)
        if action.get('status')=='unknown' or receipt.get('actionState')=='unknown':break


if __name__=='__main__':
    a=argparse.ArgumentParser(description=__doc__)
    a.add_argument('--port',type=int,required=True);a.add_argument('--profile',type=Path,required=True)
    a.add_argument('--offsets',nargs='+',type=int,default=[0,-231]);a.add_argument('--helper-source',type=Path)
    asyncio.run(verify(a.parse_args()))
