"""Generated-world observe_scene -> direct region goal; no host grid reconstruction or LLM."""
import argparse
import asyncio
import json
from pathlib import Path
from native_landing_regions import wait_file
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


SOURCE="""
local view=api('observe_scene',{})
local s=view.data
local choice=nil
if s.actor.navigationProfileSupported then
  for _,c in ipairs(s.landingRegions.items) do
    local ahead=DIRECTION==1 and c.target.minPlayerLeftX>s.actor.boundsWorldPixels.x+32 or
      DIRECTION==-1 and c.target.maxPlayerLeftX<s.actor.boundsWorldPixels.x-32
    if ahead and math.abs(c.feetDeltaY)>0.5 and math.abs(c.feetDeltaY)<=64 then choice=c;break end
  end
end
if not choice then return {scene=view,stop='no_observed_directional_region_at_different_height'} end
local action=api('navigate_to_landing_region',choice.target)
if not action.valid or not action.completed then return {scene=view,choice=choice,action=action,stop=action.reason} end
return {scene=view,choice=choice,action=action,after=api('observe_scene',{}),stop='one_action_budget'}
"""


async def verify(args):
    profile=args.profile.resolve();assert (profile/'isolated-natural-world').is_file()
    rows=[]
    for offset in args.offsets:
        for direction in (1,-1):
            case=f"scene-{'p' if offset>=0 else 'm'}{abs(offset)}-{'r' if direction==1 else 'l'}"
            ready=profile/'natural-ready.json';ready.unlink(missing_ok=True)
            temp=profile/'natural-request.tmp';temp.write_text(json.dumps(dict(caseId=case,offsetTiles=offset)));temp.replace(profile/'natural-request.json')
            fixture=await wait_file(ready)
            bridge=ServerAiBridge(args.port)
            try:
                source=SOURCE.replace('DIRECTION',str(direction));(profile/f'natural-{case}.lua').write_text(source,encoding='utf-8')
                r=await LuaRuntime(bridge,evidence_root=profile/'scene-lua').execute(source,limits=Limits(seconds=40,calls=6,actions=1))
            finally:bridge.close()
            a=(r.get('value') or {}).get('action') or r.get('lastAction') or {}
            if a.get('completed'):
                e=a['evidence'];g=a['targetRegion']
                assert a['valid'] and e['settled'] and e['safeSupport'] and e['grounded'] and a['endEvidence']['inputsReleased']
                assert g['minPlayerLeftX']<=e['x']<=g['maxPlayerLeftX'] and abs(e['y']+42-g['surfaceY'])<=.5
            (profile/'natural-finish').write_text(case);await wait_file(profile/f'natural-motion-{case}.json')
            row=dict(case=case,offset=offset,direction=direction,fixture=fixture,result=r,
                completed=a.get('completed') is True,stop=(r.get('value') or {}).get('stop') or a.get('reasonDetail') or a.get('reason') or r['status'])
            rows.append(row);(profile/'natural-results.json').write_text(json.dumps(dict(scenarios=rows),indent=2),encoding='utf-8')
            print(json.dumps(dict(case=case,completed=row['completed'],status=a.get('status'),stop=row['stop'],calls=r['calls'])),flush=True)
    return True


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--profile',type=Path,required=True);p.add_argument('--port',type=int,required=True);p.add_argument('--offsets',type=int,nargs='+',default=[0,32,-32])
    raise SystemExit(0 if asyncio.run(verify(p.parse_args())) else 1)
