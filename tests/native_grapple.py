"""Isolated dedicated-server hook ownership, latch, pull and cleanup."""
import argparse
import asyncio
import json
import sys
from pathlib import Path

from native_direct_input import wait_file
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


async def verify(args):
    await wait_file(args.profile / 'action-fixture.json', 90)
    (args.profile / 'grapple-proof-request').touch()
    proof = await wait_file(args.profile / 'grapple-proof.json', 15)
    assert proof['passed'], f"Native hook proof failed: {args.profile}"
    assert proof['owner'] == proof['slot'] != proof['local']
    assert all(f['globals'] and f['owner'] == proof['slot'] for f in proof['frames'])
    assert any(f['ai'] == 2 and f['vy'] < 0 for f in proof['frames'])
    assert proof['released']
    print(f"Native hook proof passed: owner={proof['owner']} local={proof['local']} frames={len(proof['frames'])}")
    ready = args.profile / 'grapple-scene-ready.json'
    for case in args.scenes or ['ledge', 'anchor', 'cancel', 'reuse', 'same_reuse', 'miss', 'timeout', 'no_hook', 'unsupported', 'generation']:
        ready.unlink(missing_ok=True)
        request = args.profile / 'grapple-scene-request'
        temp = request.with_suffix('.tmp')
        temp.write_text(case)
        temp.replace(request)
        scene = await wait_file(ready)
        bridge = ServerAiBridge(args.port)
        cancel = asyncio.Event()
        try:
            source = "return api('navigate_to',{x=%s,y=%s})" % (scene['x'], scene['y'])
            pending = asyncio.create_task(LuaRuntime(bridge).execute(
                source, limits=Limits(seconds=60, calls=1, actions=1), cancel=cancel))
            if case == 'cancel':
                signal = args.profile / 'grapple-cancel-now'
                while not signal.exists() and not pending.done():
                    await asyncio.sleep(.005)
                assert signal.exists(), 'Cancellation never observed native pull'
                cancel.set()
            result = await pending
        finally:
            bridge.close()
        (args.profile / f'grapple-{case}-receipt.json').write_text(json.dumps(result, indent=2))
        action = result.get('value') or result.get('lastAction') or {}
        trace = args.profile / f'grapple-{case}.jsonl'
        rows = [json.loads(line) for line in trace.read_text().splitlines()] if trace.exists() else []
        stages = [row['detail']['stage'] for row in rows]
        assert all(row['myPlayer'] == proof['local'] for row in rows)
        if case == 'ledge':
            assert action.get('completed'), (action.get('status'), action.get('reason'), args.profile)
            evidence = action['task']['lastResult']['evidence']
            assert all(evidence.get(k) for k in ('arrived', 'safeSupport', 'settled', 'grounded')), evidence
            assert stages == ['fired', 'latched', 'detached', 'settled'], rows
            assert rows[2]['detail']['vy'] < 0 and rows[0]['detail']['y'] - rows[2]['detail']['y'] > 90
            assert rows[-1]['detail']['vx'] == rows[-1]['detail']['vy'] == 0
        elif case in ('no_hook', 'unsupported'):
            assert action.get('status') == 'failed' and not stages, (action.get('reason'), rows)
        else:
            assert action.get('status') != 'completed' and stages.count('fired') == 1, (action.get('reason'), rows)
            assert 'disturbance' in stages and 'settled' not in stages, rows
            if case == 'cancel':
                assert result['status'] == 'cancelled' and stages[-1] == 'stopped', (result, rows)
                assert rows[-1]['detail']['vy'] < 0, 'Cancellation did not occur airborne'
            elif case in ('reuse', 'same_reuse'):
                assert action.get('status') == 'unknown', action
                replacement = rows[-1]['replacement']
                expected = (proof['owner'], rows[0]['detail']['identity']) if case == 'same_reuse' else (0, 987)
                assert replacement['active'] and (replacement['owner'], replacement['identity']) == expected, rows
            elif case == 'generation':
                assert action.get('status') == 'unknown' or result['status'] == 'unknown', result
                assert stages[-1] == 'binding_lost' and rows[-1]['grapCount'] == 0, rows
            else:
                failure = {'anchor': 'grapple_conditions_changed', 'miss': 'grapple_missed',
                           'timeout': 'grapple_phase_timeout'}[case]
                assert rows[-1]['detail']['Failure'] == failure, rows
        if rows:
            assert rows[-1]['inputsReleased'] and (case == 'same_reuse' or not rows[-1]['activeHook']), rows[-1]
        if case not in ('cancel', 'generation'):
            assert action['endEvidence']['inputsReleased'], action
        print(f"Native grapple {case}: {action.get('status') or result['status']} {stages}", flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--scenes', nargs='+')
    asyncio.run(verify(parser.parse_args()))
