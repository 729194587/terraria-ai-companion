import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from atomic_operations import verified_server_rope_motion
from server_ai import operations_for_capabilities


class RopeMotionReceiptTests(unittest.TestCase):
    def test_explicit_native_capability_is_required(self):
        atomic = {"server_ai_atomic_v1"}
        native = {"server_ai_actions_v1"}
        rope = {"server_ai_move_on_rope_v1"}
        self.assertNotIn("move_on_rope", operations_for_capabilities(atomic | native))
        self.assertNotIn("move_on_rope", operations_for_capabilities(atomic | rope))
        self.assertIn("move_on_rope", operations_for_capabilities(atomic | native | rope))

    def test_hold_requires_matching_target_and_settled_attachment(self):
        goal = dict(ropeX=123, targetFeetY=256, finish="hold", maxUpdates=120)
        receipt = dict(requestedInputs=goal)
        evidence = dict(**goal, updates=25, feetY=259, attachmentObserved=True,
                        attached=True, settled=True, inputReleased=True)
        self.assertTrue(verified_server_rope_motion(receipt, evidence))
        for key, value in (("feetY", 261), ("ropeX", 124), ("updates", 121), ("updates", 1),
                           ("attached", False), ("settled", False), ("attachmentObserved", False),
                           ("inputReleased", False)):
            with self.subTest(field=key, value=value):
                self.assertFalse(verified_server_rope_motion(receipt, {**evidence, key: value}))

    def test_landing_requires_detachment_and_safe_ground_support(self):
        goal = dict(ropeX=123, targetFeetY=256, finish="land", maxUpdates=120)
        receipt = dict(requestedInputs=goal)
        evidence = dict(**goal, updates=25, feetY=256, attachmentObserved=True,
                        attached=False, settled=True, inputReleased=True, grounded=True, safeSupport=True)
        self.assertTrue(verified_server_rope_motion(receipt, evidence))
        for key, value in (("feetY", 257), ("attached", True), ("grounded", False), ("safeSupport", False)):
            with self.subTest(field=key):
                self.assertFalse(verified_server_rope_motion(receipt, {**evidence, key: value}))
