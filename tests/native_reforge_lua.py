"""Buy exactly one Goblin reforge through the public Lua API in an isolated world."""
import argparse
import asyncio
import json
import uuid
from pathlib import Path

from jsonschema import Draft202012Validator
from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file
from native_g3_shop import contents, copper
from api_result_contracts import schema_for


async def verify(port, profile):
    assert (profile / 'isolated-action-fixture').exists()
    await wait_file(profile / 'g3-shop-ready.json')
    bridge = ServerAiBridge(port)
    runtime = LuaRuntime(bridge,evidence_root=profile/'reforge-lua')
    trace = (profile/'reforge-results.jsonl').open('w',encoding='utf-8')

    async def fixture(op):
        uid = uuid.uuid4().hex
        result_path = profile/'g3-shop-control-result.json'
        result_path.unlink(missing_ok=True)
        tmp = profile/'g3-shop-control.tmp'
        tmp.write_text(json.dumps(dict(id=uid,op=op)))
        tmp.replace(profile/'g3-shop-control.json')
        result = await wait_file(result_path)
        assert result['id'] == uid
        print(json.dumps(dict(op=op,result=result)),file=trace,flush=True)
        return result

    async def lua(source, calls, actions):
        results = []
        out = await runtime.execute(source,limits=Limits(seconds=15,calls=calls,actions=actions),
                                    on_result=lambda name,_,r: results.append((name,r)))
        assert len(results) == calls, out
        for name, result in results:
            print(json.dumps(dict(op=name,result=result)),file=trace,flush=True)
            if name in ('quote_reforge','reforge_item'):
                Draft202012Validator(schema_for(name)).validate(result)
        return results[-1][1]

    async def quote(ref, slot=0):
        result = await lua("""
local i=api('inspect_inventory',{mode='slots',slots={SLOT}}).data.inventory[1]
return api('quote_reforge',{entityRef=REF,sourceSlot=i.slot,sourceToken=i.inventoryToken})
""".replace('SLOT',str(slot)).replace('REF',json.dumps(ref)),2,0)
        assert result['valid'], result
        return result['data']

    async def execute(q,reason=None):
        args=','.join(k+'='+json.dumps(q[k]) for k in ('entityRef','sourceSlot','sourceToken','quoteToken'))
        before=(await fixture('snapshot'))['inventory']
        result=await lua('return api("reforge_item",{'+args+'})',1,1)
        after=(await fixture('snapshot'))['inventory']
        if reason:
            assert result['reason']==reason and result['evidence']['mutationAttempted'] is False, result
            assert before==after
        else:
            assert result['completed'],result
            evidence=result['evidence']
            old,new=(v['carried'][q['sourceSlot']] for v in (before,after))
            assert old['type']==new['type']==q['itemType'] and old['stack']==new['stack']==1
            assert old['prefix']==q['prefix']==evidence['beforePrefix']==0
            assert new['prefix']==evidence['afterPrefix']>0 and old['favorited']==new['favorited']
            paid=copper(contents(before['carried']))-copper(contents(after['carried']))
            assert paid==evidence['priceCopper']==q['priceCopper']>0 and before['banks']==after['banks']
            print(f"Item {new['type']}: prefix 0 -> {new['prefix']}, paid {paid} copper; {trace.name}")

    try:
        ref=(await fixture('snapshot'))['entityRef']
        invalid=await quote(ref,2)
        assert invalid['reason']=='item_not_reforgeable' and invalid['quoteToken'] is None
        poor=await quote(ref)
        assert poor['reason']=='insufficient_carried_funds'
        await execute(poor,'insufficient_carried_funds')
        await fixture('fund')
        old=await quote(ref)
        replacement=(await fixture('replace'))['entityRef']
        assert ref!=replacement
        await execute(old,'stale_entity_ref')
        q=await quote(replacement)
        assert q['canReforge']
        await execute(q)
        await execute(q,'stale_source_token')
        updated=await quote(replacement)
        assert updated['prefix']>0 and updated['sourceToken']!=q['sourceToken']
    finally:
        trace.close()
        bridge.close()


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--port',type=int,required=True)
    parser.add_argument('--profile',type=Path,required=True)
    args=parser.parse_args()
    asyncio.run(verify(args.port,args.profile))
