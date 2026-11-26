"""Use real player updates, item use, and a logged-in tModLoader protocol donor."""
import argparse
import asyncio
import base64
import json
import struct
from pathlib import Path

from native_direct_input import ServerAiBridge, atomic, wait_file
from verify_server_ai_container_native import Client


async def verify(args):
    await wait_file(args.profile / 'action-fixture.json', 90)
    bridge = ServerAiBridge(args.port)
    donor = None
    samples = {}

    async def fixture(op, **values):
        response = args.profile / 'potion-response.json'
        response.unlink(missing_ok=True)
        request = args.profile / 'potion-request.tmp'
        request.write_text(json.dumps(dict(op=op, **values)))
        request.replace(args.profile / 'potion-request.json')
        data = await wait_file(response)
        assert 'error' not in data, data
        return data

    async def settle(label, ticks=45):
        start = (await fixture('state'))['tick']
        async with asyncio.timeout(10):
            while True:
                row = await fixture('state')
                if row['tick'] - start >= ticks:
                    samples[label] = row
                    return row
                await asyncio.sleep(.08)

    async def wire_stack(count):
        item = await fixture('wire-item', stack=count)
        donor.send(5, struct.pack('<Bh', donor.slot, 1) + base64.b64decode(item['itemPayload']))

    def move(state, offset):
        donor.send(13, struct.pack('<6Bff', donor.slot, 0, 0, 0, 0, 0, state['x'] + offset, state['y']))

    try:
        quality = any(m['name'] == 'ImproveGame' for m in bridge.hello['compatibility']['loadedMods'])
        await fixture('stock', stack=29)
        before = await settle('below_threshold')
        assert before['ironSlot'] < 0
        defense = before['defense']
        await fixture('stock', stack=30)
        own = await settle('at_threshold')
        assert own['stack'] == 30
        assert (0 < own['iron'] <= 30 if quality else own['ironSlot'] < 0)
        assert own['defense'] == defense + (8 if quality else 0)
        await fixture('stock', stack=0)
        removed = await settle('removed')
        assert removed['ironSlot'] < 0 and removed['defense'] == defense
        if quality:
            await fixture('potion-bag', stack=30)
            bag = await settle('carried_potion_bag')
            assert bag['stack'] == 1 and 0 < bag['iron'] <= 30 and bag['defense'] == defense + 8

        await fixture('stock', stack=30)
        use = await atomic.dispatch(bridge, 'use_item', {})
        assert use['completed'] and use['inputsReleased'], use
        first = await settle('drank')
        assert first['iron'] > 27000 and first['stack'] == (30 if quality else 29), first
        await fixture('stock', stack=0)
        later = await settle('drank_then_removed')
        assert first['iron'] - later['iron'] == later['tick'] - first['tick']
        assert later['defense'] == defense + 8
        await fixture('near-expiry')
        expired = await settle('expired')
        assert expired['ironSlot'] < 0 and expired['defense'] == defense and expired['sharpened'] == 90
        if not quality:
            return

        donor = await asyncio.to_thread(Client, args.game_port, 'PotionDonor')
        team = await atomic.dispatch(bridge, 'set_team', {'team': 1})
        assert team['completed'], team
        await wire_stack(30)
        move(expired, 64)
        donor.send(45, bytes([donor.slot, 2]))
        other = await settle('other_team')
        assert other['donor']['state'] == 10 and other['donor']['team'] == 2
        assert other['donor']['stack'] == 30 and other['ironSlot'] < 0, other
        donor.send(45, bytes([donor.slot, 1]))
        shared = await settle('shared')
        assert shared['donor']['team'] == 1 and shared['donor']['distance'] <= 800, shared
        config = json.loads((args.profile / 'ModConfigs/ImproveGame_ImproveConfigs.json').read_text(encoding='utf-8-sig'))
        if not config['ShareInfBuffs']:
            assert shared['ironSlot'] < 0 and shared['defense'] == defense
            return
        assert 0 < shared['iron'] <= 30 and shared['defense'] == defense + 8 and shared['stack'] == 0
        move(shared, 960)
        far = await settle('outside_range')
        assert far['donor']['distance'] > 800 and far['ironSlot'] < 0 and far['defense'] == defense, far
        move(far, 64)
        back = await settle('within_range_again')
        assert back['iron'] > 0
        donor.send(45, bytes([donor.slot, 2]))
        switched = await settle('left_team')
        assert switched['donor']['team'] == 2 and switched['ironSlot'] < 0 and switched['defense'] == defense
        donor.send(45, bytes([donor.slot, 1]))
        assert (await settle('rejoined_team'))['iron'] > 0
        await wire_stack(0)
        empty = await settle('donor_removed')
        assert empty['donor']['stack'] == 0 and empty['ironSlot'] < 0 and empty['defense'] == defense
        await wire_stack(30)
        assert (await settle('restocked'))['iron'] > 0
        donor.close()
        donor = None
        left = await settle('disconnected', ticks=90)
        assert left['donor'] is None and left['ironSlot'] < 0 and left['defense'] == defense
    finally:
        if donor:
            donor.close()
        bridge.close()
        (args.profile / 'potion-results.json').write_text(json.dumps(samples, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--game-port', type=int, required=True)
    asyncio.run(verify(parser.parse_args()))
