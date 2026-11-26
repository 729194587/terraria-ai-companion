"""Validate and compare paired navigation trials. Run with an optional offline repeat."""
import argparse
import hashlib
import json
import math
from pathlib import Path

HERE = Path(__file__).resolve().parent


def load(path):
    return json.loads(path.read_text(encoding='utf-8'))


def key(row):
    return row['Scene'], row['Planner'], row['Config']['Group']


def collect(profile, destination):
    report = load(profile/'navigation-experiment.json')
    manifest = load(profile/'manifest.json')
    assert manifest['exitCode'] == 0 and manifest['packageSHA256'].lower() == report['PackageSHA256']
    for row in report['Replays']:
        trace = load(profile/row.pop('Trace'))
        assert trace['Native']['frames'] == row['Frames'] == len(trace['Inputs'])
        row['NativePositions'] = trace['Native']['positions']
    destination.write_text(json.dumps(report,ensure_ascii=False,separators=(',', ':')),encoding='utf-8')


def compare(offline, native, repeat):
    data, measured = load(offline), load(native)
    assert measured['OfflineSHA256'] == hashlib.sha256(offline.read_bytes()).hexdigest()
    rows = {key(r): r for r in data['Rows']}
    assert len(rows) == len(data['Rows'])
    if repeat:
        again = {key(r): r for r in load(repeat)['Rows']}
        assert again.keys() == rows.keys()
        for k, row in rows.items():
            if k[1] != 'production_local':
                assert row | {'SearchTime': 0} == again[k] | {'SearchTime': 0}, k
    positions_path = offline.with_suffix('.positions.json')
    assert measured['PositionSHA256'] == hashlib.sha256(positions_path.read_bytes()).hexdigest()
    tracks = {(t['Scene'],t['Planner'],t['Group']):t['Positions'] for t in load(positions_path)}
    results = {}
    for item in measured['Rows']:
        k = item['Scene'], item['Planner'], item['Group']
        row = rows[k]
        replay = measured['Replays'][item['Replay']]
        assert k not in results and row['FoundRoute'] == item['FoundRoute']
        assert item['ActuallyArrived'] == replay['ActuallyArrived'] and replay['Scene'] == k[0]
        assert replay['Frames'] == sum(a['Executed'] for a in row['Route'])
        observed = replay['NativePositions']
        dx,dy = replay['Origin']
        assert len(observed) == len(tracks[k])+1
        deviation = max(math.dist((x-dx,y-dy),p) for (x,y),p in zip(observed[1:],tracks[k]))
        assert abs(deviation-item['ExecutionDeviation']) < 1e-6, k
        results[k] = item
    # Exact intersection per comparison, including A2's ordinary-profile cohort.
    single_factor = {'A1': 'AllStances', 'A2-192': 'Range', 'A2-256': 'Range',
                     'A3': 'HoldStep', 'A4': 'RollY', 'budget_reference': 'LocalBudget'}
    print('planner group n | complete A0/group | native A0/group | added model/native | candidates A0/group | expansions A0/group | search ms A0/group')
    for planner in ('surface', 'local'):
        for group, field in single_factor.items():
            pairs = [(rows[s, p, 'A0'], rows[s, p, g]) for s,p,g in results if p == planner and g == group]
            if not pairs:
                continue
            for a,b in pairs:
                changes = {k for k in a['Config'] if a['Config'][k] != b['Config'][k]}
                assert changes == {'Group', field}, (key(b), changes)
            def pair_sum(get):
                return tuple(sum(get(pair[i]) for pair in pairs) for i in (0,1))
            def native_arrival(r):
                return results[key(r)]['ActuallyArrived']
            def added(get):
                return sum(not get(a) and get(b) for a,b in pairs)
            def fmt(pair):
                return '/'.join(str(round(v,1)) for v in pair)
            print(planner,group,len(pairs),'|',fmt(pair_sum(lambda r:r['FoundRoute'])),'|',
                  fmt(pair_sum(native_arrival)),'|',fmt((added(lambda r:r['FoundRoute']),added(native_arrival))),'|',
                  fmt(pair_sum(lambda r:r['CandidateCount'])),'|',fmt(pair_sum(lambda r:r['ExpandedStates'])),'|',
                  fmt(pair_sum(lambda r:r['SearchTime'])))
    for row in rows.values():
        assert row['FoundRoute'] == row['ModelArrived'] == (row['RouteKind'] == 'complete')
        assert row['ActuallyArrived'] is None
        for edge in row['Route']:
            assert 0 < edge['Executed'] <= edge['Validated']
            assert sum(r['Frames'] for r in edge['Inputs']) == edge['Executed']
    assert rows['unknown','local','A0']['FailureClass'] == 'insufficient_coverage'
    assert rows['air_start','air_task_gate','A0']['FailureClass'] == 'unsupported_model'
    assert rows['buried_goal','local','A0']['FailureClass'] == 'physically_unreachable_fixed_geometry'
    assert rows['precision','local','A3']['FoundRoute'] and not rows['precision','local','A0']['FoundRoute']
    assert rows['wing_window','local','A4']['FoundRoute'] and not rows['wing_window','local','A0']['FoundRoute']


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--offline',type=Path,default=HERE/'offline.json')
    parser.add_argument('--native',type=Path,default=HERE/'native.json')
    parser.add_argument('--repeat',type=Path)
    parser.add_argument('--profile',type=Path,help='collect the raw native traces from a completed server run')
    args = parser.parse_args()
    if args.profile:
        collect(args.profile,args.native)
    compare(args.offline,args.native,args.repeat)
