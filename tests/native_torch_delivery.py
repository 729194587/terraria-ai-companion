"""Native-client pickup and MCP receipts for full, partial and misdirected handoffs."""
import asyncio
import json
import os
import shutil
import socket
import struct
import subprocess
import sys
import time

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

from native_direct_input import wait_file
from native_storage_memory import ROOT, call, lua


ORDER = '我要下矿，帮我准备 12 支火把，拿给我'


async def command(directory, prefix, message, seconds=12):
    state = directory / (prefix + '-state.json')
    state.unlink(missing_ok=True)
    temp = directory / (prefix + '-request.tmp')
    temp.write_text(message)
    temp.replace(directory / (prefix + '-request'))
    result = await wait_file(state, seconds)
    assert result['command'] == message
    return result


def count(slots, item):
    return sum(row['stack'] for row in slots if row['type'] == item)


def counts(state, index, slot, torch):
    box, = [c for c in state['batchChests'] if c['index'] == index]
    recipient, = [c for c in state['clients'] if c['slot'] == slot]
    return dict(box=count(box['slots'], torch), actor=count(state['batchInventory'], torch),
                recipient=count(recipient['inventory'], torch))


async def observed_drop(client, target, drop, status, confirmed, ground):
    deadline = time.monotonic() + 12
    while True:
        players = await lua(client, "return api('list_teammates',{}).data.players")
        receipt, = [p['lastDrop'] for p in players if p['teammateRef'] == target]
        if (receipt and receipt['entityRef'] == drop['entityRef'] and receipt['spawnedCount'] == drop['spawnedCount']
                and (receipt['receiptStatus'], receipt['confirmedReceived'], receipt['stillOnGround'])
                == (status, confirmed, ground)):
            return receipt
        assert time.monotonic() < deadline, (status, confirmed, ground, receipt)
        await asyncio.sleep(.1)


async def throw_stack(client, slot, amount, target):
    drop = await lua(client, """
local s=api('inspect_inventory',{mode='slots',slots={%d}}).data.inventory[1]
return api('drop_inventory_item',{sourceSlot=%d,sourceToken=s.inventoryToken,count=%d,teammateRef='%s'})
""" % (slot, slot, amount, target))
    assert drop['completed'] and drop['evidence']['spawnedCount'] == amount, drop
    return drop['evidence']


