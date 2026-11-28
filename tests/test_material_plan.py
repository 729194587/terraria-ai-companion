"""The public Lua material planner delegates arbitrary inventory goals to the native registry."""
import unittest

from lupa.lua54 import LuaRuntime
from lua_modules import sources


class MaterialPlanTests(unittest.TestCase):
    def test_arbitrary_outputs_use_one_native_read(self):
        module, = sources(['material_plan'])
        self.assertEqual(('material_plan', ['plan']), (module['namespace'], module['exports']))
        for item_id, desired_total in ((8, 12), (4956, 2)):
            with self.subTest(item_id=item_id):
                lua = LuaRuntime()
                calls = []
                def read(name, args):
                    calls.append((name, dict(args)))
                    return lua.table_from(dict(valid=True, available=True))
                lua.globals().api = read
                result = lua.execute(module['source']).plan(item_id, desired_total)
                self.assertTrue(result.available)
                self.assertEqual([('plan_materials', {'typeId': item_id, 'count': desired_total})], calls)
