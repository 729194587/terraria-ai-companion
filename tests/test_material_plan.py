"""Run the bundled Lua calculation against recorded-shape, read-only API fixtures."""
from pathlib import Path
import sys
import unittest

from lupa.lua54 import LuaRuntime

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from lua_modules import sources


PICK, BAR, ORE, OBS = ['Terraria/' + name for name in ('MoltenPickaxe', 'HellstoneBar', 'Hellstone', 'Obsidian')]


class MaterialPlanTests(unittest.TestCase):
    def plan(self, bars=8, obsidian=3, yield_count=1, incomplete=False, missing=False):
        lua = LuaRuntime()
        bag = {PICK: 1, BAR: bars, ORE: 12, OBS: obsidian}
        def api(name, args):
            if name == 'resolve_content':
                key = args.query
                data = dict(status='ok', candidates=[dict(typeId={PICK: 1, BAR: 2}[key], contentKey=key)])
            elif name == 'query_recipes':
                self.assertFalse(args.availableOnly)
                pick = args.resultType == 1
                amounts = {BAR: 20} if pick else {ORE: 3, OBS: 1}
                row = dict(output=dict(typeId=args.resultType, contentKey=PICK if pick else BAR,
                                       count=1 if pick else yield_count), supported=True, available=False,
                           ingredients=[dict(item=dict(contentKey=k, count=v), acceptedGroups=[])
                                        for k, v in amounts.items()], stations=[dict(adjacent=False)],
                           reason='missing_crafting_station', conditions=[], hasOutputSpace=True)
                data = dict(items=[] if args.offset else [row], offset=args.offset, truncated=incomplete)
            elif name == 'inspect_inventory':
                key = args.item
                data = dict(status='unknown_item' if missing else 'ok', contentKey=key,
                            slots=[dict(slot=0, stack=bag[key]), dict(slot=58, stack=10)])
            else:
                self.fail(name)
            return lua.table_from(dict(valid=True, available=True, data=data), recursive=True)
        lua.globals().api = api
        return lua.execute(sources(['material_plan'])[0]['source']).molten_pickaxe()

    def test_verified_inventory_scenarios(self):
        for evidence, bars, obs, expected in [
                ('stale chest', 8, 3, (12, 24, 9)), ('five withdrawn', 8, 8, (12, 24, 4)),
                ('two withdrawn', 8, 5, (12, 24, 7)), ('locked chest', 8, 3, (12, 24, 9)),
                ('enough bars', 20, 0, (0, 0, 0))]:
            with self.subTest(evidence=evidence):
                result = self.plan(bars, obs)
                self.assertEqual('planned', result.status)
                self.assertEqual(expected, (result.barDeficit, result.rawDeficits[ORE], result.rawDeficits[OBS]))
                self.assertEqual({ORE, OBS}, set(result.rawDeficits.keys()))
                self.assertEqual(1, result.inventory[PICK])
                self.assertFalse(result.recipes[PICK].stations[1].adjacent)

    def test_native_yield_and_incomplete_evidence(self):
        result = self.plan(bars=7, yield_count=2)
        self.assertEqual((9, 4), (result.rawDeficits[ORE], result.rawDeficits[OBS]))
        self.assertEqual('incomplete_pages', self.plan(incomplete=True).reason)
        self.assertEqual('inventory_unavailable', self.plan(missing=True).reason)
