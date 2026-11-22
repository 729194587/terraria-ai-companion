from copy import deepcopy
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).parent / 'benchmark'))
from runner import HERE, evaluate, load_manifest, read, summary, validate_initial, write


class ScenarioBenchmarkTests(unittest.TestCase):
    def setUp(self):
        self.spec = load_manifest(HERE / 'scenarios.json')['ore_cluster_basic']
        self.spec['modVersion'] = 'test'
        self.binding = dict(actor='actor', world='world', generation=3, slot=254)
        targets = [dict(x=100+x, y=200+y, present=True, type=7) for x, y in self.spec['setup']['targets']]
        self.initial = dict(layout=dict(guardDigest='digest', targets=targets, origin=dict(x=100, y=200)),
                            fingerprint='A'*64, reachable=len(targets), objective=False, guardsPassed=True,
                            modVersion='test', gameUpdate=10)
        self.final = dict(targets=deepcopy(targets), guardDigest='digest', nativeProof=[], mining=[], navigation=[],
                          stanceDelta=0, alive=True, healthUnchanged=True, released=True, gameUpdate=18)
        self.noop = dict(status='completed', actions=0, calls=0)

    def test_registry_and_bad_manifest(self):
        manifest = read(HERE / 'scenarios.json')
        self.assertEqual(3, len(load_manifest(HERE / 'scenarios.json')))
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'scenarios.json'
            (path.parent / 'copper.lua').write_text('return true', encoding='utf-8')
            for edit in (
                lambda data: data['scenarios'].append(data['scenarios'][0]),
                lambda data: data['scenarios'][0]['setup'].update(targets=[[2, -2], [5, -2]]),
                lambda data: data['budget'].update(seconds=0),
                lambda data: data['scenarios'][0].update(solutionLua='../copper.lua'),
            ):
                damaged = deepcopy(manifest)
                edit(damaged)
                write(path, damaged)
                with self.assertRaises(ValueError):
                    load_manifest(path)

    def test_initial_state_is_verified_before_execution(self):
        validate_initial(self.spec, self.initial)
        for field, value in (('reachable', 0), ('objective', True), ('guardsPassed', False), ('modVersion', 'other')):
            with self.assertRaises(ValueError):
                validate_initial(self.spec, {**self.initial, field: value})
        displaced = deepcopy(self.initial)
        displaced['layout']['targets'][0]['x'] -= 1
        with self.assertRaises(ValueError):
            validate_initial(self.spec, displaced)

    def test_noop_and_guards(self):
        row = evaluate(self.spec, 'noop', self.binding, self.initial, self.final, self.noop, .2)
        self.assertEqual('passed', row['status'])
        self.assertFalse(row['objectiveSatisfied'])
        self.assertEqual(8, row['metrics']['gameTicks'])
        self.final['released'] = False
        row = evaluate(self.spec, 'noop', self.binding, self.initial, self.final, self.noop, .2)
        self.assertEqual(['inputsReleased'], row['failures'])

    def test_solution_requires_native_evidence_and_ignores_lua_value(self):
        targets = self.initial['layout']['targets']
        self.final.update(targets=[{**t, 'present': False, 'type': None} for t in targets], mining=[{'status': 'Completed'}])
        receipt = dict(effectVerified=True, inputsReleased=True, evidence=dict(targets=targets))
        task = dict(worldSessionId='world', actorGeneration=3, actorSlot=254, currentChild=None, result=receipt)
        lua = dict(status='completed', calls=3, actions=1, value='arbitrary',
                   lastAction=dict(status='completed', valid=True, completed=True, endEvidenceStatus='known',
                                   actorId='actor', task=task))
        row = evaluate(self.spec, 'solution', self.binding, self.initial, self.final, lua, 1)
        self.assertEqual('failed', row['status'])
        self.assertFalse(row['checks']['objective']['nativeProofMatchesTargets'])
        self.final['nativeProof'] = [{**t, 'tick': 16} for t in targets]
        row = evaluate(self.spec, 'solution', self.binding, self.initial, self.final, lua, 1)
        self.assertEqual('passed', row['status'])
        noop = evaluate(self.spec, 'noop', self.binding, self.initial, self.final, self.noop, 1)
        self.assertEqual(row['checks']['objective'], noop['checks']['objective'])
        self.assertIn('noopSatisfiedObjective', noop['failures'])

    def test_repeat_aggregation_and_report(self):
        record = dict(scenario='ore', status='passed', failures=[], variant='solution', repeat=1, fingerprint='same')
        rows = [record, {**record, 'repeat': 2}, {**record, 'variant': 'noop'}]
        self.assertIn('| ore | 2/2 | 1/1 | consistent |', summary(rows))
        rows[1] = {**rows[1], 'fingerprint': 'different'}
        self.assertIn('initial fingerprints differ', summary(rows))
        rows[1] = {**rows[1], 'status': 'setup_error', 'failures': ['missing ready']}
        self.assertIn('setup_error 1', summary(rows))
        self.assertIn('missing ready', summary(rows))
