"""Validate the copied-world native Boss probe independently of the stimulus driver."""
import argparse
import json
import time
from pathlib import Path


def verify(profile):
    path = profile / "boss-e0.json"
    failure = profile / "action-fixture-failure.txt"
    deadline = time.monotonic() + 225
    while not path.exists():
        if failure.exists():
            raise RuntimeError(failure.read_text())
        if time.monotonic() >= deadline:
            raise TimeoutError(path)
        time.sleep(0.2)
    report = json.loads(path.read_text())
    events = report["events"]

    def rows(kind):
        return [e["data"] for e in events if e["kind"] == kind]

    before, = rows("before")
    after, = rows("after")
    end, = rows("end")
    strikes = rows("strike")
    kills = rows("onKill")
    scenario = report["scenario"]
    if scenario == "eater":
        assert [n["type"] for n in before].count(13) == 1
        heads = [n for n in after if n["type"] == 13]
        assert len(heads) == 2 and len({n["token"] for n in heads}) == 2
        segments = {n["slot"]: n for n in after}
        visited = set()
        for head in heads:
            part = head
            while True:
                assert part["slot"] not in visited
                visited.add(part["slot"])
                if part["type"] == 15:
                    break
                child = segments[int(part["ai"][0])]
                assert int(child["ai"][1]) == part["slot"]
                part = child
        assert visited == segments.keys()
        removed = strikes[0]["before"]
        assert removed["token"] in {n["token"] for n in kills}
        previous = {n["slot"]: n for n in before}
        for slot, new_type in [(int(removed["ai"][0]), 13), (int(removed["ai"][1]), 15)]:
            assert previous[slot]["type"] == 14 and segments[slot]["type"] == new_type
            assert previous[slot]["token"] != segments[slot]["token"]
    elif scenario == "destroyers":
        roots = {n["slot"]: n for n in before if n["type"] == 134}
        assert len(roots) == 2
        assert {n["realLife"] for n in before if n["type"] == 135} == roots.keys()
        hit, = strikes
        root = hit["rootBefore"]
        assert roots[hit["before"]["realLife"]]["token"] == root["token"]
        assert root["life"] - hit["rootAfter"]["life"] == hit["damage"] > 0
        assert hit["before"]["life"] - hit["after"]["life"] == hit["damage"]
        other, = [n for n in roots.values() if n["slot"] != root["slot"]]
        assert any(n["token"] == other["token"] and n["life"] == other["life"] for n in after)
    elif scenario == "twins":
        assert [n["type"] for n in kills] == [125, 126]
        assert [n["type"] for n in after] == [126] and not end
        assert not rows("flagsAfter")[0]["downedMechBoss2"] and rows("flagsEnd")[0]["downedMechBoss2"]
    elif scenario == "two-twins":
        assert sorted(n["type"] for n in before) == [125, 125, 126, 126]
        assert len({n["token"] for n in before}) == 4
        assert all(n["realLife"] == -1 for n in before)
        assert all(s["parent"] is None for s in rows("spawn"))
        assert any(p["damage"] > 0 and p["width"] > 0 for p in rows("hostileProjectile"))
    elif scenario == "moon":
        core, = rows("seed")
        parts = [s for s in rows("spawn") if s["npc"]["type"] in (396, 397)]
        assert sorted(s["npc"]["type"] for s in parts) == [396, 397, 397]
        assert all(s["parent"]["token"] == core["token"] for s in parts)
        assert {s["before"]["token"] for s in strikes} == {core["token"], *(s["npc"]["token"] for s in parts)}
        core_strike, = [e for e in events if e["kind"] == "strike" and e["data"]["before"]["type"] == 398]
        assert core_strike["data"]["after"]["active"] and core_strike["data"]["after"]["life"] > 0
        kill, = [e for e in events if e["kind"] == "onKill"]
        assert kill["data"]["token"] == core["token"] and kill["tick"] > core_strike["tick"]
        assert not any(n["type"] == 398 for n in end) and rows("flagsEnd")[0]["downedMoonlord"]
    else:
        raise AssertionError(scenario)
    print(f"{scenario}: {len(events)} events, {len(strikes)} strikes, {len(kills)} OnKill")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--port", type=int)
    verify(parser.parse_args().profile)
