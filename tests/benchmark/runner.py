"""Phase 1 native benchmark. The existing PowerShell harness owns each server."""
import argparse
import asyncio
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import time
import uuid

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(ROOT / 'python'))
from lua_runtime import Limits, LuaRuntime
from server_ai import ServerAiBridge

VARIANTS = ('solution', 'noop')
METRICS = ('gameTicks', 'wallSeconds', 'luaCalls', 'atomicActions', 'navigationChildren', 'miningChildren')


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def write(path, value):
    temp = path.with_suffix('.tmp')
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    temp.replace(path)


def load_manifest(path):
    document = read(path)
    if document.get('version') != 1 or set(document.get('metrics', ())) != set(METRICS):
        raise ValueError('Unsupported manifest version or metrics')
    if set(document['budget']) != {'actions', 'calls', 'seconds'}:
        raise ValueError('Invalid Lua budget fields')
    Limits(**document['budget']).validate()
    defaults = document['setup']
    box = defaults.get('arena')
    if (not isinstance(box, list) or len(box) != 4 or any(type(v) is not int for v in box)
            or not (-32 <= box[0] < 0 and -32 <= box[1] < -4 and 2 < box[2] <= 32 and 0 < box[3] <= 16)
            or (defaults.get('floorType'), defaults.get('targetType'), defaults.get('itemType'), defaults.get('selectedSlot')) != (1, 7, 3509, 0)):
        raise ValueError('Invalid Phase 1 arena or loadout')
    registered = {}
    for row in document['scenarios']:
        key = row['id']
        if not re.fullmatch('[a-z][a-z0-9_]*', key) or key in registered:
            raise ValueError(f'Invalid or duplicate scenario id: {key}')
        if any(not isinstance(row[k], str) or not row[k].strip() for k in ('description', 'opening')):
            raise ValueError(f'{key}: missing description or opening')
        source = Path(row['solutionLua'])
        if source.name != str(source) or source.suffix != '.lua':
            raise ValueError(f'{key}: solution must be a sibling Lua file')
        targets = row['setup']['targets']
        if not 1 <= len(targets) <= 18 or any(
            len(cell) != 2 or any(type(v) is not int for v in cell)
            or not (box[0] < cell[0] < box[2] and box[1] < cell[1] < 0) for cell in targets
        ):
            raise ValueError(f'{key}: invalid targets')
        remaining = {tuple(cell) for cell in targets}
        if len(remaining) != len(targets):
            raise ValueError(f'{key}: duplicate targets')
        frontier = [remaining.pop()]
        while frontier:
            x, y = frontier.pop()
            for neighbor in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
                if neighbor in remaining:
                    remaining.remove(neighbor)
                    frontier.append(neighbor)
        checks = row['checks']
        if (remaining or set(checks) != {'minSuccessfulNavigation', 'minStanceDeltaPixels', 'initialReach'}
                or any(type(checks[k]) is not int or checks[k] < 0 for k in ('minSuccessfulNavigation', 'minStanceDeltaPixels'))
                or checks['initialReach'] not in ('all', 'none', 'partial')):
            raise ValueError(f'{key}: disconnected targets or invalid checks')
        registered[key] = {**row, 'setup': {**defaults, 'targets': targets}, 'budget': document['budget'],
                           'metrics': document['metrics'], 'source': (path.parent / source).read_text(encoding='utf-8')}
    if not registered:
        raise ValueError('No registered scenarios')
    return registered


def points(rows):
    return {(row['x'], row['y']) for row in rows}


def validate_initial(spec, initial):
    targets = initial['layout']['targets']
    n = len(spec['setup']['targets'])
    reach = initial['reachable']
    expected_reach = {'all': reach == n, 'none': reach == 0, 'partial': 0 < reach < n}
    origin = initial['layout']['origin']
    expected = {(origin['x'] + x, origin['y'] + y) for x, y in spec['setup']['targets']}
    if (len(targets) != n or len(points(targets)) != n or any(not t['present'] or t['type'] != 7 for t in targets)
            or points(targets) != expected or initial['modVersion'] != spec['modVersion']
            or initial['objective'] is not False or initial['guardsPassed'] is not True
            or not expected_reach[spec['checks']['initialReach']] or not re.fullmatch('[0-9A-F]{64}', initial['fingerprint'])):
        raise ValueError('Initial objective, target snapshot, reach or guards did not match the scenario')


