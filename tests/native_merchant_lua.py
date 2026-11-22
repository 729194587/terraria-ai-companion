"""Exercise the public shop APIs through a Lua worker in an isolated world."""
import argparse
import asyncio
import json
import time
import uuid
from pathlib import Path

from jsonschema import Draft202012Validator
from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file
from native_g3_shop import check_payment, contents, copper, TORCH
from api_result_contracts import schema_for


async def verify(port, profile):
    assert (profile / "isolated-action-fixture").exists()
    ready = await wait_file(profile / "g3-shop-ready.json")
    bridge = ServerAiBridge(port)
    runtime = LuaRuntime(bridge, evidence_root=profile / "merchant-lua")
    validators = {name: Draft202012Validator(schema_for(name))
                  for name in ("inspect_npc_shop", "quote_npc_purchase", "buy_npc_item",
                               "quote_npc_sale", "sell_npc_item")}
    trace = (profile / "merchant-lua.jsonl").open("w", encoding="utf-8")

    async def fixture(op, **kwargs):
        uid = uuid.uuid4().hex
        tmp = profile / "g3-shop-control.tmp"
        reply_path = profile / "g3-shop-control-result.json"
        reply_path.unlink(missing_ok=True)
        tmp.write_text(json.dumps(dict(id=uid, op=op, **kwargs)))
        tmp.replace(profile / "g3-shop-control.json")
        reply = await wait_file(reply_path)
        assert reply["id"] == uid
        print(json.dumps(dict(fixture=op, reply=reply)), file=trace, flush=True)
        return reply

    async def lua(source, calls, actions):
        results = []
        out = await runtime.execute(source, limits=Limits(seconds=15, calls=calls, actions=actions),
                                    on_result=lambda name, _, result: results.append((name, result)))
        print(json.dumps(dict(program=source, status=out["status"], failure=out.get("failure"),
                              results=results)), file=trace, flush=True)
        assert len(results) == calls, (out["status"], out.get("failure"), results)
        for name, result in results:
            if name in validators:
                validators[name].validate(result)
        return out, results[-1][1]

    def args(ref, extra):
        return '{entityRef=' + json.dumps(ref) + ',' + extra + '}'

    async def quote(ref):
        _, result = await lua('return api("quote_npc_purchase",' + args(ref, 'itemType=8') + ')', 1, 0)
        assert result["valid"], result
        return result["data"]

    async def rejected(ref, token, reason):
        before = (await fixture("snapshot"))["inventory"]
        _, result = await lua('return api("buy_npc_item",' + args(ref, 'quoteToken='+json.dumps(token)) + ')', 1, 1)
        assert result["status"] == "failed" and result["reason"] == reason, result
        assert result["evidence"]["mutationAttempted"] is False, result
        assert before == (await fixture("snapshot"))["inventory"], reason

    async def sale_quote(ref, slot):
        out, _ = await lua("""
local row=api("inspect_inventory",{mode="slots",slots={SLOT}}).data.inventory[1]
local q=api("quote_npc_sale",{entityRef=REF,sourceSlot=row.slot,sourceToken=row.inventoryToken})
assert(q.valid and q.data.canSell)
return q.data
""".replace("SLOT", str(slot)).replace("REF", json.dumps(ref)), 2, 0)
        assert out["status"] == "completed", out
        return out["value"]

    async def sell(q, refusal=None):
        before = (await fixture("snapshot"))["inventory"]
        params = ','.join(k+'='+json.dumps(q[k]) for k in ("entityRef","sourceSlot","sourceToken","quoteToken"))
        _, result = await lua('return api("sell_npc_item",{'+params+'})', 1, 1)
        after = (await fixture("snapshot"))["inventory"]
        if refusal:
            assert result["status"] == "failed" and result["reason"] == refusal, result
            assert result["evidence"]["mutationAttempted"] is False and before == after
        else:
            assert result["completed"] and result["evidence"]["unitPrice"] == q["unitPrice"], result
            a, b = contents(before["carried"]), contents(after["carried"])
            assert a[TORCH] == b[TORCH]+1 and copper(b)-copper(a) == q["unitPrice"] > 0
            assert before["banks"] == after["banks"]

    try:
        out, _ = await lua("""
local scene=api("observe_scene",{layers={"objects"}})
for _,o in ipairs(scene.data.objects.items) do
    if o.contentKey=="Terraria/Merchant" and o.details.detail.slot==SLOT then
        return o.details.detail.entityRef
    end
end
error("Merchant not visible")
""".replace("SLOT", str(ready["slot"])), 1, 0)
        ref = out["value"]
        assert isinstance(ref, str)
        async def talk(entity_ref):
            _, opened = await lua('return api("open_npc_dialogue",{entityRef='+json.dumps(entity_ref)+'})',1,1)
            assert opened["completed"], opened
        await talk(ref)
        empty = (await fixture("snapshot"))["inventory"]
        assert copper(contents(empty["carried"])) == 0 and copper(contents(empty["banks"][0])) == 0
        poor = await quote(ref)
        await rejected(ref, poor["quoteToken"], "insufficient_funds")
        before = (await fixture("fund"))["inventory"]
        out, receipt = await lua("""
local merchant=REF
local shop=api("inspect_npc_shop",{entityRef=merchant})
local torch=nil
for _,row in ipairs(shop.data.entries) do
    if row.itemType==8 then torch=row break end
end
assert(torch)
local q=api("quote_npc_purchase",{entityRef=merchant,itemType=torch.itemType,quantity=1})
assert(q.valid and q.data.unitPrice==torch.unitPrice)
local buy=api("buy_npc_item",{entityRef=merchant,quoteToken=q.data.quoteToken})
assert(buy.valid and buy.completed)
return {unitPrice=q.data.unitPrice,quoteToken=q.data.quoteToken}
""".replace("REF", json.dumps(ref)), 3, 1)
        assert out["status"] == "completed" and receipt["completed"], out
        paid = out["value"]["unitPrice"]
        after = (await fixture("snapshot"))["inventory"]
        a, b = contents(before["carried"]), contents(after["carried"])
        assert a[TORCH]+1 == b[TORCH] and paid > 0
        check_payment(before, after, paid, receipt["evidence"])
        await rejected(ref, out["value"]["quoteToken"], "stale_quote")
        slot = next(r["slot"] for r in after["carried"] if r["type"] == TORCH)
        dest = next(r["slot"] for r in after["carried"] if 10 <= r["slot"] < 50 and not r["stack"])
        stale_slot = await sale_quote(ref,slot)
        moved, _ = await lua("""
local rows=api("inspect_inventory",{mode="slots",slots={SRC,DST}}).data.inventory
local tokens={}
for _,r in ipairs(rows) do tokens[r.slot]=r.inventoryToken end
return api("swap_inventory_slots",{sourceSlot=SRC,destinationSlot=DST,sourceToken=tokens[SRC],destinationToken=tokens[DST]})
""".replace("SRC", str(slot)).replace("DST", str(dest)), 2, 1)
        assert moved["value"]["completed"], moved
        await sell(stale_slot, "stale_source_token")
        sale = await sale_quote(ref,dest)
        await sell(sale)
        old = await quote(ref)
        deadline = time.monotonic()+35
        while time.monotonic() < deadline:
            frame = bridge.command('query_atomic', operation='get_player_state', arguments={})['data']
            if frame['gameUpdate'] >= old['expiresAtGameUpdate']:
                break
            await asyncio.sleep(.2)
        else:
            raise TimeoutError("Server did not reach quote expiry")
        await rejected(ref, old["quoteToken"], "stale_quote")
        live = await quote(ref)
        replacement = await fixture("replace")
        assert replacement["entityRef"] != ref
        assert replacement["entityRef"].rsplit(":",1)[0] == ref.rsplit(":",1)[0]
        await rejected(ref, live["quoteToken"], "stale_entity_ref")
        fresh = replacement["entityRef"]
        await talk(fresh)
        fresh_buy = await quote(fresh)
        _, bought = await lua('return api("buy_npc_item",'+args(fresh,'quoteToken='+json.dumps(fresh_buy["quoteToken"]))+')',1,1)
        assert bought["completed"], bought
        slots = (await fixture("snapshot"))["inventory"]["carried"]
        new_slot = next(r["slot"] for r in slots if r["type"] == TORCH)
        stale_merchant = await sale_quote(fresh,new_slot)
        assert fresh != (await fixture("replace"))["entityRef"]
        await sell(stale_merchant, "stale_entity_ref")
        ref = (await fixture("snapshot"))["entityRef"]
        await talk(ref)
        price = (await quote(ref))["unitPrice"]
        cases = [[price if i == source else 0 for i in range(5)] for source in range(5)]
        cases.extend([[1, price-1, 0, 0, 0], [1, 1, 1, 1, price-4]])
        for amounts in cases:
            before = (await fixture("balances", copper=amounts))["inventory"]
            q = await quote(ref)
            assert q["canAfford"] and q["unitPrice"] == price
            _, result = await lua('return api("buy_npc_item",'+args(ref,'quoteToken='+json.dumps(q["quoteToken"]))+')',1,1)
            assert result["completed"], result
            after = (await fixture("snapshot"))["inventory"]
            check_payment(before, after, price, result["evidence"])
            assert contents(after["carried"])[TORCH] == contents(before["carried"])[TORCH]+1
        await fixture("balances", copper=[0, price, 0, 0, 0])
        stale = await quote(ref)
        await fixture("balances", copper=[0, 0, price, 0, 0])
        await rejected(ref, stale["quoteToken"], "stale_quote")
        full = (await fixture("fillInventory"))["inventory"]
        slot = next(r["slot"] for r in full["carried"] if r["type"] == TORCH)
        _, no_space = await lua("""
local i=api('inspect_inventory',{mode='slots',slots={SLOT}}).data.inventory[1]
return api('quote_npc_sale',{entityRef=REF,sourceSlot=i.slot,sourceToken=i.inventoryToken})
""".replace('SLOT',str(slot)).replace('REF',json.dumps(ref)),2,0)
        assert no_space['data']['reason'] == 'insufficient_coin_space' and not no_space['data']['canSell']
        q = await quote(ref)
        before = (await fixture("interruptPayment"))["inventory"]
        _, interrupted = await lua('return api("buy_npc_item",'+args(ref,'quoteToken='+json.dumps(q["quoteToken"]))+')',1,1)
        assert interrupted["status"] == "unknown", interrupted
        after = (await fixture("snapshot"))["inventory"]
        check_payment(before, after, price)
        assert contents(before["carried"])[TORCH] == contents(after["carried"])[TORCH]
        replay = 'return api("buy_npc_item",'+args(ref,'quoteToken='+json.dumps(q["quoteToken"]))+')'
        blocked = await runtime.execute(replay, limits=Limits(seconds=15, calls=1, actions=1))
        assert blocked["status"] == "blocked" and blocked["calls"] == 0, blocked
        assert after == (await fixture("snapshot"))["inventory"]
        print(f"Merchant Lua checks passed. Paid {paid} copper, received {sale['unitPrice']} copper; trace {trace.name}")
    finally:
        trace.close()
        bridge.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--profile", type=Path, required=True)
    args_ = parser.parse_args()
    asyncio.run(verify(args_.port, args_.profile))
