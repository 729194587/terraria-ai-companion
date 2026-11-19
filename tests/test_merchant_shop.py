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

    def test_sale_requires_own_capability_and_exact_source_and_payment(self):
        caps = {'server_ai_atomic_v1', 'server_ai_actions_v1', 'server_ai_merchant_purchase_v1'}
        sales = {'quote_npc_sale', 'sell_npc_item'}
        self.assertFalse(sales & operations_for_capabilities(caps))
        self.assertTrue(sales <= operations_for_capabilities(caps | {'server_ai_merchant_sale_v1'}))
        target = dict(entityRef='merchant', sourceSlot=10, sourceToken='a' * 64, quoteToken='b' * 64)
        proof = dict(**target, quantity=1, itemType=8, unitPrice=11, beforeCopper=156, afterCopper=167,
                     beforeCount=1, afterCount=0, beforeStack=1, afterStack=0, banksUnchanged=True)
        self.assertTrue(atomic.verified_sale(target, proof))
        for change in (dict(sourceSlot=9), dict(sourceToken='c' * 64), dict(entityRef='other'),
                       dict(quoteToken='c' * 64), dict(afterStack=1), dict(afterCount=1),
                       dict(afterCopper=166), dict(banksUnchanged=False)):
            with self.subTest(change=change):
                self.assertFalse(atomic.verified_sale(target, dict(proof, **change)))
