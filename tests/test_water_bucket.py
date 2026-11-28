import unittest

import atomic_operations as atomic
from server_ai import operations_for_capabilities


class WaterBucketTests(unittest.TestCase):
    def test_native_action_requires_bucket_capability(self):
        caps = {'server_ai_atomic_v1', 'server_ai_actions_v1'}
        self.assertNotIn('use_water_bucket', operations_for_capabilities(caps))
        self.assertIn('use_water_bucket', operations_for_capabilities(caps | {'server_ai_water_bucket_v1'}))
        with self.assertRaises(ValueError):
            atomic.validate('use_water_bucket', dict(x=10, y=20, mode='fill'))

    def test_receipt_requires_target_liquid_and_both_bucket_deltas(self):
        for mode, water, empty, filled in [('scoop', (510, 255), (2, 1), (0, 1)),
                                           ('pour', (0, 255), (1, 2), (2, 1))]:
            with self.subTest(mode=mode):
                target = dict(x=10, y=20, mode=mode)
                proof = dict(**target, beforeWater=water[0], afterWater=water[1], beforeEmpty=empty[0],
                             afterEmpty=empty[1], beforeFilled=filled[0], afterFilled=filled[1], verified=True)
                self.assertTrue(atomic.verified_bucket(target, proof))
                for change in (dict(x=11), dict(afterWater=water[0]), dict(afterEmpty=empty[0]),
                               dict(afterFilled=filled[0]), dict(verified=False)):
                    self.assertFalse(atomic.verified_bucket(target, dict(proof, **change)))
