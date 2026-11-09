import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from atomic_operations import verified_server_equipment
from server_ai import operations_for_capabilities


class EquipmentReceiptTests(unittest.TestCase):
    def test_capability_requires_native_actions(self):
        caps = {"server_ai_atomic_v1", "server_ai_equipment_v1"}
        self.assertNotIn("equip_item", operations_for_capabilities(caps))
        self.assertNotIn("unequip_item", operations_for_capabilities(caps - {"server_ai_equipment_v1"} | {"server_ai_actions_v1"}))
        self.assertTrue({"equip_item", "unequip_item"} <= operations_for_capabilities(caps | {"server_ai_actions_v1"}))

    @staticmethod
    def sample(op):
        equipping = op == "equip_item"
        bag = dict(instanceId="bag", token="a" * 64, typeId=2 if equipping else 0,
                   stack=1 if equipping else 0, prefix=0, favorited=equipping)
        gear = dict(instanceId="gear", token="b" * 64, typeId=3, stack=1, prefix=1, favorited=False)
        goal = dict(targetSlotId="vanilla:head", targetToken=gear["token"])
        if equipping:
            goal.update(sourceSlot=10, sourceToken=bag["token"])
        return (dict(operation=op, targetInventory=goal),
                dict(backpackSlot=10, targetSlotId=goal["targetSlotId"], equipmentVerified=True,
                     mutationAttempted=True, mutationGameUpdate=20, confirmedAtGameUpdate=21,
                     before=dict(backpack=bag, equipment=gear),
                     after=dict(backpack={**gear, "token": "c" * 64}, equipment={**bag, "token": "d" * 64})))

    def test_equip_requires_exact_two_instances_and_later_confirmation(self):
        action, evidence = self.sample("equip_item")
        self.assertTrue(verified_server_equipment(action, evidence))
        for key, value in (("confirmedAtGameUpdate", 20), ("targetSlotId", "vanilla:body"), ("backpackSlot", 11)):
            with self.subTest(key=key):
                self.assertFalse(verified_server_equipment(action, {**evidence, key: value}))
        for key, value in (("instanceId", "clone"), ("stack", 2), ("prefix", 2), ("favorited", True)):
            with self.subTest(key=key):
                after = {**evidence["after"], "backpack": {**evidence["after"]["backpack"], key: value}}
                self.assertFalse(verified_server_equipment(action, {**evidence, "after": after}))

    def test_unequip_verifies_explicit_destination_and_empty_source_slot(self):
        action, evidence = self.sample("unequip_item")
        self.assertTrue(verified_server_equipment(action, evidence))
        action["targetInventory"]["destinationSlot"] = 11
        self.assertFalse(verified_server_equipment(action, evidence))
        action["targetInventory"]["destinationSlot"] = 10
        self.assertTrue(verified_server_equipment(action, evidence))
        action["targetInventory"]["targetToken"] = "f" * 64
        self.assertFalse(verified_server_equipment(action, evidence))
