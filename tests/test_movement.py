import asyncio
import copy
import io
import json
from pathlib import Path
import sys
import threading
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from agent import Agent, TOOLS
import agent_trace as tracing
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
        self.action['endEvidence'] = {'worldSessionId': 'world', 'gameUpdate': 42 + max(0, len(self.observations)-1),
                                      'phase': 'PostUpdateRunSpeeds_before_bridge_input'}
        self.action['endEvidenceStatus'] = 'known'

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
        value['gameUpdate'] += len(self.observations)
        value['observationId'] = 'world:' + str(value['gameUpdate'])
        value["player"]["positionWorldPixels"] = dict(self.position)
        self.observations.append(copy.deepcopy(value))
        return {"observation": value, "controlGeneration": self.generation}


class StopMovementTraceTests(unittest.IsolatedAsyncioTestCase):
    async def stop_event(self, bridge):
        with tempfile.TemporaryDirectory() as directory:
            trace = tracing.Trace(Path(directory) / 'stop.jsonl', enabled=True)
            with patch.object(tracing, 'trace', trace), patch('sys.stderr', new_callable=io.StringIO):
                agent = Agent(bridge, Model(tool('stop_movement'), final('stopped')))
                agent.receive(event(1))
                await agent.task
            records = [json.loads(line) for line in trace.path.read_text(encoding='utf-8').splitlines()]
        return next(record for record in records if record['type'] == 'tool_result')

    def started_bridge(self):
        bridge = MovementBridge(complete_after=100)
        bridge.command('move_for', direction=1, durationMs=100, expectedGeneration=0)
        return bridge

    def assert_stop_event(self, record, original_status, released, success, no_op, cancel_requested):
        self.assertTrue(record['result_valid'])
        self.assertIs(record['action_success'], success)
        self.assertIs(record['success'], success)
        result = record['result']
        self.assertTrue(result['valid'])
        self.assertEqual(original_status, result['status'])
        self.assertEqual(original_status == 'completed', result['completed'])
        self.assertEqual(original_status == 'cancelled', result['cancelled'])
        self.assertIs(result['cancelRequested'], cancel_requested)
        self.assertEqual({'status': 'completed' if success else 'unknown', 'confirmed': success,
                          'inputReleased': released, 'operation': 'stop_movement', 'noOp': no_op},
                         result['execution'])
        if original_status == 'idle':
            self.assertIsNone(result['targetAction'])
        else:
            self.assertEqual(result['actionId'], result['targetAction']['actionId'])
            self.assertEqual({'status': original_status, 'confirmed': True, 'inputReleased': released},
                             result['targetAction']['execution'])

    async def test_idle_stop_is_a_confirmed_noop(self):
        bridge = MovementBridge()
        record = await self.stop_event(bridge)
        self.assert_stop_event(record, 'idle', None, True, True, False)
        self.assertFalse(any(cmd == 'cancel_move' for cmd, _ in bridge.calls))

    async def test_terminal_action_with_release_evidence_is_confirmed(self):
        for status in ('completed', 'cancelled'):
            with self.subTest(status=status):
                bridge = self.started_bridge()
                bridge.finish(status, 'already_ended')
                record = await self.stop_event(bridge)
                self.assert_stop_event(record, status, True, True, True, False)
                self.assertFalse(any(cmd == 'cancel_move' for cmd, _ in bridge.calls))

    async def test_terminal_action_without_valid_release_evidence_is_not_success(self):
        for status in ('completed', 'cancelled'):
            for evidence in ('missing', 'unknown', 'pending', 'missing_receipt', 'invalid_phase'):
                with self.subTest(status=status, evidence=evidence):
                    bridge = self.started_bridge()
                    bridge.finish(status, 'already_ended')
                    if evidence == 'missing':
                        bridge.action.pop('endEvidenceStatus')
                        bridge.action.pop('endEvidence')
                    elif evidence == 'missing_receipt':
                        bridge.action.pop('endEvidence')
                    elif evidence == 'invalid_phase':
                        bridge.action['endEvidence']['phase'] = 'unverified'
                    else:
                        bridge.action['endEvidenceStatus'] = evidence
                    record = await self.stop_event(bridge)
                    self.assert_stop_event(record, status, None, False, None, False)
                    self.assertFalse(any(cmd == 'cancel_move' for cmd, _ in bridge.calls))

    async def test_normal_cancellation_with_release_evidence_is_confirmed(self):
        bridge = self.started_bridge()
        record = await self.stop_event(bridge)
        self.assert_stop_event(record, 'cancelled', True, True, False, True)
        self.assertEqual([{'actionId': bridge.action['actionId']}],
                         [params for cmd, params in bridge.calls if cmd == 'cancel_move'])


