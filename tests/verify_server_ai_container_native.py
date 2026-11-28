"""Run P0 against a copied world and two connected tModLoader wire clients."""
import argparse
import asyncio
import json
from pathlib import Path
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import time

from jsonschema import validate

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))
from atomic_operations import dispatch
from server_ai import ServerAiBridge
from server_ai_contract import api_help


class Client:
    def __init__(self, port, name):
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.stream = self.sock.makefile("rb")
        self.chest = -1
        self.remotes = {}
        self.send(1, self.string("tModLoader v2026.9.3.1!2026.9.3"))
        self.wait(251)
        self.send(251)
        self.slot = self.wait(3)[0]
        self.send(4, bytes([self.slot, 0, 0]) + self.string(name) + bytes(28))
        self.send(16, struct.pack("<Bhh", self.slot, 100, 100))
        self.send(6)
        self.wait(7)
        self.send(8, struct.pack("<ii", -1, -1))
        self.wait(49)
        self.send(12, struct.pack("<BhhihhB", self.slot, -1, -1, 0, 0, 0, 0))
        self.wait(129)

    @staticmethod
    def string(value):
        data = value.encode()
        assert len(data) < 128
        return bytes([len(data)]) + data

    def send(self, packet, body=b""):
        self.sock.sendall(struct.pack("<HB", len(body) + 3, packet) + body)

    def wait(self, packet, match=lambda _: True):
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            header = self.stream.read(2)
            if len(header) != 2:
                raise RuntimeError(f"Disconnected while waiting for packet {packet}")
            data = self.stream.read(struct.unpack("<H", header)[0] - 2)
            kind, body = data[0], data[1:]
            if kind == 2:
                raise RuntimeError(f"Server rejected client: {body!r}")
            if kind == 33:
                self.chest = struct.unpack_from("<h", body)[0]
            if kind == 80:
                slot, index = struct.unpack("<Bh", body)
                self.remotes[slot] = index
            if kind == packet and match(body):
                return body
        raise TimeoutError(f"Missing packet {packet}")

    @staticmethod
    def item(body):
        values = []
        value = shift = 0
        for byte in body:
            value |= (byte & 127) << shift
            if byte < 128:
                values.append(value)
                if len(values) == 3:
                    break
                value = shift = 0
            else:
                shift += 7
        return tuple(values)  # type, prefix, stack

    def expect_item(self, packet, owner, slot, item):
        header = struct.pack("<Bh" if packet == 5 else "<hB", owner, slot)
        self.wait(packet, lambda body: body.startswith(header) and self.item(body[3:]) == item)

    def expect_chest(self, actor, index):
        self.wait(80, lambda body: struct.unpack("<Bh", body) == (actor, index))

    def close(self):
        self.stream.close()
        self.sock.close()


def wait_for(predicate, seconds=20):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(.05)
    raise TimeoutError("Native test condition timed out")


