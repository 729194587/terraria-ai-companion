"""Check actual terrain, inventory, native child receipts and work poses in -PlatformRoutes."""
import argparse
import json
from pathlib import Path

from verify_shared_navigation import child_rows, metrics


def verify(profile):
    data = json.loads((profile / "mining-native-result.json").read_text(encoding="utf-8-sig"))
    assert data["result"] == "passed"
    rows = {}
    expected = {
        "nav_platform", "single_platform", "cluster_platform", "nav_mixed", "nav_existing", "nav_landing",
        "cluster_workdeck", "cluster_short", "cluster_missing", "cluster_broken", "cluster_stop",
        "nav_natural", "nav_marked", "cluster_marked", "nav_housing", "nav_nofill", "nav_livemark",
    }
    for fixture in data["cases"]:
        r = fixture["row"]
        name = r["scene"]
        assert name in expected and name not in rows, name
        m = metrics(r)
        task = r["task"]
        children = child_rows(task)
        terminal = task["result"]["evidence"] or {}
        targets = terminal.get("targets", [])
        navigation = name.startswith("nav_")
        assert m["released"] and all(c["released"] for c in children), name
        assert not task["progress"]["omittedChildren"] and r["ticks"] < 2800, name
        placements = [c for c in children if c.get("placedByActor")]
        consumed = sum(c["consumedCount"] for c in placements)
        assert m["places"] == len(placements) == consumed, name
        assert all(c["typeId"] == 19 and c["consumedCount"] == 1 for c in placements), name
        assert fixture["placedTypes"] == [19] * len(r["placed"]), name
        if name == "nav_natural":
            assert m["cuts"] == fixture["wallCuts"] == 3 and r["EffectVerified"]
            assert terminal["arrived"] and terminal["settled"] and terminal["safeSupport"]
        elif name in {"nav_marked", "cluster_marked", "nav_housing", "nav_livemark", "nav_nofill"}:
            assert m["status"] in {"failed", "timeout"} and not r["EffectVerified"]
            assert m["cuts"] == m["places"] == 0 and not r["placed"]
            g = task["progress"]
            approach = g["terrainRoute"] if navigation else g["excavation"]["approach"]
            assert any(d.get("protectedEdit") for d in approach["decisions"]), name
            if name == "nav_nofill":
                assert fixture["platformStock"] == 8
            else:
                assert fixture["wallCuts"] == 0
            if name == "nav_livemark":
                assert r["injected"] and any(c["action"] == "mine_tile" for c in children)
        elif name == "cluster_short":
            assert m["status"] == "timeout" and m["cuts"] == consumed == 0
            assert fixture["platformStock"] == 1 and not r["placed"]
        elif name in {"cluster_missing", "cluster_broken", "cluster_stop"}:
            reasons = {"cluster_missing": ("unknown", "placement_stack_changed"),
                       "cluster_broken": ("unknown", "placement_target_changed"),
                       "cluster_stop": ("cancelled", "explicit_stop")}
            assert r["injected"] and (m["status"], m["reason"]) == reasons[name]
            assert consumed == 1 and r["start"]["stock"] == consumed + fixture["platformStock"] + fixture["platformsTaken"]
            proof, = placements
            assert proof["targetPresent"] == (name != "cluster_broken")
            assert r["placed"] == ([proof["x"]] if proof["targetPresent"] else [])
            if name == "cluster_broken":
                assert fixture["brokenX"] == proof["x"]
        else:
            assert m["status"] == "completed" and r["EffectVerified"] and m["cuts"] == 0
            assert sorted(c["x"] for c in placements) == r["placed"] and consumed > 0
            assert all(c["targetPresent"] for c in placements) and 8 == consumed + fixture["platformStock"]
            assert fixture["stoodOnPlaced"]
            if name == "nav_landing":
                assert any(col * 16 < r["finalX"] + 20 and (col + 1) * 16 > r["finalX"] for col in r["placed"])
            if navigation:
                assert terminal["arrived"] and terminal["safeSupport"] and terminal["settled"]
                assert abs(r["finalFeet"] - r["start"]["goalY"]) <= 0.5
                assert r["finalX"] <= r["start"]["goalX"] <= r["finalX"] + 20
            else:
                assert r["oreAbsent"] and fixture["placedFromDeck"]
                if name.startswith("cluster_"):
                    assert fixture["secondOreAbsent"] and len(targets) == 2
                    assert all(t["status"] == "verified" and t["proof"]["Strikes"] > 0 for t in targets)
                else:
                    assert any(c.get("destroyedByActor") and c.get("X") == r["start"]["oreX"] for c in children)
            if name == "cluster_workdeck":
                assert fixture["minedFromDeck"]
            if name == "nav_mixed":
                assert fixture["solidStock"] == 8
        if not navigation and m["status"] != "completed":
            assert not r["EffectVerified"] and not r["oreAbsent"] and not fixture["secondOreAbsent"]
            assert len(targets) == 2 and all(t["status"] == "pending" and t["proof"] is None for t in targets)
        rows[name] = dict(**m, consumed=consumed, platformsPresent=r["placed"], remaining=fixture["platformStock"],
                          placedFromDeck=fixture["placedFromDeck"], minedFromDeck=fixture["minedFromDeck"],
                          nativePlaces=[{k: c[k] for k in ("x", "y", "typeId", "consumedCount", "targetPresent")}
                                        for c in placements],
                          targets=[dict(x=t["x"], y=t["y"], status=t["status"],
                                        strikes=t["proof"]["Strikes"] if t["proof"] else 0) for t in targets])
    assert rows.keys() == expected
    return rows


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profile", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = dict(source=json.loads((args.profile / "fixture-source.json").read_text(encoding="utf-8-sig")),
                  scenarios=verify(args.profile))
    if args.output:
        args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf8")
    print(f"Verified {len(report['scenarios'])} native platform and protection tasks.")
