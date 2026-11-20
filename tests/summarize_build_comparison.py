"""Summarize one pair of fresh native_build_place comparison profiles."""
import argparse
from collections import Counter
import json
from pathlib import Path


def measure(profile):
    summary = json.loads((profile / "building-comparison.json").read_text())
    children = {}
    with (profile / "build-place-trace.jsonl").open() as trace:
        for row in map(json.loads, trace):
            action = row["reply"].get("data", {}).get("action", {})
            task = action.get("task") or {}
            if row["request"]["op"] == "start":
                blueprint = action["targetTask"]["blueprint"]
            child = task.get("currentChild")
            if child:
                number = int(child["taskId"].rsplit("/", 1)[1])
                children[number] = child["taskName"]
            if task.get("status") == "completed":
                # Same 10,800-update root lease in both packages.
                ticks = task["observedGameUpdate"] - (task["deadlineGameUpdate"] - 10800)
    metrics = summary["scheduler"]
    rows = summary["entries"]
    assert len(rows) == 8 and all(e["status"] == "placed" for e in rows)
    if metrics:
        sites = metrics["worksitesVisited"]
        nav = metrics["navigationCalls"]
        switches = metrics["materialSwitches"]
        surveys = metrics["targetSurveys"]
        attempts = metrics["nativePlacementAttempts"]
        unknown = metrics["nativePlacementUnknown"]
    else:
        # Reject a polling trace with missed children instead of reporting an undercount.
        assert sorted(children) == list(range(1, max(children)+1))
        counts = Counter(children.values())
        attempts = counts["place_item_native"]
        assert attempts == len(rows) and children[max(children)] == "place_item_native"
        nav = counts["navigate_to_x"]
        sites = 1 + nav
        switches = counts["hold_inventory_item"]
        # For this all-ready V1 fixture: admission + verification + final observation
        # per cell, plus one extra survey after each motion or hold child.
        surveys = 3*attempts + nav + switches
        unknown = 0
    return blueprint, dict(sites=sites, navigation=nav, holds=switches, targetSurveys=surveys,
                           attempts=attempts, verified=len(rows), unknown=unknown,
                           ticks=ticks, wallSeconds=round(summary["elapsedSeconds"], 3))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("v1", type=Path)
    parser.add_argument("v2", type=Path)
    args = parser.parse_args()
    blueprint, baseline = measure(args.v1)
    current, result = measure(args.v2)
    assert blueprint == current, "Blueprint or world coordinates differ"
    print(json.dumps(dict(v1=baseline, v2=result), indent=2))