async def verify(profile, bridge, first, second, *, transfers=False, batches=False, sorting=False, personal=False, search=False):
    ready = json.loads((profile / "container-ready.json").read_text())
    x, y, index, other = (ready[key] for key in ("x", "y", "index", "other"))
    actor = bridge.binding["slot"]
    report = {"clients": [first.slot, second.slot], "actorSlot": actor, "cases": {}}
    cases = report["cases"]
    report["sync"] = []

    async def api(name, arguments=None, *, status="completed", reason=None):
        result = await dispatch(bridge, name, arguments or {})
        help_result = api_help(bridge, {"name": name})
        assert help_result["status"] == "ok", help_result
        validate(result, help_result["returnsSchema"])
        actual = result.get("status", result.get("data", {}).get("status"))
        assert actual == status, result
        if reason:
            assert result.get("reason", result.get("data", {}).get("reason")) == reason, result
        cases.setdefault(name, []).append(result)
        return result

    async def open_at(tx, ty=y, **expected):
        return await api("open_container", {"x": tx, "y": ty}, **expected)

    async def inspect():
        return (await api("inspect_container", status="open"))["data"]["container"]

    async def close(token, **expected):
        return await api("close_container", {"containerToken": token}, **expected)

    def sync(chest):
        first.expect_chest(actor, chest)
        second.expect_chest(actor, chest)
        report["sync"].append({"chestIndex": chest, "receivedBy": [first.slot, second.slot]})

    def fixture(command):
        target = profile / "container-request"
        response = profile / "container-state.json"
        response.unlink(missing_ok=True)
        pending = profile / "container-request.tmp"
        pending.write_text(command)
        pending.replace(target)
        def read():
            try:
                data = json.loads(response.read_text())
                return data if data["command"] == command else None
            except (FileNotFoundError, json.JSONDecodeError):
                return None
        return wait_for(read)

    if search:
        from lua_runtime import LuaRuntime
        runtime = LuaRuntime(bridge)
        seed = fixture("seed-torch-search")
        item = {"item": "Terraria/Torch"}
        found = (await api("find_container_item", item, status="ok"))["data"]
        assert [(c["x"], c["y"], c["count"]) for c in found["chests"]] == [
            (x, y, 6), (x+4, y, 5), (x+16, y, 13)], found
        assert found["totalCount"] == 24 and found["matchedChests"] == 3 and not found["truncated"], found
        base = (await runtime.execute('return terraria.container.base("Terraria/Torch")'))["value"]
        base_counts = {(c["x"],c["y"]): c["count"] for c in base["chests"]}
        assert all(c["isBase"] and base_counts[c["x"],c["y"]] == c["count"] for c in found["chests"])
        assert base_counts[x+42,y] == 70 and (x+8,y) not in base_counts
        assert all(c["isBase"] and c["distanceTiles"] <= 64 for c in base["chests"])
        all_base = (await runtime.execute('return terraria.container.base()'))["value"]
        assert any((c["x"], c["y"], c["count"]) == (x+20, y, 0) for c in all_base["chests"])
        page = (await api("find_container_item", {**item, "offset": 2}, status="ok"))["data"]
        assert page["chests"] == found["chests"][2:] and page["totalCount"] == 24
        local = (await api("find_container_item", {**item, "radiusTiles": 3}, status="ok"))["data"]
        assert local["chests"] == found["chests"][:1] and local["totalCount"] == 6
        empty = await runtime.execute('return terraria.container.find("Terraria/BrokenHeroSword", 3)')
        assert empty["value"]["status"] == "ok" and empty["value"]["chests"] == [], empty
        unknown = await runtime.execute('return terraria.container.find("Terraria/MissingItemForSearchTest")')
        assert unknown["value"] == {"status": "unavailable", "reason": "unknown_item"}, unknown
        snapshot = fixture("snapshot")
        assert seed["actorChest"] == snapshot["actorChest"] == -1
        assert all(snapshot[k] == seed[k] for k in ("batchInventory", "batchChests"))
        # A search must also preserve the token issued for an already open chest.
        await open_at(x)
        token = (await inspect())["containerToken"]
        await api("find_container_item", item, status="ok")
        await close(token)
        result = await runtime.execute("""
local found = terraria.container.find("Terraria/Torch")
for _, chest in ipairs(found.chests) do
    if chest.count >= 12 then return terraria.container.take(chest, "Terraria/Torch", 12) end
end""")
        receipt = result.get("value")
        assert receipt and receipt["status"] == "completed" and receipt["moved"] == 12, result
        assert (receipt["x"], receipt["y"]) == (x+16, y)
        fresh = (await api("find_container_item", {"item": "Terraria/Torch", "radiusTiles": 20}, status="ok"))["data"]
        assert next(c["count"] for c in fresh["chests"] if c["x"] == x+16 and c["y"] == y) == 1
        after = fixture("snapshot")
        assert sum(s["stack"] for s in after["batchInventory"] if s["type"] == found["typeId"]) == 12
        assert after["actorChest"] == -1
        report["take"] = receipt
        return report

    if personal:
        async def bag(slot):
            return (await dispatch(bridge,"inspect_inventory",{"mode":"slots","slots":[slot]}))["data"]["inventory"][0]
        async def full():
            return (await dispatch(bridge,"inspect_inventory",{"mode":"full"}))["data"]

        fixture("personal-seed")
        for name, index in (("piggy",-2),("safe",-3),("forge",-4),("void",-5)):
            fixture("personal-"+name)
            await open_at(x-3,y+1)
            chest=await inspect()
            assert chest["chestIndex"]==index and chest["slots"][0]["stack"]==0
            for action in ("deposit_container_item","withdraw_container_item"):
                await api(action,dict(inventorySlot=20, containerSlot=0, count=3,
                          inventoryToken=(await bag(20))["inventoryToken"],containerToken=chest["containerToken"],
                          slotToken=chest["slots"][0]["slotToken"]))
                chest=await inspect()
                assert chest["slots"][0]["stack"]==(3 if action.startswith("deposit") else 0)
            await close(chest["containerToken"])
            for human in fixture("snapshot")["clients"]:
                assert human["bankCounts"]==[human["slot"]+1]*4
        for mode,slot in (("discard",20),("retrieve",23)):
            await api("manage_trash", dict(mode=mode,slot=slot,slotToken=(await bag(slot))["inventoryToken"],
                      trashToken=(await full())["trash"]["trashToken"]))
        assert (await bag(23))["stack"]==10
        async def favorite(value):
            return await api("set_inventory_favorite",dict(sourceSlot=23,sourceToken=(await bag(23))["inventoryToken"],
                             favorited=value))
        await favorite(True)
        await api("drop_inventory_item",dict(sourceSlot=23,sourceToken=(await bag(23))["inventoryToken"],count=3),
                  status="failed",reason="empty_or_favorited_slot")
        await favorite(False)
        teammates=(await dispatch(bridge,"list_teammates",{}))["data"]["players"]
        recipient=teammates[0]["teammateRef"]
        request=dict(sourceSlot=23,sourceToken=(await bag(23))["inventoryToken"],count=3,teammateRef=recipient)
        await api("drop_inventory_item",request | {"count":11},status="failed",reason="insufficient_stack")
        drop=await api("drop_inventory_item",request)
        await api("drop_inventory_item",request,status="failed",reason="stale_inventory_token")
        assert drop["evidence"]["spawnedCount"]==3 and (await bag(23))["stack"]==7
        for slot,gear in ((21,"vanity:head"),(22,"dye:head"),(24,"miscDye:mount")):
            rows=(await full())["cosmeticEquipmentSlots"]
            tok=next(r["targetToken"] for r in rows if r["slotId"]==gear)
            await api("equip_item",dict(sourceSlot=slot,sourceToken=(await bag(slot))["inventoryToken"],
                      targetSlotId=gear,targetToken=tok))
        original=(await full())["currentLoadout"]
        target=1 if original==2 else 2
        await api("switch_equipment_loadout",dict(expectedLoadout=original,loadout=target))
        await api("switch_equipment_loadout",dict(expectedLoadout=target,loadout=original))
        rows=(await full())["cosmeticEquipmentSlots"]
        assert all(next(r["occupied"] for r in rows if r["slotId"]==gear) for gear in ("vanity:head","dye:head","miscDye:mount"))
        return report

    first.send(31, struct.pack("<hh", x + 4, y))
    first.wait(33)
    second.expect_chest(first.slot, other)
    await open_at(x + 4, status="failed", reason="container_in_use")
    await open_at(x + 8, status="failed", reason="container_locked")
    await open_at(x + 20, status="failed", reason="out_of_native_interaction_range")
    await open_at(x + 1, y + 1)
    sync(index)
    second.send(31, struct.pack("<hh", x, y))
    second.send(31, struct.pack("<hh", x + 20, y))
    second.wait(33)
    second_chest = second.chest
    assert second_chest >= 0 and second_chest != index
    assert first.chest == other
    baseline = fixture("snapshot")
    assert {row["slot"]: row["chest"] for row in baseline["clients"]} == {
        first.slot: other, second.slot: second_chest}
    assert baseline["actorChest"] == index
    report["baseline"] = baseline

    if sorting:
        await verify_sorting(bridge, (first, second), report, api, inspect, fixture, index)
        return report
    if batches:
        await verify_batches(bridge, (first, second), report, api, inspect, open_at, close, sync, fixture, x, index)
        return report
    if transfers:
        await verify_transfers(bridge, (first, second), report, api, inspect, open_at, close, sync, fixture, x, y, index)
        return report

    old = await inspect()
    new = await inspect()
    assert (new["x"], new["y"], new["typeId"], new["chestIndex"]) == (x, y, 21, index)
    assert new["slots"][0]["typeId"] == 9 and new["slots"][0]["stack"] == 7
    assert old["containerToken"] != new["containerToken"]
    assert old["slots"][0]["slotToken"] != new["slots"][0]["slotToken"]
    await close(old["containerToken"], status="failed", reason="stale_container_token")
    await close(new["containerToken"])
    sync(-1)
    await api("inspect_container", status="unavailable")

    await open_at(x)
    sync(index)
    before_replacement = await inspect()
    fixture("replace")
    sync(-1)
    await open_at(x)
    sync(index)
    after_replacement = await inspect()
    assert after_replacement["chestIndex"] == before_replacement["chestIndex"]
    await close(before_replacement["containerToken"], status="failed", reason="stale_container_token")
    assert fixture("snapshot")["actorChest"] == index
    fixture("move-away")
    sync(-1)
    await api("inspect_container", status="unavailable")
    fixture("move-back")
    await open_at(x)
    sync(index)
    fixture("missing")
    sync(-1)
    await open_at(x, status="failed", reason="container_record_missing")
    fixture("replace")
    await open_at(x)
    sync(index)
    fixture("destroy")
    sync(-1)
    await api("inspect_container", status="unavailable")
    report["afterCleanup"] = fixture("snapshot")
    assert report["afterCleanup"]["clients"] == baseline["clients"]

    first.send(33, struct.pack("<hhhB", -1, 0, 0, 0))
    wait_for(lambda: fixture("snapshot")["clients"][0]["chest"] == -1)
    await open_at(x + 4)
    sync(other)
    assert (await inspect())["typeId"] == 467
    fixture("invalidate")
    sync(-1)
    await api("inspect_container", status="unavailable")
    fixture("restore-actor")
    await open_at(x + 4)
    sync(other)
    report["openAtWorldUnload"] = other
    return report


