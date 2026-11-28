"""Cross-session base chest commissions against an isolated dedicated server."""
import asyncio
from contextlib import asynccontextmanager
import json
from pathlib import Path
import shutil
import struct
import sys
import threading
import time

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

from native_direct_input import wait_file
from test_server_ai_agent import ChatHost


ROOT = Path(__file__).resolve().parents[1]
NOTE_DESCRIPTION = "基地里存放绳子的箱子位置"
IDENTITY = """local m=api('query_map',{mode='region',width=1,height=1,cellSize=1}).data
return {world=m.worldUid,character=m.characterId}"""


async def call(client, tool, **args):
    reply = await client.call_tool(tool, args)
    assert not reply.isError, reply.content
    return reply.structuredContent


async def lua(client, source):
    result = await call(client, 'execute_lua', source=source)
    assert result['status'] == 'completed', (source, result.get('error'), result.get('lastAction'))
    return result['value']


async def store_ropes(client, pos, count):
    return await lua(client, f"return terraria.container.put({{x={pos['x']},y={pos['y']}}},'Terraria/Rope',{count})")


async def withdraw_from_note(client, requested):
    notes = (await call(client, 'memory', command='recall'))['notes']
    entry, = [n for n in notes if n['description'] == NOTE_DESCRIPTION]
    note = (await call(client, 'memory', command='recall', name=entry['name']))['note']
    pos = json.loads(note['content'])
    assert type(pos['x']) is int and type(pos['y']) is int
    result = await lua(client, f"""
local start=api('get_player_state',{{}}).player.position.x
local r=terraria.container.take({{x={pos['x'] + 1},y={pos['y'] + 1}}},'Terraria/Rope',{requested})
r.travel=api('get_player_state',{{}}).player.position.x-start
r.closed=api('inspect_container',{{}}).data.reason=='container_not_open'
return r""")
    assert result['travel'] > 96 and result['closed'], result
    assert (result['x'], result['y']) == (pos['x'], pos['y']), result
    return dict(note=entry['name'], position=pos, **result)


def take_fourteen(client, ready, type_id):
    """Replay a joined player's normal chest and inventory slot updates."""
    x, y, index = (ready[k] for k in ('x', 'y', 'index'))
    client.send(13, struct.pack('<6Bff', client.slot, 0, 0, 0, 0, 0, x * 16, y * 16 - 10))
    client.send(31, struct.pack('<hh', x, y))
    client.wait(33, lambda b: struct.unpack_from('<h', b)[0] == index)
    # ItemIO.Send writes type, prefix and count as 7-bit integers for these
    # vanilla items. Keep the mod data from the server's serialized rope.
    client.send(33, struct.pack('<hhhB', -1, 0, 0, 0))
    client.send(31, struct.pack('<hh', x, y))
    header = struct.pack('<hB', index, 0)
    row = client.wait(32, lambda b: b.startswith(header) and client.item(b[3:]) == (type_id, 0, 20))
    client.wait(33, lambda b: struct.unpack_from('<h', b)[0] == index)
    payload = row[3:]
    offset = (type_id.bit_length() + 6) // 7 + 1
    assert payload[offset] == 20
    def stack(n):
        return payload[:offset] + bytes([n]) + payload[offset+1:]
    client.send(32, header + stack(6))
    client.send(5, struct.pack('<Bh', client.slot, 10) + stack(14))
    client.send(33, struct.pack('<hhhB', -1, 0, 0, 0))


