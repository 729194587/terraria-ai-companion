import asyncio
import copy
import json
import unittest
from unittest.mock import patch

from test_agent import Agent, Game, Model, event, final, tool
from test_game_messages import notice
from control import BridgeError
from agent import inventory_summary
import cost_metrics


def item(slot, key="Terraria/StoneBlock", count=3, **extra):
    return {"slot": slot, "empty": False, "hotbarKey": (slot + 1) % 10 if slot < 10 else None,
            "contentKey": key, "name": "stone" if key.startswith("Terraria/") else "Mod data <instruction>",
            "stack": count, "prefix": 0, "customState": "not_applicable" if key.startswith("Terraria/") else "unknown",
            "use": dict(damage=4, pick=50, axe=0, hammer=0, createTile=1, createWall=-1,
                        useStyle=1, useTime=10, useAnimation=10, ammo=0, shoot=0, consumable=True),
            **extra}


def snapshot():
    slots = [{"slot": s, "empty": True} for s in range(12)]
    for entry in (item(0), item(9, count=4), item(10, "Example/Tool", 1), item(11, "Example/Tool", 2)):
        slots[entry["slot"]] = entry
    return {"available": True, "worldSessionId": "world-a", "gameUpdate": 7,
            "data": {"timeOfDay": "23:59", "inventory": slots, "selectedSlot": 9, "selectedHotbarKey": 0,
                     "hotbarKeyBindings": [{"key": (s + 1) % 10, "inventorySlot": s} for s in range(10)],
                     "equipment": [item(0, "Example/Excluded", 99)]}}


class InventoryGame(Game):
    def __init__(self):
        super().__init__()
        self.hello = {"capabilities": ["atomic_operations_v1", "atomic_extensions_v1"]}
        self.current = snapshot()
        self.inventory_reads = []
        self.failure = None

    def command(self, cmd, **params):
        if cmd == "query_atomic":
            self.inventory_reads.append(copy.deepcopy(params))
            if self.failure:
                raise self.failure
            return {"data": copy.deepcopy(self.current)}
        return super().command(cmd, **params)

    def get_state(self):
        self.current["gameUpdate"] += 1
        self.current["data"]["inventory"][9]["stack"] = 1
        self.current["data"].update(selectedSlot=0, selectedHotbarKey=1, timeOfDay="00:00")
        return super().get_state()


def runtime(request):
    assert request[-1]["role"] == "user"
    return json.loads(request[-1]["content"])["runtimeInventory"]


