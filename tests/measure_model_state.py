"""Offline, whole-result counts. Requires tokenizers==0.22.1 and a pinned tokenizer file.

Inventory projection must come from AtomicExtensionsOfflineTests.ProjectInventoryJson,
which runs the production C# projection; this script never reimplements it.
"""
import argparse
import copy
import gzip
import hashlib
import json
from pathlib import Path
import sys

import tokenizers
from tokenizers import Tokenizer

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from observation import project_model_observation

MODEL = "deepseek-ai/DeepSeek-V4.1-Flash"
REVISION = "314113f8a7dadb388215396ba5fbee2c610a04d6"
TOKENIZER_SHA256 = "c90dfa01249db1be4245780a052ede752e1361c612ac6d08e2bdada7d599476b"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tokenizer", type=Path, required=True)
    parser.add_argument("--inventory-before", type=Path, required=True, help="Complete original tool result JSON")
    parser.add_argument("--inventory-data-after", type=Path, required=True, help="Production C# projected data JSON")
    args = parser.parse_args()
    assert tokenizers.__version__ == "0.22.1", tokenizers.__version__
    assert hashlib.sha256(args.tokenizer.read_bytes()).hexdigest() == TOKENIZER_SHA256
    tokenizer = Tokenizer.from_file(str(args.tokenizer))
    inventory = json.loads(args.inventory_before.read_text(encoding="utf-8"))
    compact = copy.deepcopy(inventory)
    compact["data"] = json.loads(args.inventory_data_after.read_text(encoding="utf-8"))
    fixture = Path(__file__).parent / "fixtures/exploration-real-observation.json.gz"
    observation = json.loads(gzip.decompress(fixture.read_bytes()))
    before = {"valid": observation["status"] != "unavailable", "observation": observation,
              "controlGeneration": None, "receivedAt": "2026-10-23T00:00:00+00:00",
              "transportMetrics": None}
    after = copy.deepcopy(before)
    after["observation"] = project_model_observation(observation)

    def count(result):
        # Identical serialization to Python tool result content, counted once in full.
        content = json.dumps(result, ensure_ascii=False)
        return {"utf8_bytes": len(content.encode("utf-8")),
                "tokens": len(tokenizer.encode(content, add_special_tokens=False).ids),
                "sha256": hashlib.sha256(content.encode("utf-8")).hexdigest()}

    print(json.dumps({"tokenizer": MODEL, "revision": REVISION, "tokenizer_sha256": TOKENIZER_SHA256,
                      "tokenizers_version": tokenizers.__version__,
                      "method": "Whole JSON tool result; ensure_ascii=False, default separators; no chat template or API billing claim",
                      "inspect_inventory_full": {"before": count(inventory), "after": count(compact)},
                      "get_observation": {"before": count(before), "after": count(after)}}, indent=2))


if __name__ == "__main__":
    main()
