"""Fixed MCP actions for the --pickup-feedback dedicated-server regression."""
import asyncio
import json
from pathlib import Path
import sys
import threading

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

from native_direct_input import wait_file
from test_server_ai_agent import ChatHost


async def verify_feedback(profile, port, *, ground):
    async def fixture(name):
        state = profile / "container-state.json"
        state.unlink(missing_ok=True)
        pending = profile / "pickup-feedback.tmp"
        pending.write_text(name)
        pending.replace(profile / "container-request")
        return await wait_file(state)

    root = Path(__file__).resolve().parents[1]
    chat = ChatHost()
    threading.Thread(target=chat.serve_forever, daemon=True).start()
    program = StdioServerParameters(command=sys.executable, args=[
        "-B", str(root / "python/agent.py"), "--server-ai", "--external-brain", "--mcp-transport", "stdio",
        "--ai-port", str(port), "--port", str(chat.server_address[1]), "--log-file", str(profile / "mcp.jsonl")])
    notices = []
    try:
        async with stdio_client(program) as (reader, writer), ClientSession(reader, writer) as client:
            await client.initialize()

            async def call(tool, args):
                reply = await client.call_tool(tool, args)
                assert not reply.isError, reply.content
                result = reply.structuredContent
                notices.extend(e for e in result.get("events", []) if e["source"] == "inventory_full")
                return result

            async def lua(source, *, failed=False):
                result = await call("execute_lua", {"source": source})
                assert result["status"] == ("failed" if failed else "completed"), result
                return result["lastAction"] if failed else result["value"]

            if ground:
                return await ground_cases(fixture, call, lua, notices)
            ready = json.loads((profile / "container-ready.json").read_text())
            return await chest_cases(fixture, lua, notices, ready)
    finally:
        chat.shutdown()
        chat.server_close()


async def ground_cases(fixture, call, lua, notices):
    async def wait_wood(bag, ground, seconds=6):
        async with asyncio.timeout(seconds):
            while True:
                state = await fixture("snapshot")
                carried = sum(i["stack"] for i in state["inventory"] if i["type"] == 9)
                remaining = [i for i in state["items"] if i["type"] == 9]
                if carried == bag and sum(i["stack"] for i in remaining) == ground:
                    return remaining
                await asyncio.sleep(.25)

    await fixture("seed-stackable")
    await wait_wood(9999, 0)
    await call("get_events", {})
    assert not notices, notices

    start = await fixture("seed-partial")
    target_x = int(start["x"] + 10 * 16 - 10)
    await lua(f"return api('navigate_to_x',{{targetX={target_x}}})")
    wood, = await wait_wood(9999, 4)
    await call("get_events", {"wait_seconds": 1})
    assert len(notices) == 1, notices
    text = notices[0]["text"]
    assert all(part in text for part in ("Terraria/Wood", "拾取 3 个", "剩 4 个", "补捡")), text
    await call("get_events", {})
    assert len(notices) == 1

    await lua("""local bag=api('inspect_inventory',{mode='slots',slots={12}}).data.inventory[1]
local full=api('inspect_inventory',{mode='full'}).data
return api('manage_trash',{mode='discard',slot=12,slotToken=bag.inventoryToken,trashToken=full.trash.trashToken})""")
    # The original seed holds ownership for 1800 ticks. Stand over the same drop
    # and allow the native owner selection to run once that hold expires.
    await lua(f"return api('navigate_to_x',{{targetX={int(wood['itemX'])-10}}})")
    await wait_wood(10003, 0, seconds=45)
    await call("get_events", {})
    assert len(notices) == 1, notices

    await fixture("seed-full-contact")
    await call("get_events", {"wait_seconds": 3})
    await call("get_events", {"wait_seconds": 1})
    remaining, = await wait_wood(0, 7)
    assert len(notices) == 2 and notices[1]["eventId"] != notices[0]["eventId"], notices
    assert "拾取 0 个" in notices[1]["text"] and "剩 7 个" in notices[1]["text"], notices
    return dict(notices=notices, partialRemainder=wood, woodAfterPickup=10003, fullyBlocked=remaining)


async def chest_cases(fixture, lua, notices, ready):
    x, y = ready["x"], ready["y"]
    inspect = "local box=api('inspect_container',{}).data.container;"
    loot = inspect + "return api('loot_all',{containerToken=box.containerToken})"
    close = inspect + "return api('close_container',{containerToken=box.containerToken})"
    async def open_box(tx):
        await lua(f"return api('open_container',{{x={tx},y={y}}})")

    await fixture("seed-batch")
    await fixture("batch-fit-bag")
    await open_box(x)
    fits = (await lua(loot))["evidence"]
    assert fits["movedCount"] == 2 and not fits["blocked"], fits
    await fixture("batch-fill-bag")
    outcomes = []
    for moved, reason in [(2, "container_batch_partial"), (0, "container_batch_blocked")]:
        result = await lua(loot, failed=True)
        evidence = result["evidence"]
        assert result["reason"] == reason and evidence["movedCount"] == moved, result
        assert evidence["sourceContainer"] == dict(x=x, y=y, chestIndex=ready["index"])
        assert evidence["blocked"] == [dict(slot=0, remaining=7, reason="inventory_full",
                                            typeId=9, contentKey="Terraria/Wood")], result
        assert "原箱" in result["recovery"]["nextStep"], result
        assert f"原箱 ({x},{y}) 还剩 Terraria/Wood ×7" in result["recovery"]["detail"]
        outcomes.append(evidence)
    await lua(close)
    await open_box(x + 4)
    await lua(inspect + """
local bag=api('inspect_inventory',{mode='slots',slots={12}}).data.inventory[1]
for _,s in ipairs(box.slots) do
  if s.stack==0 then return api('deposit_container_item',{inventorySlot=12,containerSlot=s.slot,count=9999,
    inventoryToken=bag.inventoryToken,containerToken=box.containerToken,slotToken=s.slotToken}) end
end""")
    await lua(close)
    await open_box(x)
    retry = (await lua(loot))["evidence"]
    state = await fixture("snapshot")
    assert retry["movedCount"] == 7 and not retry["blocked"], retry
    assert sum(i["stack"] for i in state["batchInventory"] if i["type"] == 9) == 10006
    assert state["batchChests"][0]["slots"][0]["stack"] == 0 and not notices
    return dict(stackable=fits, partial=outcomes[0], blocked=outcomes[1], recovered=retry)
