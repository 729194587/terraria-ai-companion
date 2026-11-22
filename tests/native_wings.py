"""Exercise equipped wings via the ordinary server AI navigate_to entry point."""
import argparse
import asyncio
import json
from pathlib import Path

from native_extra_jump import Limits, LuaRuntime, ServerAiBridge, wait_file


async def verify(args):
    profile = args.profile
    await wait_file(profile / 'action-fixture.json', 90)
    for case in args.scenes or ('angel', 'bone', 'bone_chain'):
        chain = case == 'bone_chain'
        mode = 'bone' if chain else case
        scene = 'wing_chain' if chain else 'extra_up'
        ready = profile / 'terrain-ready.json'
        ready.unlink(missing_ok=True)
        request = profile / 'terrain-request.tmp'
        request.write_text(json.dumps(dict(scene=scene, direction=1, mobility=mode)))
        request.replace(profile / 'terrain-request.json')
        fixture = await wait_file(ready)
        x, y = (fixture['originTileX'] + (20 if chain else 8)) * 16, (fixture['floor'] - (16 if chain else 8)) * 16
        bridge = ServerAiBridge(args.port)
        try:
            result = await LuaRuntime(bridge).execute(
                f"return api('navigate_to',{{x={x},y={y}}})", limits=Limits(seconds=60, calls=1, actions=1))
        finally:
            bridge.close()
        (profile / f'wing-{case}-receipt.json').write_text(json.dumps(result, indent=2))
        action = result.get('value') or result.get('lastAction') or {}
        assert action.get('completed'), (mode, action.get('reason'), action.get('reasonDetail'), profile)
        assert action['endEvidence']['inputsReleased']
        evidence = action['task']['lastResult']['evidence']
        assert all(evidence.get(k) for k in ('arrived','grounded','settled','safeSupport')), evidence
        trace = await wait_file(profile / f'terrain-motion-{scene}-1-mobility-{mode}.json')
        wings = [frame['extraObservation']['movement']['Wings'] for frame in trace]
        assert any(w['FunctionalSlot']=={'angel':2,'bone':8}[mode] and 0<w['Time']<w['TimeMax'] for w in wings)
        if chain:
            middle_feet = (fixture['floor'] - 8) * 16
            middle = next(i for i,frame in enumerate(trace)
                          if frame['Settled'] and abs(frame['y'] + 42 - middle_feet) <= 1
                          and wings[i]['Time'] == wings[i]['TimeMax'])
            assert all(any(w['Time'] < w['TimeMax'] for w in segment)
                       for segment in (wings[:middle], wings[middle+1:])), 'missing flight on either side of middle landing'
        print(f"{case}: consumed native wing time, reached supported landing", flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--scenes', choices=('angel','bone','bone_chain'), nargs='+')
    asyncio.run(verify(parser.parse_args()))
