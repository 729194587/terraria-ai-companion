"""Isolated native Nurse visit through the public Lua operations."""
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
    runtime = LuaRuntime(bridge, evidence_root=profile / 'nurse-lua')
    trace = (profile / 'nurse-results.jsonl').open('w', encoding='utf-8')

    async def fixture(op):
        uid = uuid.uuid4().hex
        result_path = profile / 'g3-shop-control-result.json'
        result_path.unlink(missing_ok=True)
        tmp = profile / 'g3-shop-control.tmp'
        tmp.write_text(json.dumps(dict(op=op, id=uid)))
        tmp.replace(profile / 'g3-shop-control.json')
        result = await wait_file(result_path)
        assert result['id'] == uid
        print(json.dumps(dict(op=op, snapshot=result)), file=trace, flush=True)
        return result

    async def call(name, ref, token=None):
        args = 'entityRef='+json.dumps(ref)
        if token:
            args += ',quoteToken='+json.dumps(token)
        results = []
        await runtime.execute('return api("'+name+'",{'+args+'})',
                              limits=Limits(seconds=15, calls=1, actions=int(token is not None)),
                              on_result=lambda _, __, r: results.append(r))
        result, = results
        print(json.dumps(dict(op=name,result=result)), file=trace, flush=True)
        Draft202012Validator(schema_for(name)).validate(result)
        return result

    async def quote(ref):
        q = await call('quote_nurse_heal',ref)
        assert q['valid'], q
        return q['data']

    async def refuse(ref, q, reason):
        before = await fixture('snapshot')
        result = await call('heal_at_nurse',ref,q['quoteToken'])
        after = await fixture('snapshot')
        assert result['status'] == 'failed' and result['reason'] == reason, result
        assert result['evidence']['mutationAttempted'] is False
        assert all(before[k] == after[k] for k in ('life','inventory','buffs'))

    try:
        ref = (await fixture('snapshot'))['entityRef']
        await fixture('injure')
        poor = await quote(ref)
        assert poor['reason'] == 'insufficient_carried_funds'
        await refuse(ref,poor,'insufficient_carried_funds')
        await fixture('fund')
        stale = await quote(ref)
        await fixture('injure')
        await refuse(ref,stale,'stale_quote')
        old = await quote(ref)
        replacement = await fixture('replace')
        await refuse(ref,old,'stale_entity_ref')
        ref = replacement['entityRef']
        q = await quote(ref)
        assert q['canTreat'] and q['heal'] > 0 and q['removableDebuffs']
        before = await fixture('snapshot')
        receipt = await call('heal_at_nurse',ref,q['quoteToken'])
        assert receipt.get('completed'), receipt
        after = await fixture('snapshot')
        assert before['life'] + q['heal'] == after['life'] == q['maxLife']
        inv = before['inventory']
        assert copper(contents(inv['carried'])) - copper(contents(after['inventory']['carried'])) == q['priceCopper']
        assert inv['banks'] == after['inventory']['banks']
        expected = set(before['buffs']) - set(q['removableDebuffs'])
        assert expected and set(after['buffs']) == expected
        await refuse(ref,q,'stale_quote')
        healthy = await quote(ref)
        assert healthy['reason'] == 'no_paid_treatment' and healthy['quoteToken'] is None
        print(f"Nurse verified: HP {before['life']} -> {after['life']}, price {q['priceCopper']} copper, "
              f"removed {q['removableDebuffs']}, retained {sorted(expected)}; {trace.name}")
    finally:
        bridge.close()
        trace.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--port',type=int,required=True)
    parser.add_argument('--profile',type=Path,required=True)
    args = parser.parse_args()
    asyncio.run(verify(args.port,args.profile))
