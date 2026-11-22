"""Real loaded recipes through Lua/IPC/native Player; no model. Isolated fixture only."""
import argparse
import asyncio
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'python'))
from lua_runtime import LuaRuntime,Limits
from server_ai import ServerAiBridge


async def verify(args):
    profile=args.profile.resolve();assert (profile/'isolated-action-fixture').is_file()
    rows=[]
    async def fixture(case):
        ready=profile/'craft-ready.json';ready.unlink(missing_ok=True)
        temp=profile/'craft-request.tmp';temp.write_text(json.dumps(dict(caseId=case)));temp.replace(profile/'craft-request.json')
        for _ in range(200):
            if ready.is_file():return json.loads(ready.read_text())
            await asyncio.sleep(.05)
        raise TimeoutError(case)
    async def program(case,source,actions=1,expected_failure=None):
        (profile/f'craft-{case}.lua').write_text(source,encoding='utf-8')
        bridge=ServerAiBridge(args.port)
        try:r=await LuaRuntime(bridge,evidence_root=profile/'craft-lua').execute(source,limits=Limits(actions=actions,calls=40,seconds=50))
        finally:bridge.close()
        rows.append(dict(case=case,result=r));(profile/'crafting-results.json').write_text(json.dumps(dict(records=rows),ensure_ascii=False,indent=2),encoding='utf-8')
        if expected_failure:
            assert r['status']=='failed' and r['actionState']=='released',(case,r)
            assert r['lastAction']['valid'] and r['lastAction']['reason']==expected_failure,(case,r['lastAction'])
        else:
            assert r['status']=='completed',(case,r)
        print(json.dumps(dict(case=case,calls=r['calls'],actions=r['actions'],status=r['status'])),flush=True)
        return r
    # Item type IDs are diagnostic selectors, never recipe IDs; resolve recipe registry live.
    query="local q=api('query_recipes',{resultType=OUTPUT,availableOnly=false}); assert(q.valid and #q.data.items>0); local r=q.data.items[1]; "
    craft="local a=api('craft_recipe',{recipeId=r.recipeId,recipeToken=r.recipeToken,crafts=COUNT}); "
    for case in ('insufficient','full','station_absent','station_present','group','water_absent','water_present','partial_space'):
        case_spec=await fixture(case)
        output=case_spec['resultType'] if case.startswith('water_') else 24 if case.startswith('station_') else 94 if case=='partial_space' else 8 if case=='group' else 36
        expected={'insufficient':'insufficient_materials','full':'insufficient_inventory_space','station_absent':'missing_crafting_station','water_absent':'environment_not_met','partial_space':'insufficient_inventory_space'}.get(case)
        count=3 if case=='group' else 2 if case=='partial_space' else 1
        source=query.replace('OUTPUT',str(output))+"local before=api('inspect_inventory',{mode='full'}); "+craft.replace('COUNT',str(count))+"local after=api('inspect_inventory',{mode='full'}); "
        if not expected:
            source+=f"assert(r.supported and r.available and a.valid and a.completed and a.evidence.inventoryVerified and a.evidence.completedCrafts=={count} and a.evidence.producedCount==r.output.count*{count}); "
        source+="return {recipe=r,action=a,before=before,after=after}"
        r=await program(case,source,expected_failure=expected)
        a=r['lastAction'] if expected else r['value']['action']
        assert a['endEvidence']['inputsReleased']
        if expected:
            assert not a['completed'] and a['evidence']['completedCrafts']==(1 if case=='partial_space' else 0)
            assert a['evidence']['mutationAttempted']==(case=='partial_space')
        if case=='group':assert any(i['typeId']==2503 for i in a['evidence']['batches'][0]['consumed'])  # pinned engine BorealWood
    await fixture('insufficient')
    stale=await program('stale-token',query.replace('OUTPUT','36')+"return api('craft_recipe',{recipeId=r.recipeId,recipeToken='"+'0'*64+"',crafts=1})",expected_failure='recipe_changed')
    assert not stale['lastAction']['evidence']['mutationAttempted']
    await fixture('cancel')
    bridge=ServerAiBridge(args.port)
    try:
        q=await asyncio.to_thread(bridge.command,'query_atomic',operation='query_recipes',arguments=dict(resultType=36))
        recipe=q['data']['data']['items'][0]
        await asyncio.to_thread(bridge.command,'start_atomic',operation='craft_recipe',arguments=dict(recipeId=recipe['recipeId'],recipeToken=recipe['recipeToken'],crafts=20),expectedGeneration=bridge.binding['generation'])
        stopped=await asyncio.to_thread(bridge.command,'cancel_move',actionId=bridge._action_id)
        action=stopped['movement']['action'];assert action['status']=='cancelled' and action['endEvidence']['inputsReleased']
        rows.append(dict(case='cancel',action=action));(profile/'crafting-results.json').write_text(json.dumps(dict(records=rows),ensure_ascii=False,indent=2),encoding='utf-8')
    finally:bridge.close()
    await fixture('selftest')
    sentinel=json.loads((profile/'craft-selftest.json').read_text());assert sentinel['humanAndUiUnchanged'] and sentinel['ownedCacheRestored']
    rows.append(dict(case='native-ui-and-partial-cancel',result=sentinel))
    spec=await fixture('harvest')
    source="""
local before=api('inspect_inventory',{mode='full'})
api('select_hotbar_slot',{slot=2})
local tree=api('inspect_tile',{x=TX,y=TY});assert(tree.data.cell.hasTile)
local mined=api('mine_tile',{x=TX,y=TY});assert(mined.completed)
local p=api('get_player_state',{})
local moved=api('navigate_to_x',{targetX=TX*16});assert(moved.completed)
api('wait',{milliseconds=700})
local bag=api('inspect_inventory',{mode='full'})
local found=api('resolve_content',{kind='item',query='Terraria/WorkBench'})
local q=api('query_recipes',{resultType=found.data.candidates[1].typeId})
assert(#q.data.items>0)
local r=q.data.items[1]
local made=api('craft_recipe',{recipeId=r.recipeId,recipeToken=r.recipeToken,crafts=1})
assert(made.completed and made.evidence.inventoryVerified)
return {before=before,mined=mined,moved=moved,afterHarvest=bag,recipe=r,made=made,after=api('inspect_inventory',{mode='full'})}
""".replace('TX',str(spec['tree']['x'])).replace('TY',str(spec['tree']['y']))
    await program('harvest-pickup-craft',source,actions=4)


if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--profile',type=Path,required=True);p.add_argument('--port',type=int,required=True)
    asyncio.run(verify(p.parse_args()))
