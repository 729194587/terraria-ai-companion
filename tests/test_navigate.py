import copy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
import navigate
from control import BridgeError


class Clock:
    now = 0
    def __call__(self): return self.now
    def sleep(self, duration): self.now += duration


class FakeBridge:
    def __init__(self, statuses=("running", "arrived")):
        self.hello = {"capabilities": ["local_navigation", "guarded_control"]}
        self.statuses = iter(statuses)
        self.commands = []
        self.started = self.closed = False
        self.target = None
        self.override = False

    def get_navigation(self):
        status = next(self.statuses, "running") if self.started else "idle"
        return {"mode": "none" if self.override or not self.started else "external",
                "navigation": {"session": 1, "generation": int(self.started) + int(self.override),
                               "status": status, "targetX": self.target, "reason": "test",
                               "completedActions": 2 if status == "arrived" else 0},
                "state": {"inWorld": True, "position": {"x": 1600, "y": 1558}, "life": 100}}

    def command(self, cmd, **kwargs):
        self.commands.append((cmd, copy.deepcopy(kwargs)))
        self.started = True
        self.target = kwargs.get("targetX", self.target)
        return {"status": "applied", "mode": "none" if cmd == "stop" else "external"}

    def close(self): self.closed = True


class NavigationTests(unittest.TestCase):
    def run_task(self, bridge, **kwargs):
        clock = Clock()
        return navigate.run(bridge, 1, 40, kwargs.pop("seconds", 2), clock=clock,
                            sleep=clock.sleep, log=lambda _: None, **kwargs)

    def test_arrival_renews_same_target_without_action_restart(self):
        bridge = FakeBridge()
        result = self.run_task(bridge)
        self.assertEqual(result["status"], "arrived")
        self.assertEqual([c[0] for c in bridge.commands], ["navigate", "navigate", "stop"])
        self.assertEqual([c[1].get("expectedGeneration") for c in bridge.commands], [0, 1, 1])
        self.assertEqual(result["targetX"], 2240)
        self.assertTrue(bridge.closed)

    def test_failed_task_is_not_restarted(self):
        bridge = FakeBridge(("failed",))
        self.assertEqual(self.run_task(bridge)["status"], "failed")
        self.assertEqual([c[0] for c in bridge.commands], ["navigate", "stop"])

    def test_fractional_goal_survives_single_precision_serialization(self):
        bridge = FakeBridge()
        original = bridge.get_navigation
        def observation():
            reply = original()
            if bridge.started:
                reply["navigation"]["targetX"] = round(bridge.target, 3)
            else:
                reply["state"]["position"]["x"] = 1600.1234
            return reply
        bridge.get_navigation = observation
        self.assertEqual(self.run_task(bridge)["status"], "arrived")

    def test_unsupported_version_never_starts(self):
        bridge = FakeBridge(); bridge.hello = {"capabilities": []}
        self.assertEqual(self.run_task(bridge)["status"], "interrupted")
        self.assertEqual(bridge.commands, [])

    def test_runtime_limit_releases_task(self):
        bridge = FakeBridge(("running",))
        self.assertEqual(self.run_task(bridge, seconds=0.25)["status"], "stopped")
        self.assertEqual(bridge.commands[-1][0], "stop")
        self.assertTrue(bridge.closed)

    def test_interruption_does_not_renew_or_stop_new_owner(self):
        bridge = FakeBridge()
        original = bridge.command
        def command(cmd, **kwargs):
            result = original(cmd, **kwargs)
            bridge.override = True
            return result
        bridge.command = command
        self.assertEqual(self.run_task(bridge)["status"], "interrupted")
        self.assertEqual(len(bridge.commands), 1)

    def test_observation_failure_closes_connection(self):
        bridge = FakeBridge()
        original = bridge.get_navigation
        def observation():
            if bridge.started: raise BridgeError("EOF")
            return original()
        bridge.get_navigation = observation
        self.assertEqual(self.run_task(bridge)["status"], "interrupted")
        self.assertTrue(bridge.closed)


if __name__ == "__main__": unittest.main()
