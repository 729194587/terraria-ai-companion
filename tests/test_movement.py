import asyncio
import copy
import json
from pathlib import Path
import sys
import threading
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from agent import Agent, TOOLS
from control import BridgeError
from movement import move, stop_movement
from test_agent import Game, Model, event, tool, final
from test_observation import fixture


class MovementBridge(Game):
    """Protocol fake, not a Terraria physics simulation."""
    def __init__(self, complete_after=2):
        super().__init__()
        self.hello = {"capabilities": ["bounded_movement_v1"]}
        self.calls = []
        self.generation = 0
        self.action = None
        self.position = {"x": 100, "y": 20}
        self.complete_after = complete_after
        self.polls = 0
        self.lock = threading.Lock()
        self.started = threading.Event()
        self.start_release = None
        self.fail_start = False
        self.fail_poll = False
        self.fail_renew = False
        self.observations = []

    def state(self):
        return {"inWorld": True, "position": dict(self.position), "life": 100}

    def reply(self, status="movement"):
        return copy.deepcopy({"ok": True, "status": status, "state": self.state(),
                              "movement": {"generation": self.generation, "action": self.action}})

    def finish(self, status, reason):
        self.position["x"] += self.action["direction"] * 10
        self.action.update(status=status, reason=reason, completed=status == "completed", cancelled=status == "cancelled",
                           after=self.state(), elapsedMilliseconds=self.action["durationMs"])
        self.generation += 1

    def command(self, cmd, **parameters):
        with self.lock:
            self.calls.append((cmd, parameters))
            if cmd == "get_movement":
                if self.action and self.fail_poll:
                    raise BridgeError("fixture disconnected")
                if self.action and self.action["status"] == "running":
                    self.polls += 1
                    if self.polls >= self.complete_after:
                        self.finish("completed", "duration_elapsed")
                return self.reply()
            if cmd == "move_for":
                if self.fail_start:
                    raise BridgeError("Command rejected: manual_active", status="manual_active")
                self.generation += 1
                self.action = {"actionId": self.generation, "generation": self.generation, "status": "running",
                    "direction": parameters["direction"], "durationMs": parameters["durationMs"], "reason": "started",
                    "before": self.state(), "after": None, "elapsedMilliseconds": 0, "completed": False, "cancelled": False}
                reply = self.reply("movement_started")
                self.started.set()
            elif cmd == "renew_move":
                if self.fail_renew:
                    self.finish("cancelled", "lease_expired")
                    raise BridgeError("Command rejected: action_changed")
                return self.reply()
            elif cmd == "cancel_move":
                if not self.action or parameters["actionId"] != self.action["actionId"]:
                    raise BridgeError("Command rejected: action_changed")
                if self.action["status"] == "running":
                    self.finish("cancelled", "stopped")
                return self.reply()
            else:
                return super().command(cmd, **parameters)
        if self.start_release is not None:
            if not self.start_release.wait(2):
                raise BridgeError("fixture start ack timeout")
        return reply

    def get_observation(self):
        value = fixture()
        value["player"]["positionWorldPixels"] = dict(self.position)
        self.observations.append(copy.deepcopy(value))
        return {"observation": value}


