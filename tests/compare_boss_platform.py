"""Compare the nine copied-world profiles from the Boss platform experiment."""
import argparse
import json
import math
from pathlib import Path


def compare(profiles):
    runs = {}
    packages = set()
    for profile in profiles:
        manifest = json.loads((profile / 'manifest.json').read_text(encoding='utf-8-sig'))
        assert manifest['exitCode'] == 0, profile
        packages.add(manifest['packageSHA256'])
        results = json.loads((profile / 'combat-results.json').read_text())
        sample = results['arena']
        boss = results['initial']['type']
        arm = sample['arenaArm']
        assert (boss, arm) not in runs and sample['updates'] == 1200, profile
        runs[boss, arm] = sample
    assert len(packages) == 1
    for boss in (125, 134, 398):
        round_trips = 0
        base = runs[boss, 'flat']['initialPose']
        for arm in ('flat', 'platform', 'shield'):
            sample = runs[boss, arm]
            initial = dict(sample['initialPose'])
            accessory = initial.pop('accessory')
            # These coordinates are sampled after native NPC AI has already advanced.
            root_offset = math.hypot(initial.pop('targetX') - base['targetX'],
                                     initial.pop('targetY') - base['targetY'])
            expected = {k: v for k, v in base.items() if k not in ('accessory', 'targetX', 'targetY')}
            assert initial == expected, (boss, arm, initial, expected)
            assert (accessory != base['accessory']) == (arm == 'shield')
            drops = jumps = returns = 0
            lower_than = None
            for change in sample['changes']:
                if change['from'] > change['to'] and change['downSinceSupport']:
                    drops += 1
                    lower_than = change['from'] if lower_than is None else max(lower_than, change['from'])
                if change['from'] < change['to'] and change['jumpSinceSupport']:
                    jumps += 1
                    if lower_than is not None and change['to'] >= lower_than:
                        returns += 1
                        lower_than = None
            round_trips += returns
            if arm == 'flat':
                assert not sample['changes'] and sample['openingDistance'] == 0
            print(boss, arm, jumps, drops, returns, round(sample['openingDistance'],1),
                  sample['hurtCount'], sample['hurtTotal'], sample['arenaHits'], sample['arenaDamage'], round(root_offset, 2))
        assert round_trips > 0, boss


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('profiles', nargs=9, type=Path)
    compare(parser.parse_args().profiles)
