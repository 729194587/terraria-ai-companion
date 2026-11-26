"""Verify native approach receipts and produce the compact baseline comparison."""
import argparse
import hashlib
import json
from pathlib import Path
from statistics import median


def read(path):
    return json.loads(path.read_bytes())


def proof_summary(task):
    end = task["result"]["evidence"] or {}
    targets = end.get("targets", [])
    proofs = [t["proof"] for t in targets if t["status"] == "verified"]
    for t in targets:
        if t["status"] == "verified":
            assert (t["x"], t["y"], t["typeId"]) == tuple(t["proof"][k] for k in ("X", "Y", "Type"))
            assert t["proof"]["Strikes"] > 0
        if t["status"] == "pending":
            assert t["proof"] is None
    if not targets and end.get("destroyedByActor") and end.get("targetAbsent"):
        proofs = [{k: end[k] for k in ("X", "Y", "TileType", "Ticks", "Strikes")}]
    return proofs, [[t["x"], t["y"]] for t in targets if t["status"] == "pending"]


def summarize(task, ticks):
    progress = task["progress"]
    end = task["result"]
    approach = (progress.get("excavation") or {}).get("approach", {})
    proofs, pending = proof_summary(task)
    children = []
    for c in progress["children"]:
        result = c.get("result") or c
        evidence = result.get("evidence") or {}
        children.append(dict(operation=c["taskName"], status=result["status"], reason=result["reason"],
                             observedGameUpdate=c.get("observedGameUpdate"),
                             **{k: evidence[k] for k in ("X", "Y", "Ticks", "destroyedByActor", "x", "y", "arrived",
                                                          "workLeftX", "workFeetY") if k in evidence}))
    return dict(taskId=task["taskId"], status=end["status"], reason=end["reason"], released=end["inputsReleased"],
                effectVerified=end["effectVerified"], ticks=ticks, proofs=proofs, pending=pending,
                children=children, omittedChildren=progress.get("omittedChildren", 0), **approach)


def check_controlled(scene, row, observation):
    assert row["released"] and observation["controlsReleased"], scene
    assert observation["oreAbsent"] == bool(row["proofs"]), scene
    if scene in {"stop_clearing", "stop_after_cut", "path_changed", "falling_sand", "dead_end"}:
        assert row["pending"] and not row["effectVerified"], scene
        if scene.startswith("stop_"):
            assert observation["injected"] and row["status"] == "cancelled", scene
            assert row["actualCuts"] == observation["clearedAtChange"], scene
        elif scene == "path_changed":
            assert observation["injected"] and row["actualCuts"] == observation["clearedAtChange"] == 1, scene
            assert row["replans"] and row["status"] in ("timeout", "failed"), scene
        else:
            assert row["actualCuts"] == 0 and row["ticks"] < 2700, scene
        return
    assert row["status"] == "completed" and row["effectVerified"] and len(row["proofs"]) == 1, scene
    decisions = row["decisions"]
    if scene == "direct":
        assert not decisions and row["navigationDispatches"] == row["actualCuts"] == 0
        return
    choice = decisions[0]
    if scene in {"detour", "slow"}:
        assert choice["choice"] == "movement" and row["actualCuts"] == 0, scene
    else:
        assert choice["choice"] == "clearance" and choice["predictedGoal"] and row["actualCuts"] > 0, scene
        assert choice["dependencies"] and row["navigationFailures"] == 0, scene
        actual = next(c for c in row["children"] if c["operation"] == "mine_tile")
        assert choice["gameUpdate"] <= actual["observedGameUpdate"] - actual["Ticks"], scene
        predicted = next(a for a in choice["actions"] if a["kind"] == "Mine")
        assert (predicted["X"], predicted["Y"]) == (actual["X"], actual["Y"])
        if scene in {"sequential", "single"}:
            moves = [a["kind"] for a in choice["actions"]]
            assert "Mine" in moves[moves.index("Move") + 1:] and row["actualCuts"] == 6
            assert len({a["fromX"] for a in choice["actions"] if a["kind"] == "Mine"}) >= 2
    if scene in {"fast", "slow"}:
        assert (choice["clearance"]["Total"] + 8 < choice["movement"]["Total"]) == (scene == "fast")


