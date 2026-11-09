"""Isolated Server AI equipment through native Terraria, IPC and Lua."""
import argparse
import asyncio
import json
import sys
from pathlib import Path

from native_direct_input import wait_file

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from lua_runtime import Limits, LuaRuntime
from server_ai import ServerAiBridge


async def verify(args):
    profile = args.profile
    await wait_file(profile / "action-fixture.json", 90)
    bridge = ServerAiBridge(args.port)
    assert {"equip_item", "unequip_item"} <= bridge.supported_atomic_operations
    rows = []

    def new_test_session():
        # An unknown action fences the old session. These tests explicitly open
        # a fresh connection to inspect effects, without replaying the action.
        nonlocal bridge
        bridge.close()
        bridge = ServerAiBridge(args.port)

    async def setup(mode):
        ready = profile / "equipment-ready.json"
        ready.unlink(missing_ok=True)
        temp = profile / "equipment-request.tmp"
        temp.write_text(mode)
        temp.replace(profile / "equipment-request")
        return await wait_file(ready)

    async def query(operation="inspect_inventory", **arguments):
        reply = await asyncio.to_thread(bridge.command, "query_atomic", operation=operation, arguments=arguments)
        return reply["data"]["data"]

    async def gear():
        full = await query(mode="full")
        assert all(key in full for key in ("equipment", "dyes", "miscEquipment", "miscDyes", "inventoryScope"))
        return {row["slotId"]: row for row in full["functionalEquipmentSlots"]}

    async def bag(slot):
        row, = (await query(mode="slots", slots=[slot]))["inventory"]
        return row

    async def action(label, op, params, status="completed", reason=None):
        fields = ",".join(f"{key}={json.dumps(value)}" for key, value in params.items())
        result = await LuaRuntime(bridge).execute(f"return api('{op}',{{{fields}}})",
                                                  limits=Limits(seconds=12, calls=1, actions=1))
        act = result.get("value") or result.get("lastAction") or {}
        rows.append(dict(label=label, action=act))
        (profile / "equipment-results.json").write_text(json.dumps(rows, indent=2))
        assert act.get("status") == status, (label, act, result)
        if reason:
            assert act.get("reason") == reason, (label, act)
        assert act["endEvidence"]["inputsReleased"] and not act["endEvidence"]["inputsAcquired"], act
        if status == "completed":
            e = act["evidence"]
            assert e["confirmedAtGameUpdate"] > e["mutationGameUpdate"] and e["equipmentVerified"], e
            before, after = e["before"], e["after"]
            assert before["equipment"]["instanceId"] == after["backpack"]["instanceId"], e
            assert before["backpack"]["instanceId"] == after["equipment"]["instanceId"], e
            assert (await gear())[params["targetSlotId"]]["targetToken"] == after["equipment"]["token"], e
            assert (await bag(e["backpackSlot"]))["inventoryToken"] == after["backpack"]["token"], e
        print(f"Native equipment {label}: {act['status']} {act['reason']}", flush=True)
        return act

    async def equip(label, slot, source, status="completed", reason=None, token=None, target=None):
        return await action(label, "equip_item", dict(
            sourceSlot=source, sourceToken=token or (await bag(source))["inventoryToken"],
            targetSlotId=slot, targetToken=target or (await gear())[slot]["targetToken"]), status, reason)

    async def remove(label, slot, destination=None, status="completed", reason=None):
        params = dict(targetSlotId=slot, targetToken=(await gear())[slot]["targetToken"])
        if destination is not None:
            params["destinationSlot"] = destination
        return await action(label, "unequip_item", params, status, reason)

    try:
        ready = await setup("reset")
        mod = "mod:" + ready["slot"]
        initial = await gear()
        assert len(initial) == 11 and initial[mod]["source"] == "mod_accessory_slot", initial
        assert initial[mod]["enabled"] and not initial[mod]["occupied"] and initial[mod]["item"] is None
        assert len((await query("get_player_state"))["functionalEquipmentSlots"]) == len(initial)
        for kind, source in (("head", 10), ("body", 12), ("legs", 14)):
            slot = f"vanilla:{kind}"
            first, second = (await bag(source))["typeId"], (await bag(source + 1))["typeId"]
            await equip(f"{kind}_equip", slot, source)
            assert (await gear())[slot]["item"]["typeId"] == first and (await bag(source))["typeId"] == 0
            await equip(f"{kind}_replace", slot, source + 1)
            assert (await gear())[slot]["item"]["typeId"] == second and (await bag(source + 1))["typeId"] == first
            await remove(f"{kind}_remove", slot, 30 + source - 10)
            assert not (await gear())[slot]["occupied"]
        await equip("wrong_item", "vanilla:head", 22, "failed", "equipment_item_incompatible")
        locked = [slot for slot, row in initial.items() if row["source"] == "vanilla" and not row["enabled"]]
        assert locked, initial
        await equip("locked_vanilla", locked[0], 16, "failed", "equipment_slot_disabled")
        acc1, acc2 = "vanilla:accessory:1", "vanilla:accessory:2"
        await equip("vanilla_accessory", acc1, 16)
        await equip("vanilla_replace", acc1, 17)
        await equip("duplicate_vanilla", acc2, 21, "failed", "equipment_accessory_rejected")
        await equip("duplicate_vanilla_in_mod", mod, 21, "failed", "equipment_accessory_rejected")
        await equip("mod_item_vanilla_hook", acc2, 24, "failed", "equipment_accessory_rejected")
        await equip("mod_item_in_mod_slot", mod, 24)
        assert (await gear())[mod]["item"]["contentKey"] == "CompanionBridge/EquipmentDiagnosticAccessory"
        await equip("mod_accessory", mod, 18)
        await equip("duplicate_mod_in_vanilla", acc2, 20, "failed", "equipment_accessory_rejected")
        await equip("mod_replace", mod, 19)
        await remove("occupied_destination", mod, 22, "failed", "equipment_destination_occupied")
        await setup("disabled")
        disabled = (await gear())[mod]
        assert not disabled["enabled"] and disabled["occupied"]
        await equip("disabled_equip", mod, 23, "failed", "equipment_slot_disabled")
        await remove("disabled_remove", mod, 31, "failed", "equipment_slot_disabled")
        await setup("reject")
        await equip("custom_reject", mod, 23, "failed", "equipment_accessory_rejected")
        await setup("throw")
        await equip("hook_throws", mod, 23, "unknown", "equipment_native_exception")
        new_test_session()
        await setup("mutate")
        before = (await gear())[mod]["targetToken"]
        await equip("hook_changes_inventory", mod, 23, "unknown", "equipment_changed_during_compatibility_hook")
        new_test_session()
        assert (await gear())[mod]["targetToken"] == before and (await bag(35))["typeId"] != 0
        ready = await setup("normal")
        assert ready["permissionCalls"] >= 6
        await remove("mod_remove_auto", mod)
        await setup("sync-mutate")
        sync = await equip("change_during_sync", mod, 23, "unknown", "equipment_changed_during_sync")
        new_test_session()
        assert (await gear())[mod]["targetToken"] == sync["evidence"]["after"]["equipment"]["token"]
        assert (await bag(36))["typeId"] != 0
        await setup("full")
        await remove("backpack_full", acc1, status="failed", reason="equipment_backpack_full")

        await setup("reset")
        source_token = (await bag(10))["inventoryToken"]
        await setup("new-source")
        await equip("stale_source_instance", "vanilla:head", 10, "failed", "stale_inventory_snapshot", token=source_token)
        target_token = (await gear())["vanilla:head"]["targetToken"]
        await setup("new-target")
        await equip("stale_target_after_observation", "vanilla:head", 10, "failed", "stale_equipment_snapshot",
                    target=target_token)
        stale = (await gear())["vanilla:head"]["targetToken"]
        await setup("new-target")
        await action("stale_unequip_instance", "unequip_item", dict(targetSlotId="vanilla:head", targetToken=stale),
                     "failed", "stale_equipment_snapshot")
        await setup("lifecycle")
        lifecycle = await wait_file(profile / "equipment-lifecycle.json")
        assert set(lifecycle) == {"cancel_before", "cancel_after", "cancel_unequip_after", "death", "generation", "world"}
        print(f"PASS native equipment: {len(rows)} IPC/Lua cases, {len(lifecycle)} dispatcher lifecycle cases", flush=True)
    finally:
        bridge.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--port", type=int, required=True)
    asyncio.run(verify(parser.parse_args()))