class RuntimeInventoryTests(unittest.IsolatedAsyncioTestCase):
    async def test_each_actual_request_gets_fresh_ephemeral_inventory_and_actual_cost_input(self):
        game, model = InventoryGame(), Model(tool(), final(), final())
        agent = Agent(game, model)
        agent.receive(notice(1))
        measured = []
        original = cost_metrics.TaskCosts.request
        def capture(costs, messages):
            measured.append(copy.deepcopy(messages))
            original(costs, messages)
        with patch.object(cost_metrics.TaskCosts, "request", capture):
            agent.receive(event(1))
            original_user = copy.deepcopy(agent.history[-1])
            await agent.task
            persisted = copy.deepcopy(agent.history)
            agent.receive(event(2))
            await agent.task
        self.assertEqual(measured, model.requests)
        self.assertEqual(len(game.inventory_reads), 3)
        self.assertTrue(all(p == {"operation": "inspect_inventory", "arguments": {"mode": "full"}}
                            for p in game.inventory_reads))
        first, second = (runtime(r) for r in model.requests[:2])
        self.assertEqual(["23:59", "00:00"], [json.loads(r[-1]["content"])["runtimeWorldTime"]
                                               for r in model.requests[:2]])
        self.assertFalse(any("runtimeWorldTime" in (m.get("content") or "") for m in agent.history[1:]))
        self.assertEqual((first["status"], first["worldSessionId"], first["gameUpdate"]), ("available", "world-a", 7))
        self.assertEqual(first["items"][0]["count"], 7)
        self.assertTrue(all("stacks" not in entry for entry in first["items"]))
        self.assertEqual(first["selected"], {
            "hotbarKey": 0, "empty": False,
            "contentKey": "Terraria/StoneBlock", "name": "stone", "prefix": 0})
        self.assertNotIn('"use":', json.dumps(first))
        self.assertNotIn('"damage":', json.dumps(first))
        self.assertFalse({'scope','hotbarNumbering','details'}.intersection(first))
        self.assertEqual(first["emptySlots"], 8)
        self.assertEqual(len(first["items"]), 3)
        self.assertEqual([i["count"] for i in first["items"][1:]], [1, 2])
        self.assertTrue(all(i["contentKey"] == "Example/Tool" for i in first["items"][1:]))
        self.assertNotIn("Example/Excluded", json.dumps(first))
        self.assertEqual((second["gameUpdate"], second["items"][0]["count"]), (9, 4))
        self.assertEqual(second["selected"]["hotbarKey"], 1)
        self.assertEqual([m["role"] for m in model.requests[1]], ["system", "user", "user", "assistant", "tool", "user"])
        self.assertEqual(model.requests[1][3]["tool_calls"][0]["id"], model.requests[1][4]["tool_call_id"])
        self.assertEqual(agent.history[1], original_user)
        self.assertEqual(agent.history[:len(persisted)], persisted)
        self.assertTrue(all("runtimeInventory" not in (m.get("content") or "") for m in agent.history[1:]))
        self.assertIn("recentGameEvents", model.requests[0][2]["content"])
        self.assertNotIn("recentGameEvents", agent.history[1]["content"])
        self.assertEqual(len(agent.bridge.sent), 2)

    async def test_failed_or_invalid_query_never_reuses_previous_snapshot(self):
        for failure in ("ipc", "not_in_world", "malformed"):
            game = InventoryGame()
            class FailingAfterRead(InventoryGame):
                def get_state(self):
                    if failure == "ipc":
                        self.failure = BridgeError("fixture disconnected")
                    elif failure == "not_in_world":
                        self.current = {"available": False, "reason": "not_in_world"}
                    else:
                        self.current["data"]["inventory"][0] = {"slot": 0, "empty": False}
                    return Game.get_state(self)
            game = FailingAfterRead()
            model = Model(tool(), final())
            agent = Agent(game, model)
            agent.receive(event(1))
            await agent.task
            self.assertEqual(runtime(model.requests[0])["status"], "available")
            unavailable = runtime(model.requests[1])
            self.assertEqual(unavailable["status"], "unavailable")
            self.assertNotIn("runtimeWorldTime", json.loads(model.requests[1][-1]["content"]))
            self.assertNotIn("items", unavailable)
            self.assertNotIn("emptySlots", unavailable)
            self.assertEqual(len(game.inventory_reads), 2)

    async def test_empty_selected_world_change_and_empty_inventory(self):
        game, model = InventoryGame(), Model(final(), final())
        agent = Agent(game, model)
        agent.receive(event(1))
        await agent.task
        game.current = snapshot()
        game.current.update(worldSessionId="world-b", gameUpdate=0)
        game.current["data"]["inventory"] = [{"slot": i, "empty": True} for i in range(12)]
        agent.receive(event(2))
        await agent.task
        current = runtime(model.requests[1])
        self.assertEqual((current["worldSessionId"], current["gameUpdate"]), ("world-b", 0))
        self.assertEqual(current["items"], [])
        self.assertEqual(current["emptySlots"], 12)
        self.assertEqual(current["selected"], {"empty": True, "hotbarKey": 0})

    async def test_cancel_during_refresh_and_budget_preserve_history(self):
        started = asyncio.Event()
        async def waiting(bridge):
            started.set()
            await asyncio.Future()
        agent = Agent(InventoryGame(), Model())
        with patch("agent.read_runtime_inventory", waiting):
            agent.receive(event(1))
            await started.wait()
            await agent.close()
        self.assertEqual([m["role"] for m in agent.history], ["system", "user"])
        self.assertEqual(agent.llm.requests, [])
        game, model = InventoryGame(), Model(tool())
        agent = Agent(game, model, max_model_calls=1)
        agent.receive(event(1))
        await agent.task
        self.assertEqual(len(game.inventory_reads), 1)
        self.assertEqual(len(model.requests), 1)
        self.assertFalse(any(m["role"] == "tool" for m in agent.history[1:]))

    async def test_superseded_refresh_cannot_send_a_stale_model_request(self):
        started, release = asyncio.Event(), asyncio.Event()
        reads = 0
        async def delayed(bridge):
            nonlocal reads
            reads += 1
            if reads == 1:
                started.set()
                try:
                    await release.wait()
                except asyncio.CancelledError:
                    await release.wait()
            return {"status": "unavailable", "reason": "fixture"}
        agent = Agent(InventoryGame(), Model(final("new")))
        with patch("agent.read_runtime_inventory", delayed):
            agent.receive(event(1))
            await started.wait()
            old = agent.task
            agent.receive(event(2))
            await agent.task
            release.set()
            await old
        self.assertEqual(len(agent.llm.requests), 1)
        self.assertEqual(agent.history[-1]["content"], "new")
        self.assertEqual(sum(m["role"] == "user" for m in agent.history), 2)


