import asyncio
import copy
import io
import json
from pathlib import Path
import socket
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from agent import Agent, TOOLS, read_observation
from control import Bridge, BridgeError, MAX_RESPONSE_BYTES
from observation import ObservationValidationError, row_indices, validate_observation
from agent_trace import Trace
from test_agent import Game, Model, event, tool, final


def fixture():
    return {"schemaVersion": 1, "worldSessionId": "world", "observationId": "world:1",
            "gameUpdate": 42, "sampledAt": "fixture-utc", "phase": "pre_DoUpdate", "status": "ok",
            "samplingMilliseconds": 1.2, "player": {"id": 1}, "environment": {"dayTime": True},
            "camera": {"status": "recent_render", "ageMilliseconds": 16,
                       "frame": {"renderSequence": 1, "sourceGameUpdate": 41,
                                 "worldCorners": [{"x": x, "y": y} for x, y in ((0, 0), (32, 0), (32, 16), (0, 16))]}},
            "terrain": {"encodingVersion": 1, "tileSize": 16,
                        "bounds": {"x": 0, "y": 0, "width": 2, "height": 1}, "types": [],
                        "palette": [{"knowledge": "read", "hasTile": False, "collisionKnowledge": "known"}],
                        "rows": [{"encoding": "rle", "data": [2, 0]}], "unknownCount": 0, "uninterpretedCount": 0},
            "entities": {"items": [], "coverage": {"matchedCount": 0, "returnedCount": 0, "omittedCount": 0}},
            "completeness": {"completeWithinScope": True, "reasons": [], "limitations": ["client-known only"]}}


