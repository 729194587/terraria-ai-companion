"""First workbench from seven or ten wood on the same isolated server, via stdio MCP."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
import threading

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

from native_direct_input import wait_file
from test_server_ai_agent import ChatHost


READ_BAG = """
local function bag()
    local q=api('inspect_inventory',{mode='full'})
    assert(q.valid)
    local b={wood=0,bench=0}
    for _,i in ipairs(q.data.inventory) do
        if i.contentKey=='Terraria/Wood' then b.wood=b.wood+i.stack end
        if i.contentKey=='Terraria/WorkBench' then b.bench=b.bench+i.stack end
        if i.contentKey=='Terraria/CopperAxe' then b.axeKey=i.hotbarKey end
    end
    return b
end
"""

READ_RECIPE = """
local target=api('resolve_content',{kind='item',query='Terraria/WorkBench'})
local id=target.data.candidates[1].typeId
local q=api('query_recipes',{resultType=id,availableOnly=false})
for _,r in ipairs(q.data.items) do
    local i=r.ingredients[1].item
    if r.supported and #r.ingredients==1 and i.contentKey=='Terraria/Wood' then
        return {bag=bag(),required=i.count,woodType=i.typeId,available=r.available,
                reason=r.reason,recipeId=r.recipeId,recipeToken=r.recipeToken}
    end
end
error('No supported wood recipe for WorkBench')
"""

CHOP = """
local trees=sense_world({trees=true,ores=false,drops=false}).objects
local trunk=nil
for _,tree in ipairs(trees) do
    if tree.kind=='tree_trunk' and tree.candidate.status=='observed_present'
        and (not trunk or tree.observedTileCount>trunk.observedTileCount) then
        trunk=tree
    end
end
assert(trunk,'No observed trunk')
local t=trunk.candidate
api('select_hotbar_slot',{slot=bag().axeKey})
local a=api('mine_tile',{x=t.x,y=t.y})
assert(a.completed and a.endEvidence.inputsReleased)
local moved=api('navigate_to_x',{targetX=t.x*16})
assert(moved.completed and moved.endEvidence.inputsReleased)
return {x=t.x,y=t.y}
"""


async def verify(profile, port):
    assert (profile / 'isolated-action-fixture').is_file()
    await wait_file(profile / 'action-fixture.json', 90)
    chat = ChatHost()
    threading.Thread(target=chat.serve_forever, daemon=True).start()
    root = Path(__file__).resolve().parents[1]
    host = StdioServerParameters(command=sys.executable, args=[
        '-B', str(root / 'python/agent.py'), '--server-ai', '--external-brain', '--mcp-transport', 'stdio',
        '--ai-port', str(port), '--port', str(chat.server_address[1]), '--log-file', str(profile/'mcp.jsonl')])
    records = []
    try:
        async with stdio_client(host) as (reader, writer), ClientSession(reader, writer) as client:
            await client.initialize()

            async def lua(source):
                reply = await client.call_tool('execute_lua', {'source': READ_BAG + source})
                r = reply.structuredContent
                assert not reply.isError and r['status'] == 'completed', r or reply.content
                return r['value']

            for name, starting_wood in [('workbench_ten', 10), ('workbench_seven', 7)]:
                ready = profile/'craft-ready.json'
                ready.unlink(missing_ok=True)
                pending = profile/'craft-request.tmp'
                pending.write_text(json.dumps({'caseId': name}))
                pending.replace(profile/'craft-request.json')
                fixture = await wait_file(ready)
                assert fixture['caseId'] == name
                recipe = await lua(READ_RECIPE)
                start = recipe['bag']
                assert (start['wood'], start['bench']) == (starting_wood, 0), start
                cost = recipe['required']
                missing = max(0, cost - start['wood'])
                assert cost == 10 and recipe['available'] == (missing == 0), recipe
                initial_recipe = recipe
                tree = None
                if missing:
                    assert recipe['reason'] == 'insufficient_materials', recipe
                    tree = await lua(CHOP)
                    async with asyncio.timeout(20):
                        while True:
                            recipe = await lua(READ_RECIPE)
                            if recipe['available']:
                                break
                            await asyncio.sleep(.25)
                before = recipe['bag']
                assert before['wood'] >= cost and before['bench'] == 0, before
                receipt = await lua("return api('craft_recipe',{recipeId=%d,recipeToken='%s',crafts=1})"
                                    % (recipe['recipeId'], recipe['recipeToken']))
                e = receipt['evidence']
                assert receipt['completed'] and receipt['endEvidence']['inputsReleased'], receipt
                assert e['inventoryVerified'] and e['completedCrafts'] == e['producedCount'] == 1, e
                batch, = e['batches']
                assert batch['consumed'] == [{'typeId': recipe['woodType'], 'count': cost}], batch
                after = await lua('return bag()')
                assert after['bench'] == 1, after
                # The native receipt verifies the inventory delta on the crafting tick.
                # Additional wood may reach the actor between the two MCP bag reads.
                late_pickup = after['wood'] + cost - before['wood']
                assert late_pickup >= 0 and (tree or late_pickup == 0), (before, after)
                row = dict(world=fixture['world'], generation=fixture['generation'], slot=fixture['slot'],
                           startingWood=starting_wood, initialRecipe=initial_recipe, missing=missing,
                           tree=tree, beforeCraft=before, afterCraft=after,
                           woodPickedUpBetweenReads=late_pickup, receipt=receipt)
                records.append(row)
                (profile/'workbench-results.json').write_text(json.dumps(records, indent=2), encoding='utf-8')
                print(f'{name}: wood {starting_wood} -> {before["wood"]} -> {after["wood"]}, benches 0 -> 1',
                      flush=True)
            assert all(records[0][k] == records[1][k] for k in ('world', 'generation', 'slot'))
    finally:
        chat.shutdown()
        chat.server_close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    args = parser.parse_args()
    asyncio.run(verify(args.profile, args.port))