class MovementTests(unittest.IsolatedAsyncioTestCase):
    async def test_left_right_results_and_renewal(self):
        for direction, expected in (("left", 90), ("right", 110)):
            bridge = MovementBridge()
            result = await move(bridge, direction, 200, chat_id="chat:1")
            self.assertTrue(result["completed"])
            self.assertFalse(result["cancelled"])
            self.assertEqual(result["beforePositionWorldPixels"]["x"], 100)
            self.assertEqual(result["afterPositionWorldPixels"]["x"], expected)
            start = next(p for c, p in bridge.calls if c == "move_for")
            self.assertEqual(start["chatId"], "chat:1")
            self.assertEqual(start["expectedGeneration"], 0)
            self.assertTrue(any(c == "renew_move" for c, _ in bridge.calls))

    async def test_invalid_duration_direction_and_capability_never_start(self):
        for direction, duration in (("up", 100), ("left", True), ("right", 49), ("left", 2001), ("left", 100.0)):
            bridge = MovementBridge()
            with self.assertRaises(ValueError):
                await move(bridge, direction, duration)
            self.assertEqual(bridge.calls, [])
        bridge = MovementBridge(); bridge.hello = {}
        with self.assertRaises(BridgeError):
            await move(bridge, "left", 100)
        self.assertEqual(bridge.calls, [])

    async def test_stop_active_and_idle(self):
        bridge = MovementBridge(complete_after=100)
        self.assertEqual((await stop_movement(bridge))["status"], "idle")
        task = asyncio.create_task(move(bridge, "right", 2000))
        self.assertTrue(await asyncio.to_thread(bridge.started.wait, 1))
        stopped = await stop_movement(bridge)
        self.assertTrue(stopped["cancelled"])
        result = await task
        self.assertTrue(result["cancelled"])
        self.assertEqual(result["reason"], "stopped")

    async def test_lease_cancellation_and_completion_heartbeat_race(self):
        bridge = MovementBridge(complete_after=100); bridge.fail_renew = True
        result = await move(bridge, "right", 1500)
        self.assertTrue(result["cancelled"])
        self.assertEqual(result["reason"], "lease_expired")
        class CompletionRace(MovementBridge):
            def command(self, cmd, **parameters):
                if cmd == "renew_move":
                    with self.lock:
                        self.finish("completed", "duration_elapsed")
                    raise BridgeError("Command rejected: action_changed")
                return super().command(cmd, **parameters)
        result = await move(CompletionRace(complete_after=100), "right", 200)
        self.assertTrue(result["completed"])

    async def test_disconnect_is_unknown_not_fabricated_success(self):
        bridge = MovementBridge(); bridge.fail_poll = True
        result = await move(bridge, "right", 100)
        self.assertFalse(result["valid"])
        self.assertEqual(result["status"], "unknown")
        self.assertIsNone(result["completed"])
        self.assertIsNone(result["cancelled"])
        self.assertIsNone(result["afterPositionWorldPixels"])
        self.assertFalse(any(c == "renew_move" for c, _ in bridge.calls))
        bridge = MovementBridge(); bridge.fail_start = True
        result = await move(bridge, "right", 100)
        self.assertEqual(result["status"], "rejected")
        self.assertIn("manual_active", result["reason"])

    async def test_lost_start_ack_and_changed_ownership_are_unknown(self):
        class LostAck(MovementBridge):
            def command(self, cmd, **parameters):
                reply = super().command(cmd, **parameters)
                if cmd == "move_for":
                    raise BridgeError("fixture start request timeout")
                return reply
        bridge = LostAck()
        result = await move(bridge, "right", 100)
        self.assertEqual(result["status"], "unknown")
        self.assertIsNone(result["completed"])
        self.assertFalse(any(c == "renew_move" for c, _ in bridge.calls))
        class OwnershipChanged(MovementBridge):
            def command(self, cmd, **parameters):
                if cmd == "get_movement" and self.action:
                    self.action["actionId"] += 100
                return super().command(cmd, **parameters)
        bridge = OwnershipChanged(complete_after=100)
        result = await move(bridge, "right", 100)
        self.assertEqual(result["status"], "unknown")
        self.assertFalse(any(c == "cancel_move" for c, _ in bridge.calls))

    async def test_cancel_during_uncancellable_start_waits_for_ack_and_stops_exact_action(self):
        bridge = MovementBridge(complete_after=100); bridge.start_release = threading.Event()
        task = asyncio.create_task(move(bridge, "left", 2000))
        self.assertTrue(await asyncio.to_thread(bridge.started.wait, 1))
        task.cancel()
        bridge.start_release.set()
        with self.assertRaises(asyncio.CancelledError):
            await task
        cancels = [p for c, p in bridge.calls if c == "cancel_move"]
        self.assertEqual(cancels, [{"actionId": bridge.action["actionId"]}])
        self.assertEqual(bridge.action["status"], "cancelled")


class MovementAgentTests(unittest.IsolatedAsyncioTestCase):
    async def test_observe_move_observe_tool_flow(self):
        bridge = MovementBridge()
        model = Model(tool("get_observation"), tool("move", '{"direction":"right","duration_ms":200}'),
                      tool("get_observation"), final("moved and checked"))
        agent = Agent(bridge, model)
        self.assertTrue({"move", "stop_movement"}.issubset({t["function"]["name"] for t in TOOLS}))
        try:
            agent.receive(event(1)); await agent.task
            self.assertEqual(len(bridge.observations), 2)
            result = json.loads(model.requests[2][-1]["content"])
            self.assertTrue(result["completed"])
            self.assertEqual(bridge.observations[0]["player"]["positionWorldPixels"]["x"], 100)
            self.assertEqual(bridge.observations[1]["player"]["positionWorldPixels"]["x"], 110)
        finally:
            await agent.close()

    async def test_new_user_preempts_movement_and_discards_old_reply(self):
        bridge = MovementBridge(complete_after=100); bridge.start_release = threading.Event()
        model = Model(tool("move", '{"direction":"left","duration_ms":2000}'), final("stopped"))
        agent = Agent(bridge, model)
        try:
            agent.receive(event(1))
            self.assertTrue(await asyncio.to_thread(bridge.started.wait, 1))
            old = agent.task
            agent.receive(event(2)); bridge.start_release.set()
            await agent.task
            await asyncio.gather(old, return_exceptions=True)
            self.assertEqual(bridge.action["status"], "cancelled")
            self.assertEqual([p["text"] for _, p in bridge.sent], ["stopped"])
        finally:
            bridge.start_release.set(); await agent.close()

    async def test_stop_tool_and_invalid_move_arguments(self):
        for call, valid in ((tool("stop_movement"), True), (tool("move", '{"direction":"up","duration_ms":200}'), False),
                            (tool("move", '{"direction":"left","duration_ms":200,"jump":true}'), False)):
            bridge = MovementBridge(); model = Model(call, final("done")); agent = Agent(bridge, model)
            try:
                agent.receive(event(1)); await agent.task
                self.assertEqual(json.loads(model.requests[1][-1]["content"])["valid"], valid)
                self.assertFalse(any(c == "move_for" for c, _ in bridge.calls))
            finally:
                await agent.close()
