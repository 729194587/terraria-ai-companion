"""Run with verify_server_ai_direct_input_native.ps1 in a fresh copied world."""
import argparse
import asyncio
import hashlib
import json
import math
from pathlib import Path
import sys

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE.parent), str(HERE.parents[1] / 'python')]
from native_direct_input import wait_file
from native_grapple import verify as verify_grapple
from native_extra_jump import verify as verify_cloud
from lua_runtime import LuaRuntime, Limits
from server_ai import ServerAiBridge


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def key(row):
    return row['Scene'], row['Planner'], row['Config']['Group']


async def prepare(profile, fixture, inputs=None):
    ready = profile / 'terrain-ready.json'
    ready.unlink(missing_ok=True)
    request = dict(scene=fixture['Geometry'], direction=1, mobility=fixture['Capability'])
    if inputs is not None:
        request['replay'] = inputs
    temp = profile / 'terrain-request.tmp'
    temp.write_text(json.dumps(request))
    temp.replace(profile / 'terrain-request.json')
    return await wait_file(ready)


async def action(port, source):
    bridge = ServerAiBridge(port)
    try:
        result = await LuaRuntime(bridge).execute("return "+source, limits=Limits(seconds=60, calls=1, actions=1))
    finally:
        bridge.close()
    return result.get('value') or result.get('lastAction') or {}


async def replay(args, fixture, keys, positions, goal):
    trace = args.profile / 'navigation-replay.json'
    trace.unlink(missing_ok=True)
    ready = await prepare(args.profile, fixture, keys)
    receipt = await action(args.port, 'api("move_input",{horizontal="none",vertical="none",jumpHoldUpdates=0,maxUpdates=%d})' % len(keys))
    assert receipt.get('completed') and receipt['evidence']['inputReleased'], receipt
    end = await wait_file(trace)
    assert end['frames'] == len(keys) == end['lastTick'] - end['firstTick'] + 1
    assert end['released'] and all(ready[k] == end[k] for k in ('world', 'generation', 'slot'))
    assert len(end['positions']) == len(positions)+1
    dx, dy = (ready['originTileX']-100)*16, (ready['floor']-100)*16
    assert end['positions'][0] == [fixture['X']+dx, fixture['Y']+dy], (ready,end['positions'][0])
    errors = [math.dist((x-dx, y-dy), p) for (x,y), p in zip(end['positions'][1:], positions)]
    x, y = end['positions'][-1]
    arrived = (end['Settled'] and end['SafeSupport'] and end['Grounded'] and not end['dead']
               and goal['MinPlayerLeftX'] <= x-dx <= goal['MaxPlayerLeftX']
               and abs(y-dy+42-goal['SurfaceY']) <= .5)
    # Retain the full native trajectory once per distinct schedule in the run profile.
    path = args.profile / f"replay-{fixture['Name']}-{end['firstTick']}.json"
    path.write_text(json.dumps(dict(Inputs=keys, Predicted=positions, Native=end)))
    return dict(Trace=path.name, ActuallyArrived=arrived, ExecutionDeviation=max(errors), End=[x-dx, y-dy],
                EndError=errors[-1], Settled=end['Settled'], SafeSupport=end['SafeSupport'],
                Frames=len(keys), FirstTick=end['firstTick'], LastTick=end['lastTick'],
                Origin=[dx,dy], Binding=[end[k] for k in ('world','generation','slot')])


