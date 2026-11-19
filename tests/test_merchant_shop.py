import unittest

from test_server_ai_contract import BASE, bridge
import atomic_operations as atomic
from server_ai import operations_for_capabilities


class MerchantShopTests(unittest.TestCase):
    def test_capabilities_and_arguments(self):
        caps = {'server_ai_atomic_v1', 'server_ai_actions_v1', 'server_ai_merchant_shop_v1'}
        ops = operations_for_capabilities(caps)
        for read in ('inspect_npc_shop', 'quote_npc_purchase'):
            self.assertIn(read, ops)
            with self.assertRaises(atomic.PreDispatchRejected):
                atomic.validate_backend(bridge(BASE), read, {})
        self.assertNotIn('buy_npc_item', ops)
        self.assertIn('buy_npc_item', operations_for_capabilities(caps | {'server_ai_merchant_purchase_v1'}))
        for name, args in (
            ('quote_npc_purchase', dict(entityRef='npc', itemType=8, quantity=2)),
            ('quote_npc_purchase', dict(entityRef='npc', itemType=0)),
            ('buy_npc_item', dict(entityRef='npc', quoteToken='invalid')),
        ):
            with self.subTest(name=name, args=args), self.assertRaises(ValueError):
                atomic.validate(name, args)

    def test_completed_requires_matching_receipt_and_exact_deltas(self):
        target = dict(entityRef='merchant', quoteToken='a' * 64)
        receipt = dict(**target, itemType=8, quantity=1, unitPrice=44, beforeCopper=200,
                       afterCopper=156, beforeCount=0, afterCount=1, banksUnchanged=True)
        self.assertTrue(atomic.verified_purchase(target, receipt))
        for change in (dict(entityRef='other'), dict(quoteToken='b' * 64), dict(unitPrice=0),
                       dict(afterCopper=155), dict(afterCount=2), dict(banksUnchanged=False)):
            with self.subTest(change=change):
                self.assertFalse(atomic.verified_purchase(target, dict(receipt, **change)))
