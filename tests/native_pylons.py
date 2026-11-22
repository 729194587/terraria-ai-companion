"""One destination, unchanged eligibility, vanilla and GlobalPylon decisions via Lua."""
import argparse
import asyncio
import json
from pathlib import Path

from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file


async def verify(port, profile):
    await wait_file(profile / 'action-fixture.json', 90)
    bridge = ServerAiBridge(port)

    async def scene(name):
        ready = profile / 'direct-input-ready.json'
        ready.unlink(missing_ok=True)
        request = profile / 'direct-input-request.tmp'
        request.write_text(json.dumps({'scene': 'pylon-' + name}))
        request.replace(profile / 'direct-input-request.json')
        return await wait_file(ready)

    async def run(code, expected):
        result = await LuaRuntime(bridge).execute(code, limits=Limits(seconds=30,calls=4,actions=1))
        assert result['status']==expected, result
        return result['value'] or result['lastAction']

    try:
        assert 'server_ai_pylons_v1' in bridge.hello['capabilities']
        where = await scene('create')
        token = await run(f"""for _,p in ipairs(api("list_pylons").data.pylons) do
            if p.x=={where['x']} and p.y=={where['y']} then return p.pylonRef end
            end error("fixture destination missing")""", 'completed')
        request = 'return api("teleport_to_pylon",{pylonRef="' + token + '"})'
        vanilla = await run(request,'failed')
        assert vanilla['reason']=='pylon_rejected' and vanilla['reasonDetail'], vanilla
        await scene('deny')
        custom = await run(request,'failed')
        assert 'CustomPylonDenial' in custom['reasonDetail'], custom
        await scene('allow')
        arrival = await run(request,'completed')
        proof = arrival['evidence']
        assert arrival['completed'] and proof['actorX']==proof['targetX']!=where['actorX'], arrival
        await scene('vanilla')
        near = await run(request,'completed')
        assert near['evidence']['nativeTeleport'] is True, near
        await scene('remove')
        stale = await run(request,'completed')
        assert stale['status']=='failed' and stale['reason']=='stale_pylon_ref', stale
        print(vanilla['reasonDetail'], custom['reasonDetail'], proof, stale['reason'])
    finally:
        bridge.close()


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--port',type=int,required=True)
    parser.add_argument('--profile',type=Path,required=True)
    args=parser.parse_args()
    asyncio.run(verify(args.port,args.profile))