class InventorySummaryTests(unittest.TestCase):
    def test_invalid_slots_selection_and_use_are_rejected(self):
        for modify in (lambda d: d["inventory"][0].update(slot=1),
                       lambda d: d.update(selectedSlot=99),
                       lambda d: d.update(selectedHotbarKey=9),
                       lambda d: d.update(hotbarKeyBindings=[]),
                       lambda d: d["inventory"][0].update(use={}),
                       lambda d: d["inventory"][0].update(stack=0)):
            value = snapshot()
            modify(value["data"])
            with self.assertRaises(ValueError):
                inventory_summary({"valid": True, **value})

    def test_different_prefix_or_use_attributes_are_not_merged(self):
        value = snapshot()
        value["data"]["inventory"][9]["prefix"] = 1
        result = inventory_summary({"valid": True, **value})
        self.assertEqual(len(result["items"]), 4)


    def test_execution_property_differences_remain_separate_without_exposing_use(self):
        value = snapshot()
        value["data"]["inventory"][9]["use"]["pick"] = 100
        before = copy.deepcopy(value)
        result = inventory_summary({"valid": True, **value})
        self.assertEqual(len(result["items"]), 4)
        self.assertNotIn('"use":', json.dumps(result))
        self.assertEqual(value, before)

    def test_unknown_custom_state_prevents_merging_even_for_vanilla_items(self):
        value = snapshot()
        for slot in (0, 9):
            value["data"]["inventory"][slot]["customState"] = "unknown; GlobalItem data"
        result = inventory_summary({"valid": True, **value})
        self.assertEqual(len(result["items"]), 4)
        self.assertEqual([entry["count"] for entry in result["items"]], [3, 4, 1, 2])
        self.assertTrue(all("unknown" in entry["customState"] for entry in result["items"]))


class InventoryDetailContractTests(unittest.IsolatedAsyncioTestCase):
    async def test_inspect_inventory_keeps_details_outside_runtime_summary(self):
        game = InventoryGame()
        model = Model(tool("inspect_inventory", '{"mode":"full"}'), final())
        agent = Agent(game, model)
        before = copy.deepcopy(game.current["data"])
        agent.receive(event(1))
        await agent.task
        for request in model.requests:
            self.assertNotIn('"use":', json.dumps(runtime(request)))
        detailed = next(m for m in model.requests[1] if m["role"] == "tool")
        result = json.loads(detailed["content"])
        self.assertEqual(result["data"], before)
        self.assertEqual(result["data"]["inventory"][0]["use"]["pick"], 50)
        self.assertEqual(result["data"]["inventory"][0]["use"]["damage"], 4)
        self.assertEqual(result["data"]["inventory"][0]["use"]["useTime"], 10)
