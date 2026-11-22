"""Exercise teammate discovery, team selection and stale-session refusal over real dedicated IPC."""
import argparse
import asyncio
import json
from pathlib import Path
from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file


async def verify(port, profile):
    await wait_file(profile / 'action-fixture.json', 90)
    bridge = ServerAiBridge(port)
    results = []

    async def call(name, args='{}'):
        calls = []
        response = await LuaRuntime(bridge, evidence_root=profile/'lua').execute(
            f'return api("{name}",{args})', limits=Limits(seconds=15, calls=1, actions=1),
            on_result=lambda _, __, result: calls.append(result))
        assert calls, response
        results.append(calls[-1])
        return calls[-1]

    try:
        observed = await call('list_teammates')
        assert observed['data']['players'] == [], observed
        joined = await call('set_team', '{team=3}')
        assert joined['completed'] and joined['evidence']['team'] == 3, joined
        assert (await call('list_teammates'))['data']['actorTeam'] == 3
        stale = await call('teleport_to_teammate', '{teammateRef="'+'a'*32+'"}')
        assert stale['reason'] == 'teammate_session_unavailable' and not stale['completed'], stale
        temp = profile/'direct-input-request.tmp'
        temp.write_text(json.dumps({'scene':'regroup-native'}))
        temp.replace(profile/'direct-input-request.json')
        native = await wait_file(profile/'direct-input-ready.json')
        results.append(native)
        assert (native['missing'],native['different'],native['dead']) == (
            'wormhole_potion_missing','different_team','teammate_dead'), native
        assert native['success']=='wormhole_verified' and native['remaining']==1 and native['deltaX']==128, native
    finally:
        (profile/'regroup-results.json').write_text(json.dumps(results, indent=2))
        bridge.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    args = parser.parse_args()
    asyncio.run(verify(args.port, args.profile))