async def run_cases(profile, recipient_dir, client, port):
    ready = await wait_file(profile / 'container-ready.json')
    torch = await lua(client, "return api('resolve_content',{kind='item',query='Terraria/Torch'}).data.candidates[1].typeId")
    potion = await lua(client, "return api('resolve_content',{kind='item',query='Terraria/WormholePotion'}).data.candidates[1].typeId")
    assert 'terraria.inventory.give(' in (await call(client, 'guide'))['instructions']
    await lua(client, "return api('set_team',{team=1})")
    records = {}
    for case, stock in [('stocked', 12), ('short', 6)]:
        before_client = await command(recipient_dir, 'recipient', 'prepare')
        assert before_client['torches'] == 0 and not before_client['dead'], before_client
        slot = before_client['slot']
        await command(profile, 'container', 'seed-torches-' + case)
        async with asyncio.timeout(12):
            while True:
                before = counts(await command(profile, 'container', 'snapshot'), ready['index'], slot, torch)
                if before == dict(box=stock, actor=0, recipient=0):
                    break
                await asyncio.sleep(.15)
        await command(recipient_dir, 'recipient', 'ask')
        async with asyncio.timeout(12):
            while True:
                events = (await call(client, 'get_events', wait_seconds=1))['events']
                order = next((e for e in events if e.get('source') == 'local_ai_command' and e['text'] == ORDER), None)
                if order:
                    break
        assert order['senderName'] == before_client['name'] and order['senderId'] == slot, order
        started = time.monotonic()
        members = await lua(client, "return api('list_teammates',{}).data.players")
        member, = [m for m in members if m['name'] == order['senderName'] and not m.get('reason')]
        taken = await lua(client, f"return terraria.container.take({{x={ready['x']},y={ready['y']}}},'Terraria/Torch',12)")
        assert taken['status'] == ('completed' if stock == 12 else 'partial') and taken['moved'] == stock, taken
        missing = taken['remaining']
        craft = None
        if missing:
            made = await lua(client, f"return terraria.crafting.make({torch},{missing})")
            assert made['status'] == 'completed' and made['producedCount'] == missing, made
            craft = made['receipt']['evidence']
            assert craft['inventoryVerified'] and craft['producedCount'] == missing, craft
        staged = counts(await command(profile, 'container', 'snapshot'), ready['index'], slot, torch)
        assert staged == dict(box=0, actor=12, recipient=0), staged
        target = member['teammateRef']
        if case == 'stocked':
            split = await lua(client, """
local s=api('inspect_inventory',{mode='slots',slots={10,11}}).data.inventory
return api('transfer_inventory_item',{sourceSlot=10,sourceToken=s[1].inventoryToken,
    destinationSlot=11,destinationToken=s[2].inventoryToken,count=5})""")
            assert split['completed'], split
        away = await command(profile, 'container', 'move-away')
        potions_before = count(away['batchInventory'], potion)
        assert potions_before == int(case == 'short')
        gift = await lua(client, "return terraria.inventory.give('%s','Terraria/Torch',12)" % target)
        assert gift['status'] == 'completed' and gift['dropped'] == 12, gift
        amounts = [d['spawnedCount'] for d in gift['drops']]
        assert sum(amounts) == 12 and (case != 'stocked' or amounts == [7, 5]), gift
        last_drop = gift['drops'][-1]
        assert gift['entityRef'] == last_drop['entityRef']
        assert gift['approach'] == ('walk' if case == 'stocked' else 'wormhole'), gift
        deadline = time.monotonic() + 20
        while True:
            received = await command(recipient_dir, 'recipient', 'snapshot')
            final_state = await command(profile, 'container', 'snapshot')
            after = counts(final_state, ready['index'], slot, torch)
            if received['torches'] == 12 and after == dict(box=0, actor=0, recipient=12):
                break
            assert time.monotonic() < deadline, (received, after, gift)
            await asyncio.sleep(.2)
        potions_after = count(final_state['batchInventory'], potion)
        assert potions_after == 0
        receipt = await observed_drop(client, target, last_drop, 'received', last_drop['spawnedCount'], False)
        records[case] = dict(order=order, before=before, withdrawn=stock, craft=craft, staged=staged, gift=gift,
                             potionsBefore=potions_before, potionsAfter=potions_after,
                             lastDrop=receipt, clientBefore=before_client, clientAfter=received,
                             after=after, seconds=round(time.monotonic()-started, 2))
        print(f'{case}: {stock} withdrawn, {missing} crafted, 12 received', flush=True)
        await command(profile, 'container', 'move-back')
    # Give the receiving player's real inventory exactly five spaces in its last torch stack.
    baseline = await command(recipient_dir, 'recipient', 'partial')
    seeded = await command(profile, 'container', 'seed-handoff')
    assert count(seeded['batchInventory'], potion) == 1
    members = await lua(client, "return api('list_teammates',{}).data.players")
    assert next(m['handoff']['inRange'] for m in members if m['teammateRef'] == target)
    gift = await lua(client, "return terraria.inventory.give('%s','Terraria/Torch',12)" % target)
    assert (gift['status'], gift['approach'], gift['dropped']) == ('completed', 'wormhole', 12), gift
    drop = dict(entityRef=gift['entityRef'], spawnedCount=gift['dropped'])
    async with asyncio.timeout(12):
        while True:
            first = await command(recipient_dir, 'recipient', 'snapshot')
            state = await command(profile, 'container', 'handoff-snapshot')
            actual = counts(state, ready['index'], first['slot'], torch)
            if first['torches'] == actual['recipient'] == baseline['torches'] + 5:
                break
            await asyncio.sleep(.1)
    assert actual['actor'] == 0 and sum(i['stack'] for i in state['groundTorches']) == 7, (actual, state['groundTorches'])
    potions_after = count(state['batchInventory'], potion)
    assert potions_after == 0
    partial = await observed_drop(client, target, drop, 'partial', 5, True)
    await command(recipient_dir, 'recipient', 'make-room')
    deadline = time.monotonic() + 25
    while True:
        last = await command(recipient_dir, 'recipient', 'snapshot')
        state_after = await command(profile, 'container', 'handoff-snapshot')
        actual_after = counts(state_after, ready['index'], first['slot'], torch)
        if last['torches'] == actual_after['recipient'] == baseline['torches'] + 12:
            break
        assert time.monotonic() < deadline, (last, actual_after, state_after['groundTorches'])
        await asyncio.sleep(.2)
    complete = await observed_drop(client, target, drop, 'received', 12, False)
    records['partial_then_full'] = dict(before=baseline, gift=gift, potionsAfter=potions_after, partial=partial, first=first, ground=state['groundTorches'],
                                        complete=complete, last=last)

    # Address a different connected teammate. Only the graphical client approaches and picks up.
    from verify_server_ai_container_native import Client
    other = await asyncio.to_thread(Client, port, 'HandoffObserver')
    try:
        other.send(45, bytes([other.slot, 1]))
        other.send(13, struct.pack('<6Bff', other.slot, 0, 0, 0, 0, 0, (ready['x']+24)*16, ready['y']*16))
        async with asyncio.timeout(12):
            while True:
                players = await lua(client, "return api('list_teammates',{}).data.players")
                intended = next((p['teammateRef'] for p in players if p['name'] == 'HandoffObserver' and not p.get('reason')), None)
                if intended:
                    break
                await asyncio.sleep(.1)
        baseline = await command(recipient_dir, 'recipient', 'far')
        before = counts(await command(profile, 'container', 'seed-handoff'), ready['index'], other.slot, torch)
        drop = await throw_stack(client, 10, 12, intended)
        ground = await observed_drop(client, intended, drop, 'unconfirmed', 0, True)
        state = await command(profile, 'container', 'handoff-snapshot')
        assert sum(i['stack'] for i in state['groundTorches']) == 12, state['groundTorches']
        await command(recipient_dir, 'recipient', 'approach')
        async with asyncio.timeout(20):
            while True:
                picker = await command(recipient_dir, 'recipient', 'snapshot')
                if picker['torches'] == baseline['torches'] + 12:
                    break
                await asyncio.sleep(.15)
        gone = await observed_drop(client, intended, drop, 'unconfirmed', 0, False)
        sample = await command(profile, 'container', 'snapshot')
        after = counts(sample, ready['index'], other.slot, torch)
        assert after['recipient'] == before['recipient'] == 0
        assert counts(sample, ready['index'], picker['slot'], torch)['recipient'] == picker['torches']
        records['ground_then_other_player'] = dict(before=before, ground=ground, picker=picker, after=after, gone=gone)
    finally:
        other.close()
    return records