class ObservationTests(unittest.TestCase):
    @staticmethod
    def entity():
        return {"kind": "npc", "instanceKey": "world:1:npc:3", "sampleGameUpdate": 42, "inViewport": True,
                "boundsWorldPixels": {"x": 0, "y": 0, "width": 10, "height": 10},
                "velocityPixelsPerTick": {"x": 0, "y": 0}}

    def test_entity_failures_have_field_level_context(self):
        cases = [
            ({"kind": "invalid"}, "invalid_kind", "kind"),
            ({"instanceKey": None}, "invalid_instance_key_type", "instanceKey"),
            ({"instanceKey": 123}, "invalid_instance_key_type", "instanceKey"),
            ({"instanceKey": ""}, "empty_instance_key", "instanceKey"),
            ({"sampleGameUpdate": 41}, "sample_tick_mismatch", "sampleGameUpdate"),
            ({"sampleGameUpdate": 42.0}, "sample_tick_mismatch", "sampleGameUpdate"),
            ({"inViewport": False}, "invalid_in_viewport", "inViewport"),
            ({"inViewport": 1}, "invalid_in_viewport", "inViewport"),
            ({"inViewport": "true"}, "invalid_in_viewport", "inViewport"),
        ]
        for changes, code, field in cases:
            value = fixture(); entity = {**self.entity(), **changes}
            value["entities"]["items"] = [entity]
            value["entities"]["coverage"].update(matchedCount=1, returnedCount=1)
            with self.subTest(code=code, changes=changes), self.assertRaises(ObservationValidationError) as failure:
                validate_observation(value)
            diagnostic = failure.exception.diagnostic
            self.assertEqual(diagnostic["code"], code)
            self.assertEqual(diagnostic["observationId"], "world:1")
            self.assertEqual(diagnostic["gameUpdate"], 42)
            self.assertEqual(diagnostic["entityIndex"], 0)
            self.assertEqual(diagnostic["kind"], entity["kind"])
            self.assertEqual(diagnostic["instanceKey"], entity["instanceKey"])
            self.assertEqual(diagnostic["field"], field)
            self.assertEqual(diagnostic["actual"], entity[field])
            self.assertTrue(diagnostic["expected"])

    def test_missing_duplicate_and_nonobject_entities_are_distinct(self):
        for case, code, index in (("missing", "missing_instance_key", 0),
                                  ("duplicate", "duplicate_instance_key", 1),
                                  ("nonobject", "invalid_entity", 0)):
            value = fixture(); entity = self.entity()
            if case == "missing":
                del entity["instanceKey"]
            items = [entity, dict(entity)] if case == "duplicate" else [None if case == "nonobject" else entity]
            value["entities"]["items"] = items
            value["entities"]["coverage"].update(matchedCount=len(items), returnedCount=len(items))
            with self.subTest(case=case), self.assertRaises(ObservationValidationError) as failure:
                validate_observation(value)
            diagnostic = failure.exception.diagnostic
            self.assertEqual(diagnostic["code"], code)
            self.assertEqual(diagnostic["entityIndex"], index)
            if case == "duplicate":
                self.assertEqual(diagnostic["firstEntityIndex"], 0)
            if case == "missing":
                self.assertFalse(diagnostic["present"])

    def test_valid_entities_still_pass_strict_validation(self):
        value = fixture()
        value["entities"]["items"] = [self.entity()]
        value["entities"]["coverage"].update(matchedCount=1, returnedCount=1)
        self.assertIs(validate_observation(value), value)

    def test_live_duplicate_item_key_regression(self):
        # Sanitized shape of the failing live reply: distinct types/bounds, same item:0 key.
        value = fixture()
        item = {**self.entity(), "kind": "item", "slot": 0, "instanceKey": "world:1:item:0", "typeId": 23}
        other = {**item, "typeId": 321,
                 "boundsWorldPixels": {"x": 30, "y": 0, "width": 20, "height": 20}}
        value["entities"]["items"] = [item, other]
        value["entities"]["coverage"].update(matchedCount=2, returnedCount=2)
        with self.assertRaises(ObservationValidationError) as failure:
            validate_observation(value)
        self.assertEqual(failure.exception.diagnostic["code"], "duplicate_instance_key")
        self.assertEqual(failure.exception.diagnostic["firstEntityIndex"], 0)
        # Once producer uses the actual array slot, both objects survive unchanged in the observation.
        for entity, slot in zip(value["entities"]["items"], (4, 17)):
            entity["slot"] = slot
            entity["instanceKey"] = f"world:1:item:{slot}"
        self.assertIs(validate_observation(value), value)
        self.assertEqual([e["typeId"] for e in value["entities"]["items"]], [23, 321])

    def test_bridge_getter_and_versioned_schema(self):
        bridge = Bridge.__new__(Bridge)
        bridge.hello = {"capabilities": ["viewport_observation_v1"]}
        bridge.command = lambda cmd: {"status": "observation", "observation": fixture()}
        self.assertEqual(bridge.get_observation()["observation"]["gameUpdate"], 42)
        for field in ("schemaVersion",):
            value = fixture(); value[field] = 2
            bridge.command = lambda cmd: {"status": "observation", "observation": value}
            with self.assertRaises(BridgeError):
                bridge.get_observation()
        bridge.hello = {}
        with self.assertRaisesRegex(BridgeError, "capability"):
            bridge.get_observation()

    def test_invalid_encodings_coverage_and_metadata(self):
        mutations = [
            lambda v: v["terrain"].update(encodingVersion=2),
            lambda v: v["terrain"]["rows"][0].update(encoding="future"),
            lambda v: v["terrain"]["rows"][0].update(data=[1, 0]),
            lambda v: v["terrain"]["rows"][0].update(data=[3, 0]),
            lambda v: v["terrain"]["rows"][0].update(data=[0, 0]),
            lambda v: v["terrain"]["rows"][0].update(data=[2, 99]),
            lambda v: v["terrain"].update(unknownCount=1),
            lambda v: v["entities"]["coverage"].update(matchedCount=13),
            lambda v: v["camera"].update(ageMilliseconds=float("nan")),
            lambda v: v["completeness"].update(completeWithinScope=False),
            lambda v: v.update(worldSessionId=None),
        ]
        for mutate in mutations:
            value = fixture(); mutate(value)
            with self.subTest(value=value), self.assertRaises(ValueError):
                validate_observation(value)

    def test_unknown_and_stale_are_not_empty_or_fresh(self):
        value = fixture()
        value["status"] = "stale"
        value["camera"]["status"] = "stale"
        value["completeness"].update(completeWithinScope=False, reasons=["camera_stale", "unloaded"])
        value["terrain"]["palette"][0]["knowledge"] = "unloaded"
        value["terrain"]["unknownCount"] = 2
        self.assertIs(validate_observation(value), value)
        value["status"] = "unavailable"; value["camera"] = None
        value["terrain"] = None; value["entities"] = None
        value["worldSessionId"] = None; value["observationId"] = None
        self.assertIs(validate_observation(value), value)

    def test_mixed_rows_decode(self):
        self.assertEqual(list(row_indices({"encoding": "indices", "data": [0, 1, 0]}, 2, 3)), [0, 1, 0])
        self.assertEqual(list(row_indices({"encoding": "rle", "data": [2, 1, 1, 0]}, 2, 3)), [1, 1, 0])

    def test_large_valid_json_and_hard_limits(self):
        bridge = Bridge.__new__(Bridge)
        value = fixture(); value["fixturePadding"] = "x" * 100000
        wire = (json.dumps({"status": "observation", "observation": value}) + "\n").encode()
        self.assertGreater(len(wire), 65536)
        bridge.reader = io.BytesIO(wire)
        reply = bridge._read()
        self.assertEqual(validate_observation(reply["observation"]), value)
        self.assertEqual(reply["transportMetrics"]["receivedBytes"], len(wire))
        for raw in (b" " * (MAX_RESPONSE_BYTES + 1), wire[:-1], b"invalid\n", b"[]\n"):
            bridge.reader = io.BytesIO(raw)
            with self.subTest(size=len(raw)), self.assertRaises((BridgeError, ValueError)):
                bridge._read()

    def test_more_than_twelve_entities_and_geometry_validation(self):
        value = fixture()
        item = {"kind": "projectile", "instanceKey": "", "sampleGameUpdate": 42, "inViewport": True,
                "boundsWorldPixels": {"x": 0, "y": 0, "width": 10, "height": 10},
                "velocityPixelsPerTick": {"x": 1, "y": -2}, "life": None}
        value["entities"]["items"] = [{**item, "instanceKey": str(i)} for i in range(25)]
        value["entities"]["coverage"].update(matchedCount=25, returnedCount=25)
        self.assertEqual(len(validate_observation(value)["entities"]["items"]), 25)
        value["entities"]["items"][0]["velocityPixelsPerTick"] = {"x": float("inf"), "y": 0}
        with self.assertRaises(ValueError):
            validate_observation(value)

    def test_real_bridge_timeout_and_explicit_oversize_error(self):
        for status in ("timeout", "response_too_large"):
            listener = socket.socket(); listener.bind(("127.0.0.1", 0)); listener.listen()
            errors = []
            def serve():
                try:
                    with listener, listener.accept()[0] as peer:
                        peer.sendall(b'{"v":1,"type":"hello","capabilities":["viewport_observation_v1"]}\n')
                        request = json.loads(peer.makefile("rb").readline())
                        if status == "response_too_large":
                            peer.sendall((json.dumps({"v": 1, "id": request["id"], "ok": False,
                                "status": status}) + "\n").encode())
                        peer.recv(1)  # Wait for client close, no sleep or model call.
                except Exception as error:
                    errors.append(error)
            thread = threading.Thread(target=serve, daemon=True); thread.start()
            with Bridge(listener.getsockname()[1], timeout=0.1) as bridge:
                with self.assertRaisesRegex(BridgeError, "timeout" if status == "timeout" else status):
                    bridge.get_observation()
            thread.join(2)
            self.assertFalse(thread.is_alive())
            self.assertFalse(errors)


