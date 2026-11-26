"""Verify native shared-route receipts and summarize paired runs from the same copied world."""
import argparse
import hashlib
import json
from pathlib import Path

from prepare_shared_navigation_baseline import BASELINE
from verify_terrain_mining_results import check_controlled, natural, read, summarize


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load(profile):
    data = read(profile / "mining-native-result.json")
    assert data["result"] == "passed"
    return {(r["scene"], r["repeat"]): r for r in data["cases"]}


def child_rows(task):
    rows = []
    for child in task["progress"]["children"]:
        result = child["result"]
        proof = result["evidence"] or {}
        row = dict(action=child["taskName"], reason=result["reason"], released=result["inputsReleased"])
        for name in ("X", "Y", "x", "y", "typeId", "TileType", "placedByActor", "destroyedByActor",
                     "consumedCount", "targetPresent", "arrived", "settled", "safeSupport"):
            if name in proof:
                row[name] = proof[name]
        rows.append(row)
    return rows


def metrics(r):
    task = r["task"]
    g = task["progress"]
    e = g.get("terrainRoute") or g.get("excavation", {}).get("approach", {})
    rolling = g.get("rollingPlanning") or {}
    children = child_rows(task)
    ore_run = not r["scene"].startswith("nav_")
    seed = (r["start"]["oreX"], r["start"]["oreY"])
    batches = sum(c["action"] == "excavate_tiles" or
                  c["action"] == "mine_tile" and (c.get("X"), c.get("Y")) == seed for c in children) if ore_run else 0
    return dict(updates=r["ticks"], planningMs=round(e.get("planningMs", 0) + rolling.get("planningMs", 0), 2),
                maxSliceMs=round(max(e.get("maxSliceMs", 0), rolling.get("maxSliceMs", 0)), 2),
                cuts=e.get("actualCuts", g.get("dugTiles", 0)), places=e.get("actualPlaces", g.get("placedTiles", 0)),
                moves=e.get("navigationDispatches", sum(c["action"] == "navigate_to_landing_region" for c in children)),
                batches=batches,
                status=r["Status"], reason=r["Reason"], released=r["InputsReleased"] and r["controlsReleased"])


def check_shared(r):
    name, task = r["scene"], r["task"]
    g = task["progress"]
    nav = name.startswith("nav_")
    approach = g["terrainRoute"] if nav else g["excavation"]["approach"]
    m = metrics(r)
    steps = child_rows(task)
    assert not g["omittedChildren"] and m["released"] and all(s["released"] for s in steps), name
    terminal = task["result"]["evidence"] or {}
    if name in {"ore_target", "ore_gear", "nav_cancel", "ore_stop_place", "ore_stock"}:
        assert not r["EffectVerified"] and (nav or not r["oreAbsent"]), name
        if name == "ore_stock":
            assert m["status"] == "timeout" and m["cuts"] == m["places"] == 0 and r["stockLeft"] == 1
            assert g["excavation"]["routeSearchUpdates"] >= 900
        else:
            assert r["injected"]
            if name == "ore_target":
                assert m["status"] == "failed" and terminal["targets"][0]["status"] == "changed" and m["cuts"] == 1
            else:
                assert m["status"] == "cancelled"
                assert (m["cuts"], m["places"]) == ((0, 1) if name == "ore_stop_place" else (1, 0))
        if not nav and name != "ore_target":
            assert terminal["targets"][0]["status"] == "pending" and terminal["targets"][0]["proof"] is None
        return
    assert m["status"] == "completed" and r["EffectVerified"], name
    if nav:
        assert terminal["settled"] and terminal["safeSupport"] and terminal["arrived"]
        assert abs(r["finalFeet"]-r["start"]["goalY"]) <= .5
        assert r["finalX"] <= r["start"]["goalX"] <= r["finalX"] + 20
    else:
        assert r["oreAbsent"] and terminal["targets"][0]["proof"]["Strikes"] > 0
    decision = approach["decisions"][0]
    if name in {"nav_slow", "nav_detour"}:
        assert decision["choice"] == "movement" and m["cuts"] == m["places"] == 0
    else:
        assert decision["choice"] == "clearance" and decision["dependencies"]
    if name in {"nav_fast", "nav_slow"}:
        assert (decision["clearance"]["Total"]+8 < decision["movement"]["Total"]) == (name == "nav_fast")
    if name in {"nav_sequential", "nav_thin"}:
        assert m["cuts"] == (6 if name == "nav_sequential" else 3)
        assert all(s["reason"] != "navigation_local_frontier_exhausted" for s in steps)
        if name == "nav_sequential":
            actions = [s["action"] for s in steps]
            assert "mine_tile" in actions[actions.index("navigate_to_landing_region")+1:]
            assert len({a["fromX"] for a in decision["actions"] if a["kind"] == "Mine"}) >= 2
    if name in {"ore_support", "nav_support", "nav_deck"}:
        placed = [s for s in steps if s["action"] == "place_tile"]
        assert m["places"] == len(placed) == r["start"]["stock"]-r["stockLeft"] > 0
        assert sorted(s["x"] for s in placed) == r["placed"]
        assert all(s["placedByActor"] and s["targetPresent"] and s["consumedCount"] == 1 for s in placed)
        if name == "nav_deck":
            assert any(col*16 < r["finalX"]+20 and (col+1)*16 > r["finalX"] for col in r["placed"])