async def verify_transfers(bridge, clients, report, api, inspect, open_at, close, sync, fixture, x, y, index):
    actor = bridge.binding["slot"]
    baseline = report["baseline"]["clients"]

    async def bag(slot):
        result = await dispatch(bridge, "inspect_inventory", {"mode": "slots", "slots": [slot]})
        return result["data"]["inventory"][0]

    async def args(inventory_slot, container_slot, count):
        inventory = await bag(inventory_slot)
        container = await inspect()
        return dict(inventorySlot=inventory_slot, containerSlot=container_slot, count=count,
                    inventoryToken=inventory["inventoryToken"], containerToken=container["containerToken"],
                    slotToken=container["slots"][container_slot]["slotToken"])

    def packets(inventory_slot, container_slot, inventory_count, container_count, item_type=9):
        for client in clients:
            client.expect_item(5, actor, inventory_slot, (item_type if inventory_count else 0, 0, inventory_count))
            client.expect_item(32, index, container_slot, (item_type if container_count else 0, 0, container_count))
        report.setdefault("itemSync", []).append(dict(inventorySlot=inventory_slot, containerSlot=container_slot,
            inventoryCount=inventory_count, containerCount=container_count, receivedBy=[client.slot for client in clients]))

    async def move(deposit, request, *, moved, reason=None):
        name = "deposit_container_item" if deposit else "withdraw_container_item"
        result = await api(name, request, status="completed" if moved == request["count"] else "failed", reason=reason)
        evidence = result["evidence"]
        assert evidence["movedCount"] == moved and evidence["remainingCount"] == request["count"] - moved, result
        if moved:
            assert result["effectVerified"] is True
            assert evidence["effectStatus"] == ("complete" if moved == request["count"] else "partial")
            before, after = evidence["before"], evidence["after"]
            assert before["source"]["stack"] + before["destination"]["stack"] == after["source"]["stack"] + after["destination"]["stack"]
        else:
            assert evidence["effectStatus"] == "none"
        return evidence

    fixture("seed-transfer")
    initial = await args(10, 1, 12)
    await move(True, initial, moved=12)
    packets(10, 1, 18, 12)
    await move(True, initial, moved=0, reason="stale_container_token")
    await move(False, await args(11, 1, 5), moved=5)
    packets(11, 1, 5, 7)
    await move(True, await args(10, 2, 5), moved=2, reason="container_transfer_partial_capacity")
    packets(10, 2, 16, 9999)
    await move(True, await args(10, 3, 2), moved=0, reason="container_destination_full")
    await move(False, await args(12, 1, 2), moved=0, reason="container_destination_full")
    await move(False, await args(11, 1, 8), moved=0, reason="container_source_count_insufficient_or_invalid")
    await move(False, await args(11, 1, 7), moved=7)
    packets(11, 1, 12, 0)
    assert (await bag(10))["stack"] == 16 and (await bag(11))["stack"] == 12

    await move(True, await args(10, 1, 4), moved=4)
    packets(10, 1, 12, 4)
    fixture("partial-bag")
    await move(False, await args(12, 1, 4), moved=2, reason="container_transfer_partial_capacity")
    packets(12, 1, 9999, 2)
    fixture("fill")
    await move(True, await args(10, 1, 2), moved=0, reason="container_destination_full")
    await move(False, await args(11, 1, 2), moved=0, reason="container_destination_full")
    fixture("seed-transfer")
    expired = await args(10, 1, 1)
    fixture("expire")
    await move(True, expired, moved=0, reason="stale_container_token")
    stale_inventory = {**await args(10, 1, 1), "inventoryToken": "0" * 64}
    await move(True, stale_inventory, moved=0, reason="stale_inventory_snapshot")
    stale_slot = {**await args(10, 1, 1), "slotToken": "0" * 64}
    await move(True, stale_slot, moved=0, reason="stale_container_slot")

    prior = await args(10, 1, 1)
    await close(prior["containerToken"])
    sync(-1)
    await move(True, prior, moved=0, reason="stale_container_token")
    await open_at(x)
    sync(index)
    prior = await args(10, 1, 1)
    fixture("move-away")
    sync(-1)
    await move(True, prior, moved=0, reason="stale_container_token")
    fixture("move-back")
    await open_at(x)
    sync(index)
    prior = await args(10, 1, 1)
    fixture("replace")
    sync(-1)
    await open_at(x)
    sync(index)
    await move(True, prior, moved=0, reason="stale_container_token")

    fixture("seed-mod")
    await move(True, await args(10, 1, 4), moved=0, reason="container_stacking_denied")
    fixture("allow-mod")
    item_type = (await bag(10))["typeId"]
    await move(True, await args(10, 1, 4), moved=4)
    packets(10, 1, 16, 6, item_type)
    assert fixture("snapshot")["modStackCalls"] == 1
    fixture("throw-mod")
    uncertain = await api("deposit_container_item", await args(10, 1, 1), status="unknown",
                          reason="container_transfer_native_exception")
    assert uncertain["evidence"]["movedCount"] is None and uncertain["effectVerified"] is False
    assert fixture("snapshot")["clients"] == baseline

    prior = await args(10, 1, 1)
    fixture("generation")
    sync(-1)
    rebound = ServerAiBridge(port=bridge.sock.getpeername()[1])
    try:
        assert rebound.binding["generation"] > bridge.binding["generation"]
        result = await dispatch(rebound, "inspect_container", {})
        assert result["data"]["status"] == "unavailable"
        rejected = await dispatch(rebound, "deposit_container_item", prior)
        assert rejected["reason"] == "stale_container_token"
        reopened = await dispatch(rebound, "open_container", {"x": x, "y": y})
        assert reopened["completed"]
        sync(index)
    finally:
        rebound.close()
    report["generationCleanup"] = {"oldGeneration": bridge.binding["generation"], "actorChest": index}
    fixture("retire")
    sync(-1)
    report["retireWhileOpen"] = {"closeReceivedBy": [client.slot for client in clients]}


