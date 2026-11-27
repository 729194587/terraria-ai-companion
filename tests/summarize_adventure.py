"""Package replayable route, decisions and receipts from one isolated run."""
import argparse
import gzip
import json
from collections import Counter
from pathlib import Path


def summarize(profile):
    native = json.loads((profile / "result.json").read_text(encoding="utf-8-sig"))
    run = native["results"]
    frames = json.loads((profile / "natural-motion-adventure.json").read_text(encoding="utf-8"))
    samples = []
    for frame in frames:
        cell = (int(frame["x"] // 64), int(frame["y"] // 64))
        if not samples or cell != samples[-1][1:]:
            samples.append((frame["tick"], *cell))
    windows = []
    recorded = set()
    last_known = run["initialKnown"]
    for cycle in run["cycles"]:
        trip = cycle["receipt"].get("explorationResult", {})
        since = trip.get("baseline", {}).get("gameUpdate")
        for leg, scan in zip(trip.get("segments", ()), trip.get("coverage", ())):
            if leg["taskId"] in recorded:
                continue  # A no_progress cycle may expose the prior terminal receipt again.
            recorded.add(leg["taskId"])
            until = scan["gameUpdate"]
            rerouted = leg.get("rerouted") or leg["start"] == trip.get("reroutedAt")
            windows.append((since, until, rerouted, trip.get("findings"),
                            last_known == cycle["known"]))
            since = until
        last_known = cycle["known"]

    edges = Counter()
    counts = Counter()
    for a, b in zip(samples, samples[1:]):
        edge = tuple(sorted((a[1:], b[1:])))
        if edges[edge]:
            window = next((w for w in windows if w[0] <= b[0] <= w[1]), None)
            if window and window[2]:
                counts["rerouting"] += 1
            elif window and window[4] and not window[3]:
                counts["noMapOrFindingGain"] += 1
            else:
                counts["otherRepeated"] += 1
        edges[edge] += 1

    return dict(mode=native["mode"], worldSHA256=native["initialWorldSHA256"],
                packageSHA256=native["packageSHA256"], teardown=native["teardown"],
                routeUnit="native player top-left, 4x4 Tile cells, [gameUpdate, column, row]",
                route=samples, repeatedEdges=dict(counts), distinctEdges=len(edges),
                totalTransitions=sum(edges.values()), run=run)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("profile", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result = summarize(args.profile)
    with gzip.open(args.output, "xt", encoding="utf-8") as target:
        json.dump(result, target, ensure_ascii=False, separators=(",", ":"))