def compare(old, new, shared):
    before, after = load(old), load(new)
    required = ({"nav_detour","nav_fast","nav_slow","nav_thin","nav_sequential","nav_support","nav_deck",
                 "ore_support","ore_stock","ore_gear","ore_target","nav_cancel","ore_stop_place"} if shared else
                {"direct","detour","fast","slow","thin","sequential","single","dead_end","path_changed",
                 "stop_clearing","stop_after_cut","falling_sand"})
    assert before.keys() == after.keys() and {scene for scene,_ in after} == required
    assert len(after) == (23 if shared else 22)
    results = {}
    for (scene, repeat), r in after.items():
        base = before[scene, repeat]
        assert r["start"] == base["start"], (scene, repeat)
        if shared:
            check_shared(r)
        else:
            for row in (base, r):
                check_controlled(scene, summarize(row["task"], row["ticks"]), row)
        g = r["task"]["progress"]
        e = g.get("terrainRoute") or g["excavation"]["approach"]
        scenario = results.setdefault(scene, dict(start=r["start"], runs=[]))
        scenario["runs"].append(dict(repeat=repeat, baseline=metrics(base), candidate=metrics(r)))
        if repeat == 0:
            end = r["task"]["result"]["evidence"] or {}
            scenario.update(targets=end.get("targets"), children=child_rows(r["task"]), placed=r["placed"],
                            finalLeft=r["finalX"], finalFeet=r["finalFeet"],
                            decisions=[{key:d[key] for key in ("choice","movement","clearance","actions","predictedGoal","dependencies")}
                                       for d in e["decisions"]])
    return results


def main(args):
    report = dict(baseline=BASELINE, worldSHA256=sha(args.world), controlled={}, packages={})
    for kind, pair in (("shared", args.shared), ("mining", args.mining)):
        inputs = [read(p / "fixture-source.json") for p in pair]
        assert all(m["worldSHA256"].lower() == report["worldSHA256"] for m in inputs), kind
        assert inputs[0]["modWorldSHA256"] == inputs[1]["modWorldSHA256"], kind
        report["modWorldSHA256"] = inputs[0]["modWorldSHA256"]
        assert all(m["packageSHA256"].lower() == sha(p / "Mods/CompanionBridge.tmod") for m,p in zip(inputs,pair))
        report["controlled"][kind] = compare(*pair, kind == "shared")
        report["packages"][kind] = [sha(p / "Mods/CompanionBridge.tmod") for p in pair]
    if args.natural:
        a,b = map(natural,args.natural)
        old, new = (read(path)["results"]["scenarios"][0] for path in args.natural)
        for key in ("items", "selected"):
            assert old["before"][key] == new["before"][key], key
        for key in ("x","y","cloudEnabled","cloudActive","wingsLogic","wingTime","wingTimeMax","Grounded","Settled","SafeSupport"):
            assert old["fixture"][key] == new["fixture"][key], key
        for key in ("worldSHA256","seed","start","x","y","inventoryBefore"):
            assert a[key] == b[key], key
        assert a["released"] and b["released"] and len(a["proofs"]) == len(b["proofs"]) > 0
        fields = ("ticks","planningMs","maxSliceMs","actualCuts","navigationDispatches","released","status","reason","packageSHA256")
        report["natural"] = dict(worldSHA256=a["worldSHA256"], seed=a["seed"], start=a["start"], x=a["x"],y=a["y"],
                                 inventoryBefore=old["before"]["items"], selected=old["before"]["selected"], runs=[
            dict(**{k:r[k] for k in fields}, proofs=len(r["proofs"]),
                 batches=sum(c["operation"] == "excavate_tiles" for c in r["children"])) for r in (a,b)])
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2)+"\n", encoding="utf8")
    print("Passed", sum(len(v["runs"]) for group in report["controlled"].values() for v in group.values()), "paired native tasks")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--shared", nargs=2, type=Path, required=True, metavar=("BASE", "NEW"))
    parser.add_argument("--mining", nargs=2, type=Path, required=True, metavar=("BASE", "NEW"))
    parser.add_argument("--natural", nargs=2, type=Path, metavar=("BASE", "NEW"))
    parser.add_argument("--world", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    main(parser.parse_args())