async def verify_batches(bridge, clients, report, api, inspect, open_at, close, sync, fixture, x, index):
    from collections import Counter
    actor = bridge.binding["slot"]
    baseline = report["baseline"]["clients"]

    def total(state):
        counts = Counter()
        for item in state["batchInventory"] + [item for chest in state["batchChests"] for item in chest["slots"]]:
            if item["type"] and item["stack"]:
                counts[item["type"], item["prefix"]] += item["stack"]
        return counts

    async def batch(name, count, *, effect="complete", token=None):
        arguments = {} if name == "quick_stack_nearby" else {
            "containerToken": token or (await inspect())["containerToken"]}
        before = fixture("snapshot")
        status = "unknown" if effect == "unknown" else "failed" if effect in ("partial", "blocked") else "completed"
        result = await api(name, arguments, status=status)
        evidence = result["evidence"]
        assert evidence["effectStatus"] == ("none" if effect == "blocked" else effect), result
        assert evidence["movedCount"] == count, result
        after = fixture("snapshot")
        assert after["clients"] == baseline
        if effect == "unknown":
            return after
        assert total(before) == total(after), result
        bag_slots = {move["inventorySlot"] for move in evidence["moves"]}
        chest_slots = {(move["chestIndex"], move["containerSlot"]) for move in evidence["moves"]}
        chests = {chest["index"]: chest["slots"] for chest in after["batchChests"]}
        expected = []
        for slot in sorted(bag_slots):
            expected.append((5, actor, slot, after["batchInventory"][slot]))
        for chest, slot in sorted(chest_slots):
            expected.append((32, chest, slot, chests[chest][slot]))
        for client in clients:
            for packet, owner, slot, item in expected:
                client.expect_item(packet, owner, slot, (item["type"], item["prefix"], item["stack"]))
        report.setdefault("batchSync", []).append({"operation": name, "slots": len(expected),
                                                    "receivedBy": [client.slot for client in clients]})
        return after

    fixture("seed-batch")
    await batch("deposit_all", 0, effect="none")
    await batch("loot_all", 0, effect="none")
    fixture("seed-nearby")
    token = (await inspect())["containerToken"]
    after = await batch("quick_stack_container", 11, token=token)
    assert [item["stack"] for item in after["batchChests"][0]["slots"][:3]] == [9999, 9999, 8]
    assert [after["batchInventory"][slot]["stack"] for slot in (0, 11, 12, 13)] == [2, 3, 4, 13]
    await api("deposit_all", {"containerToken": token}, status="failed", reason="stale_container_token")
    await close((await inspect())["containerToken"])
    sync(-1)

    fixture("seed-nearby")
    after = await batch("quick_stack_nearby", 24)
    assert after["batchChests"][1]["slots"][1]["stack"] == 18
    assert [chest["slots"][1]["stack"] for chest in after["batchChests"][2:]] == [5, 5, 5]
    await open_at(x)
    sync(index)
    fixture("seed-batch")
    fixture("seed-nearby")
    after = await batch("deposit_all", 28)
    assert after["batchInventory"][0]["stack"] == 2 and after["batchInventory"][11]["favorited"]

    fixture("seed-batch")
    fixture("batch-fill-chest")
    await batch("deposit_all", 2, effect="partial")
    await batch("quick_stack_container", 0, effect="blocked")
    fixture("seed-batch")
    fixture("batch-fill-bag")
    await batch("loot_all", 2, effect="partial")
    await batch("loot_all", 0, effect="blocked")
    fixture("seed-batch")
    fixture("batch-restock")
    after = await batch("restock", 5, effect="partial")
    assert after["batchInventory"][9]["favorited"] and after["batchChests"][0]["slots"][1]["stack"] == 3
    async def bag_token(slot):
        view = await dispatch(bridge, "inspect_inventory", {"mode": "slots", "slots": [slot]})
        return view["data"]["inventory"][0]["inventoryToken"]

    await batch("loot_all", 7)
    fixture("seed-batch")
    fixture("batch-roundtrip")
    old_empty = await bag_token(49)
    await batch("loot_all", 7)
    await batch("deposit_all", 7)
    assert (await bag_token(49)) != old_empty
    fixture("seed-batch")
    fixture("seed-mod")
    await batch("quick_stack_container", 20)
    assert fixture("snapshot")["modStackCalls"] == 0
    fixture("seed-batch")
    fixture("seed-mod")
    fixture("allow-mod")
    await batch("quick_stack_container", 20)
    assert fixture("snapshot")["modStackCalls"] == 1
    fixture("seed-batch")
    fixture("seed-mod")
    fixture("throw-mod")
    await batch("quick_stack_container", None, effect="unknown")


