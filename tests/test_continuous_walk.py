import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from control import BridgeError
from continuous_walk import main, run
from test_terrain_walk import FakeClock, scene


class ExecutorBridge:
    """Small stateful executor: renewals cannot overlap or reset a jump."""
    def __init__(self, plans=(), *, moving=True):
        self.hello = {"capabilities": ["guarded_control"]}
        self.plans = iter(plans)
        self.plan = []
        self.mode, self.generation = "none", 0
        self.state = scene()["state"]
        self.nav = self.navigation("idle")
        self.calls = []
        self.closed = False
        self.starts = 0
        self.moving = moving
        self.expire_on_renew = False
        self.interrupt_after_landing = False
        self.interrupted = False
        self.corrupt_landing = False
        self.delay = 0
        self.clock = None
        self.direction = 1

    def navigation(self, status, reason="fixture"):
        p = copy.copy(self.state["position"])
        return {"session": 1, "generation": self.generation, "status": status,
                "action": "jump" if status == "landed" else "walk", "reason": reason,
                "holdFrames": 8, "predictedLanding": p, "actualLanding": p,
                "observedPosition": p, "observedVelocity": {"x": 0, "y": 0}}

    def get_navigation(self):
        self.calls.append("observe")
        if self.clock:
            self.clock.sleep(self.delay)
        if self.mode == "external":
            status = self.plan.pop(0) if self.plan else "running"
            if isinstance(status, Exception):
                raise status
            if status in ("manual", "expired", "other_generation", "other_session"):
                self.generation += 1
                self.mode = "manual" if status == "manual" else "none"
                self.nav = self.navigation("idle")
                if status == "other_session":
                    self.nav["session"] = 2
            else:
                if self.moving:
                    self.state["position"]["x"] += 10 * self.direction
                self.nav = self.navigation(status)
                if status == "landed" and self.corrupt_landing:
                    self.nav["observedVelocity"]["x"] = 1
        elif self.interrupt_after_landing and self.starts and not self.interrupted:
            self.generation += 1
            self.nav = self.navigation("idle")
            self.interrupted = True
        return copy.deepcopy({"mode": self.mode, "state": self.state, "navigation": self.nav})

    def get_terrain(self):
        self.calls.append("terrain")
        result = scene(mode=self.mode)
        result["state"] = copy.deepcopy(self.state)
        return result

    def command(self, command, **parameters):
        self.calls.append((command, parameters))
        if command == "terrain_step" and self.mode == "external" and self.expire_on_renew:
            self.generation += 1
            self.mode = "none"
        if parameters["expectedGeneration"] != self.generation:
            raise BridgeError("Command rejected: generation_changed")
        if command == "stop":
            self.mode = "none"
            self.generation += 1
            self.nav = self.navigation("idle")
        elif self.mode == "none":
            self.direction = parameters["direction"]
            self.starts += 1
            self.generation += 1
            self.mode = "external"
            self.plan = list(next(self.plans, []))
            self.nav = self.navigation("idle")
        return {"status": "applied", "mode": self.mode}

    def close(self):
        self.calls.append("close")
        self.closed = True


