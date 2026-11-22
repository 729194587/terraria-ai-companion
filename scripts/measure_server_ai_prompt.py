"""Measure static server system text + tool definitions; no model/network calls.

Token ranges are a transparent character heuristic, not tokenizer or billing data.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys
import types

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'python'))
import lua_agent
from server_ai import operations_for_capabilities

BASE = {'server_ai_atomic_v1', 'atomic_operations_v1', 'hotbar_keys_v1'}
READS = {'server_ai_reads_v1', 'server_ai_perception_v1'}
SCENARIOS = {
    'minimal': BASE,
    'perception': BASE | READS,
    'full': BASE | READS | {'server_ai_map_v1', 'server_ai_scene_v1', 'server_ai_crafting_v1',
                          'server_ai_actions_v1', 'server_ai_direct_input_v1',
                          'server_ai_place_tile_v1', 'server_ai_place_object_workbench_v1',
                          'landing_region_navigation_v1'},
}


def measure(contract):
    rows = {}
    for name, capabilities in SCENARIOS.items():
        bridge = types.SimpleNamespace(hello={'capabilities': sorted(capabilities)},
            supported_atomic_operations=operations_for_capabilities(capabilities))
        system, tools = contract(bridge)
        text = system + json.dumps(tools, ensure_ascii=False, separators=(',', ':'))
        cjk = sum('\u3400' <= c <= '\u9fff' for c in text)
        other = (len(text)-cjk)/4
        rows[name] = dict(systemCharacters=len(system), totalCharacters=len(text),
                          estimatedTokensLow=math.ceil(cjk+other), estimatedTokensHigh=math.ceil(2*cjk+other))
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline', type=Path, help='Pre-change lua_agent.py snapshot; trusted Python source')
    args = parser.parse_args()
    result = dict(scope='system text + compact JSON model tools; excludes runtimeInventory/history/transport',
                  estimator='CJK U+3400..U+9FFF: 1..2 tokens/character; other text: 4 characters/token; round up',
                  handshakes={name:sorted(caps) for name,caps in SCENARIOS.items()}, after=measure(lua_agent.server_contract))
    if args.baseline:
        raw = args.baseline.read_bytes()
        module = types.ModuleType('baseline_lua_agent')
        module.__file__ = str(args.baseline.resolve())
        exec(compile(raw, module.__file__, 'exec'), module.__dict__)
        result['baselineSourceSha256'] = hashlib.sha256(raw).hexdigest()
        result['before'] = measure(module.server_contract)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