async def verify_sorting(bridge, clients, report, api, inspect, fixture, index):
    from collections import Counter

    async def bag_token(slot):
        result = await dispatch(bridge, "inspect_inventory", {"mode": "slots", "slots": [slot]})
        return result["data"]["inventory"][0]["inventoryToken"]

    def totals(rows):
        counts = Counter()
        for item in rows:
            if not item["type"] or not item["stack"]:
                continue
            if 71 <= item["type"] <= 74:
                counts["coinValue"] += item["stack"] * 100 ** (item["type"] - 71)
            else:
                counts[item["type"], item["prefix"]] += item["stack"]
        return counts

    async def check(name, arguments, key, other_key):
        before = fixture("snapshot")
        result = await api(name, arguments)
        after = fixture("snapshot")
        assert after["clients"] == report["baseline"]["clients"]
        assert before["selectedItem"] == after["selectedItem"]
        assert before[other_key] == after[other_key]
        assert totals(before[key]) == totals(after[key])
        changes = [slot for slot, (left, right) in enumerate(zip(before[key], after[key])) if left != right]
        assert changes == [row["slot"] for row in result["evidence"]["changedSlots"]], result
        packet, owner = (5, bridge.binding["slot"]) if key == "sortInventory" else (32, index)
        for client in clients:
            for slot in changes:
                item = after[key][slot]
                client.expect_item(packet, owner, slot, (item["type"], item["prefix"], item["stack"]))
        report.setdefault("sortSync", []).append({"operation": name, "slots": changes, "receivedBy": [c.slot for c in clients]})
        return before[key], after[key]

    fixture("seed-sort")
    old_bag = await bag_token(0)
    before, after = await check("sort_inventory", {}, "sortInventory", "sortChest")
    assert after[0] == before[0] and after[14] == before[14]
    assert sum(item["stack"] for item in after[54:58] if item["type"] == 40) == 4
    assert any(item["type"] == 72 and item["stack"] == 1 for item in after[50:54])
    assert any(item["instance"] == before[28]["instance"] and item["stack"] == 7 and item["state"] == 8 for item in after)
    assert await bag_token(0) != old_bag
    stale = await dispatch(bridge, "transfer_inventory_item", {
        "sourceSlot": 0, "destinationSlot": 49, "count": 1,
        "sourceToken": old_bag, "destinationToken": await bag_token(49)})
    assert stale["status"] == "failed" and stale["reason"] == "stale_inventory_snapshot"
    report["staleInventoryToken"] = stale

    token = (await inspect())["containerToken"]
    before, after = await check("sort_container", {"containerToken": token}, "sortChest", "sortInventory")
    assert after[8] == before[8]
    assert any(item["instance"] == before[15]["instance"] and item["stack"] == 16 for item in after[:8])
    assert any(item["instance"] == before[29]["instance"] and item["state"] == 17 for item in after[:8])
    await api("sort_container", {"containerToken": token}, status="failed", reason="stale_container_token")
    await api("sort_container", {"containerToken": (await inspect())["containerToken"]})
    fixture("seed-sort-throw")
    try:
        await api("sort_container", {"containerToken": (await inspect())["containerToken"]},
                  status="unknown", reason="native_sort_exception")
    finally:
        fixture("clear-sort-fixture")


