"""Loopback peers on the isolated game port; replay their wire payloads through native client chat."""
import argparse
import asyncio
import base64
import json
from pathlib import Path
from uuid import uuid4

from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file


async def receive_window(reader, module, marker):
    packets = []
    # A unique ordered chat packet closes the window, including empty windows.
    fence = bytes([255, 0, len(marker)]) + marker.encode() + bytes([255] * 3)
    while True:
        length = int.from_bytes(await reader.readexactly(2), 'little')
        assert length >= 3, length
        body = await reader.readexactly(length - 2)
        if body[0] != 82 or int.from_bytes(body[1:3], 'little') != module:
            continue
        payload = body[3:]
        if payload == fence:
            return packets
        packets.append(base64.b64encode(payload).decode())


async def verify(args):
    profile = args.profile
    await wait_file(profile / 'action-fixture.json', 90)
    bridge = ServerAiBridge(args.port)
    peers = []
    report = {}

    async def fixture(op, **kwargs):
        path = profile / 'observer-response.json'
        path.unlink(missing_ok=True)
        temp = profile / 'observer-request.tmp'
        temp.write_text(json.dumps(dict(op=op, **kwargs)))
        temp.replace(profile / 'observer-request.json')
        result = await wait_file(path)
        assert 'error' not in result, result
        return result

    async def call(name, args='{}'):
        rows = []
        response = await LuaRuntime(bridge, evidence_root=profile/'lua').execute(
            f'return api("{name}",{args})', limits=Limits(seconds=15, calls=1, actions=1),
            on_result=lambda _, __, result: rows.append(result))
        assert rows, response
        return rows[-1]

    try:
        for _ in range(4):
            peers.append(await asyncio.open_connection('127.0.0.1', args.game_port))
        ports = [writer.get_extra_info('sockname')[1] for _, writer in peers]
        async def wait_for_peers():
            while (setup := await fixture('setup', ports=ports)).get('waitingForAccept'):
                await asyncio.sleep(0.05)
            return setup
        setup = report['setup'] = await asyncio.wait_for(wait_for_peers(), 10)

        async def observe(stage, expected):
            marker = uuid4().hex
            report[stage + '_connections'] = await fixture('fence', marker=marker)
            assert all(s['state'] == 10 and s['connected'] and s['active']
                       for s in report[stage + '_connections']['states']), report
            windows = await asyncio.wait_for(asyncio.gather(*(
                receive_window(reader, setup['module'], marker) for reader, _ in peers)), 10)
            records = report[stage] = []
            for packets, wanted in zip(windows, expected):
                native = await fixture('receive', packets=packets)
                records.append(dict(packets=packets, **native))
                assert native['lines'] == wanted, (stage, records, wanted)
                assert all(base64.b64decode(p)[0] == 255 for p in packets)

        team = [dict(text=setup['teamText'], rgb=setup['teamColor'])]
        teleport = [dict(text=setup['teleportText'], rgb=[250, 250, 0])]
        result = await call('set_team', '{team=5}')
        assert result['completed'] and result['evidence']['beforeTeam'] == 2, result
        await observe('switch_team', [team, team, [], []])
        result = await call('set_team', '{team=5}')
        assert result['completed'] and result['evidence']['beforeTeam'] == 5, result
        await observe('same_team', [[], [], [], []])
        members = (await call('list_teammates'))['data']['players']
        report['members'] = members
        target, = [p for p in members if p['name'] == 'Observer1']
        result = await call('teleport_to_teammate', '{teammateRef="' + target['teammateRef'] + '"}')
        assert result['completed'] and result['reason'] == 'wormhole_verified', result
        report['wormhole'] = result['evidence']
        await observe('teleport', [teleport] * len(peers))
    finally:
        (profile/'observer-results.json').write_text(json.dumps(report, indent=2))
        bridge.close()
        for _, writer in peers:
            writer.close()
            await writer.wait_closed()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--game-port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    asyncio.run(asyncio.wait_for(verify(parser.parse_args()), 180))
