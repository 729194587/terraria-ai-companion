"""Single native Merchant journey in the copied turnaround world."""
import argparse
import asyncio
import json
import uuid
from pathlib import Path

from native_direct_input import Limits, LuaRuntime, ServerAiBridge, wait_file
from native_g3_shop import TORCH, check_payment, contents


async def verify(port, profile):
    assert (profile / "isolated-action-fixture").is_file()
    await wait_file(profile / "action-fixture.json", 90)
    request = profile / "terrain-request.tmp"
    request.write_text(json.dumps(dict(scene="flat", direction=1, recovery="turnaround")))
    request.replace(profile / "terrain-request.json")
    ready = await wait_file(profile / "g3-shop-ready.json")
    bridge = ServerAiBridge(port)
    runtime = LuaRuntime(bridge, evidence_root=profile / "journey-lua")

    async def snapshot(op="snapshot"):
        uid = uuid.uuid4().hex
        reply = profile / "g3-shop-control-result.json"
        reply.unlink(missing_ok=True)
        temp = profile / "g3-shop-control.tmp"
        temp.write_text(json.dumps(dict(op=op, id=uid)))
        temp.replace(profile / "g3-shop-control.json")
        data = await wait_file(reply)
        assert data["id"] == uid
        return data

    with (profile / "merchant-journey.jsonl").open("w", encoding="utf-8") as trace:
        async def call(name, params="", action=False):
            src = f'return api("{name}",{{{params}}})'
            out = await runtime.execute(src, limits=Limits(seconds=40, calls=1, actions=int(action)))
            print(json.dumps(dict(operation=name, result=out)), file=trace, flush=True)
            assert out["status"] == "completed", out
            value = out["value"]
            assert value["valid"] and (not action or value["completed"]), value
            return value

        try:
            await snapshot("fund")
            approach = await runtime.execute('''local nearby=api("scan_entities",{category="npc",radius=800})
if not nearby.valid or not nearby.available then return nearby end
for _,npc in ipairs(nearby.data.items) do
    if npc.details.contentKey=="Terraria/Merchant" then
        local args={entityRef=npc.details.entityRef}
        local far=api("inspect_npc_shop",args)
        assert(far.available==false and far.reason=="npc_out_of_range")
        local nav,near
        for attempt=1,4 do
            nav=api("navigate_to_x",{targetX=npc.centerWorldPixels.x-10})
            if not nav.completed then return nav end
            near=api("inspect_npc_shop",args)
            if near.reason~="npc_out_of_range" and near.reason~="merchant_dialogue_required" then return near end
            local fresh=api("scan_entities",{category="npc",radius=800,typeIds={npc.typeId}})
            if not fresh.available then return fresh end
            npc=nil
            for _,row in ipairs(fresh.data.items) do
                if row.details.entityRef==args.entityRef then npc=row; break end
            end
            if not npc then return {found=false} end
            if near.reason=="merchant_dialogue_required" and npc.distancePixels<=48 then break end
        end
        assert(near.available==false and near.reason=="merchant_dialogue_required", near.reason)
        local dialogue=api("open_npc_dialogue",args)
        if not dialogue.completed then return dialogue end
        local shop=api("inspect_npc_shop",args)
        return {vendor=npc,nav=nav,far=far,near=near,dialogue=dialogue,shop=shop}
    end
end
return {found=false}''', limits=Limits(seconds=40, calls=16, actions=5))
            print(json.dumps(dict(operation="identify_and_approach", result=approach)), file=trace, flush=True)
            assert approach["status"] == "completed" and "vendor" in approach["value"], approach
            vendor = approach["value"]["vendor"]
            assert vendor["slot"] == ready["slot"] and vendor["details"]["typeName"], vendor
            nav = approach["value"]["nav"]
            assert nav["valid"] and nav["completed"], nav
            frames = await wait_file(profile / "terrain-motion-flat-1-turnaround.json")
            assert max(f["x"] for f in frames) >= 2095 * 16
            assert nav["endEvidence"]["inputsReleased"]
            scene = await call("observe_scene", 'layers={"objects"}')
            merchant = next(o for o in scene["data"]["objects"]["items"]
                            if o["contentKey"] == "Terraria/Merchant"
                            and o["details"]["detail"]["slot"] == ready["slot"])
            ref = "entityRef=" + json.dumps(merchant["details"]["detail"]["entityRef"])
            dialogue = approach["value"]["dialogue"]
            line = dialogue["evidence"]["text"]
            assert isinstance(line, str) and line.strip()
            opened = await snapshot()
            assert opened["talkNPC"] == ready["slot"]
            shop = approach["value"]["shop"]
            assert shop["valid"] and shop["available"], shop
            assert any(row["itemType"] == TORCH for row in shop["data"]["entries"])
            count = 12
            quote = (await call("quote_npc_purchase", ref + f",itemType={TORCH},quantity={count}"))["data"]
            assert quote["canAfford"] is True and quote["quantity"] == count
            await call("buy_npc_item", ref + ",quoteToken=" + json.dumps(quote["quoteToken"]), True)
            for _ in range(30):
                after = await snapshot()
                assert after["talkNPC"] == ready["slot"]
                if after["talkFrames"] - opened["talkFrames"] >= 60:
                    break
                await asyncio.sleep(.1)
            assert after["talkFrames"] - opened["talkFrames"] >= 60
            assert after["talkTravel"] < 0.1, after["talkTravel"]
            before_inv, after_inv = opened["inventory"], after["inventory"]
            assert contents(after_inv["carried"])[TORCH] - contents(before_inv["carried"])[TORCH] == count
            check_payment(before_inv, after_inv, count * quote["unitPrice"])
            unused = (await call("quote_npc_purchase", ref + f",itemType={TORCH}"))["data"]
            await call("close_npc_dialogue", action=True)
            closed = await snapshot()
            assert closed["talkNPC"] == -1
            refused = await runtime.execute('return api("buy_npc_item",{' + ref + ',quoteToken=' +
                                            json.dumps(unused["quoteToken"]) + '})', limits=Limits(seconds=15,calls=1,actions=1))
            assert refused["lastAction"]["reason"] == "merchant_dialogue_required", refused
            assert (await snapshot())["inventory"] == closed["inventory"]
            await call("open_npc_dialogue", ref, True)
            tight = await snapshot("leaveOneTorchSpace")
            q = (await call("quote_npc_purchase", ref + f",itemType={TORCH},quantity=3"))["data"]
            out = await runtime.execute('return api("buy_npc_item",{' + ref + ',quoteToken=' +
                                        json.dumps(q["quoteToken"]) + '})', limits=Limits(seconds=15,calls=1,actions=1))
            partial = out["lastAction"]
            print(json.dumps(dict(partialPurchase=partial)), file=trace, flush=True)
            assert partial["status"] == "failed" and partial["reason"] == "inventory_full", partial
            assert partial["evidence"]["verifiedQuantity"] == 1 and partial["evidence"]["effectStatus"] == "partial"
            filled = (await snapshot())["inventory"]
            assert contents(filled["carried"])[TORCH] - contents(tight["inventory"]["carried"])[TORCH] == 1
            check_payment(tight["inventory"], filled, q["unitPrice"])
            await call("close_npc_dialogue", action=True)
            closed = await snapshot()
            assert closed["talkNPC"] == -1
            print(json.dumps(dict(text=line, frames=after["talkFrames"], travel=after["talkTravel"],
                                  quantity=count, talkAfterClose=closed["talkNPC"])), file=trace)
        finally:
            bridge.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--profile", type=Path, required=True)
    args = parser.parse_args()
    asyncio.run(verify(args.port, args.profile))