def evaluate(spec, variant, binding, initial, final, lua, wall):
    targets = points(initial['layout']['targets'])
    native_proof = final['nativeProof']
    native_nav = [n for n in final['navigation'] if n['status'] == 'completed' and n['settled'] and n['Updates'] > 0]
    mine = lua.get('lastAction') or {}
    task = mine.get('task') or {}
    receipt = task.get('result') or {}
    children = (task.get('progress') or {}).get('children', [])
    nav_children = [c for c in children if c['taskName'] == 'navigate_to_landing_region']
    mine_children = [c for c in children if c['taskName'] in ('mine_tile', 'excavate_tiles')]
    final_points = points(final['targets'])
    if final_points != targets or len(final['targets']) != len(targets) or final['gameUpdate'] < initial['gameUpdate']:
        raise ValueError('Invalid final world snapshot')
    objective = {
        'targetsAbsent': all(not t['present'] for t in final['targets']),
        'nativeProofMatchesTargets': len(native_proof) == len(targets) and points(native_proof) == targets
            and all(p['type'] == 7 for p in native_proof),
        'nativeMiningCompleted': bool(final['mining']) and all(m['status'] == 'Completed' for m in final['mining']),
        'successfulNavigation': len(native_nav) >= spec['checks']['minSuccessfulNavigation'],
        'minedAfterNavigation': not spec['checks']['minSuccessfulNavigation'] or any(
            n['tick'] is not None and any(p['tick'] >= n['tick'] for p in native_proof) for n in native_nav),
        'effectiveStanceChange': final['stanceDelta'] >= spec['checks']['minStanceDeltaPixels'],
    }
    guards = {'alive': final['alive'], 'healthUnchanged': final['healthUnchanged'], 'inputsReleased': final['released'],
              'unrelatedTerrainUnchanged': final['guardDigest'] == initial['layout']['guardDigest']}
    execution = {'luaCompleted': lua.get('status') == 'completed'}
    if variant == 'solution':
        execution.update(
            terminalReceipt=mine.get('valid') is True and mine.get('completed') is True and mine.get('status') == 'completed'
            and mine.get('endEvidenceStatus') == 'known' and receipt.get('effectVerified') is True
            and receipt.get('inputsReleased') is True and task.get('currentChild') is None,
            boundReceipt=task.get('worldSessionId') == binding['world'] and task.get('actorGeneration') == binding['generation']
            and task.get('actorSlot') == binding['slot'] and mine.get('actorId') == binding['actor'],
            taskTargetsMatch=points((receipt.get('evidence') or {}).get('targets', ())) == targets,
            nativeNavigationReceipts=sum(c.get('status') == 'completed' and (c.get('result') or {}).get('effectVerified') is True
                                         for c in nav_children) >= spec['checks']['minSuccessfulNavigation'])
    else:
        execution.update(noActions=lua.get('actions') == 0 and not final['mining'] and not final['navigation'],
                         targetsUnchanged=final['targets'] == initial['layout']['targets'])
    satisfied = all(objective.values())
    accepted = satisfied if variant == 'solution' else not satisfied
    failures = [k for section in (guards, execution) for k, ok in section.items() if not ok]
    if not accepted:
        failures.extend([k for k, ok in objective.items() if not ok] if variant == 'solution' else ['noopSatisfiedObjective'])
    return dict(status='passed' if not failures else 'failed', failures=failures, objectiveSatisfied=satisfied,
                checks=dict(objective=objective, guard=guards, execution=execution),
                fingerprint=initial['fingerprint'], metrics=dict(
                    gameTicks=final['gameUpdate'] - initial['gameUpdate'], wallSeconds=round(wall, 3),
                    luaCalls=lua.get('calls'), atomicActions=lua.get('actions'),
                    navigationChildren=len(nav_children), miningChildren=len(mine_children)))


async def exchange(profile, message):
    reply = profile / 'benchmark-response.json'
    message = dict(message, nonce=uuid.uuid4().hex)
    write(profile / 'benchmark-request.json', message)
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if reply.exists():
            response = read(reply)
            if all(response.get(k) == message[k] for k in ('run', 'scenario', 'phase', 'nonce', 'binding')):
                if response['error']:
                    raise RuntimeError(response['error'])
                return response['evidence']
        await asyncio.sleep(.03)
    raise TimeoutError(f"No bound fixture response for {message['phase']}")