class ObservationAgentTests(unittest.IsolatedAsyncioTestCase):
    async def test_validation_diagnostic_reaches_trace_and_model_error_result(self):
        value = fixture(); entity = ObservationTests.entity()
        value["entities"]["items"] = [entity, dict(entity)]
        value["entities"]["coverage"].update(matchedCount=2, returnedCount=2)
        bridge = Bridge.__new__(Bridge)
        bridge.hello = {"capabilities": ["viewport_observation_v1"]}
        bridge.command = lambda cmd: {"status": "observation", "observation": value}
        game = Game(); game.get_observation = bridge.get_observation
        model = Model(tool("get_observation"), final("Observation failed: duplicate_instance_key"))
        agent = Agent(game, model)
        try:
            with tempfile.TemporaryDirectory() as directory:
                trace = Trace(Path(directory) / "trace.jsonl", enabled=True)
                with patch("agent_trace.trace", trace):
                    agent.receive(event(1)); await agent.task
                records = [json.loads(line) for line in trace.path.read_text(encoding="utf-8").splitlines()]
            result = json.loads(model.requests[1][-1]["content"])
            self.assertFalse(result["valid"])
            self.assertEqual(result["diagnostic"]["code"], "duplicate_instance_key")
            self.assertIn('"entityIndex": 1', result["error"])
            diagnostic_event = next(r for r in records if r["type"] == "observation_validation_failed")
            self.assertEqual(diagnostic_event["diagnostic"], result["diagnostic"])
            tool_event = next(r for r in records if r["type"] == "tool_result")
            self.assertEqual(tool_event["result"]["diagnostic"], result["diagnostic"])
            self.assertNotIn("player", diagnostic_event)
            self.assertNotIn("history", diagnostic_event)
            self.assertEqual(game.sent[-1][1]["text"], "Observation failed: duplicate_instance_key")
        finally:
            await agent.close()

    async def test_same_agent_can_read_and_chat_while_dead_then_respawned(self):
        value = fixture(); value["status"] = "partial"; value["player"]["dead"] = True
        value["completeness"].update(completeWithinScope=False, reasons=["local_player_dead_or_ghost"])
        game = Game()
        def read():
            return {"observation": validate_observation(copy.deepcopy(value))}
        game.get_observation = read
        model = Model(tool("get_observation"), final("dead but observing"), tool("get_observation"), final("respawned"))
        agent = Agent(game, model)
        try:
            agent.receive(event(1)); await agent.task
            dead = json.loads(model.requests[1][-1]["content"])
            self.assertTrue(dead["valid"])
            self.assertEqual(dead["observation"]["status"], "partial")
            value["player"]["dead"] = False; value["gameUpdate"] = 43; value["observationId"] = "world:2"
            value["status"] = "ok"; value["completeness"].update(completeWithinScope=True, reasons=[])
            agent.receive(event(2)); await agent.task
            revived = json.loads(model.requests[3][-1]["content"])
            self.assertEqual(revived["observation"]["gameUpdate"], 43)
            self.assertFalse(revived["observation"]["player"]["dead"])
            self.assertEqual([p["text"] for _, p in game.sent], ["dead but observing", "respawned"])
        finally:
            await agent.close()

    async def test_tool_call_reaches_model_and_old_snapshot_expires(self):
        game = Game()
        game.get_observation = lambda: {"observation": fixture()}
        model = Model(tool("get_observation"), final("observed"), final("hello"))
        agent = Agent(game, model)
        self.assertIn("get_observation", [t["function"]["name"] for t in TOOLS])
        try:
            agent.receive(event(1)); await agent.task
            result = json.loads(model.requests[1][-1]["content"])
            self.assertTrue(result["valid"])
            expected = fixture()
            expected["player"]["inventoryScope"] = result["observation"]["player"]["inventoryScope"]
            self.assertEqual(result["observation"], expected)
            agent.receive(event(2)); await agent.task
            old = [m for m in model.requests[2] if m["role"] == "tool"]
            self.assertTrue(json.loads(old[0]["content"])["expired"])
            self.assertNotIn("observation", json.loads(old[0]["content"]))
        finally:
            await agent.close()

    async def test_partial_and_failed_observation_tool_results(self):
        for fail in (False, True):
            game = Game(); value = fixture(); value.update(status="partial")
            value["completeness"].update(completeWithinScope=False, reasons=["paused"])
            def read():
                if fail:
                    raise BridgeError("fixture observation failed")
                return {"observation": value}
            game.get_observation = read
            model = Model(tool("get_observation"), final("answer"))
            agent = Agent(game, model)
            try:
                agent.receive(event(1)); await agent.task
                result = json.loads(model.requests[1][-1]["content"])
                self.assertEqual(result["valid"], not fail)
                if not fail:
                    self.assertFalse(result["observation"]["completeness"]["completeWithinScope"])
            finally:
                await agent.close()
