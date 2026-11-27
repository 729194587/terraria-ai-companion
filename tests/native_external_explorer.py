"""Scripted MCP client, real external-only stdio host and copied-world dedicated server."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
import threading

from test_server_ai_agent import ChatHost
from test_external_brain_stdio import StdioClient


async def verify_cave(client, child, report):
    async def lua(source):
        reply = await client.request(child,'tools/call',dict(name='execute_lua',arguments=dict(source=source)))
        assert not reply['isError'], reply
        return reply['structuredContent']['value']

    async def survey():
        return await lua("local s=api('observe_scene',{layers={'landingRegions'},includePassages=true}); return s.data.passages")

    async def go(point):
        task = await lua("return api('submit_task',{operation='navigate_to',arguments={x=%d,y=%d}})" % (point['x'],point['y']))
        async with asyncio.timeout(48):
            while task['status'] in ('running','submitting'):
                task = await lua("return api('await_task',{taskId='%s',milliseconds=1000})" % task['taskId'])
        proof = task['result']
        actual = (await survey())['returnTarget']
        report.setdefault('visits',[]).append(dict(target=point,actual=actual,status=proof['status'],released=proof['inputsReleased']))
        assert proof['completed'] and proof['inputsReleased'] and actual and abs(actual['x']-point['x']) <= 11 and actual['y']==point['y'], report['visits']

    entry = await survey()
    report['entry'] = entry
    assert any(e['kind']=='possible_cave_entrance' for e in entry['exits']), entry
    for _ in range(5):
        request_id = await client.send(child,'tools/call',dict(name='explore',arguments=dict(direction='down',objective='investigate the cave opening',seek='passage')))
        msg = json.loads(await asyncio.wait_for(child.stdout.readline(),60))
        assert msg['id']==request_id and not msg['result']['isError'], msg
        trip = msg['result']['structuredContent']['explorationResult']
        report['trips'].append(trip)
        if trip['finish']['feetY'] >= entry['returnTarget']['y']+6*16:
            break
    else:
        raise AssertionError('No verified descent into the lower tunnel')
    underground = await survey()
    report['underground'] = underground
    assert any(seg['receipt']['status']=='completed' and seg['receipt']['inputsReleased']
               for trip in report['trips'] for seg in trip['segments'])
    # Select offered arms and a remembered return point. No hidden fixture coordinates.
    forks = [s for trip in report['trips'] for s in trip['passageMemory'] if s['kind']=='junction']
    if underground['junction']:
        forks.append(underground)
    fork = next(s for s in reversed(forks) if {'left','right'} <= {e['direction'] for e in s['exits']})
    for direction in ('left','right'):
        await go(fork['returnTarget'])
        await go(next(e['target'] for e in fork['exits'] if e['direction']==direction))


async def verify(port, profile, cave=False):
    assert (profile / 'isolated-action-fixture').is_file()
    async with asyncio.timeout(30):
        while not (profile / 'action-fixture.json').exists():
            await asyncio.sleep(.1)
    # Use the same wire client as the stdio regression suite. Only chat is simulated.
    client = StdioClient()
    chat = ChatHost()
    threading.Thread(target=chat.serve_forever, daemon=True).start()
    log = profile / 'external-host.jsonl'
    report = {'trips': []}
    try:
        child = await asyncio.create_subprocess_exec(
            sys.executable, '-B', 'python/start_agent.py', '--server-ai', '--external-brain',
            '--mcp-transport', 'stdio', '--port', str(chat.server_address[1]), '--ai-port', str(port),
            '--log-file', str(log), stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE,
            limit=2**20)
        try:
            await client.initialize(child)
            tools = await client.request(child, 'tools/list')
            assert 'explore' in [tool['name'] for tool in tools['tools']]
            if cave:
                await verify_cave(client,child,report)
                child.stdin.close()
                await asyncio.wait_for(child.communicate(),15)
                assert child.returncode == 0
                return
            args = dict(direction='right', objective='survey the eastern corridor')
            call = dict(name='explore', arguments=args)
            for _ in range(8):
                request_id = await client.send(child, 'tools/call', call)
                response = json.loads(await asyncio.wait_for(child.stdout.readline(), 60))
                assert response['id'] == request_id and not response['result']['isError'], response
                trip = response['result']['structuredContent']['explorationResult']
                report['trips'].append(trip)
                if any(hit['kind'] == 'ore_component' for finding in trip['findings'] for hit in finding['items']):
                    break
                assert trip['returnReason'] in ('observation_incomplete', 'budget_reached', 'discovery'), trip
            else:
                raise AssertionError('No ore discovered during bounded calls')
            segments = [s for t in report['trips'] for s in t['segments']]
            assert len(segments) >= 2 and all(s['receipt']['inputsReleased'] for s in segments)
            ore = next(hit for finding in trip['findings'] for hit in finding['items'] if hit['kind'] == 'ore_component')
            x, y = ore['anchor']['x'], ore['anchor']['y']

            async def lua(source):
                reply = await client.request(child, 'tools/call', dict(name='execute_lua', arguments=dict(source=source)))
                assert not reply['isError'], reply
                return reply['structuredContent']['value']

            for operation, target in [('navigate_to', f'x={x*16-24},y={y*16}'), ('mine_target', f'x={x},y={y}')]:
                task = await lua(f"return api('submit_task',{{operation='{operation}',arguments={{{target}}}}})")
                async with asyncio.timeout(45):
                    while task['status'] in ('submitting', 'running'):
                        task = await lua(f"return api('await_task',{{taskId='{task['taskId']}',milliseconds=1000}})")
                proof = task['result']
                report[operation] = dict(taskId=task['taskId'], status=proof['status'], inputsReleased=proof['inputsReleased'])
                assert proof['completed'] and proof['inputsReleased'], report[operation]
            # The removal itself is a meaningful baseline change; hand it back before moving.
            removed = await client.request(child, 'tools/call', call)
            change = removed['structuredContent']['explorationResult']
            report['afterMining'] = change
            assert not change['segments'] and any(h['anchor'] == ore['anchor'] for finding in change['findings']
                                                  for h in finding['changes'])
            # Close stdin while a new native segment is running. The client shares no
            # task handles with the host and observes admission through get_events.
            pending = await client.send(child, 'tools/call', call)
            async with asyncio.timeout(15):
                while True:
                    poll = await client.send(child, 'tools/call', dict(name='get_events', arguments={}))
                    response = json.loads(await child.stdout.readline())
                    assert response['id'] != pending, 'Continuation ended before EOF test'
                    assert response['id'] == poll
                    active = response['result']['structuredContent']['runtimeTasks']['active']
                    if any(task['status'] == 'running' for task in active):
                        break
                    await asyncio.sleep(.03)
            child.stdin.close()
            await asyncio.wait_for(child.communicate(), 15)
            assert child.returncode == 0
            events = [json.loads(line) for line in log.read_text(encoding='utf-8').splitlines()]
            cleanup = next(e for e in reversed(events) if e['type'] == 'mcp_explore_cleanup')
            report['eofCleanup'] = cleanup
            assert cleanup['taskId'] in [r['taskId'] for r in active]
            assert cleanup['releaseConfirmed'] and cleanup['receipt']['status'] == 'cancelled'
            assert any(e['type'] == 'mcp_lease_released' and e['safe'] for e in events)
        finally:
            if child.returncode is None:
                child.stdin.close()
                await asyncio.wait_for(child.communicate(), 15)
    finally:
        (profile / 'external-explore.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
        await asyncio.to_thread(chat.shutdown)
        chat.server_close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--cave', action='store_true')
    args = parser.parse_args()
    asyncio.run(verify(args.port, args.profile, args.cave))