async def execute_lua(bridge, profile, source, budget):
    (profile / 'executed.lua').write_text(source, encoding='utf-8')
    return await LuaRuntime(bridge, evidence_root=profile / 'lua').execute(source, limits=Limits(**budget))


async def attempt(args):
    profile = args.profile
    spec = read(args.spec)
    record = {k: spec[k] for k in ('run', 'scenario', 'variant', 'repeat', 'commit', 'modVersion', 'engineProfile')}
    status = 'setup_error'
    start = time.monotonic()
    bridge = None
    try:
        bridge = ServerAiBridge(args.port)
        if bridge.binding != read(profile / 'benchmark-binding.json'):
            raise RuntimeError('Python connected to a different actor than the native harness')
        record['binding'] = bridge.binding
        record['capabilities'] = bridge.hello['capabilities']
        if 'server_ai_excavation_v1' not in record['capabilities']:
            raise RuntimeError('Server lacks the excavation capability')
        request = {**{k: record[k] for k in ('run', 'scenario', 'binding')}, 'phase': 'setup', 'setup': spec['setup']}
        initial = await exchange(profile, request)
        record['initial'] = initial
        record['fingerprint'] = initial['fingerprint']
        validate_initial(spec, initial)
        status = 'evaluation_error'
        source = spec['source'] if spec['variant'] == 'solution' else 'return true'
        start = time.monotonic()
        lua = await execute_lua(bridge, profile, source, spec['budget'])
        wall = time.monotonic() - start
        record['lua'] = lua
        final = await exchange(profile, {**request, 'phase': 'evaluate'})
        record['final'] = final
        record.update(evaluate(spec, spec['variant'], bridge.binding, initial, final, lua, wall))
    except Exception as error:
        record.update(status=status, failures=[f'{type(error).__name__}: {error}'])
    finally:
        if bridge:
            bridge.close()
        write(profile / 'benchmark-attempt.json', record)


def summary(rows):
    lines = ['# Terraria Benchmark Phase 1', '', 'Solution runs a scripted Lua baseline. No-op is the negative control.', '',
             '| Scenario | Solution passed | No-op passed | Initial fingerprint | Navigation children | Mining children |',
             '| --- | ---: | ---: | --- | ---: | ---: |']
    for scenario in sorted({r['scenario'] for r in rows}):
        group = [r for r in rows if r['scenario'] == scenario]
        counts = [f"{sum(r['status'] == 'passed' for r in group if r['variant'] == v)}/{sum(r['variant'] == v for r in group)}"
                  for v in VARIANTS]
        fingerprints = [r.get('fingerprint') for r in group]
        present = set(filter(None, fingerprints))
        consistency = ('consistent' if len(present) == 1 and all(fingerprints) else
                       'mismatch' if len(present) > 1 else 'missing')
        totals = [sum(r.get('metrics', {}).get(k, 0) for r in group) for k in ('navigationChildren', 'miningChildren')]
        lines.append(f"| {scenario} | {counts[0]} | {counts[1]} | {consistency} | {totals[0]} | {totals[1]} |")
    counts = Counter(r['status'] for r in rows)
    lines += ['', 'Results by status', '', *(f'- {status} {counts[status]}' for status in
                                            ('passed', 'failed', 'setup_error', 'evaluation_error')), '',
              'Failures and anomalies', '']
    anomalies = [f"- {r['scenario']} {r['variant']} {r['repeat']} {reason}" for r in rows for reason in r['failures']]
    for scenario in {r['scenario'] for r in rows}:
        if len({r['fingerprint'] for r in rows if r['scenario'] == scenario and r.get('fingerprint')}) > 1:
            anomalies.append(f'- {scenario} initial fingerprints differ')
    return '\n'.join(lines + (anomalies or ['- None observed.']) + [''])


