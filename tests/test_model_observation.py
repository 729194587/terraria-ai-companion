import copy
import unittest
from unittest.mock import patch

from test_exploration import recorded, synthetic, cell
from test_agent import Game
from agent import read_observation
from observation import project_model_observation


class ModelObservationTests(unittest.TestCase):
    def test_projection_preserves_world_geometry_safety_and_completeness(self):
        source = recorded()
        before = copy.deepcopy(source)
        result = project_model_observation(source)
        self.assertEqual(source, before)
        for key in source:
            if key != "player":
                self.assertEqual(result[key], source[key], key)
        omitted = {"inventory", "equipment", "dyes", "miscEquipment", "miscDyes", "buffs"}
        for key in omitted:
            self.assertNotIn(key, result["player"])
        for key in source["player"].keys() - omitted:
            self.assertEqual(result["player"][key], source["player"][key], key)
        self.assertIn("inspect_inventory", result["player"]["inventoryScope"])

    def test_unknown_terrain_and_unavailable_are_preserved(self):
        source = synthetic([cell(knowledge="unloaded", collisionKnowledge="unknown_mod_hooks")])
        self.assertEqual(project_model_observation(source)["terrain"], source["terrain"])
        source = recorded()
        source.update(status="unavailable", player=None)
        source["completeness"].update(completeWithinScope=False, reasons=["unavailable"])
        self.assertIsNone(project_model_observation(source)["player"])

    def test_reader_validates_before_projecting_even_with_unvalidated_bridge(self):
        game = Game()
        source = recorded()
        game.get_observation = lambda: {"observation": source}
        with patch("observation.validate_observation", wraps=__import__("observation").validate_observation) as validate:
            result = read_observation(game)
            validate.assert_called_once_with(source)
        self.assertTrue(result["valid"])
        self.assertNotIn("buffs", result["observation"]["player"])
        for mutate in (lambda s: s.update(schemaVersion=999),
                       lambda s: s["terrain"]["rows"][0].update(data=[999999, 0]),
                       lambda s: s["completeness"].update(completeWithinScope="yes")):
            broken = copy.deepcopy(source)
            mutate(broken)
            game.get_observation = lambda: {"observation": broken}
            with self.assertRaises(ValueError):
                read_observation(game)