class ContinuousTests(unittest.TestCase):
    def execute(self, bridge, seconds=1, max_actions=8, no_progress=3, sleep=None):
        clock = bridge.clock or FakeClock()
        result = run(bridge, 1, seconds, max_actions, no_progress, clock=clock,
                     sleep=sleep or clock.sleep, log=lambda _: None)
        self.assertTrue(bridge.closed)
        return result

    def commands(self, bridge, name):
        return [item for item in bridge.calls if isinstance(item, tuple) and item[0] == name]

    def test_flat_walk_renews_one_action_until_deadline(self):
        b = ExecutorBridge()
        r = self.execute(b)
        self.assertEqual(r["reason"], "runtime limit reached")
        self.assertEqual(b.starts, 1)
        self.assertGreater(len(self.commands(b, "terrain_step")), 5)
        self.assertEqual(len(self.commands(b, "stop")), 1)
        self.assertEqual(b.calls[-1], "close")

    def test_two_obstacles_reobserve_after_each_verified_landing(self):
        b = ExecutorBridge([["running", "landed"], ["running", "landed"]])
        r = self.execute(b, seconds=3, max_actions=2)
        self.assertEqual((r["status"], r["completedJumps"], b.starts), ("completed", 2, 2))
        self.assertEqual(r["reason"], "action limit reached")
        self.assertEqual(b.calls.count("terrain"), 2)
        stops = [i for i, c in enumerate(b.calls) if isinstance(c, tuple) and c[0] == "stop"]
        terrains = [i for i, c in enumerate(b.calls) if c == "terrain"]
        self.assertLess(stops[0], terrains[1])
        self.assertEqual(b.calls[stops[0] + 1], "observe")
        self.assertEqual([c[1]["expectedGeneration"] for c in self.commands(b, "stop")], [1, 3])

    def test_landing_continues_into_flat_movement(self):
        b = ExecutorBridge([["landed"]])
        r = self.execute(b)
        self.assertEqual((b.starts, r["completedJumps"]), (2, 1))
        self.assertEqual(r["reason"], "runtime limit reached")

    def test_leftward_progress_uses_requested_direction(self):
        b, clock = ExecutorBridge(), FakeClock()
        r = run(b, -1, 2, no_progress=1, clock=clock, sleep=clock.sleep, log=lambda _: None)
        self.assertEqual(r["status"], "completed")
        self.assertLess(b.state["position"]["x"], 1600 - 100)

    def test_backward_oscillation_is_not_forward_progress(self):
        b = ExecutorBridge(moving=False)
        original = b.get_navigation
        def feedback():
            b.state["position"]["x"] = 1590 if b.state["position"]["x"] == 1600 else 1600
            return original()
        b.get_navigation = feedback
        self.assertIn("no forward progress", self.execute(b, seconds=3, no_progress=1)["reason"])

    def test_deep_pit_unknown_and_jump_failure_are_terminal(self):
        for reason in ("no safe landing; deep pit", "unknown tile", "jump landing mismatch"):
            b = ExecutorBridge([["failed"]])
            original = b.get_navigation
            def feedback():
                reply = original()
                reply["navigation"]["reason"] = reason
                return reply
            b.get_navigation = feedback
            r = self.execute(b)
            self.assertEqual(r["status"], "failed")
            self.assertIn(reason, r["reason"])
            self.assertEqual(b.starts, 1)

    def test_incorrect_landing_feedback_cannot_start_next_action(self):
        b = ExecutorBridge([["landed"]])
        b.corrupt_landing = True
        self.assertIn("verification failed", self.execute(b)["reason"])
        self.assertEqual(b.starts, 1)

    def test_no_progress_stops_and_is_not_reset_by_action_boundaries(self):
        for plans in ([], [["landed"]] * 50):
            b = ExecutorBridge(plans, moving=False)
            r = self.execute(b, seconds=5, max_actions=100, no_progress=1)
            self.assertIn("no forward progress", r["reason"])
            self.assertLess(b.starts, 10)

    def test_manual_lease_and_identity_interruptions_never_restart_or_stop_new_owner(self):
        for status in ("manual", "expired", "other_generation", "other_session"):
            b = ExecutorBridge([[status]])
            r = self.execute(b)
            self.assertIn("no automatic resume", r["reason"])
            self.assertEqual(b.starts, 1)
            self.assertEqual(self.commands(b, "stop"), [])

    def test_atomic_expiry_during_renewal_cannot_restart(self):
        b = ExecutorBridge()
        b.expire_on_renew = True
        self.assertIn("generation_changed", self.execute(b)["reason"])
        self.assertEqual(b.starts, 1)
        self.assertEqual(self.commands(b, "stop"), [])

    def test_interruption_between_actions_does_not_start_next(self):
        b = ExecutorBridge([["landed"]])
        b.interrupt_after_landing = True
        self.assertIn("no automatic resume", self.execute(b)["reason"])
        self.assertEqual(b.starts, 1)

    def test_disconnect_and_bad_feedback_close_without_retry(self):
        for error in (BridgeError("disconnected"), OSError("socket lost")):
            b = ExecutorBridge([[error]])
            self.assertIn("Bridge failure", self.execute(b)["reason"])
            self.assertEqual(b.starts, 1)
            self.assertEqual(self.commands(b, "stop"), [])
        b = ExecutorBridge()
        b.get_navigation = lambda: {"navigation": {}, "state": {}}
        self.assertIn("Bridge failure", self.execute(b)["reason"])
        self.assertEqual(b.starts, 0)
        b = ExecutorBridge()
        b.get_navigation = lambda: {"navigation": {}, "state": None}
        self.assertIn("Bridge failure", self.execute(b)["reason"])

    def test_keyboard_interrupt_releases_owned_generation(self):
        b, clock = ExecutorBridge(), FakeClock()
        def interrupt(_):
            raise KeyboardInterrupt
        with self.assertRaises(KeyboardInterrupt):
            run(b, 1, 1, clock=clock, sleep=interrupt, log=lambda _: None)
        self.assertTrue(b.closed)
        self.assertEqual(len(self.commands(b, "stop")), 1)

    def test_stalled_loop_and_slow_observation_stop_without_new_actions(self):
        b, clock = ExecutorBridge(), FakeClock()
        r = run(b, 1, 3, clock=clock, sleep=lambda _: clock.sleep(0.8), log=lambda _: None)
        self.assertIn("no automatic resume", r["reason"])
        self.assertEqual(b.starts, 1)
        b = ExecutorBridge()
        b.clock, b.delay = FakeClock(), 0.3
        self.assertIn("250ms", self.execute(b)["reason"])
        self.assertEqual(b.starts, 0)

    def test_old_package_and_busy_controller_do_not_take_control(self):
        b = ExecutorBridge()
        b.hello = {}
        self.assertIn("0.1.10", self.execute(b)["reason"])
        self.assertEqual(b.starts, 0)
        b = ExecutorBridge()
        b.mode = "external"
        self.assertIn("interrupted", self.execute(b)["reason"])
        self.assertEqual(b.starts, 0)

    def test_unsettled_or_damaged_reobservation_stops(self):
        for field in ("grounded", "supported", "velocity", "life"):
            b = ExecutorBridge()
            original = b.get_terrain
            def terrain():
                reply = original()
                if field in ("grounded", "supported"):
                    reply["terrain"][field] = False
                elif field == "velocity":
                    reply["state"][field]["x"] = 1
                else:
                    reply["state"][field] = 99
                return reply
            b.get_terrain = terrain
            self.assertEqual(self.execute(b)["status"], "stopped")
            self.assertEqual(b.starts, 0)

    def test_cli_bounds_and_bounded_report(self):
        b = ExecutorBridge([["landed"], ["landed"]])
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "report.json"
            with patch("continuous_walk.Bridge", return_value=b), patch("builtins.print"):
                self.assertEqual(main(["--max-actions", "2", "--output", str(output)]), 0)
            self.assertEqual(json.loads(output.read_text(encoding="utf-8"))["completedJumps"], 2)
        with patch("continuous_walk.Bridge") as connect, patch("sys.stderr"):
            for args in (["--seconds", "nan"], ["--seconds", "121"], ["--max-actions", "0"],
                         ["--no-progress", "inf"], ["--no-progress", "0"], ["--port", "0"]):
                with self.assertRaises(SystemExit):
                    main(args)
            connect.assert_not_called()
        r = self.execute(ExecutorBridge(), seconds=4)
        self.assertEqual(len(r["samples"]), 20)


if __name__ == "__main__":
    unittest.main()
