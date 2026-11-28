"""Export compact, replayable evidence from preserved native exploration profiles."""
import argparse
import gzip
import hashlib
import json
from pathlib import Path


def read(path):
    return json.loads(path.read_bytes())


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def fields(data, names):
    return {k:data[k] for k in names if k in data}


def project(profile):
    raw=profile/'natural-results.json'
    run=read(raw)
    row=fields(run,['algorithm','budget','seconds','stop','error','release','fixture','trajectory',
                    'initialVisits','finalVisits','investigation'])
    if 'error' in row:
        row['stop']='error'
    row.update(profile=profile.name,rawSha256=digest(raw),packageSha256=digest(profile/'Mods/CompanionBridge.tmod'))
    if (profile/'source.wld').exists():
        row['initialWorldSha256']=digest(profile/'source.wld')
    for name in ('maze','divider'):
        if (profile/(name+'.json')).exists():
            row[name]=read(profile/(name+'.json'))
    row['trips']=[]
    for trip in run['trips']:
        t=fields(trip,['start','finish','direction','seek','returnReason','reason','progress','segments'])
        t['findings']=[fields(item,['kind','contentKey','anchor','state','tiles']) for event in trip['findings']
                       for item in event['items']]
        row['trips'].append(t)
    row['tasks']=[]
    for receipt in run.get('tasks',[]):
        task=receipt.get('task') or {}
        progress=task.get('progress') or {}
        evidence=(task.get('result') or {}).get('evidence') or {}
        row['tasks'].append(dict(**fields(receipt,['actionId','operation','status','reason','endEvidence',
                                                   'inputsAcquired','inputsReleased','dispatchState','targetTask']),
            edits=fields(progress,['dugTiles','placedTiles','verifiedTerrainActions']),
            arrival=fields(evidence,['arrived','x','feetY','grounded','safeSupport','settled','destroyedByActor',
                                     'targetAbsent','X','Y','TileType'])))
    row['decisions']=[]
    for decision in run.get('decisions',[]):
        reply=decision['response']
        step=fields(decision,['tool','args'])
        if isinstance(reply,dict):
            step.update(fields(reply,['status','actionState','reason','closing','value']))
            if reply.get('lastAction'):
                step['lastAction']=fields(reply['lastAction'],['operation','reason','actionId','inputsAcquired',
                                                               'inputsReleased','dispatchState','endEvidenceStatus'])
        row['decisions'].append(step)
    return row


def stats(row):
    segments=[s for t in row['trips'] for s in t['segments']]
    receipts=[s.get('receipt',{}) for s in segments]
    track=row['trajectory']
    x0,y0=track[0][2:4]
    return dict(calls=len(row['trips']),segments=len(segments),
                reached=sum(r.get('status')=='completed' for r in receipts),
                released=sum(r.get('inputsReleased') is True for r in receipts),
                refused=sum(r.get('dispatchState')=='not_admitted' for r in receipts),
                investigated=sum(s.get('surveyed') is True for s in segments),
                newFootTiles=row['finalVisits']-row['initialVisits'] if 'finalVisits' in row else None,
                maxHorizontal=round(max(abs(p[2]-x0) for p in track)/16,1),
                maxDescent=round(max(p[3]-y0 for p in track)/16,1))


def check(row):
    segments=[s for t in row['trips'] for s in t['segments']]
    ids=set()
    for s in segments:
        proof=s.get('receipt',{})
        if proof.get('dispatchState')=='not_admitted':
            assert not proof.get('actionId') and not proof.get('inputsAcquired'),s
            continue
        assert s['taskId'] not in ids and proof.get('actionId')==s['taskId'],s
        ids.add(s['taskId'])
        assert proof.get('inputsReleased') is True and proof.get('endEvidenceStatus')=='known',s
    assert row['release']['released'] and row['trajectory'],row['profile']
    if 'seed' not in row['fixture'] and 'maze' not in row and 'divider' not in row:
        assert stats(row)['maxDescent']>=8 and any(s.get('surveyed') for s in segments),row['profile']
    if 'maze' in row:
        maze=row['maze']
        assert row['investigation']['receipt']['reason']=='mine_target_verified'
        for zone in ('lowerEast','upperEast','upperWest'):
            left,right,y=maze[zone]
            assert any(left<=(p[2]+10)/16-maze['originX']<=right and abs(p[3]/16-maze['floor']-y)<.1
                       for p in row['trajectory']),zone
        assert any(s.get('source')=='saved_landing' and s.get('surveyed') for s in segments)
    if 'divider' in row:
        wall=row['divider']['wallX']
        edited={t['actionId'] for t in row['tasks'] if t['edits'].get('dugTiles',0)>0}
        assert segments[0]['natural'] and segments[0]['surveyed']
        assert any(s.get('surveyed') and not s['natural'] and s['taskId'] in edited
                   and (s['finish']['leftX']+10)/16>wall+1 for s in segments)


def check_pairs(runs):
    pairs={}
    for row in runs:
        seed=row['fixture'].get('seed')
        if seed and row['algorithm'] in ('directional','v2'):
            pairs.setdefault((seed,row['packageSha256']),{})[row['algorithm']]=row
    for pair in pairs.values():
        if 'directional' in pair and 'v2' in pair:
            a,b=pair['directional'],pair['v2']
            assert a['initialWorldSha256']==b['initialWorldSha256']
            assert a['fixture']==b['fixture'] and a['budget']==b['budget']
            assert a['trajectory'][0][2:]==b['trajectory'][0][2:]


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive',type=Path)
    parser.add_argument('profiles',nargs='*',type=Path)
    args=parser.parse_args()
    if args.profiles:
        runs=[project(p) for p in args.profiles]
        payload=json.dumps(runs,ensure_ascii=False,separators=(',',':')).encode()
        args.archive.write_bytes(gzip.compress(payload,mtime=0))
    runs=json.loads(gzip.decompress(args.archive.read_bytes()))
    check_pairs(runs)
    for run in runs:
        check(run)
        print(run['profile'],run['algorithm'],run['stop'],stats(run))
