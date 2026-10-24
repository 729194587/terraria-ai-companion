"""Offline size comparison using the deidentified recorded-game fixture, not a model call."""
import gzip
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from exploration import project_observation


def size(value):
    return len(json.dumps(value, ensure_ascii=False).encode('utf-8'))


def measure(source):
    projected = project_observation(source)
    sections = {k: projected[k] for k in ('camera', 'player', 'terrain', 'entities', 'investigate', 'completeness')}
    sections['metadata'] = {k:v for k,v in projected.items() if k not in sections}
    parts = {k:size(v) for k,v in sections.items()}
    parts['framing_remainder'] = size(projected) - sum(parts.values())
    assert sum(parts.values()) == size(projected)
    return {'source': 'deidentified recorded game observation; no synthetic terrain',
            'serialization': 'json.dumps ensure_ascii=False, default separators; UTF-8 bytes, not Tokens',
            'old_observation_bytes': size(source), 'exploration_bytes': size(projected),
            'parts': parts, 'regions': len(projected['terrain']['regions']),
            'entities':len(projected['entities']['items']),
            'candidate_anchors':projected['investigate']['candidateCount']}


if __name__ == '__main__':
    source=json.loads(gzip.decompress((Path(__file__).parent/'fixtures/exploration-real-observation.json.gz').read_bytes()))
    print(json.dumps(measure(source),ensure_ascii=False,indent=2))
