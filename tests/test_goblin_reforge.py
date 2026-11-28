import unittest

from test_server_ai_contract import BASE, bridge
import atomic_operations as atomic
from server_ai import operations_for_capabilities


def funds(balance):
    return dict(carriedCopper=0, piggyBank=balance, safe=0, defendersForge=0, voidVault=0, totalCopper=balance)


class GoblinReforgeTests(unittest.TestCase):
    def test_capability_and_source_guard(self):
        caps = {'server_ai_atomic_v1', 'server_ai_actions_v1', 'server_ai_nurse_v1'}
        names = {'quote_reforge', 'reforge_item'}
        self.assertFalse(names & operations_for_capabilities(caps))
        self.assertTrue(names <= operations_for_capabilities(caps | {'server_ai_reforge_v1'}))
        for name in names:
            with self.assertRaises(atomic.PreDispatchRejected):
                atomic.validate_backend(bridge(BASE), name, {})
        with self.assertRaises(ValueError):
            atomic.validate('quote_reforge', dict(entityRef='g', sourceSlot=50, sourceToken='a' * 64))

    def test_same_prefix_is_a_valid_paid_roll(self):
        target = dict(entityRef='g', sourceSlot=2, sourceToken='a' * 64, quoteToken='b' * 64)
        proof = dict(**target, itemType=24, beforePrefix=3, afterPrefix=3, rolled=True, itemVerified=True,
                     beforeFunds=funds(200), afterFunds=funds(163), priceCopper=37, bankNonCurrencyUnchanged=True)
        self.assertTrue(atomic.verified_reforge(target, proof))
        for change in (dict(afterPrefix=0), dict(rolled=False), dict(afterFunds=funds(162)),
                       dict(bankNonCurrencyUnchanged=False),
                       dict(itemVerified=False), dict(sourceSlot=1), dict(quoteToken='c' * 64)):
            with self.subTest(change=change):
                self.assertFalse(atomic.verified_reforge(target, dict(proof, **change)))

    def test_target_rolls_and_preexisting_prefix(self):
        target = dict(entityRef='g', sourceSlot=2, sourceToken='a'*64, quoteToken='b'*64, targetPrefix=81)
        proof = dict(**target, itemType=24, beforePrefix=3, afterPrefix=81, rolls=4, rolled=True,
                     priceCopper=137, beforeFunds=funds(200), afterFunds=funds(63),
                     paymentsVerified=True, itemVerified=True, bankNonCurrencyUnchanged=True)
        self.assertTrue(atomic.verified_reforge(target, proof))
        self.assertFalse(atomic.verified_reforge(target, dict(proof, afterPrefix=3)))
        self.assertFalse(atomic.verified_reforge(target, dict(proof, paymentsVerified=False)))
        idle = dict(proof, rolls=0, rolled=False, beforePrefix=81, priceCopper=0, afterFunds=funds(200))
        self.assertTrue(atomic.verified_reforge(target, idle))
        self.assertFalse(atomic.verified_reforge(target, dict(idle, priceCopper=1)))
        self.assertFalse(atomic.verified_reforge(target, dict(idle, beforePrefix=3)))
        with self.assertRaises(atomic.PreDispatchRejected):
            atomic.validate_backend(bridge(BASE | {'server_ai_reforge_v1'}), 'reforge_item', target)

    def test_cannot_claim_target_after_running_out_of_money(self):
        target = dict(entityRef='g', sourceSlot=2, sourceToken='a'*64, quoteToken='b'*64, targetPrefix=81)
        proof = dict(**target, itemType=24, beforePrefix=3, afterPrefix=8, rolls=2, rolled=True,
                     priceCopper=200, beforeFunds=funds(200), afterFunds=funds(0),
                     paymentsVerified=True, itemVerified=True, bankNonCurrencyUnchanged=True)
        self.assertFalse(atomic.verified_reforge(target, proof))
