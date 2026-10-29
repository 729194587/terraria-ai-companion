"""Vertical matrix through real Lua Worker/IPC and natural native Player updates.

Only the isolated terrain fixture writes geometry and seeds initial position.
The alternating case performs four actions without resetting between goals.
"""
import argparse
import asyncio
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge
from native_landing_regions import wait_file


async def verify(args):
    profile = args.profile.resolve()
    assert (profile / 'isolated-action-fixture').is_file()
    results = []
    scenes = ('obstacle_one', 'solid_up_three', 'solid_up_five', 'shaft_up',
              'shaft_ceiling', 'alternating', 'no_landing', 'descent_five', 'hazard_target')
    for scene in scenes:
        for direction in (1, -1):
            ready = profile / 'terrain-ready.json'
            ready.unlink(missing_ok=True)
            temp = profile / 'terrain-request.tmp'
            temp.write_text(json.dumps(dict(scene=scene, direction=direction)))
            temp.replace(profile / 'terrain-request.json')
            fixture = await wait_file(ready)
            x, floor = fixture['originTileX'] * 16, fixture['floor'] * 16

            def goal(q1, q2, height):
                # Intervals constrain player LEFT, including the full 20px body
                # on the two mirrored tile runs; these are explicit test targets.
                a, b = sorted((x + direction*q1*16, x + direction*q2*16))
                return dict(minPlayerLeftX=a, maxPlayerLeftX=b-4, surfaceY=floor-height)

            if scene.startswith('shaft'):
                # Mirroring 2 columns also mirrors the player within their 32px span.
                sx = x+6 if direction == 1 else x-10
                goals = [dict(minPlayerLeftX=sx-2, maxPlayerLeftX=sx+2, surfaceY=floor-64)]
            elif scene == 'alternating':
                goals = [goal(6, 11, 64), goal(14, 19, 32), goal(6, 11, 64), goal(-2, 2, 0)]
            else:
                height = {'solid_up_three': 48, 'solid_up_five': 80, 'no_landing': 64}.get(scene, 0)
                goals = [goal(7, 11, height)]
            expected = scene not in ('shaft_ceiling', 'no_landing', 'descent_five', 'hazard_target')
            lua_goals = '{'+','.join('{'+','.join(f'{k}={v}' for k, v in g.items())+'}' for g in goals)+'}'
            source = "local goals="+lua_goals+"; local results={}\n" + """
for _,goal in ipairs(goals) do
  local before=api('observe_terrain',{})
  local action=api('navigate_to_landing_region',goal)
  table.insert(results,{goal=goal,before=before,action=action,after=api('get_player_state',{})})
  if not action.completed then break end
end
return results
"""
            (profile/f'vertical-{scene}-{direction}.lua').write_text(source, encoding='utf-8')
            bridge = ServerAiBridge(args.port)
            try:
                r = await LuaRuntime(bridge, evidence_root=profile/'vertical-lua').execute(
                    source, limits=Limits(seconds=100, calls=20, actions=4))
            finally:
                bridge.close()
            actions = [row['action'] for row in (r.get('value') or [])] or [r.get('lastAction') or {}]
            checks = []
            for action, target in zip(actions, goals):
                e, end = action.get('evidence') or {}, action.get('endEvidence') or {}
                ok = (action.get('valid') is True and action.get('targetRegion') == target
                      and action.get('endEvidenceStatus') == 'known' and end.get('inputsReleased') is True
                      and end.get('worldSessionId') == fixture['world']
                      and end.get('actorGeneration') == fixture['generation'] and end.get('actorSlot') == fixture['slot'])
                if expected:
                    ok = (ok and action.get('completed') is True
                          and all(e.get(k) is True for k in ('arrived', 'grounded', 'safeSupport', 'settled'))
                          and target['minPlayerLeftX'] <= e['x'] <= target['maxPlayerLeftX']
                          and abs(e['y']+42-target['surfaceY']) <= .5
                          and abs(e['velocityBeforeRelease']['X']) <= .1 and abs(e['velocityBeforeRelease']['Y']) <= .1)
                else:
                    ok = ok and action.get('status') == 'failed' and e.get('settled') is True
                checks.append(ok)
            passed = bool(checks) and all(checks) and (not expected or len(actions) == len(goals))
            trace = await wait_file(profile/f'terrain-motion-{scene}-{direction}.json')
            if scene == 'shaft_up':
                inputs = [f['injectedInput'] for f in trace if f.get('injectedInput') is not None]
                passed = (passed and bool(inputs) and any(f['jump'] for f in inputs)
                          and all(not f['left'] and not f['right'] for f in inputs)
                          and max(f.get('maxError') or 0 for f in trace) <= .01)
            row = dict(scene=scene, direction=direction, expected=expected, passed=passed,
                       fixture=fixture, goals=goals, result=r, motion=trace)
            results.append(row)
            (profile/'vertical-result.json').write_text(json.dumps(dict(passed=all(x['passed'] for x in results), scenarios=results), indent=2))
            print(json.dumps(dict(scene=scene, direction=direction, passed=passed,
                actions=[dict(status=a.get('status'), reason=a.get('reasonDetail'),
                    x=(a.get('evidence') or {}).get('x'), y=(a.get('evidence') or {}).get('y')) for a in actions])), flush=True)
    return all(x['passed'] for x in results)


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--port', type=int, required=True)
    p.add_argument('--profile', type=Path, required=True)
    raise SystemExit(0 if asyncio.run(verify(p.parse_args())) else 1)