async def verify_collect(profile, bridge, clients, engine_log):
    from program_tasks import TaskCoordinator, validate as validate_task
    tasks = TaskCoordinator(bridge)
    report = {"clients": [client.slot for client in clients], "cases": {}}

    def record(label, result):
        report["cases"][label] = result
        (profile / "collect-case-receipts.json").write_text(json.dumps(report, indent=2), encoding="utf-8")

    def queue_fixture(command):
        response = profile / "container-state.json"
        response.unlink(missing_ok=True)
        request = profile / "container-request.tmp"
        request.write_text(command)
        request.replace(profile / "container-request")
        def read():
            try:
                data = json.loads(response.read_text())
                return data if data["command"] == command else None
            except (FileNotFoundError, json.JSONDecodeError):
                return None
        return lambda: wait_for(read)

    def fixture(command):
        return queue_fixture(command)()

    async def call(name, args):
        result = await tasks.invoke(name, validate_task(name, args))
        detail = api_help(bridge, {"name": name})
        assert detail["status"] == "ok", detail
        try:
            validate(result, detail["returnsSchema"])
        except Exception:
            (profile / "collect-invalid-receipt.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
            raise
        return result

    async def start(arguments):
        result = await call("submit_task", {"operation": "collect_drops", "arguments": arguments})
        assert result["status"] == "running", result
        return {"taskId": result["taskId"]}

    async def run(label, arguments, *, status="completed", intervene=None):
        gated = intervene in ("allow-contact-pickup", "move-drop", "merge-drop", "remove-drop")
        pending = queue_fixture(intervene) if gated else None
        task = await start(arguments)
        if intervene:
            if gated:
                intervention = await asyncio.to_thread(pending)
            else:
                await asyncio.sleep(.4)
                intervention = fixture(intervene)
            if intervene == "move-drop":
                assert intervention["vy"] < -0.1, intervention
                report["movingIntervention"] = {key: intervention[key] for key in ("x", "y", "vx", "vy", "inputsReleased")}
        result = await call("await_task", {**task, "milliseconds": 28000})
        (profile / "collect-latest.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        if result["status"] != status:
            fixture("snapshot")
        assert result["status"] == status, (label, result["status"], result.get("result", {}).get("reason"))
        queried = await call("query_task", task)
        assert queried["result"] == result["result"]
        record(label, result)
        return result["result"]["task"]["result"]["evidence"]

    initial = fixture("seed-step")
    result = await run("mixed_step", {})
    assert {row["typeId"]: row["count"] for row in result["collected"]} == {9: 7, 3: 5}, result
    after = fixture("snapshot")
    assert after["clients"] == initial["clients"]
    assert not any(item["type"] in (3, 9) for item in after["items"])
    for client in clients:
        pending = {item["slot"] for item in initial["items"] if item["type"] in (3, 9)}
        while pending:
            body = client.wait(21, lambda body: struct.unpack_from("<h", body)[0] in pending and body[18:23] == bytes(5))
            pending.remove(struct.unpack_from("<h", body)[0])
        for item in after["inventory"]:
            if item["type"] in (3, 9):
                client.expect_item(5, bridge.binding["slot"], item["slot"], (item["type"], 0, item["stack"]))
    report["sync"] = {"receivedBy": report["clients"], "actorInventory": after["inventory"]}

    fixture("seed")
    result = await run("type_filter", {"typeIds": [9]})
    assert result["collected"] == [{"typeId": 9, "count": 7}], result
    fixture("seed-identity")
    async def observe_wood():
        observation = await dispatch(bridge, "scan_entities", {"category": "item", "radius": 320, "typeIds": [9]})
        return observation["data"]["items"][0]

    before = await observe_wood()
    fixture("reinitialize-drop")
    after = await observe_wood()
    assert before["slot"] == after["slot"] and before["typeId"] == after["typeId"]
    old_ref, new_ref = (row["details"]["entityRef"] for row in (before, after))
    assert old_ref != new_ref, (before, after)
    task = await start({"entityRef": old_ref})
    result = await call("await_task", {**task, "milliseconds": 5000})
    assert result["status"] == "failed" and result["result"]["reason"] == "drop_reference_unavailable", result
    record("reinitialized_stale_ref", result)
    result = await run("reinitialized_fresh_ref", {"entityRef": new_ref}, intervene="allow-contact-pickup")
    assert result["collected"] == [{"typeId": 9, "count": 7}], result
    fixture("seed-stone")
    observation = await dispatch(bridge, "scan_entities", {"category": "item", "radius": 320, "typeIds": [3]})
    ref = observation["data"]["items"][0]["details"]["entityRef"]
    result = await run("specific_target", {"entityRef": ref}, intervene="allow-contact-pickup")
    assert result["collected"] == [{"typeId": 3, "count": 5}], result

    fixture("seed")
    result = await run("moving", {"typeIds": [9]}, intervene="move-drop")
    assert result["collected"] == [{"typeId": 9, "count": 7}], result
    assert fixture("snapshot")["inputsReleased"]
    task_id = report["cases"]["moving"]["taskId"]
    events = [json.loads(line[line.index("{"):]) for line in engine_log.read_text(encoding="utf-8").splitlines()
              if task_id in line and ('"eventName":"collect-approach-transition"' in line
                                      or '"eventName":"task-native-start"' in line)]
    interrupted = next(e for e in events if e["detail"].get("reason") == "drop_target_changed"
                       and e["detail"]["vy"] < -0.1)
    settled = next(e for e in events if e["gameUpdate"] > interrupted["gameUpdate"]
                   and e["detail"].get("reason") == "settled")
    restarted = next(e for e in events if e["gameUpdate"] > interrupted["gameUpdate"]
                     and e["eventName"] == "task-native-start")
    assert interrupted["detail"]["inputsReleased"] and not interrupted["detail"]["grounded"], interrupted
    assert settled["detail"]["Settled"] and settled["detail"]["inputsReleased"], settled
    assert settled["gameUpdate"] < restarted["gameUpdate"], (settled, restarted)
    report["movingNavigation"] = [{"gameUpdate": e["gameUpdate"], **e["detail"]}
                                  for e in (interrupted, settled, restarted)]
    fixture("seed")
    result = await run("disappearing", {}, status="failed", intervene="remove-drop")
    assert result["outcome"] == "partial" and len(result["unavailable"]) == 1, result
    fixture("seed")
    result = await run("merged", {"typeIds": [9]}, intervene="merge-drop")
    assert result["collected"] == [{"typeId": 9, "count": 10}], result
    fixture("seed-full")
    result = await run("full", {}, status="failed")
    assert all(row["reason"] == "inventory_full" for row in result["remaining"]), result
    fixture("seed-partial")
    result = await run("partial_capacity", {}, status="failed")
    assert result["collected"] == [{"typeId": 9, "count": 3}] and result["outcome"] == "partial", result
    fixture("seed-timeout")
    result = await run("navigation_timeout", {"radiusPixels": 960, "typeIds": [9]}, status="timeout")
    assert result["remaining"][0]["reason"] == "navigation_timeout", result
    fixture("seed")
    task = await start({})
    await asyncio.sleep(.25)
    result = await call("cancel_task", task)
    assert result["status"] == "cancelled" and result["result"]["inputsReleased"], result
    record("cancelled", result)
    fixture("seed-batch")
    result = await run("batch_limit", {}, intervene="allow-contact-pickup")
    assert sum(row["count"] for row in result["collected"]) == 16 and result["remaining"] == [], result
    assert result["outcome"] == "partial" and result["canContinue"] and result["nextTarget"], result
    last = await run("batch_continuation", {"entityRef": result["nextTarget"]["entityRef"]},
                     intervene="allow-contact-pickup")
    assert sum(row["count"] for row in last["collected"]) == 1 and not last["canContinue"], last
    fixture("seed")
    proof = fixture("hazards")["hazardProof"]
    assert proof == dict(blocked=None, visible="task_interrupt_nearby_hostile", untargeted=None,
                         contact="task_interrupt_nearby_hostile", health="task_interrupt_health_decreased"), proof
    report["hazards"] = proof
    coords = fixture("seed-mixed-tool")
    mined = await dispatch(bridge, "mine_tile", {"x": coords["mineX"], "y": coords["mineY"]})
    assert mined["status"] == "completed" and mined["evidence"]["toolKind"] == "pickaxe", mined
    assert mined["evidence"]["pick"] > 0 and mined["evidence"]["axe"] > 0, mined
    report["mixedTool"] = mined
    fixture("seed-region")
    sweep = await run("shared_pickup_region", {})
    assert {row["typeId"]: row["count"] for row in sweep["collected"]} == {9: 7, 3: 5}, sweep
    task_id = report["cases"]["shared_pickup_region"]["taskId"]
    approaches = [line for line in engine_log.read_text(encoding="utf-8").splitlines()
                  if f"{task_id}/approach-" in line and '"eventName":"task-native-start"' in line]
    assert len(approaches) == 1, approaches
    report["sharedRegionApproaches"] = len(approaches)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine", type=Path, default=Path("D:/steam/steamapps/common/tModLoader"))
    parser.add_argument("--world", required=True, type=Path)
    parser.add_argument("--package", required=True, type=Path)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--transfers", action="store_true")
    mode.add_argument("--batches", action="store_true")
    mode.add_argument("--sorting", action="store_true")
    mode.add_argument("--personal", action="store_true")
    mode.add_argument("--collect", action="store_true")
    mode.add_argument("--storage-memory", action="store_true", help="Verify two MCP commissions against the same base chest")
    mode.add_argument("--search", action="store_true", help="Find nearby torch stocks, choose a chest and withdraw through Lua")
    mode.add_argument("--follow", action="store_true", help="Follow a moving player in the isolated multiplayer arena")
    mode.add_argument("--torch-recipient", type=Path, metavar="PLAYER.plr",
                      help="Verify stock and craft delivery to a native client using a copy of this player")
    parser.add_argument("--pickup-feedback", action="store_true",
                        help="Run just the scripted MCP capacity cases for --collect or --batches")
    parser.add_argument("--port", default=27941, type=int)
    parser.add_argument("--ipc-port", default=27942, type=int)
    args = parser.parse_args()
    if args.pickup_feedback and not (args.collect or args.batches):
        parser.error("--pickup-feedback requires --collect or --batches")
    args.engine = args.engine.resolve()
    if args.port == args.ipc_port:
        parser.error("Game and IPC ports must be distinct.")
    for port in (args.port, args.ipc_port):
        with socket.socket() as probe:
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            probe.bind(("127.0.0.1", port))
    base = ROOT / ".local"
    base.mkdir(exist_ok=True)
    profile = Path(tempfile.mkdtemp(prefix="container-native-", dir=base))
    print(f"Native test profile: {profile}", flush=True)
    mods = profile / "Mods"
    mods.mkdir()
    (mods / "enabled.json").write_text('["CompanionBridge"]')
    shutil.copyfile(args.package, mods / "CompanionBridge.tmod")
    world = profile / "test.wld"
    shutil.copyfile(args.world, world)
    (profile / ("isolated-collect-fixture" if args.collect else "isolated-container-fixture")).touch()
    clients = []
    bridge = None
    with (profile / "server.log").open("w") as log:
        process = subprocess.Popen(
            [str(args.engine / "dotnet/dotnet.exe"), "exec", "--additionalprobingpath", str(args.engine / "Libraries"), "tModLoader.dll", "-server",
             "-world", str(world), "-port", str(args.port), "-tmlsavedirectory", str(profile),
             "-modpath", str(mods), "-companionserverai", "-companionaiport", str(args.ipc_port),
             "-companionaiipctest", "-companioncontainertest"]
            + (["-companioncontainertransfertest"] if args.transfers else [])
            + (["-companioncontainerbatchtest"] if args.batches or args.storage_memory or args.torch_recipient or args.search else [])
            # Both inventory commissions use the fixture's quiet arena.
            + (["-companionhandofftest"] if args.torch_recipient or args.storage_memory or args.search or args.follow else [])
            + (["-companionfollowtest"] if args.follow else [])
            + (["-companioncontainersorttest"] if args.sorting else [])
            + (["-companionpersonaltest"] if args.personal else [])
            + (["-companioncollecttest"] if args.collect else []),
            cwd=args.engine, stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        try:
            def ready():
                if process.poll() is not None:
                    raise RuntimeError(f"Dedicated server exited: {process.returncode}")
                return (profile / "container-ready.json").exists()
            wait_for(ready, 90)
            if not args.torch_recipient:
                clients.append(Client(args.port, "ChestObserverA"))
                clients.append(Client(args.port, "ChestObserverB"))
            if not (args.storage_memory or args.torch_recipient):
                bridge = ServerAiBridge(port=args.ipc_port)
            if args.torch_recipient:
                from native_torch_delivery import verify_delivery
                report = asyncio.run(verify_delivery(profile, args))
            elif args.storage_memory:
                from native_storage_memory import verify_storage_memory
                report = asyncio.run(verify_storage_memory(profile, args.ipc_port, clients[0]))
            elif args.follow:
                from native_follow import verify_follow
                report = asyncio.run(verify_follow(profile, bridge, clients[0]))
            elif args.pickup_feedback:
                from native_pickup_feedback import verify_feedback
                report = asyncio.run(verify_feedback(profile, args.ipc_port, ground=args.collect))
            elif args.collect:
                report = asyncio.run(verify_collect(profile, bridge, clients, args.engine / "tModLoader-Logs/server.log"))
            else:
                report = asyncio.run(verify(profile, bridge, *clients, transfers=args.transfers, batches=args.batches, sorting=args.sorting, personal=args.personal, search=args.search))
            if "openAtWorldUnload" in report:
                (profile / "g81-test-stop").touch()
                for client in clients:
                    client.expect_chest(report["actorSlot"], -1)
                report["worldUnloadCloseReceivedBy"] = [client.slot for client in clients]
        finally:
            if bridge:
                bridge.close()
            for client in clients:
                client.close()
            (profile / "g81-test-stop").touch()
            try:
                process.wait(timeout=12)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


    if process.returncode != 0:
        raise RuntimeError(f"Dedicated server shutdown failed: {process.returncode}")
    report["result"] = "passed"
    report["engineVersion"] = "2026.9.3.1"
    report["serverExitCode"] = process.returncode
    (profile / "container-results.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Passed. Evidence: {profile / 'container-results.json'}")

if __name__ == "__main__":
    main()
