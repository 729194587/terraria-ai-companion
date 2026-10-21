import asyncio
import copy
import io
import json
from pathlib import Path
import socket
import sys
import threading
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from agent import Agent, TOOLS, read_observation
from control import Bridge, BridgeError, MAX_RESPONSE_BYTES
from observation import row_indices, validate_observation
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
            self.assertEqual(result["observation"], fixture())
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
