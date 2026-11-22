"""Source contract for native magic mana payment and observation timing."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
COMBAT = (ROOT / 'CompanionBridge/ServerIpc/NativeCombatWorld.cs').read_text(encoding='utf-8')
BALLISTICS = (ROOT / 'CompanionBridge/ServerIpc/CombatBallistics.cs').read_text(encoding='utf-8')


class MagicManaTimingTests(unittest.TestCase):
    def test_shoot_never_rechecks_mana_after_native_payment(self):
        start = COMBAT.index('internal static void Shoot(')
        end = COMBAT.index('internal static Shot CaptureShot', start)
        shoot = COMBAT[start:end]
        # ItemCheck_PayMana already paid (or funded QuickMana) before this step.
        self.assertNotIn('GetManaCost', shoot)
        self.assertNotIn('statMana<', shoot)

    def test_mana_change_is_observed_around_the_whole_native_item_check(self):
        start = COMBAT.index('private void CheckItem(')
        end = COMBAT.index('public static bool AllowsItem', start)
        check = COMBAT[start:end]
        before = check.index('int manaBefore=player.statMana;')
        native = check.index('runNative(new(aim, press));')
        after = check.index('bool manaChanged=player.statMana!=manaBefore;')
        self.assertLess(before, native)
        self.assertLess(native, after)
        tail = check[after:]
        self.assertIn('MessageID.PlayerMana', tail)
        self.assertIn('if (ammoChanged) NetworkStateSync.Full(player)', tail)
        # Payment stays exclusively in native code.
        self.assertNotIn('player.CheckMana', COMBAT)
        self.assertNotIn('statMana-=', COMBAT)

    def test_preflight_keeps_native_quickmana_reachable(self):
        self.assertIn('ManaAffordable(p,weapon)', BALLISTICS)
        start = BALLISTICS.index('internal static bool ManaAffordable(')
        body = BALLISTICS[start:start + 700]
        self.assertIn('manaFlower', body)
        self.assertIn('QuickMana_GetItemToUse', body)


if __name__ == '__main__':
    unittest.main()
