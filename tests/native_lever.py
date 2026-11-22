"""Copied-world lever and adjacent wired torch, through the public Lua surface."""
import argparse
import asyncio
import json
from pathlib import Path

from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file


async def verify(port, profile):
    await wait_file(profile / 'action-fixture.json', 90)
    bridge = ServerAiBridge(port)
    receipts = []
    try:
        assert 'interact_tile' in bridge.supported_atomic_operations
        request = profile / 'direct-input-request.tmp'
        request.write_text(json.dumps({'scene': 'lever'}))
        request.replace(profile / 'direct-input-request.json')
        start = await wait_file(profile / 'direct-input-ready.json')
        left = int(start['startX']) // 16
        y = int(start['floorY']) // 16 - 1
        source = f"local left, y = {left}, {y}\n" + """
local function tile(x,y)
    return api("inspect_tile",{x=x,y=y}).data
end
local function lamp()
    local c=tile(left+7,y).cell
    assert(c.hasTile and c.wireMask~=0)
    return c.frameX
end
local function click(observation)
    return api("interact_tile",{target=observation.target})
end
local function ensureLever(on)
    local state=tile(left+3,y).interaction
    assert(state.kind=="lever")
    if state.active==on then return end
    assert(click(state).completed)
end
local initial=lamp()
ensureLever(true)
local toggled=lamp()
assert(initial~=toggled)
ensureLever(true)
assert(lamp()==toggled)
ensureLever(false)
assert(lamp()==initial)
for _,entry in ipairs({{x=left-2,kind="switch"},{x=left-4,kind="door"}}) do
    for _,active in ipairs({false,true}) do
        local state=tile(entry.x,y).interaction
        assert(state.kind==entry.kind and state.active==active)
        local result=click(state)
        assert(result.completed and result.evidence.afterActive~=active)
    end
end
return true
"""
        runtime = LuaRuntime(bridge, evidence_root=profile/'lua')
        async def run(script):
            return await runtime.execute(script, limits=Limits(seconds=40, calls=24, actions=8),
                on_result=lambda name, args, result: receipts.append({'name':name, 'result':result}))
        assert (result := await run(source))['status'] == 'completed', result
        far = f'local t=api("inspect_tile",{{x={left+12},y={y}}}).data.interaction.target\n'
        await run(far + 'return api("interact_tile",{target=t})')
        assert receipts[-1]['result']['reason'] == 'interaction_out_of_reach', receipts[-1]
        near = f'local function target() return api("inspect_tile",{{x={left+3},y={y}}}).data.interaction.target end\n'
        await run(near + '''local old=target()
assert(api("interact_tile",{target=old}).completed)
assert(api("interact_tile",{target=target()}).completed)
return api("interact_tile",{target=old})''')
        assert receipts[-1]['result']['reason'] == 'target_changed_since_observation', receipts[-1]
    finally:
        bridge.close()
        (profile/'lever-results.json').write_text(json.dumps(receipts, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    args = parser.parse_args()
    asyncio.run(verify(args.port, args.profile))
