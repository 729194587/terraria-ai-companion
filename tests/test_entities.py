import copy
import io
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from agent import Agent, read_state, SYSTEM, TOOLS
from control import Bridge, BridgeError, MAX_RESPONSE_BYTES
from test_agent import Game, Model, event, tool, final


def entities():
    return {"available": True, "sampledAt": "fixture-time", "gameUpdate": 42, "observerPlayerId": 1,
        "referenceCenterWorldPixels": {"x": 110, "y": 221}, "radiusPixels": 480, "limit": 12,
        "matchedCount": 1, "truncated": False, "scope": "client-known only",
        "items": [{"kind": "npc", "id": 2, "typeId": 1, "typeName": "Slime", "name": "史莱姆",
            "classification": "hostile_creature", "classificationBasis": "inferred from engine flags; not actual aggression",
            "centerWorldPixels": {"x": 126, "y": 221}, "deltaPixels": {"x": 16, "y": 0},
            "distancePixels": 16, "horizontal": "right", "vertical": "aligned"}]}


class EntityTests(unittest.TestCase):
    def test_uses_same_terrain_reply_and_preserves_all_data(self):
        game = Game()
        reply = game.get_terrain()
        reply["entities"] = entities()
        original = copy.deepcopy(reply)
        game.get_terrain = lambda: reply
        # Separate get_state must never supply the entity observation or reference position.
        game.get_state = lambda: {"state": {"inWorld": True, "position": {"x": 9999, "y": 9999}},
                                  "entities": {"available": False}}
        result = read_state(game)
        self.assertIs(result["entities"], reply["entities"])
        self.assertEqual(result["entities"]["gameUpdate"], 42)
        self.assertEqual(result["spatial"]["playerAnchors"]["center"]["worldPixels"],
                         result["entities"]["referenceCenterWorldPixels"])
        self.assertEqual(result["terrain"], original["terrain"])
        self.assertEqual(result["player"], original["state"])
        self.assertEqual(reply, original)

    def test_missing_entities_is_unavailable_not_empty(self):
        result = read_state(Game())
        self.assertFalse(result["entities"]["available"])
        self.assertIn("0.1.18", result["entities"]["reason"])
        self.assertNotIn("items", result["entities"])

    def test_empty_and_truncated_observations_stay_distinct(self):
        game = Game()
        reply = game.get_terrain()
        reply["entities"] = entities()
        game.get_terrain = lambda: reply
        observation = reply["entities"]
        observation.update(items=[], matchedCount=0)
        self.assertEqual(read_state(game)["entities"]["items"], [])
        observation.update(items=[entities()["items"][0]] * 12, matchedCount=20, truncated=True)
        self.assertTrue(read_state(game)["entities"]["truncated"])
        self.assertEqual(read_state(game)["entities"]["matchedCount"], 20)

    def test_malformed_metadata_entries_and_self_are_not_valid_observations(self):
        broken = []
        value = entities(); value["truncated"] = True; broken.append(value)
        value = entities(); value["items"][0]["distancePixels"] = float("nan"); broken.append(value)
        value = entities(); value["items"][0]["classification"] = "certain_enemy"; broken.append(value)
        value = entities(); value["items"][0].update(kind="player", id=1); broken.append(value)
        value = entities(); value.pop("gameUpdate"); broken.append(value)
        value = entities(); value["items"] = [None]; broken.append(value)
        for observation in broken:
            with self.subTest(observation=observation):
                game = Game()
                reply = game.get_terrain()
                reply["entities"] = observation
                game.get_terrain = lambda: reply
                result = read_state(game)
                self.assertFalse(result["entities"]["available"])
                self.assertTrue(result["valid"])
                self.assertIn("spatial", result)

    def test_larger_entity_reply_is_accepted_but_still_bounded(self):
        payload = Game().get_terrain()
        payload["entities"] = entities()
        payload["entities"]["items"] *= 12
        raw = (json.dumps(payload) + "\n").encode()
        self.assertGreater(len(raw), 4096)
        bridge = Bridge.__new__(Bridge)
        bridge.reader = io.BytesIO(raw)
        self.assertEqual(bridge._read(), payload)
        bridge.reader = io.BytesIO(b" " * (MAX_RESPONSE_BYTES + 1) + b"\n")
        with self.assertRaises(BridgeError):
            bridge._read()


class EntityAgentTests(unittest.IsolatedAsyncioTestCase):
    async def test_entities_reach_model_tool_message(self):
        game = Game()
        reply = game.get_terrain()
        reply["entities"] = entities()
        game.get_terrain = lambda: reply
        model = Model(tool(), final("I see a slime to my right."))
        agent = Agent(game, model)
        try:
            agent.receive(event(1))
            await agent.task
            result = json.loads(model.requests[1][-2]["content"])
            self.assertEqual(result["entities"], reply["entities"])
            self.assertIn("spatial", result)
            self.assertEqual(result["terrain"]["rows"], reply["terrain"]["rows"])
            self.assertIn("NPC", TOOLS[0]["function"]["description"])
            self.assertIn("推断", SYSTEM)
        finally:
            await agent.close()


if __name__ == "__main__":
    unittest.main()