async def verify(args):
    await wait_file(args.profile / 'action-fixture.json', 90)
    source = HERE / 'offline.json'
    offline = json.loads(source.read_text())
    fixture_by_name = {f['Name']: f for f in offline['Fixtures']}
    tracks = {(r['Scene'],r['Planner'],r['Group']):r['Positions']
              for r in json.loads(source.with_suffix('.positions.json').read_text())}
    report = dict(OfflineSHA256=digest(source), PackageSHA256=digest(args.profile/'Mods/CompanionBridge.tmod'),
                  PositionSHA256=digest(source.with_suffix('.positions.json')),
                  WorldSHA256=digest(args.profile/'Worlds/test.wld'), Evidence='native_Player.Update', Replays=[], Rows=[], Controller=[])
    cache = {}
    for row in offline['Rows']:
        if key(row) not in tracks:
            continue
        scene, planner, group = key(row)
        fixture = fixture_by_name[scene]
        keys = []
        for segment in row['Route']:
            for run in segment['Inputs']:
                assert not run['Down']
                keys += [1+run['Direction']+3*run['Jump']]*run['Frames']
        positions = tracks[key(row)]
        signature = (scene, tuple(keys))
        if signature not in cache:
            print(f"Native input replay {scene} {planner}/{group} ({len(keys)} frames)", flush=True)
            evidence = await replay(args, fixture, keys, positions, fixture['Goal'])
            cache[signature] = (len(report['Replays']),positions)
            report['Replays'].append(dict(Scene=scene, **evidence))
        index, cached_positions = cache[signature]
        assert positions == cached_positions
        evidence = report['Replays'][index]
        report['Rows'].append(dict(Scene=scene,Planner=planner,Group=group,Replay=index,
            FoundRoute=row['FoundRoute'],ActuallyArrived=evidence['ActuallyArrived'],
            ExecutionDeviation=evidence['ExecutionDeviation'],RecoverySuccess=None,
            FailureClass='real_execution_failure' if row['FoundRoute'] and not evidence['ActuallyArrived'] else None))
    # Production entry point includes replanning, input ownership and recovery.
    for scene in dict.fromkeys(r['Scene'] for r in report['Rows']):
        f = fixture_by_name[scene]
        ready = await prepare(args.profile, f)
        dx,dy = (ready['originTileX']-100)*16,(ready['floor']-100)*16
        g = f['Goal']
        lua = 'api("navigate_to_landing_region",{minPlayerLeftX=%r,maxPlayerLeftX=%r,surfaceY=%r})' % (
            g['MinPlayerLeftX']+dx,g['MaxPlayerLeftX']+dx,g['SurfaceY']+dy)
        result = await action(args.port, lua)
        ev = result.get('evidence') or {}
        end = result.get('endEvidence') or {}
        assert result.get('status') in ('completed','failed') and end.get('inputsReleased'), result
        assert (end['worldSessionId'], end['actorGeneration'],end['actorSlot']) == (ready['world'],ready['generation'],ready['slot'])
        arrived = bool(result.get('completed') and all(ev.get(k) for k in ('arrived','settled','safeSupport','grounded'))
                       and g['MinPlayerLeftX'] <= ev['x']-dx <= g['MaxPlayerLeftX']
                       and abs(ev['y']-dy+42-g['SurfaceY'])<=.5)
        nav = ev.get('navigation') or {}
        report['Controller'].append(dict(Scene=scene,ActuallyArrived=arrived,Status=result['status'],
            Reason=result.get('reasonDetail'),RecoveryCount=nav.get('RecoveryCount'),
            RecoverySuccess=arrived if nav.get('RecoveryCount',0)>0 else None))
        print('Native controller',scene,arrived, flush=True)
    args.scenes = ['cloud_offset']
    await verify_cloud(args)
    disturbed = json.loads((args.profile/'extra-cloud_offset-receipt.json').read_text())
    a = disturbed.get('value') or disturbed['lastAction']
    nav = a['evidence']['navigation']
    report['RecoveryProbe'] = dict(Scene='cloud_offset', InjectionX=-32,
        ActuallyArrived=a['completed'], RecoveryCount=nav['RecoveryCount'],
        RecoveryReplans=nav['RecoveryReplans'], RecoverySuccess=a['completed'] and nav['RecoveryReplans']>0)
    # Restore basic gear before using the separate hook fixture.
    await prepare(args.profile, fixture_by_name['flat_trip'])
    args.scenes = ['no_hook','ledge']
    await verify_grapple(args)
    for mode in args.scenes:
        result = json.loads((args.profile/f'grapple-{mode}-receipt.json').read_text())
        a = result.get('value') or result['lastAction']
        report['Controller'].append(dict(Scene='hook_shaft' if mode=='ledge' else 'ordinary_shaft',
            ActuallyArrived=bool(a.get('completed')),Status=a['status'],Reason=a.get('reasonDetail')))
    native_witness = {r['Scene'] for r in report['Controller']+report['Rows'] if r['ActuallyArrived']}
    for row in report['Rows']:
        row['FalseNegative'] = False if row['FoundRoute'] else True if row['Scene'] in native_witness else None
    out = args.profile/'navigation-experiment.json'
    out.write_text(json.dumps(report,indent=2))
    print(out, flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port',type=int,required=True)
    parser.add_argument('--profile',type=Path,required=True)
    asyncio.run(verify(parser.parse_args()))
