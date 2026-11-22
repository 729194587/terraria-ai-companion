"""Stage a Mod accessory and consume a real Mod potion through normal Server AI IPC."""
import argparse
import asyncio
import json
from pathlib import Path

from native_direct_input import wait_file
from atomic_operations import dispatch
from server_ai import ServerAiBridge


async def verify(args):
    await wait_file(args.profile / 'action-fixture.json', 120)
    bridge = ServerAiBridge(args.port)
    async def stage(mode):
        ready = args.profile / 'equipment-ready.json'
        ready.unlink(missing_ok=True)
        request = args.profile / 'equipment-request.tmp'
        request.write_text(mode)
        request.replace(args.profile / 'equipment-request')
        return await wait_file(ready)

    try:
        diagnostic = await stage('reset')
        loaded = {row['name'] for row in bridge.hello['compatibility']['loadedMods']}
        calamity = 'CalamityMod' in loaded
        if calamity:
            await stage('loaded-content')
        source = 25 if calamity else 24
        async def slots():
            result = await dispatch(bridge, 'inspect_inventory', {'mode': 'slots', 'slots': [0, source]})
            return {row['slot']: row for row in result['data']['inventory']}
        before = await slots()
        target = 'vanilla:accessory:1' if calamity else 'mod:' + diagnostic['slot']
        state = await dispatch(bridge, 'inspect_inventory', {'mode': 'full'})
        gear = next(row for row in state['data']['functionalEquipmentSlots'] if row['slotId'] == target)
        equip = await dispatch(bridge, 'equip_item', {'targetSlotId': target, 'targetToken': gear['targetToken'],
                               'sourceSlot': source, 'sourceToken': before[source]['inventoryToken']})
        assert equip['completed'] and equip['evidence']['equipmentVerified'], equip
        result = {'equipped': before[source]['contentKey'], 'equipVerified': True}
        if calamity:
            use = await dispatch(bridge, 'use_item', {})
            after = await slots()
            assert use['completed'] and before[0]['stack'] - after[0]['stack'] == 1, use
            result['consumed'] = before[0]['contentKey']
        (args.profile / 'mod-equipment.json').write_text(json.dumps(result, indent=2))
    finally:
        bridge.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    asyncio.run(verify(parser.parse_args()))
