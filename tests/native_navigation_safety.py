"""Dedicated server evidence for native fall thresholds and rolling navigation."""
import argparse
import json
from pathlib import Path
import sys
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from server_ai import ServerAiBridge


def verify(args):
    rows = []
    profile = args.profile.resolve()
    output = profile / 'navigation-safety-results.json'
    cases = args.case or ('drop13', 'drop27', 'extra', 'immune', 'dense', 'surface_detour', 'long')
    for case in cases:
        kind = 'navigation_surface_detour' if case == 'surface_detour' else f'navigation_safety_{case}'
        ready = profile / 'mine-task-ready.json'
        ready.unlink(missing_ok=True)
        request = profile / 'mine-task-request.tmp'
        request.write_text(json.dumps({'caseId': kind}), encoding='utf-8')
        request.replace(profile / 'mine-task-request.json')
        until = time.monotonic() + 15
        while not ready.exists() and time.monotonic() < until:
            time.sleep(.04)
        # The fixture publishes before the next native update applies equipment.
        time.sleep(.25)
        scene = json.loads(ready.read_text())
        with Bridge(args.port) as bridge:
            assert all(scene[k] == bridge.binding[k] for k in ('world', 'generation', 'slot')), scene
            before = bridge._request('state')['data']
            action_id = uuid.uuid4().hex
            started = time.monotonic()
            bridge._request('start', actionId=action_id, operation='navigate_to',
                            arguments={'x': scene['destinationX'], 'y': scene['destinationY']},
                            expiresAtUnixMs=int(time.time() * 1000) + 1500)
            until = started + 185
            while time.monotonic() < until:
                action = bridge._request('action', actionId=action_id)['data']['action']
                if action['status'] != 'running' and action.get('endEvidence'):
                    break
                time.sleep(.1)
            else:
                bridge._request('cancel', actionId=action_id)
                raise TimeoutError(case)
            elapsed = time.monotonic() - started
            after = bridge._request('state')['data']
        task = action['evidence']['task']
        children = task['progress']['children']
        moves = [child for child in children if child['taskName'] == 'navigate_to_landing_region']
        snapshots = [(child['result']['evidence'] or {}).get('navigation') or {} for child in moves]
        profiles = [(s.get('Mobility') or {}).get('EffectivePhysics', {}).get('Fall') for s in snapshots]
        nodes = max(((s.get('SurfaceGraph') or {}).get('Nodes', 0) for s in snapshots), default=0)
        checkpoints = [child for child in moves if child['reason'] == 'native_navigation_work_checkpoint']
        row = dict(case=case, elapsedSeconds=elapsed, scene=scene, before=before, after=after, action=action,
                   nodes=nodes, fallProfiles=profiles, checkpoints=len(checkpoints))
        rows.append(row)
        output.write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(dict(case=case, status=action['status'], reason=action['reason'],
                              elapsed=round(elapsed, 2), nodes=nodes, checkpoints=len(checkpoints),
                              fall=profiles)), flush=True)
        assert action['endEvidence']['inputsReleased'] and after['active'] and after['life'] == before['life'], case
        if case == 'drop27':
            assert action['status'] in ('failed', 'timeout') and after['position']['y'] <= before['position']['y'] + 1, case
            assert moves[-1]['result']['evidence']['settled'], case
            continue
        assert action['status'] == 'completed' and action['endEvidence']['effectVerified'], case
        final = task['result']['evidence']
        assert final['settled'] and final['safeSupport'] and final['grounded'], final
        assert abs(final['feetY'] - scene['destinationY']) <= .5, final
        if case == 'extra':
            assert any(p and p['ExtraFall'] >= 2 and not p['Immune'] for p in profiles), profiles
        if case == 'immune':
            assert any(p and p['Immune'] for p in profiles), profiles
        if case == 'dense':
            assert nodes > 8192, nodes
        if case == 'long':
            assert elapsed > 45 and after['position']['x'] - before['position']['x'] > 128*16, row
            assert sum(c['result']['evidence']['updates'] for c in moves) > 2700, row
            assert checkpoints and all(c['result']['inputsReleased'] and c['result']['evidence']['settled']
                                       for c in checkpoints), checkpoints
        if case == 'surface_detour':
            assert any(s.get('CompletedJumps', 0) > 0 for s in snapshots), case


class Bridge(ServerAiBridge):
    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--profile', required=True, type=Path)
    parser.add_argument('--port', required=True, type=int)
    parser.add_argument('--case', action='append')
    verify(parser.parse_args())