async def verify_storage_memory(profile, port, other_player):
    ready = await wait_file(profile / 'container-ready.json')
    # World copies keep their notebook identity. Give the MCP hosts their own
    # workspace so a prior run or an actual player's notes cannot seed this one.
    host_root = profile / 'host'
    for folder in ('python', 'skills'):
        shutil.copytree(ROOT / folder, host_root / folder, ignore=shutil.ignore_patterns('__pycache__'))
    note_name = '基地绳箱'
    report = {'cases': {}, 'sessions': []}
    chat = ChatHost()
    threading.Thread(target=chat.serve_forever, daemon=True).start()

    @asynccontextmanager
    async def session(label):
        log = profile / (label + '.jsonl')
        host = StdioServerParameters(command=sys.executable, args=[
            '-B', str(host_root / 'python/agent.py'), '--server-ai', '--external-brain', '--mcp-transport', 'stdio',
            '--ai-port', str(port), '--port', str(chat.server_address[1]), '--log-file', str(log)])
        async with stdio_client(host) as streams, ClientSession(*streams) as client:
            await client.initialize()
            identity = await lua(client, IDENTITY)
            assert all(identity.get(k) for k in ('world', 'character')), identity
            assert identity == report.setdefault('identity', identity)
            yield client
        messages = [json.loads(line) for line in log.read_text(encoding='utf-8').splitlines()]
        release, = [row for row in messages if row['type'] == 'mcp_lease_released']
        assert release['safe'] and all(release['mcp_session_id'] != row['id'] for row in report['sessions'])
        report['sessions'].append(dict(label=label, id=release['mcp_session_id'], safe=release['safe']))

    async def fixture(command):
        response = profile / 'container-state.json'
        response.unlink(missing_ok=True)
        tmp = profile / 'storage-request.tmp'
        tmp.write_text(command)
        tmp.replace(profile / 'container-request')
        state = await wait_file(response)
        assert state['command'] == command
        return state

    def counts(state):
        box, = [c for c in state['batchChests'] if c['index'] == ready['index']]
        human, = [c for c in state['clients'] if c['slot'] == other_player.slot]
        def total(slots):
            return sum(i['stack'] for i in slots if i['type'] == rope)
        return dict(bag=total(state['batchInventory']), box=total(box['slots']), human=total(human['inventory']))

    try:
        seed = await fixture('seed-rope-storage')
        rope = seed['batchInventory'][10]['type']
        assert counts(seed) == dict(bag=20, box=0, human=0)
        async with session('store') as client:
            split = await lua(client, """
local s=api('inspect_inventory',{mode='slots',slots={10,11}}).data.inventory
return api('transfer_inventory_item',{sourceSlot=10,sourceToken=s[1].inventoryToken,
    destinationSlot=11,destinationToken=s[2].inventoryToken,count=7})""")
            assert split['completed'], split
            deposit = await store_ropes(client, ready, 20)
            assert deposit['status'] == 'completed' and deposit['moved'] == 20, deposit
            pos = {k: deposit[k] for k in ('x', 'y')}
            assert (await call(client, 'memory', command='recall'))['notes'] == []
            note = await call(client, 'memory', command='remember', name=note_name,
                              description=NOTE_DESCRIPTION, content=json.dumps(pos))
            assert note['valid'], note
        stored = await fixture('snapshot')
        box = next(c for c in stored['batchChests'] if c['index'] == ready['index'])
        assert box['slots'][0]['type'] == rope and box['slots'][0]['stack'] == 20, box['slots'][:2]
        baseline = counts(stored)
        assert baseline == dict(bag=0, box=20, human=0)
        report['deposit'] = dict(receipt=deposit, after=baseline)
        for case, expected in [('stocked', 10), ('player_took_14', 6)]:
            if case == 'player_took_14':
                # Restore the post-deposit inventory through the normal game interface.
                async with session('restore') as client:
                    restored = await store_ropes(client, ready, 10)
                    assert restored['status'] == 'completed' and restored['moved'] == 10, restored
                assert counts(await fixture('snapshot')) == baseline
                await asyncio.to_thread(take_fourteen, other_player, ready, rope)
                async with asyncio.timeout(5):
                    while counts(await fixture('snapshot')) != dict(bag=0, box=6, human=14):
                        await asyncio.sleep(.1)
            before = counts(await fixture('move-away'))
            started = time.monotonic()
            async with session(case) as client:
                receipt = await withdraw_from_note(client, 10)
            after = counts(await fixture('snapshot'))
            assert receipt['status'] == ('completed' if expected == 10 else 'partial') and receipt['moved'] == expected
            assert receipt['remaining'] == 10-expected and after == dict(
                bag=expected, box=before['box']-expected, human=before['human'])
            report['cases'][case] = dict(before=before, receipt=receipt, after=after,
                                         seconds=round(time.monotonic()-started, 2))
        async with session('container-limits') as client:
            report['transfers'] = {}
            for setup, method, requested, expected, reason in (
                ('amount-fill-bag', 'take', 9, 2, 'destination_full'),
                ('amount-fill-chest', 'put', 9, 2, 'destination_full'),
                # 11 ordinary + 2 hotbar are available; 3 more are favorited.
                ('amount-favorites', 'put', 16, 13, 'insufficient_items'),
            ):
                await fixture('seed-batch')
                before = await fixture(setup)
                silk = before['batchInventory'][10]['type']
                def totals(state):
                    chest, = [c for c in state['batchChests'] if c['index'] == ready['index']]
                    return tuple(sum(i['stack'] for i in slots if i['type'] == silk)
                                 for slots in (state['batchInventory'], chest['slots']))
                receipt = await lua(client, f"return terraria.container.{method}("
                                    f"{{x={pos['x']},y={pos['y']}}},'Terraria/Silk',{requested})")
                after = await fixture('snapshot')
                bag, box = totals(before)
                delta = expected if method == 'take' else -expected
                assert totals(after) == (bag+delta, box-delta), (setup, receipt, totals(before), totals(after))
                assert (receipt['status'], receipt['reason'], receipt['moved'], receipt['remaining']) == (
                    'partial', reason, expected, requested-expected), receipt
                if setup == 'amount-favorites':
                    assert after['batchInventory'][11] == before['batchInventory'][11]
                report['transfers'][setup] = dict(receipt=receipt, before=totals(before), after=totals(after))
        return report
    finally:
        chat.shutdown()
        chat.server_close()
