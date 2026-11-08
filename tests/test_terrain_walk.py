import sys
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from control import BridgeError
from terrain_walk import Walker, SafetyStop, View, main, run


def scene(*, x=1600, bottom=1600, grounded=True, platform=False, cells=(), mode="none"):
    left = int((x + 10) // 16) - 22
    top = int(bottom // 16) - 17
    rows = [["=" if platform and y == 100 else "#" if not platform and y >= 100 else "."
             for _ in range(45)] for y in range(top, top + 29)]
    for tx, ty, cell in cells:
        if 0 <= ty - top < 29 and 0 <= tx - left < 45:
            rows[ty - top][tx - left] = cell
    return {"mode": mode, "state": {"inWorld": True, "position": {"x": x, "y": bottom - 42},
             "velocity": {"x": 0, "y": 0}, "life": 100, "lifeMax": 100},
            "terrain": {"originX": left, "originY": top, "tileSize": 16,
                        "rows": ["".join(row) for row in rows], "playerWidth": 20,
                        "playerHeight": 42, "grounded": grounded, "supported": True}}


class FakeClock:
    def __init__(self):
        self.now = 0

    def __call__(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


class FakeBridge:
    def __init__(self, clock, frames=None, delay=0, fail_input=False):
        self.clock, self.delay, self.fail_input = clock, delay, fail_input
        self.frames = iter(frames or [scene()])
        self.last = scene()
        self.commands, self.closed = [], False

    def get_terrain(self):
        self.clock.sleep(self.delay)
        self.last = next(self.frames, self.last)
        if isinstance(self.last, Exception):
            raise self.last
        return self.last

    def input(self, direction, jump):
        self.commands.append((direction, jump))
        if self.fail_input:
            raise BridgeError("Command rejected: manual_active")

    def command(self, cmd):
        self.commands.append(cmd)

    def close(self):
        self.closed = True


class Decisions(unittest.TestCase):
    def test_flat_ground_both_directions(self):
        for direction in (-1, 1):
            decision = Walker(direction).decide(scene(), 0)
            self.assertEqual((decision.direction, decision.jump), (direction, False))

    def test_platform_support_is_not_a_wall(self):
        self.assertFalse(Walker().decide(scene(platform=True), 0).jump)
        self.assertFalse(Walker().decide(scene(cells=[(103, 99, "=")]), 0).jump)

    def test_low_block_and_half_block_trigger_one_jump(self):
        for cells in ([(103, 99, "#")], [(103, 99, "h")], [(103, 98, "#"), (103, 99, "#")]):
            with self.subTest(cells=cells):
                walker = Walker()
                self.assertTrue(walker.decide(scene(cells=cells), 0).jump)
                self.assertTrue(walker.decide(scene(x=1604, bottom=1580, grounded=False, cells=cells), 0.1).jump)
                self.assertFalse(walker.decide(scene(x=1610, bottom=1560, grounded=False, cells=cells), 0.3).jump)

    def test_left_obstacle_jump(self):
        self.assertTrue(Walker(-1).decide(scene(cells=[(98, 99, "#")]), 0).jump)

    def test_distant_obstacle_walks_before_jumping(self):
        cells = [(106, 99, "#")]
        self.assertFalse(Walker().decide(scene(cells=cells), 0).jump)

    def test_obstacle_at_lookahead_edge_uses_visible_landing_both_directions(self):
        for direction, edge in ((1, 117), (-1, 83)):
            for width in (1, 2):
                cells = [(edge + direction * i, 99, "#") for i in range(width)]
                with self.subTest(direction=direction, width=width):
                    self.assertFalse(Walker(direction).decide(scene(cells=cells), 0).jump)

    def test_lookahead_edge_does_not_hide_gap_after_obstacle(self):
        cells = [(117, 99, "#")] + [(118, y, ".") for y in range(100, 104)]
        self.assertFalse(Walker().decide(scene(cells=cells), 0).jump)
        # Once approached, the missing landing is still rejected.
        with self.assertRaisesRegex(SafetyStop, "gap or drop"):
            Walker().decide(scene(x=1836, cells=cells), 0)

    def test_half_block_then_long_raised_ground_can_be_approached(self):
        cells = [(117, 99, "h")] + [(x, 99, "#") for x in range(118, 123)]
        self.assertFalse(Walker().decide(scene(cells=cells), 0).jump)

    def test_low_plateau_jump_lands_on_top_both_directions(self):
        for direction in (-1, 1):
            edge = 103 if direction == 1 else 98
            cells = [(edge + direction * i, 99, "#") for i in range(20)]
            walker = Walker(direction)
            with self.subTest(direction=direction):
                self.assertEqual(walker.decide(scene(cells=cells), 0).reason, "try one low step-up jump")
                walker.decide(scene(x=1600 + direction * 10, bottom=1580, grounded=False, cells=cells), 0.1)
                decision = walker.decide(scene(x=1600 + direction * 100, bottom=1584, cells=cells), 0.8)
                self.assertEqual(decision.reason, "landed")
                self.assertFalse(walker.decide(scene(x=1600 + direction * 104, bottom=1584, cells=cells), 0.9).jump)

    def test_half_block_step_up_nearby_jumps_once(self):
        cells = [(103, 99, "h")] + [(x, 99, "#") for x in range(104, 123)]
        self.assertEqual(Walker().decide(scene(cells=cells), 0).reason, "try one low step-up jump")

    def test_step_up_with_pit_ahead_stops(self):
        cells = [(x, 99, "#") for x in range(103, 110)] + [(110, y, ".") for y in range(100, 104)]
        self.assertTrue(Walker().decide(scene(cells=cells), 0).jump)
        with self.assertRaisesRegex(SafetyStop, "gap or drop"):
            Walker().decide(scene(x=1696, bottom=1584, cells=cells), 0)

    def test_step_up_with_low_ceiling_stops(self):
        cells = [(x, 99, "#") for x in range(103, 123)] + [(104, 94, "#")]
        with self.assertRaisesRegex(SafetyStop, "headroom"):
            Walker().decide(scene(cells=cells), 0)

    def test_deep_gap_stops_before_edge(self):
        cells = [(104, y, ".") for y in range(100, 104)]
        with self.assertRaisesRegex(SafetyStop, "gap or drop"):
            Walker().decide(scene(cells=cells), 0)

    def test_one_tile_drop_is_allowed(self):
        self.assertFalse(Walker().decide(scene(cells=[(110, 100, ".")]), 0).jump)

    def test_tall_wall_or_wide_obstacle_stops(self):
        for cells in ([(103, y, "#") for y in range(90, 100)],
                      [(x, 99, "#") for x in range(103, 106)]):
            with self.assertRaises(SafetyStop):
                Walker().decide(scene(cells=cells), 0)

    def test_low_ceiling_stops_jump(self):
        with self.assertRaisesRegex(SafetyStop, "headroom"):
            Walker().decide(scene(cells=[(103, 99, "#"), (102, 94, "#")]), 0)

    def test_unsafe_unknown_and_slope_stop(self):
        for cell in "!?s":
            with self.subTest(cell=cell), self.assertRaises(SafetyStop):
                Walker().decide(scene(cells=[(103, 100, cell)]), 0)

    def test_unsafe_diagnostic_names_cell_and_distance(self):
        for cell, name in (("s", "untyped slope (requires Bridge 0.1.7+)"), ("!", "liquid or marked hazard"), ("?", "unknown terrain")):
            with self.subTest(cell=cell), self.assertRaises(SafetyStop) as failure:
                View(scene(cells=[(103, 100, cell)])).cell(103, 100)
            self.assertEqual(str(failure.exception), f"{name} at tile (103,100), 28px horizontally from player")

    def test_split_liquid_layer_keeps_dry_walker_conservative(self):
        reply = scene()
        reply["terrain"]["liquids"] = [{"x": 103, "y": 99, "type": "water", "amount": 1}]
        with self.assertRaisesRegex(SafetyStop, "liquid or marked hazard"):
            View(reply).cell(103, 99)

    def test_joined_uphill_floor_and_stairs_walk_both_directions(self):
        for direction, code, edge in ((1, "2", 103), (-1, "1", 98), (1, "b", 103), (-1, "a", 98)):
            cells = [(edge, 99, code)] + [(edge + direction * i, 99, "#") for i in range(1, 22)]
            with self.subTest(code=code):
                decision = Walker(direction).decide(scene(cells=cells), 0)
                self.assertFalse(decision.jump)
                self.assertEqual(decision.reason, "walk slope")

    def test_joined_downhill_floor_walk_both_directions(self):
        for direction, code, edge in ((1, "1", 103), (-1, "2", 98)):
            cells = [(edge, 100, code)] + [(edge + direction * i, 100, ".") for i in range(1, 22)]
            # Approach from ground at y=1600 to ground at y=1616.
            with self.subTest(direction=direction):
                self.assertEqual(Walker(direction).decide(scene(cells=cells), 0).reason, "walk slope")

    def test_slope_surface_uses_actual_body_overlap(self):
        for code, x, bottom in (("1", 1652, 1588), ("2", 1636, 1592), ("a", 1652, 1588), ("b", 1636, 1592)):
            with self.subTest(code=code):
                view = View(scene(x=x, bottom=bottom, cells=[(103, 99, code)]))
                self.assertEqual(view.surface(103, bottom), bottom)

    def test_slope_from_steep_side_can_be_jumped_as_low_obstacle(self):
        decision = Walker().decide(scene(cells=[(103, 99, "1")]), 0)
        self.assertTrue(decision.jump)

    def test_ceiling_slopes_are_solid_overhead_not_walkable_ramps(self):
        for code in "34":
            with self.subTest(code=code), self.assertRaisesRegex(SafetyStop, "headroom"):
                Walker().decide(scene(cells=[(103, 99, "#"), (102, 94, code)]), 0)

    def test_ceiling_triangle_empty_corner_is_not_a_full_block(self):
        for code, empty_x, solid_x in (("3", 1660, 1648), ("4", 1632, 1660)):
            with self.subTest(code=code):
                View(scene(x=empty_x, cells=[(103, 99, code)])).clear(103, 1589, 1595)
                with self.assertRaisesRegex(SafetyStop, "headroom"):
                    View(scene(x=solid_x, cells=[(103, 99, code)])).clear(103, 1589, 1595)

    def test_sloped_platform_is_not_a_ceiling(self):
        for code in "ab":
            View(scene(cells=[(103, 99, code)])).clear(103, 1584, 1600)

    def test_sloped_tile_flat_top_supports_standing_above_it(self):
        for code in "34":
            self.assertEqual(View(scene(cells=[(103, 100, code)])).surface(103, 1600), 1600)

    def test_slope_followed_by_deep_gap_stops(self):
        cells = [(103, 99, "2")] + [(x, 99, "#") for x in range(104, 110)]
        cells += [(110, y, ".") for y in range(100, 104)]
        self.assertFalse(Walker().decide(scene(cells=cells), 0).jump)
        with self.assertRaisesRegex(SafetyStop, "gap or drop"):
            Walker().decide(scene(x=1696, bottom=1584, cells=cells), 0)

    def test_slope_followed_by_high_wall_stops(self):
        cells = [(103, 99, "2")] + [(x, 99, "#") for x in range(104, 110)]
        cells += [(110, y, "#") for y in range(90, 100)]
        self.assertFalse(Walker().decide(scene(cells=cells), 0).jump)
        with self.assertRaisesRegex(SafetyStop, "headroom"):
            Walker().decide(scene(x=1696, bottom=1584, cells=cells), 0)

    def test_continuous_multi_tile_slopes_both_directions(self):
        for direction in (-1, 1):
            for up in (True, False):
                edge = 103 if direction == 1 else 98
                code = "2" if (up == (direction == 1)) else "1"
                cells = []
                for i in range(24):
                    x = edge + direction * i
                    y = (99 - i if up else 100 + i) if i < 6 else (94 if up else 106)
                    for row in range(94, 107):
                        cells.append((x, row, "." if row < y else "#"))
                    if i < 6:
                        cells.append((x, y, code))
                with self.subTest(direction=direction, up=up):
                    walker = Walker(direction)
                    self.assertEqual(walker.decide(scene(cells=cells), 0).reason, "walk slope")
                    if direction == 1:
                        # The body spans two slope cells. On descent its rear
                        # supports it while the front reference is 20px lower.
                        bottom = 1568 if up else 1612
                        self.assertEqual(walker.decide(scene(x=1660, bottom=bottom, cells=cells), 0.2).reason,
                                         "walk slope")

    def test_distant_wall_is_forecast_then_stops_when_near(self):
        for direction in (-1, 1):
            wall = 115 if direction == 1 else 85
            cells = [(wall, y, "#") for y in range(90, 100)]
            walker = Walker(direction)
            self.assertFalse(walker.decide(scene(cells=cells), 0).jump)
            self.assertGreater(walker.forecast["distance"], walker.forecast["budget"])
            with self.assertRaisesRegex(SafetyStop, "headroom.*distance=.*stop budget"):
                walker.decide(scene(x=1600 + direction * 160, cells=cells), 0.1)

    def test_stopping_budget_grows_with_speed_and_preserves_margin(self):
        cells = [(110, y, ".") for y in range(100, 104)]
        self.assertFalse(Walker().decide(scene(cells=cells), 0).jump)
        for vx in (-4.1, 4.1):
            frame = scene(cells=cells)
            frame["state"]["velocity"]["x"] = vx
            with self.assertRaisesRegex(SafetyStop, "gap or drop"):
                Walker().decide(frame, 0)

    def test_near_jump_with_far_wall_and_changes_during_flight(self):
        cells = [(103, 99, "#")] + [(115, y, "#") for y in range(90, 100)]
        walker = Walker()
        self.assertTrue(walker.decide(scene(cells=cells), 0).jump)
        changed = cells + [(112, y, "?") for y in range(90, 101)]
        self.assertEqual(walker.decide(scene(x=1605, bottom=1580, grounded=False, cells=changed), 0.1).reason,
                         "jump in progress")
        self.assertEqual(walker.decide(scene(x=1680, cells=changed), 0.8).reason, "landed")

    def test_jump_still_rejects_changed_landing_and_immediate_danger(self):
        for changed, category in (([(104, 100, "!")], "immediate_danger"),
                                  ([(104, 99, "#")], "jump_failed"),
                                  ([(100, 98, "!")], "immediate_danger")):
            cells = [(103, 99, "#")]
            walker = Walker()
            walker.decide(scene(cells=cells), 0)
            with self.assertRaises(SafetyStop) as failure:
                walker.decide(scene(bottom=1580, grounded=False, cells=cells + changed), 0.1)
            self.assertEqual(failure.exception.category, category)

    def test_reported_half_step_then_far_slope_does_not_stop_early(self):
        # Relative layout from the user's saved snapshot; old 's' did not preserve orientation.
        for code in "12":
            cells = [(102, 99, "h")] + [(x, 99, "#") for x in range(103, 123)]
            cells += [(115, 98, code)] + [(x, 98, "#") for x in range(116, 123)]
            with self.subTest(code=code):
                self.assertTrue(Walker().decide(scene(cells=cells), 0).jump)

    def test_failed_takeoff_has_no_retry(self):
        walker = Walker()
        frame = scene(cells=[(103, 99, "#")])
        walker.decide(frame, 0)
        with self.assertRaisesRegex(SafetyStop, "take off"):
            walker.decide(frame, 0.5)

    def test_landing_before_obstacle_is_terminal(self):
        walker = Walker()
        cells = [(103, 99, "#")]
        walker.decide(scene(cells=cells), 0)
        walker.decide(scene(x=1605, bottom=1580, grounded=False, cells=cells), 0.1)
        with self.assertRaisesRegex(SafetyStop, "no retry"):
            walker.decide(scene(x=1610, cells=cells), 0.7)

    def test_successful_jump_lands_past_obstacle(self):
        walker = Walker()
        cells = [(103, 99, "#")]
        walker.decide(scene(cells=cells), 0)
        walker.decide(scene(x=1605, bottom=1580, grounded=False, cells=cells), 0.1)
        self.assertEqual(walker.decide(scene(x=1680, cells=cells), 0.8).reason, "landed")

    def test_high_point_retains_ground_lookahead_in_window(self):
        walker = Walker()
        cells = [(103, 99, "#")]
        walker.decide(scene(cells=cells), 0)
        self.assertFalse(walker.decide(scene(x=1640, bottom=1504, grounded=False, cells=cells), 0.4).jump)

    def test_jump_timeout_and_start_in_air_stop(self):
        walker = Walker()
        cells = [(103, 99, "#")]
        walker.decide(scene(cells=cells), 0)
        with self.assertRaisesRegex(SafetyStop, "finish in time"):
            walker.decide(scene(bottom=1580, grounded=False, cells=cells), 2)
        with self.assertRaisesRegex(SafetyStop, "start on supported ground"):
            Walker().decide(scene(grounded=False), 0)

    def test_stuck_stops_instead_of_repeating(self):
        walker = Walker()
        walker.decide(scene(), 0)
        with self.assertRaisesRegex(SafetyStop, "no forward progress"):
            walker.decide(scene(), 1.1)

    def test_life_loss_and_nonstandard_motion_stop(self):
        walker = Walker()
        frame = scene()
        walker.decide(frame, 0)
        frame["state"]["life"] = 90
        with self.assertRaisesRegex(SafetyStop, "life decreased"):
            walker.decide(frame, 0.1)
        frame = scene()
        frame["state"]["velocity"]["x"] = 10
        with self.assertRaisesRegex(SafetyStop, "speed"):
            Walker().decide(frame, 0)

    def test_invalid_frames_stop(self):
        for section, key, value in (("terrain", "rows", []), ("terrain", "supported", False),
                                    ("state", "inWorld", False), ("state", "position", {"x": float("nan"), "y": 0})):
            frame = scene()
            frame[section][key] = value
            with self.subTest(key=key), self.assertRaises(SafetyStop):
                View(frame)


class LoopTests(unittest.TestCase):
    def execute(self, **kwargs):
        clock = FakeClock()
        bridge = FakeBridge(clock, **kwargs)
        reason = run(bridge, seconds=0.3, clock=clock, sleep=clock.sleep, log=lambda _: None)
        self.assertTrue(bridge.closed)
        return bridge.commands, reason

    def test_deadline_and_explicit_stop(self):
        commands, reason = self.execute()
        self.assertIn("runtime", reason)
        self.assertEqual(commands[-1], "stop")

    def test_late_observation_never_executes_input(self):
        commands, reason = self.execute(delay=0.3)
        self.assertEqual(commands, [])
        self.assertIn("late", reason)

    def test_manual_mode_is_never_stopped(self):
        commands, reason = self.execute(frames=[scene(), scene(mode="manual")])
        self.assertNotIn("stop", commands)
        self.assertIn("manual", reason)

    def test_rejected_input_is_never_followed_by_stop(self):
        commands, reason = self.execute(fail_input=True)
        self.assertEqual(commands, [(1, False)])
        self.assertIn("manual_active", reason)

    def test_disconnect_closes_without_more_commands(self):
        commands, reason = self.execute(frames=[scene(), BridgeError("disconnected")])
        self.assertEqual(commands, [(1, False)])
        self.assertIn("Bridge failure", reason)

    def test_gap_discovered_while_moving_sends_stop(self):
        commands, reason = self.execute(frames=[scene(), scene(cells=[(104, y, ".") for y in range(100, 104)])])
        self.assertEqual(commands, [(1, False), "stop"])

    def test_failure_snapshot_reproduces_decision_without_connecting(self):
        clock = FakeClock()
        frames = [scene(), scene(cells=[(104, y, ".") for y in range(100, 104)])]
        bridge = FakeBridge(clock, frames=frames)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "failure.json"
            reason = run(bridge, clock=clock, sleep=clock.sleep, log=lambda _: None, failure_log=path)
            record = json.loads(path.read_text(encoding="utf-8"))
            self.assertTrue(reason.startswith("terrain:"))
            self.assertEqual(record["category"], "terrain")
            self.assertEqual(len(record["recent_decisions"]), 1)
            walker = Walker()
            walker.__dict__.update(record["walker_before"])
            from collections import deque
            walker.progress = deque(walker.progress)
            with self.assertRaisesRegex(SafetyStop, record["reason"].split(";")[0]):
                walker.decide(record["snapshot"], record["decision_time"])
            with patch("terrain_walk.Bridge") as connect, patch("builtins.print") as output:
                self.assertEqual(main(["--replay", str(path)]), 0)
                connect.assert_not_called()
                self.assertIn("Stopped: terrain:", output.call_args.args[0])
        self.assertEqual(bridge.commands, [(1, False), "stop"])

    def test_diagnostic_write_failure_does_not_prevent_stop(self):
        clock = FakeClock()
        bridge = FakeBridge(clock, frames=[scene(), scene(cells=[(104, y, ".") for y in range(100, 104)])])
        with patch("terrain_walk.Path.write_text", side_effect=OSError("disk full")):
            run(bridge, clock=clock, sleep=clock.sleep, log=lambda _: None, failure_log="failure.json")
        self.assertTrue(bridge.closed)
        self.assertEqual(bridge.commands, [(1, False), "stop"])

    def test_runtime_snapshot_is_reported_without_replaying_or_connecting(self):
        clock = FakeClock()
        bridge = FakeBridge(clock, frames=[scene(mode="manual")])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "failure.json"
            reason = run(bridge, clock=clock, sleep=clock.sleep, log=lambda _: None, failure_log=path)
            self.assertTrue(reason.startswith("runtime:"))
            with patch("terrain_walk.Bridge") as connect, patch("builtins.print") as output:
                self.assertEqual(main(["--replay", str(path)]), 0)
                connect.assert_not_called()
                self.assertIn("Runtime interruption", output.call_args.args[0])
        self.assertEqual(bridge.commands, [])

    def test_keyboard_interrupt_closes_and_stops_owned_input(self):
        clock = FakeClock()
        bridge = FakeBridge(clock)
        def interrupt(_):
            raise KeyboardInterrupt
        with self.assertRaises(KeyboardInterrupt):
            run(bridge, clock=clock, sleep=interrupt, log=lambda _: None)
        self.assertTrue(bridge.closed)
        self.assertEqual(bridge.commands, [(1, False), "stop"])

    def test_stop_failure_still_closes(self):
        clock = FakeClock()
        bridge = FakeBridge(clock)
        with patch.object(bridge, "command", side_effect=BridgeError("timeout")):
            run(bridge, seconds=0.1, clock=clock, sleep=clock.sleep, log=lambda _: None)
        self.assertTrue(bridge.closed)

    def test_invalid_cli_limits_never_connect(self):
        with patch("terrain_walk.Bridge") as connect, patch("sys.stderr"):
            for args in (["--seconds", "nan"], ["--seconds", "61"], ["--port", "0"]):
                with self.assertRaises(SystemExit):
                    main(args)
            connect.assert_not_called()


if __name__ == "__main__":
    unittest.main()
