"""Read the same underground lighting fixture through real stdio MCP and native IPC."""
import argparse
import asyncio
import base64
import json
from pathlib import Path
import sys
import threading

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

from native_direct_input import wait_file
from test_server_ai_agent import ChatHost
from server_ai import ServerAiBridge
import atomic_operations as atomic


async def verify_fork(client, bridge, x, y, profile):
    async def lua(source):
        reply = await client.call_tool('execute_lua', {'source': source})
        result = reply.structuredContent
        assert not reply.isError and result['status'] == 'completed', result
        return result['value']

    async def routes():
        items = await lua("""local q=api('observe_scene',{layers={'landingRegions'},includePassages=true})
            return q.data.passages.routes""")
        return {d: max((r for r in items if r['direction'] == d and r['target']['y'] == y*16),
                       key=lambda r: abs(r['target']['x']-x*16)) for d in ('left', 'right')}

    async def go(px, py):
        action = await lua(f"return api('navigate_to',{{x={px},y={py}}})")
        assert action['valid'] and action['completed'], action

    async def remembered(tx, ty):
        q = await atomic.dispatch(bridge, 'query_map', dict(mode='region', centerX=tx, centerY=ty,
                                                           width=1, height=1, cellSize=1))
        return q['data']['rows'][0]

    # No look_around call since lighting the fork. Background sampling alone
    # must discover both the remote mouth and the floor supporting it.
    for _ in range(80):
        if await remembered(x+12, y-2) == ' ':
            break
        await asyncio.sleep(.05)
    assert await remembered(x+12, y-2) == ' ' and await remembered(x+12, y) == '#'
    initial = await routes()
    assert all(initial[d]['visitState'] == 'no_recent_visit' for d in ('left', 'right')), initial
    right = initial['right']['target']
    assert await remembered(right['x']//16, y-1) == ' '
    await go((x-38)*16, y*16)
    far = (await atomic.dispatch(bridge, 'look_around', {}))['data']
    assert far['actor']['x'] <= x-34, far['actor']
    col = right['x']//16 - far['originX']
    assert col >= far['width'] or far['rows'][y-1-far['originY']][col] == '?'
    assert await remembered(right['x']//16, y-1) == ' '
    await go(x*16+10, y*16)
    returned = await routes()
    assert returned['left']['visitState'] == 'walked_nearby', returned
    assert returned['right']['visitState'] == 'no_recent_visit', returned
    choice, = [r for r in returned.values() if r['visitState'] == 'no_recent_visit']
    await go(choice['target']['x'], choice['target']['y'])
    end = (await atomic.dispatch(bridge, 'look_around', {}))['data']['actor']
    assert abs(end['x'] - choice['target']['x']//16) <= 2, end
    evidence = dict(initial=initial, away=far['actor'], returned=returned, chosen=choice, end=end)
    (profile/'fork-memory.json').write_text(json.dumps(evidence, indent=2))
    print('Lit fork remembered without opening the map; returned and entered the unwalked arm.', flush=True)


async def verify(profile, port):
    (profile / 'torch-hold').touch()
    await wait_file(profile / 'action-fixture.json', 90)
    bridge = ServerAiBridge(port)
    chat = ChatHost()
    threading.Thread(target=chat.serve_forever, daemon=True).start()
    root = Path(__file__).resolve().parents[1]
    host = StdioServerParameters(command=sys.executable, args=[
        '-B', str(root / 'python/agent.py'), '--server-ai', '--external-brain', '--mcp-transport', 'stdio',
        '--ai-port', str(port), '--port', str(chat.server_address[1]), '--log-file', str(profile / 'mcp.jsonl')])
    try:
        async with stdio_client(host) as (reader, writer), ClientSession(reader, writer) as client:
            await client.initialize()
            assert 'look_around' in {tool.name for tool in (await client.list_tools()).tools}
            for mode in ('dark', 'fork', 'lit', 'held', 'occluded', 'gemspark', 'daylight'):
                ready = profile / 'torch-ready.json'
                ready.unlink(missing_ok=True)
                pending = profile / 'torch-request.tmp'
                pending.write_text(mode)
                pending.replace(profile / 'torch-request')
                scene = await wait_file(ready)
                x, y = scene['x'], scene['y']
                if mode == 'fork':
                    await verify_fork(client, bridge, x, y, profile)
                    continue
                observed = await atomic.dispatch(bridge, 'look_around', {})
                assert observed['valid'], observed
                view = observed['data']
                def cell(tx, ty):
                    return view['rows'][ty - view['originY']][tx - view['originX']]
                if mode == 'dark':
                    assert cell(x, y-2) == cell(x+7, y-2) == '?'
                elif mode == 'occluded':
                    # Four solid columns separate the actor from a lamp. The remote
                    # corridor must still be visible past the intervening wall.
                    assert cell(x-7, y-1) == 't' and cell(x-12, y-2) == '.'
                    assert cell(x-12, y) == '#' and cell(x+35, y-2) == '?'
                else:
                    assert cell(x+7, y-2) == '.' and cell(x+7, y) == '#', mode
                    if mode == 'lit':
                        assert cell(x+2, y-1) == 't'
                assert view['visibleTiles'] == sum(len(row)-row.count('?') for row in view['rows'])
                reply = await client.call_tool('look_around', {})
                assert not reply.isError and [part.type for part in reply.content] == ['text', 'image'], reply
                details = reply.structuredContent
                assert (details['width'], details['height']) == (119, 67) and 'rows' not in details
                assert details['actor'] == view['actor'] and details['gameUpdate'] >= observed['gameUpdate']
                image = reply.content[1]
                png = base64.b64decode(image.data, validate=True)
                assert image.mimeType == 'image/png' and png.startswith(b'\x89PNG\r\n\x1a\n')
                (profile / f'view-{mode}.png').write_bytes(png)
                print(f'{mode}: {details["visibleTiles"]} visible tiles, {len(png)} PNG bytes', flush=True)
    finally:
        bridge.close()
        chat.shutdown()
        chat.server_close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    args = parser.parse_args()
    asyncio.run(verify(args.profile, args.port))
