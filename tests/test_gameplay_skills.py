import tempfile
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from gameplay_skills import MAX_SKILL_BYTES, SkillRegistry, bundled_skills


class SkillRegistryTests(unittest.TestCase):
    def test_tool_upgrade_knowledge_is_versioned_and_loaded_on_demand(self):
        registry = bundled_skills()
        skill = registry.load({'name': 'tool_upgrades'})
        knowledge = skill['knowledge']
        self.assertEqual(skill['game_version'], knowledge['game_version'])
        aliases = {name: key for key, row in knowledge['records'].items() for name in row['names']}
        self.assertEqual('Terraria/MoltenPickaxe', aliases['熔岩镐'])
        self.assertEqual(aliases['熔岩镐'], aliases['Molten Pickaxe'])
        self.assertNotIn('minPickaxePower', registry.render_index())
        self.assertIn('tool_upgrades', registry.render_index())
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'tool_upgrades' / 'SKILL.md'
            path.parent.mkdir()
            path.write_bytes((Path(__file__).resolve().parents[1] / 'skills/tool_upgrades/SKILL.md').read_bytes())
            path.with_name('knowledge.json').write_text('{"game_version":"other"}')
            with self.assertRaisesRegex(ValueError, 'version'):
                SkillRegistry(Path(directory)).load({'name': 'tool_upgrades'})

    def test_bundled_index_and_read_only_load(self):
        registry = bundled_skills()
        index = registry.render_index()
        skill = registry.load({'name': 'mining_and_ores'})
        self.assertTrue(skill['valid'])
        self.assertEqual('mining_and_ores', skill['name'])
        self.assertIn(f"- mining_and_ores — {skill['description']}", index.splitlines())
        self.assertTrue(skill['content'])
        self.assertNotIn(skill['content'], index)
        skill['content'] = 'mutated'
        self.assertNotEqual('mutated', registry.load({'name': 'mining_and_ores'})['content'])

    def test_unknown_and_malformed_names_cannot_select_a_path(self):
        registry = bundled_skills()
        self.assertEqual({'valid': False, 'error': 'unknown_skill'}, registry.load({'name': 'missing'}))
        for arguments in ({}, [], {'name': 1}, {'name': 'mining_and_ores', 'path': 'anything'},
                          {'name': '../agent.py'}, {'name': 'c:\\agent.py'}, {'name': 'mining_and_ores\n'}):
            with self.subTest(arguments=arguments), self.assertRaisesRegex(ValueError, 'read_skill requires'):
                registry.load(arguments)

    def test_registration_checks_metadata_and_size(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / 'example' / 'SKILL.md'
            path.parent.mkdir()
            header = '---\nname: example\ndescription: Short hint\ngame_version: Terraria 1.4.4.9\nscope: Vanilla\n---\n'
            path.write_text(header + '# Full guide\n', encoding='utf-8')
            registry = SkillRegistry(root)
            self.assertEqual('- example — Short hint', registry.render_index())
            path.write_text(header + '# Updated guide\n', encoding='utf-8')
            self.assertEqual('# Updated guide', registry.load({'name': 'example'})['content'])
            path.write_text(header.replace('name: example', 'name: other') + '# Full guide', encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'metadata'):
                SkillRegistry(root)
            path.write_bytes(b'a' * (MAX_SKILL_BYTES + 1))
            with self.assertRaisesRegex(ValueError, 'size limit'):
                SkillRegistry(root)