def run(args):
    scenarios = load_manifest(args.manifest)
    selected = scenarios if not args.scenario else {key: scenarios[key] for key in args.scenario}
    run_id = time.strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:8]
    directory = args.output.resolve() / run_id
    attempts = directory / 'attempts'
    logs = directory / 'logs'
    attempts.mkdir(parents=True)
    logs.mkdir()
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    dirty = bool(subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=no'], cwd=ROOT, text=True).strip())
    mod_version = re.search(r'^version\s*=\s*(\S+)', (ROOT / 'CompanionBridge/build.txt').read_text(), re.M)[1]
    catalog = read(ROOT / 'CompanionBridge/EngineProfiles.json')
    engine = args.engine_profile or catalog['default']
    tml = catalog['profiles'][engine]['tml']
    package = args.package.resolve(strict=True)
    package_sha = hashlib.sha256(package.read_bytes()).hexdigest()
    world = args.world.resolve(strict=True)
    source_files = [world] + ([world.with_suffix('.twld')] if world.with_suffix('.twld').exists() else [])
    hashes = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in source_files}
    rows = []
    for scenario, definition in selected.items():
        for variant in args.variant or VARIANTS:
            for repeat in range(1, args.repeat + 1):
                name = f'{scenario}-{variant}-{repeat:03d}'
                profile = logs / name
                metadata = dict(run=run_id, scenario=scenario, variant=variant, repeat=repeat,
                                commit=commit, dirty=dirty, modVersion=mod_version, tModLoaderVersion=tml,
                                engineProfile=engine, packageSHA256=package_sha)
                spec_path = attempts / f'{name}.spec.json'
                write(spec_path, {**definition, **metadata})
                command = ['pwsh', '-NoProfile', '-File', str(ROOT / 'tests/verify_server_ai_native.ps1'),
                           '-BenchmarkSpec', str(spec_path), '-BenchmarkProfile', str(profile),
                           '-PackagePath', str(package), '-WorldPath', str(world), '-EngineProfile', engine,
                           '-Port', str(args.port), '-IpcPort', str(args.ipc_port), '-PythonExecutable', sys.executable]
                if args.tml_path:
                    command.extend(['-TModLoaderPath', str(args.tml_path.resolve())])
                started = time.monotonic()
                with (logs / f'{name}.launcher.log').open('wb') as log:
                    process = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
                raw = profile / 'benchmark-attempt.json'
                record = read(raw) if raw.exists() else dict(status='setup_error', failures=['Native harness failed before attempt evidence'])
                if process.returncode and raw.exists():
                    record.update(status='evaluation_error', failures=[*record['failures'], 'Native harness teardown failed'])
                if any(hashlib.sha256(Path(p).read_bytes()).hexdigest() != digest for p, digest in hashes.items()):
                    record.update(status='evaluation_error', failures=[*record['failures'], 'Source world changed'])
                evidence = attempts / f'{name}.json'
                row = {k: record[k] for k in ('status', 'failures', 'objectiveSatisfied', 'checks', 'fingerprint', 'metrics') if k in record}
                row.update(metadata, evidence=str(evidence.relative_to(directory)),
                           logs=str(profile.relative_to(directory)), harnessWallSeconds=round(time.monotonic() - started, 3))
                write(evidence, {**record, **row, 'sourceWorldHashes': hashes})
                rows.append(row)
                with (directory / 'runs.jsonl').open('a', encoding='utf-8') as report:
                    report.write(json.dumps(row, ensure_ascii=False) + '\n')
                (directory / 'summary.md').write_text(summary(rows), encoding='utf-8')
                print(f'{name}: {row["status"]}', flush=True)
    print(directory, flush=True)
    inconsistent = any(len({r.get('fingerprint') for r in rows if r['scenario'] == key}) > 1 for key in selected)
    return int(inconsistent or any(r['status'] != 'passed' for r in rows))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    suite = commands.add_parser('run')
    suite.add_argument('--manifest', type=Path, default=HERE / 'scenarios.json')
    suite.add_argument('--scenario', action='append')
    suite.add_argument('--variant', action='append', choices=VARIANTS)
    suite.add_argument('--repeat', type=int, choices=range(1, 101), default=1)
    suite.add_argument('--output', type=Path, default=ROOT / '.local/benchmark/runs')
    suite.add_argument('--package', type=Path, required=True)
    suite.add_argument('--world', type=Path, default=ROOT / '.local/virtual-player-control-host/Worlds/test.wld')
    suite.add_argument('--engine-profile')
    suite.add_argument('--tml-path', type=Path)
    suite.add_argument('--port', type=int, default=28021)
    suite.add_argument('--ipc-port', type=int, default=28022)
    leaf = commands.add_parser('attempt')
    leaf.add_argument('--port', type=int, required=True)
    leaf.add_argument('--profile', type=Path, required=True)
    leaf.add_argument('--spec', type=Path, required=True)
    args = parser.parse_args()
    if args.command == 'attempt':
        asyncio.run(attempt(args))
        return 0
    return run(args)


if __name__ == '__main__':
    sys.exit(main())
