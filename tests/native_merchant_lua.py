"""Exercise the public shop APIs through a Lua worker in an isolated world."""
import argparse
import asyncio
import json
import time
import uuid
from pathlib import Path

from jsonschema import Draft202012Validator
from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file
from native_g3_shop import contents, copper, TORCH
from api_result_contracts import schema_for


async def verify(port, profile):
    assert (profile / "isolated-action-fixture").exists()
    ready = await wait_file(profile / "g3-shop-ready.json")
    bridge = ServerAiBridge(port)
    runtime = LuaRuntime(bridge, evidence_root=profile / "merchant-lua")
    validators = {name: Draft202012Validator(schema_for(name))
                  for name in ("inspect_npc_shop", "quote_npc_purchase", "buy_npc_item")}
    trace = (profile / "merchant-lua.jsonl").open("w", encoding="utf-8")

    async def fixture(op):
        uid = uuid.uuid4().hex
        tmp = profile / "g3-shop-control.tmp"
        reply_path = profile / "g3-shop-control-result.json"
        reply_path.unlink(missing_ok=True)
        tmp.write_text(json.dumps(dict(id=uid, op=op)))
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
        empty = (await fixture("snapshot"))["inventory"]
        assert copper(contents(empty["carried"])) == 0 and copper(contents(empty["banks"][0])) == 10000
        poor = await quote(ref)
        await rejected(ref, poor["quoteToken"], "insufficient_carried_funds")
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
        assert a[TORCH]+1 == b[TORCH] and copper(a)-copper(b) == paid > 0
        assert before["banks"] == after["banks"]
        await rejected(ref, out["value"]["quoteToken"], "stale_quote")
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
        print(f"Merchant Lua checks passed. Paid {paid} copper; trace {trace.name}")
    finally:
        trace.close()
        bridge.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--profile", type=Path, required=True)
    args_ = parser.parse_args()
    asyncio.run(verify(args_.port, args_.profile))
