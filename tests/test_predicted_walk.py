import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from control import BridgeError
from predicted_walk import run, main
from test_terrain_walk import FakeClock


def feedback(status="running", mode="external"):
    return {"mode": mode, "navigation": {"status": status, "action": "jump",
            "holdFrames": 8, "reason": "fixture", "maxError": 0.25,
            "predictedLanding": {"x": 100, "y": 200}, "actualLanding": {"x": 101, "y": 200}}}


class BridgeFixture:
    def __init__(self, frames=(), *, fail_input=False, clock=None, delay=0):
        self.frames = iter([feedback("idle")] + list(frames))
        self.commands, self.closed = [], False
        self.fail_input, self.clock, self.delay = fail_input, clock, delay

    def get_navigation(self):
        if self.clock:
            self.clock.sleep(self.delay)
        frame = next(self.frames, feedback())
        if isinstance(frame, Exception):
            raise frame
        return frame

    def terrain_step(self, direction):
        self.commands.append(("terrain_step", direction))
        if self.fail_input:
            raise BridgeError("manual_active")

    def command(self, command):
        self.commands.append(command)

    def close(self):
        self.closed = True


class PredictedWalkTests(unittest.TestCase):
    def execute(self, bridge, seconds=0.4):
        clock = FakeClock()
        report = run(bridge, 1, seconds, clock=clock, sleep=clock.sleep, log=lambda _: None)
        self.assertTrue(bridge.closed)
        return report

    def test_one_jump_reports_landing_then_stops(self):
        bridge = BridgeFixture([feedback(), feedback("landed")])
        report = self.execute(bridge)
        self.assertEqual(report["status"], "landed")
        self.assertEqual(len(report["samples"]), 2)
        self.assertEqual(bridge.commands, [("terrain_step", 1), ("terrain_step", 1), "stop"])

    def test_failure_is_terminal_without_retry(self):
        bridge = BridgeFixture([feedback("failed")])
        self.assertEqual(self.execute(bridge)["status"], "failed")
        self.assertEqual(bridge.commands, [("terrain_step", 1), "stop"])

    def test_flat_walk_runtime_is_bounded_and_renewed(self):
        bridge = BridgeFixture()
        self.assertIn("runtime", self.execute(bridge)["reason"])
        self.assertEqual(bridge.commands[-1], "stop")
        self.assertEqual(len(bridge.commands), 5)

    def test_manual_feedback_does_not_send_stop(self):
        bridge = BridgeFixture([feedback(mode="manual")])
        self.assertIn("manual", self.execute(bridge)["reason"])
        self.assertEqual(bridge.commands, [("terrain_step", 1)])

    def test_rejected_input_or_disconnect_does_not_send_stop(self):
        for bridge in (BridgeFixture(fail_input=True), BridgeFixture([BridgeError("disconnected")])):
            self.assertIn("Bridge failure", self.execute(bridge)["reason"])
            self.assertEqual(bridge.commands, [("terrain_step", 1)])

    def test_old_package_is_checked_before_control(self):
        bridge = BridgeFixture()
        with patch.object(bridge, "get_navigation", side_effect=BridgeError("unsupported")):
            self.assertIn("Bridge failure", self.execute(bridge)["reason"])
        self.assertEqual(bridge.commands, [])

    def test_malformed_feedback_closes_without_reconnecting(self):
        bridge = BridgeFixture([{"mode": "external", "navigation": {}}])
        self.assertIn("Bridge failure", self.execute(bridge)["reason"])
        self.assertEqual(bridge.commands, [("terrain_step", 1)])

    def test_late_round_trip_stops(self):
        clock = FakeClock()
        bridge = BridgeFixture(clock=clock, delay=0.3)
        result = run(bridge, 1, 1, clock=clock, sleep=clock.sleep, log=lambda _: None)
        self.assertIn("250ms", result["reason"])
        self.assertEqual(bridge.commands, [("terrain_step", 1), "stop"])

    def test_keyboard_interrupt_releases_control(self):
        clock = FakeClock()
        bridge = BridgeFixture()
        def interrupt(_):
            raise KeyboardInterrupt
        with self.assertRaises(KeyboardInterrupt):
            run(bridge, 1, 1, clock=clock, sleep=interrupt, log=lambda _: None)
        self.assertEqual(bridge.commands, [("terrain_step", 1), "stop"])
        self.assertTrue(bridge.closed)

    def test_stalled_loop_never_resumes_old_action(self):
        clock = FakeClock()
        bridge = BridgeFixture()
        def stalled_sleep(_):
            clock.sleep(0.8)
        report = run(bridge, 1, 2, clock=clock, sleep=stalled_sleep, log=lambda _: None)
        self.assertIn("no automatic resume", report["reason"])
        self.assertEqual(bridge.commands, [("terrain_step", 1), "stop"])

    def test_cli_saves_report_after_closing_and_checks_bounds(self):
        bridge = BridgeFixture([feedback("landed")])
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "report.json"
            with patch("predicted_walk.Bridge", return_value=bridge), patch("builtins.print"):
                self.assertEqual(main(["--output", str(output)]), 0)
            self.assertTrue(bridge.closed)
            self.assertEqual(json.loads(output.read_text(encoding="utf-8"))["status"], "landed")
        with patch("predicted_walk.Bridge") as connect, patch("sys.stderr"):
            for seconds in ("nan", "0", "31"):
                with self.assertRaises(SystemExit):
                    main(["--seconds", seconds])
            connect.assert_not_called()


if __name__ == "__main__":
    unittest.main()
