import copy
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from agent import read_state
from spatial import describe_space
from test_agent import Game


def snapshot(x=1598, y=1571):
    reply = Game().get_terrain()
    reply["state"]["position"] = {"x": x, "y": y}
    reply["terrain"].update(originX=78, originY=83)
    return reply


def put(reply, tx, ty, kind):
    terrain = reply["terrain"]
    row_index, column = ty - terrain["originY"], tx - terrain["originX"]
    row = list(terrain["rows"][row_index])
    row[column] = kind
    terrain["rows"][row_index] = "".join(row)


class SpatialTests(unittest.TestCase):
    def test_anchors_and_zero_based_grid(self):
        reply = snapshot(1600.5, 1558.25)
        anchors = describe_space(reply["state"], reply["terrain"])["playerAnchors"]
        for name, world, tile, grid in (
            ("center", (1610.5, 1579.25), (100, 98), (15, 22)),
            ("feet", (1610.5, 1600.25), (100, 100), (17, 22)),
            ("head", (1610.5, 1558.25), (100, 97), (14, 22))):
            self.assertEqual(anchors[name]["worldPixels"], dict(zip(("x", "y"), world)))
            self.assertEqual(anchors[name]["tile"], dict(zip(("x", "y"), tile)))
            self.assertEqual(anchors[name]["grid"], dict(zip(("row", "column"), grid)))
            self.assertTrue(anchors[name]["insideGrid"])

    def test_negative_and_exact_boundary_coordinates(self):
        reply = snapshot(-11, -43)
        anchors = describe_space(reply["state"], reply["terrain"])["playerAnchors"]
        self.assertEqual(anchors["center"]["tile"], {"x": -1, "y": -2})
        self.assertEqual(anchors["feet"]["tile"], {"x": -1, "y": -1})
        self.assertFalse(anchors["feet"]["insideGrid"])
        reply = snapshot(6, 6)
        feet = describe_space(reply["state"], reply["terrain"])["playerAnchors"]["feet"]
        self.assertEqual(feet["worldPixels"], {"x": 16, "y": 48})
        self.assertEqual(feet["tile"], {"x": 1, "y": 3})

    def test_relative_directions_and_distances(self):
        reply = snapshot()
        for tx, ty, kind in ((99, 99, "h"), (101, 99, "1"), (100, 98, "2"),
                             (100, 100, "3"), (99, 98, "4"), (100, 99, "a")):
            put(reply, tx, ty, kind)
        result = describe_space(reply["state"], reply["terrain"])
        items = {item["collectorClass"]: item for item in result["nearbySpecialTiles"]["items"]}
        for kind, horizontal, vertical, dx, dy in (
            ("h", "left", "aligned", -16, 0), ("1", "right", "aligned", 16, 0),
            ("2", "aligned", "above", 0, -16), ("3", "aligned", "below", 0, 16),
            ("4", "left", "above", -16, -16), ("a", "aligned", "aligned", 0, 0)):
            self.assertEqual(items[kind]["direction"], {"horizontal": horizontal, "vertical": vertical})
            self.assertEqual(items[kind]["deltaPixels"], {"x": dx, "y": dy})
            self.assertAlmostEqual(items[kind]["distancePixels"], (dx * dx + dy * dy) ** 0.5)
            self.assertAlmostEqual(items[kind]["distanceTiles"], items[kind]["distancePixels"] / 16)
        self.assertEqual(items["h"]["tile"], {"x": 99, "y": 99})
        self.assertEqual(items["h"]["cellCenterWorldPixels"], {"x": 1592, "y": 1592})
        self.assertEqual(items["h"]["grid"], {"row": 16, "column": 21})

    def test_radius_limit_and_deterministic_order(self):
        reply = snapshot()
        put(reply, 106, 99, "h")
        put(reply, 107, 99, "1")
        nearby = describe_space(reply["state"], reply["terrain"])["nearbySpecialTiles"]
        self.assertEqual(nearby["matchedCount"], 1)
        self.assertEqual(nearby["items"][0]["distancePixels"], 96)
        reply["terrain"]["rows"] = ["=" * 45] * 29
        space = describe_space(reply["state"], reply["terrain"])
        nearby = space["nearbySpecialTiles"]
        self.assertGreater(nearby["matchedCount"], 16)
        self.assertEqual(len(nearby["items"]), 16)
        self.assertTrue(nearby["truncated"])
        self.assertEqual(nearby["countsBeforeLimit"]["="], nearby["matchedCount"])
        self.assertEqual(describe_space(reply["state"], reply["terrain"]), space)
        self.assertEqual(nearby["items"][0]["distancePixels"], 0)

    def test_unknown_absence_and_origin_mismatch_do_not_change_data(self):
        reply = snapshot()
        put(reply, 99, 99, "?")
        reply["terrain"]["originX"] += 1
        original = copy.deepcopy(reply)
        space = describe_space(reply["state"], reply["terrain"])
        self.assertEqual(space["unknown"]["gridUnknownCount"], 1)
        self.assertTrue(any("origin_mismatch" in warning for warning in space["warnings"]))
        self.assertFalse(any(item["collectorClass"] == "h" for item in space["nearbySpecialTiles"]["items"]))
        self.assertEqual(space["nearbySpecialTiles"]["countsBeforeLimit"]["h"], 0)
        self.assertEqual(space["nearbySpecialTiles"]["items"][0]["tile"], {"x": 100, "y": 99})
        self.assertEqual(reply, original)

    def test_read_state_uses_only_the_terrain_reply_for_derivations(self):
        reply = snapshot(1600.5, 1558.25)
        original = copy.deepcopy(reply)
        class Bridge:
            def get_state(self):
                return {"state": {"inWorld": True, "position": {"x": 99999, "y": 99999}}}
            def get_terrain(self):
                return reply
        result = read_state(Bridge())
        self.assertIs(result["terrain"], reply["terrain"])
        self.assertIs(result["player"], reply["state"])
        self.assertEqual(result["spatial"]["playerAnchors"]["feet"]["worldPixels"], {"x": 1610.5, "y": 1600.25})
        self.assertEqual(json.loads(json.dumps(result))["terrain"], original["terrain"])
        self.assertEqual(reply, original)

    def test_unsupported_pose_and_invalid_dimensions(self):
        reply = snapshot()
        reply["terrain"]["supported"] = False
        space = describe_space(reply["state"], reply["terrain"])
        self.assertTrue(any("geometric" in warning for warning in space["warnings"]))
        reply["terrain"]["playerWidth"] = 0
        with self.assertRaises(ValueError):
            describe_space(reply["state"], reply["terrain"])


if __name__ == "__main__":
    unittest.main()
