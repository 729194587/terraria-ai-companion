import copy
import unittest

from test_server_ai_contract import BASE, bridge
import atomic_operations as atomic
from server_ai import operations_for_capabilities


class NpcHomeTests(unittest.TestCase):
    def test_independent_capabilities_and_arguments(self):
        read = 'inspect_npc_housing'
        assign = 'assign_npc_home'
        caps = {'server_ai_atomic_v1', 'server_ai_actions_v1', 'server_ai_npc_housing_v1'}
        self.assertIn(read, operations_for_capabilities(caps))
        self.assertNotIn(assign, operations_for_capabilities(caps))
        self.assertIn(assign, operations_for_capabilities(caps | {'server_ai_housing_assignment_v1'}))
        for name in (read, assign):
            with self.assertRaises(atomic.PreDispatchRejected):
                atomic.validate_backend(bridge(BASE), name, {})
        with self.assertRaises(ValueError):
            atomic.validate(assign, {'slot': 1, 'x': 10, 'y': 10})

    def test_two_independent_home_records_required(self):
        target = dict(entityRef='g', x=10, y=10)
        after = dict(entityRef='g', type=22, slot=1, active=True, townNPC=True, homeless=False,
                     homeTileX=11, homeTileY=13, managerHasRoom=True, managerHomeTile=dict(x=11,y=13))
        proof = dict(entityRef='g', queryTile=dict(x=10,y=10), homeTile=dict(x=11,y=13),
                     before=dict(after, homeless=True), after=after)
        self.assertTrue(atomic.verified_npc_home(target, proof))
        for mismatch in (dict(entityRef='replacement'), dict(slot=2), dict(homeless=True),
                         dict(homeTileX=10), dict(managerHasRoom=False), dict(managerHomeTile=None)):
            changed = copy.deepcopy(proof)
            changed['after'].update(mismatch)
            self.assertFalse(atomic.verified_npc_home(target, changed))
