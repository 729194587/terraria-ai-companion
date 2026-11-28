"""Exercise shared exploration on a fresh native profile. No fixture waypoints."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
import time
from types import SimpleNamespace

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
import atomic_operations as atomic
from autonomous_mode import AutonomousMode
from control import autonomy_version
import directional_explorer as explorer
from server_ai import ServerAiBridge


async def measure(args, report, bridge, control, deadline):
    mode = AutonomousMode(SimpleNamespace(bridge=bridge),control)
    await mode.command('on')
    mode.version = (await mode.control_request('grant'))['version']
    assert mode.version
    marker = autonomy_version.set(mode.version)
    mode.objective = 'Survey nearby unexplored terrain'
    try:
        while time.monotonic()<deadline:
            baseline = await mode.resource_scan(mode.version)
            if args.algorithm=='directional':
                receipt = await explorer.run_directional(mode,'right',mode.version,baseline)
            else:
                options={} if args.cave else {'direction':'right'}
                if args.cave and (args.profile/'maze.json').exists() and 'investigation' not in report:
                    options['seek']='Terraria/Copper'
                receipt = await mode.act('explore',options,mode.version,baseline=baseline)
            trip = receipt['explorationResult']
            report['trips'].append(trip)
            if args.cave and trip.get('seek')=='Terraria/Copper' and trip['returnReason']=='discovery':
                ore=next((f for f in explorer.brief(trip)['findings'] if f.get('targetTile')),None)
                if ore:
                    proof=await mode.act('mine_target',ore['targetTile'],mode.version)
                    report['investigation']=dict(target=ore['targetTile'],receipt=mode.receipt_fields(proof))
                    if proof.get('inputsReleased') is not True:
                        report['stop']='investigation release unknown'
                        break
            save(args,report)
            reason=trip['returnReason']
            if not receipt['inputsReleased'] or reason in ('interrupted','outcome_unknown') or (
                    reason=='not_admitted' and trip['reason']!='action_busy'):
                report['stop']=reason
                break
            if reason not in ('budget_reached','discovery'):
                # Give an intervening reflex time to finish before a fresh tool call.
                await asyncio.sleep(min(5,max(0,deadline-time.monotonic())))
        if mode.mailbox:
            report['tasks']=[r['view']['result'] for r in mode.mailbox.records.values() if r['done'].is_set()]
    finally:
        autonomy_version.reset(marker)
        await mode.command('off')
        report['release']=await mode.control_request('status')


async def driver_run(args, report, bridge, control, deadline):
    from test_external_brain_stdio import StdioClient, ChatHost
    import threading
    seat=args.seat.resolve()
    seat.mkdir(parents=True,exist_ok=False)
    chat=ChatHost()
    threading.Thread(target=chat.serve_forever,daemon=True).start()
    client=StdioClient()
    child=await asyncio.create_subprocess_exec(sys.executable,'-B','python/start_agent.py','--server-ai',
        '--external-brain','--mcp-transport','stdio','--port',str(chat.server_address[1]),'--ai-port',str(args.port),
        '--log-file',str(args.profile/'driver-events.jsonl'),stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE,limit=2**21)
    async def call(name,arguments):
        request=await client.send(child,'tools/call',dict(name=name,arguments=arguments))
        msg=json.loads(await asyncio.wait_for(child.stdout.readline(),220))
        assert msg['id']==request,msg
        value=msg['result']
        return value.get('structuredContent',value)
    try:
        await client.initialize(child)
        (seat/'ready').touch()
        while time.monotonic()<deadline:
            path=next(seat.glob('*.request'),None)
            if not path:
                await asyncio.sleep(.2)
                continue
            req=json.loads(path.read_bytes())
            name=req['tool']
            if name=='close':
                reply={'closing':True}
            elif name=='list':
                reply=[dict(name=t['name'],description=t['description']) for t in
                       (await client.request(child,'tools/list'))['tools']]
            else:
                reply=await call(name,req['args'])
                if name=='explore':
                    trace=await call('exploration_status',{'lastTrip':True})
                    if trace.get('lastTrip'):
                        report['trips'].append(trace['lastTrip'])
            report['decisions'].append(dict(tool=name,args=req['args'],response=reply))
            tmp=path.with_suffix('.tmp')
            tmp.write_text(json.dumps(reply,ensure_ascii=False),encoding='utf-8')
            tmp.replace(path.with_suffix('.response'))
            path.unlink()
            save(args,report)
            if name=='close':
                report['stop']='driver finished'
                break
    finally:
        (seat/'closed').touch()
        child.stdin.close()
        await asyncio.wait_for(child.communicate(),25)
        await asyncio.to_thread(chat.shutdown)
        chat.server_close()
        report['release']=await asyncio.to_thread(control.control_autonomy,'status')
        assert child.returncode==0


def save(args,report):
    (args.profile/'natural-results.json').write_text(json.dumps(report,ensure_ascii=False),encoding='utf-8')


async def main(args):
    marker=args.profile/('action-fixture.json' if args.cave else 'exploration-field.json')
    async with asyncio.timeout(30):
        while not marker.is_file():
            await asyncio.sleep(.1)
    observer=ServerAiBridge(args.port)
    if args.algorithm=='driver':
        bridge=control=observer
    else:
        bridge,control=ServerAiBridge(args.port),ServerAiBridge(args.port)
    start=time.monotonic()
    report=dict(algorithm=args.algorithm,budget=args.seconds,fixture=json.loads(marker.read_bytes()),
                trips=[],trajectory=[],decisions=[],stop='checkpoint')
    async def sample():
        while True:
            r=await atomic.dispatch(observer,'get_player_state',{})
            if r['valid']:
                p=r['player']
                report['trajectory'].append([round(time.monotonic()-start,2),r['gameUpdate'],
                    p['position']['x'],p['position']['y']+r['motion']['playerHeight'],p['life']])
            await asyncio.sleep(1)
    probe=asyncio.create_task(sample())
    async def visits():
        return (await atomic.dispatch(observer,'query_map',dict(mode='region',width=1,height=1,
                                    cellSize=1,includeVisits=True)))['data']['visitedCount']
    try:
        report['initialVisits']=await visits()
        await (driver_run if args.algorithm=='driver' else measure)(args,report,bridge,control,start+args.seconds)
        report['finalVisits']=await visits()
        if probe.done():
            probe.result()
        assert report['release']['released'],report['release']
    except Exception as error:
        report['stop']='error'
        report['error']=str(error) or type(error).__name__
        raise
    finally:
        probe.cancel()
        await asyncio.gather(probe,return_exceptions=True)
        report['seconds']=round(time.monotonic()-start,2)
        save(args,report)
        for conn in {bridge,control,observer}:
            conn.close()
    if args.cave:
        from exploration_evidence import check, project
        check(project(args.profile))


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--profile',required=True,type=Path)
    parser.add_argument('--port',required=True,type=int)
    parser.add_argument('--seconds',required=True,type=int)
    parser.add_argument('--algorithm',choices=('v2','directional','driver'),required=True)
    parser.add_argument('--seat',type=Path)
    parser.add_argument('--cave',action='store_true')
    asyncio.run(main(parser.parse_args()))