class MovementTests(unittest.IsolatedAsyncioTestCase):
    async def test_terminal_ack_waits_for_actual_player_release_receipt_without_renewing(self):
        class DelayedReceipt(MovementBridge):
            def finish(self, status, reason):
                super().finish(status, reason)
                self.receipt = self.action.pop('endEvidence')
                self.action['endEvidenceStatus'] = 'pending'
            def command(self, cmd, **parameters):
                if cmd == 'get_movement' and self.action and self.action['status'] == 'completed':
                    self.action['endEvidence'] = self.receipt
                    self.action['endEvidenceStatus'] = 'known'
                return super().command(cmd, **parameters)
        bridge = DelayedReceipt(complete_after=1)
        result = await move(bridge, 'right', 100)
        self.assertEqual(result['observation']['endGameUpdate'], 42)
        self.assertTrue(result['execution']['inputReleased'])
        self.assertEqual(sum(cmd == 'get_movement' for cmd, _ in bridge.calls), 3)
        self.assertFalse(any(cmd == 'renew_move' for cmd, _ in bridge.calls))

    async def test_paused_player_boundary_never_invents_an_end_update(self):
        class Paused(MovementBridge):
            def finish(self, status, reason):
                super().finish(status, reason)
                self.action.pop('endEvidence')
                self.action['endEvidenceStatus'] = 'pending'
        bridge = Paused(complete_after=1)
        result = await move(bridge, 'right', 50)
        self.assertEqual(result['execution']['status'], 'completed')
        self.assertIsNone(result['execution']['inputReleased'])
        self.assertIsNone(result['observation']['endGameUpdate'])
        self.assertTrue(result['observation']['required'])
        self.assertFalse(any(cmd == 'renew_move' for cmd, _ in bridge.calls))

    async def test_left_right_results_and_renewal(self):
        for direction, expected in (("left", 90), ("right", 110)):
            bridge = MovementBridge()
            result = await move(bridge, direction, 200, chat_id="chat:1")
            self.assertTrue(result["completed"])
            self.assertFalse(result["cancelled"])
            self.assertEqual(result['execution'], {'status': 'completed', 'confirmed': True, 'inputReleased': True})
            self.assertEqual(result['effects']['displacementWorldPixels'], {'x': expected-100, 'y': 0})
            self.assertEqual(result['goal']['status'], 'not_applicable')
            self.assertTrue(result['observation']['required'])
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
        self.assertEqual(stopped['execution']['operation'], 'stop_movement')
        self.assertEqual(stopped['execution']['status'], 'completed')
        self.assertEqual(stopped['targetAction']['execution']['status'], 'cancelled')
        self.assertEqual(result['effects']['knowledge'], 'known')
        self.assertEqual(result['effects']['displacementWorldPixels'], {'x': 10, 'y': 0})
        self.assertIsNone(result['effects']['attributedToAction'])
        again = await stop_movement(bridge)
        self.assertTrue(again['execution']['noOp'])
        self.assertEqual(again['targetAction']['execution']['status'], 'cancelled')

    async def test_stop_of_completed_action_is_not_original_action_success(self):
        bridge = MovementBridge(complete_after=1)
        moved = await move(bridge, 'right', 100)
        stopped = await stop_movement(bridge)
        self.assertTrue(stopped['completed'])  # Legacy field still describes the original action.
        self.assertTrue(stopped['execution']['noOp'])
        self.assertEqual(stopped['targetAction']['actionId'], moved['actionId'])
        self.assertEqual(stopped['goal']['status'], 'not_applicable')
        self.assertFalse(any(cmd == 'cancel_move' for cmd, _ in bridge.calls))

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
        self.assertEqual(result['execution'], {'status': 'unknown', 'confirmed': False, 'inputReleased': None})
        self.assertEqual(result['effects']['knowledge'], 'unknown')
        self.assertIsNone(result['effects']['displacementWorldPixels'])
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
        self.assertEqual(result['effects']['beforePositionWorldPixels'], {'x': 100, 'y': 20})
        self.assertIsNone(result['effects']['afterPositionWorldPixels'])
        self.assertEqual(result['execution']['status'], 'unknown')
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
    async def test_goal_evidence_through_agent_and_stop_after_arrival(self):
        class Wall(MovementBridge):
            def finish(self, status, reason):
                original = self.position['x']
                super().finish(status, reason)
                self.position['x'] = original
                self.action['after'] = self.state()
        for bridge, expected in ((MovementBridge(), 'achieved'), (Wall(), 'not_achieved')):
            model = Model(tool('get_observation'),
                          tool('move', '{"direction":"right","duration_ms":200,"target_position":{"x":110,"y":20},"arrival_tolerance":1}'),
                          tool('get_observation'), tool('stop_movement'), final('arrived'))
            agent = Agent(bridge, model)
            agent.receive(event(1)); await agent.task
            replies = [json.loads(next(m['content'] for m in reversed(request) if m['role'] == 'tool'))
                       for request in model.requests[2:]]
            self.assertEqual(replies[0]['execution']['status'], 'completed')
            self.assertEqual(replies[0]['goal']['status'], 'unknown')
            self.assertEqual(replies[0]['effects']['displacementWorldPixels']['x'], 10 if expected == 'achieved' else 0)
            verified, stopped = replies[1], replies[2]
            self.assertEqual(verified['movementLoop']['goal']['status'], expected)
            self.assertEqual(verified['movementLoop']['goal'], stopped['movementLoop']['goal'])
            self.assertEqual(stopped['execution']['operation'], 'stop_movement')
            self.assertTrue(stopped['execution']['noOp'])
            self.assertEqual(agent.last_movement_result['status'], 'arrived' if expected == 'achieved' else 'stopped')
            self.assertEqual(agent.last_movement_result['execution']['status'], 'completed')

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
        model = Model(tool("get_observation"), tool("move", '{"direction":"left","duration_ms":2000}'), final("stopped"))
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