async def verify_delivery(profile, args):
    folder = profile / 'recipient'
    (folder / 'Players').mkdir(parents=True)
    mods = folder / 'Mods'
    mods.mkdir()
    for name in ('CompanionBridge.tmod', 'enabled.json'):
        shutil.copyfile(profile / 'Mods' / name, mods / name)
    (folder / 'isolated-torch-recipient').touch()
    player = folder / 'Players' / args.torch_recipient.name
    shutil.copyfile(args.torch_recipient, player)
    # A fresh profile otherwise waits for a click on tML's first-launch welcome screen.
    (folder / 'config.json').write_text(json.dumps(dict(
        Language='en-US', LastLaunchedTModLoaderVersion='2026.9.3.1',
        Fullscreen=False, DisplayWidth=800, DisplayHeight=720, ThrottleWhenInactive=False,
        VolumeSound=0, VolumeMusic=0, VolumeAmbient=0)))
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        chat_port = probe.getsockname()[1]
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = subprocess.SW_HIDE
    native = [str(args.engine / 'dotnet/dotnet.exe'), 'exec', '--additionalprobingpath', str(args.engine / 'Libraries'),
              'tModLoader.dll', '-join', '127.0.0.1', '-port', str(args.port), '-plr', str(player),
              '-tmlsavedirectory', str(folder), '-modpath', str(mods), '-companiontorchrecipient',
              '-companionbridgeport', str(chat_port)]
    env = os.environ.copy()
    env['PATH'] = str(args.engine / 'Libraries/Native/Windows') + os.pathsep + env['PATH']
    with (folder / 'client.log').open('w') as log:
        process = subprocess.Popen(native, cwd=args.engine, env=env, stdout=log, stderr=subprocess.STDOUT,
                                   startupinfo=startup, creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            pending = asyncio.create_task(command(folder, 'recipient', 'snapshot', seconds=150))
            try:
                while not pending.done():
                    if process.poll() is not None:
                        raise RuntimeError(f'Native client exited {process.returncode}; see {log.name}')
                    await asyncio.sleep(.2)
                await pending
            finally:
                pending.cancel()
                await asyncio.gather(pending, return_exceptions=True)
            host = StdioServerParameters(command=sys.executable, args=[
                '-B', str(ROOT/'python/agent.py'), '--server-ai', '--external-brain', '--mcp-transport', 'stdio',
                '--ai-port', str(args.ipc_port), '--port', str(chat_port), '--log-file', str(profile/'mcp.jsonl')])
            async with stdio_client(host) as streams, ClientSession(*streams) as client:
                await client.initialize()
                records = await run_cases(profile, folder, client, args.port)
            log_rows = (json.loads(line) for line in (profile/'mcp.jsonl').read_text(encoding='utf-8').splitlines())
            release, = [r for r in log_rows if r['type'] == 'mcp_lease_released']
            assert release['safe']
            return dict(cases=records, release=release)
        finally:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=15)