def controlled(profile, candidate):
    result = read(profile / "mining-native-result.json")
    assert result["result"] == "passed"
    cases = {}
    for observation in result["cases"]:
        scene = observation["scene"]
        row = summarize(observation["task"], observation["ticks"])
        if candidate:
            check_controlled(scene, row, observation)
        else:
            row.update(planningMs=observation["planningMs"], maxProbeMs=observation["maxSliceMs"])
        case = cases.setdefault(scene, dict(tool=observation["tool"], prefix=observation["prefix"], runs=[],
                                          firstReceipt=row))
        case["runs"].append({k: row[k] for k in ("status", "ticks", "planningMs", "maxSliceMs", "maxProbeMs",
                                                 "actualCuts", "navigationFailures") if k in row})
    for case in cases.values():
        for key in ("ticks", "planningMs"):
            values = [r[key] for r in case["runs"]]
            case[key] = dict(median=median(values), minimum=min(values), maximum=max(values))
    return dict(packageSHA256=hashlib.sha256((profile / "Mods/CompanionBridge.tmod").read_bytes()).hexdigest(), cases=cases)


def natural(path):
    source = read(path)
    scenario, = source["results"]["scenarios"]
    assert scenario["fixture"]["fixture"]["terrainWrites"] is False
    task = scenario["receipt"]["task"]
    ticks = task["observedGameUpdate"] - (task["deadlineGameUpdate"] - 2700)
    frames = read(path.parent / ("natural-motion-" + scenario["case"] + ".json"))
    poses = []
    for frame in frames:
        if not (task["deadlineGameUpdate"] - 2700 <= frame["tick"] <= task["observedGameUpdate"] and frame["Settled"]):
            continue
        if not poses or (frame["x"], frame["y"]) != (poses[-1]["x"], poses[-1]["y"]):
            poses.append({k: frame[k] for k in ("tick", "x", "y", "SafeSupport", "life")})
    def inventory(name):
        return {i["contentKey"]: i["count"] for i in scenario[name]["items"] if i["contentKey"].endswith("Ore")}
    return dict(seed=scenario["fixture"]["fixture"]["seed"], start=scenario["start"],
                x=scenario["fixture"]["x"], y=scenario["fixture"]["y"],
                worldSHA256=source["initialWorldSHA256"], packageSHA256=source["packageSHA256"],
                inventoryBefore=inventory("before"), inventoryAfter=inventory("after"), settledPoses=poses, **summarize(task, ticks))


def main(args):
    parity = read(args.parity / "mining-native-result.json")
    assert parity["result"] == "passed"
    report = dict(baseline="fe6190efd8250b2dbe8e8f16e11f36c41e4fcce2", candidateRevision=args.revision,
                  engine="2026.9.3.1", engineSHA256="BF2CA4D53512D064FAE664170A0D21ECC5E2ED51B5E917D871EC78CC449263E9",
                  parity=parity["evidence"][-1]["parity"], natural={})
    report["control"] = controlled(args.control, False)
    report["candidate"] = controlled(args.candidate, True)
    for name, base, new in args.natural:
        control, candidate = natural(Path(base)), natural(Path(new))
        for k in ("worldSHA256", "seed", "start", "x", "y", "inventoryBefore"):
            assert control[k] == candidate[k], (name, k)
        assert candidate["released"] and candidate["ticks"] < 2700
        if name in {"buried", "buried-iron", "large-vein", "change-stance"}:
            assert candidate["status"] == "completed" and len(candidate["proofs"]) == candidate["start"]["count"], name
        if name in {"buried", "buried-iron"}:
            assert candidate["actualCuts"] and any(d["choice"] == "clearance" and d["predictedGoal"]
                                                   for d in candidate["decisions"]), name
            assert any(p["y"] > candidate["y"] and p["SafeSupport"] for p in candidate["settledPoses"]), name
        if name == "enclosed" and candidate["status"] != "completed":
            assert "budget_exhausted" in candidate["reason"] and candidate["pending"]
        report["natural"][name] = dict(control=control, candidate=candidate)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf8")
    print(f"Verified {len(report['candidate']['cases'])} controlled cases and {len(report['natural'])} paired worlds.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("control", "candidate", "parity", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--revision", required=True, help="Git SHA of the candidate package source")
    parser.add_argument("--natural", action="append", nargs=3, default=[], metavar=("NAME", "CONTROL", "CANDIDATE"))
    main(parser.parse_args())
