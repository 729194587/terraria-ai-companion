"""Isolated native Cloud navigation; equipment/state injection is fixture-only."""
import argparse
import asyncio
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from server_ai import ServerAiBridge
from lua_runtime import LuaRuntime, Limits
from native_direct_input import wait_file


async def verify(args):
    p = args.profile
    await wait_file(p / 'action-fixture.json', 90)
    results = []
    for case in args.scenes or ['cloud', 'cloud_chain', 'cloud_fail', 'cloud_offset', 'cloud_root', 'angel', 'demon']:
        mode = 'cloud' if case in ['cloud_chain', 'cloud_root'] else case
        scene = 'extra_chain' if case == 'cloud_chain' else 'extra_up'
        ready = p / 'terrain-ready.json'
        ready.unlink(missing_ok=True)
        temp = p / 'terrain-request.tmp'
        temp.write_text(json.dumps(dict(scene=scene, direction=1, mobility=mode)))
        temp.replace(p / 'terrain-request.json')
        fixture = await wait_file(ready)
        x = fixture['originTileX'] * 16
        offset, rise = (14, 16) if case == 'cloud_chain' else (6, 8)
        source = "return api('navigate_to_landing_region',{minPlayerLeftX=%s,maxPlayerLeftX=%s,surfaceY=%s})" % (x + offset*16, x + (offset+4)*16-4, (fixture['floor']-rise)*16)
        if case == 'cloud_root':
            source = "return api('navigate_to',{x=%s,y=%s})" % (x+8*16, (fixture['floor']-rise)*16)
        bridge = ServerAiBridge(args.port)
        try:
            result = await LuaRuntime(bridge).execute(source, limits=Limits(seconds=60, calls=1, actions=1))
        finally:
            bridge.close()
        (p / f'extra-{case}-receipt.json').write_text(json.dumps(result, indent=2))
        trace = await wait_file(p / f'terrain-motion-{scene}-1-mobility-{mode}.json')
        action = result.get('value') or result.get('lastAction') or {}
        starts = []
        previous = False
        for frame in trace:
            observation = frame.get('extraObservation') or {}
            jumps = observation.get('movement', {}).get('ExtraJumps', [])
            active = any(j['Active'] for j in jumps)
            if active and not previous:
                starts.append(dict(tick=frame['tick'], x=frame['x'], y=frame['y'], vy=frame['vy'],
                                   jumpCounter=observation['jumpCounter'], jumps=jumps))
            previous = active
        row = dict(case=case, status=action.get('status'), reason=action.get('reason'),
                   detail=action.get('reasonDetail'), starts=starts,
                   navigation=(action.get('evidence') or {}).get('navigation'))
        results.append(row)
        (p / 'extra-jump-results.json').write_text(json.dumps(results, indent=2))
        print(json.dumps(dict(case=case,status=row['status'],reason=row['reason'],extraJumpActivations=len(starts))), flush=True)
        assert action.get('completed'), row
        assert action['endEvidence']['inputsReleased'], action
        evidence = action['task']['lastResult']['evidence'] if case == 'cloud_root' else action['evidence']
        assert all(evidence.get(k) for k in ['arrived', 'safeSupport', 'settled', 'grounded']), evidence
        if case in ('angel', 'demon'):
            samples = [(frame, frame['extraObservation']['movement']['Wings']) for frame in trace if frame.get('extraObservation')]
            slot = 2 if case == 'angel' else 1
            assert samples and all(wing['FunctionalSlot'] == slot for _, wing in samples)
            assert any(frame['vy'] < 0 and wing['Time'] < wing['TimeMax'] for frame, wing in samples)
            nav = evidence['navigation']
            actual, predicted = nav['ActualLanding'], nav['PredictedLanding']
            error = {axis: abs(actual[axis] - predicted[axis]) for axis in ('X', 'Y')}
            assert max(error.values()) <= 2 and action['endEvidence']['inputsReleased'], (error, nav)
            print(case, 'wing time', min(w['Time'] for _, w in samples), 'of', max(w['TimeMax'] for _, w in samples),
                  'predicted', predicted, 'actual', actual, 'error', error, flush=True)
            continue
        assert len(starts) >= (2 if case == 'cloud_chain' else 1), row
        assert all(s['jumpCounter'] == 11 and abs(s['vy'] + 4.61) < .01 for s in starts), starts
        if case == 'cloud_root':
            children = action['task']['progress']['children']
            assert all(c['taskName'] == 'navigate_to_landing_region' for c in children), children
            nav = children[-1]['result']['evidence']['navigation']
            row['navigation'] = nav
        else:
            nav = evidence['navigation']
        if case in ['cloud', 'cloud_chain', 'cloud_root']:
            assert nav['RecoveryCount'] == 0, nav
        else:
            assert nav['RecoveryCount'] >= 1 and nav['RecoveryReplans'] >= 1, nav
        (p / 'extra-jump-results.json').write_text(json.dumps(results, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--scenes', nargs='+')
    asyncio.run(verify(parser.parse_args()))
