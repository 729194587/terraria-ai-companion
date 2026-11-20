"""Exercise the isolated G3-A merchant probe against a real bound server AI."""
import argparse
from collections import Counter
import json
from pathlib import Path
import socket
import time
import uuid


COINS = {71: 1, 72: 100, 73: 10000, 74: 1000000}
TORCH = 8


def contents(rows):
    totals = Counter()
    for row in rows:
        if row["type"] > 0 and row["stack"] > 0:
            totals[row["type"]] += row["stack"]
    return totals


def copper(totals):
    return sum(totals[item] * value for item, value in COINS.items())


def balance(inventory):
    stores = [inventory['carried'][:54], *inventory['banks']]
    names = ('carriedCopper', 'piggyBank', 'safe', 'defendersForge', 'voidVault')
    funds = {name: copper(contents(rows)) for name, rows in zip(names, stores)}
    return dict(funds, totalCopper=sum(funds.values()))


def check_payment(before, after, price, evidence=None):
    old, new = balance(before), balance(after)
    assert old['totalCopper'] - new['totalCopper'] == price
    def noncoins(snapshot):
        return [[row for row in bank if row['type'] > 0 and row['type'] not in COINS]
                for bank in snapshot['banks']]
    assert noncoins(before) == noncoins(after)
    if evidence is not None:
        assert evidence['beforeFunds'] == old and evidence['afterFunds'] == new
        assert evidence['bankNonCurrencyUnchanged'] is True


def wait_file(path):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        error = path.parent / "g3-shop-error.txt"
        if error.exists():
            raise RuntimeError(error.read_text(encoding="utf-8"))
        if path.exists():
            return json.loads(path.read_text(encoding="utf-8-sig"))
        time.sleep(.05)
    raise TimeoutError(path)


def verify(port, profile):
    assert (profile / "isolated-action-fixture").exists()
    slot = wait_file(profile / "g3-shop-ready.json")["slot"]
    binding = {}
    with socket.create_connection(("127.0.0.1", port), timeout=8) as sock, sock.makefile("rb") as reader:
        def request(op, **args):
            msg = dict(v=1, id=uuid.uuid4().hex, op=op, **args)
            if binding:
                msg["binding"] = binding
            sock.sendall((json.dumps(msg, separators=(",", ":")) + "\n").encode())
            result = json.loads(reader.readline(1048577))
            assert result["ok"], result
            return result

        binding.update(request("bind")["binding"])
        scene = request("query", operation="observe_scene", arguments={"layers": ["objects"]})
        merchant, = [o["details"]["detail"] for o in scene["data"]["data"]["objects"]["items"]
                     if o.get("contentKey") == "Terraria/Merchant" and o["details"]["detail"]["slot"] == slot]
        tmp = profile / "g3-shop-request.tmp"
        tmp.write_text(json.dumps(dict(slot=slot, entityRef=merchant["entityRef"])), encoding="utf-8")
        tmp.replace(profile / "g3-shop-request.json")
        record = wait_file(profile / "g3-shop-result.json")
        final_inventory = contents(request("state")["data"]["inventory"])

    assert record["observed"] == merchant["entityRef"] != record["replacement"]
    assert record["observed"].rsplit(":", 1)[0] == record["replacement"].rsplit(":", 1)[0]
    assert (record["World"], record["Generation"], record["Slot"]) == (
        binding["world"], binding["generation"], binding["slot"])
    catalog = record["catalog"]
    assert catalog["name"] == "Terraria/Merchant/Shop" and catalog["name"] in catalog["shops"]
    torch, = [entry for entry in catalog["entries"] if entry["itemType"] == TORCH and not entry["disabled"]]
    assert torch["conditions"] == []
    sampled = {c["key"]: c["value"] for entry in catalog["entries"] for c in entry["conditions"]}
    assert sampled["Conditions.TimeDay"] == catalog["day"]
    assert sampled["Conditions.InSnow"] == catalog["actorSnow"]
    quote = record["quote"]
    assert quote["before"] == quote["after"]
    insufficient, stale = record["insufficient"], record["stale"]
    for case in (insufficient, stale):
        assert case["after"] == case["before"], case["result"]
    assert copper(contents(insufficient["before"]["carried"])) == 0
    assert balance(insufficient["before"])["totalCopper"] == 0
    purchase = record["purchase"]
    before = contents(purchase["before"]["carried"])
    after = contents(purchase["after"]["carried"])
    paid = purchase["result"]["unitPrice"]
    assert paid > 0
    assert after[TORCH] == before[TORCH] + 1
    assert all(before[t] == after[t] for t in before.keys() | after.keys() if t not in COINS and t != TORCH)
    check_payment(purchase["before"], purchase["after"], paid)
    assert final_inventory == after == contents(stale["after"]["carried"])
    check_receipts(quote["result"], insufficient["result"], purchase["result"], stale["result"], paid)
    (profile / "g3-shop-ipc-readback.json").write_text(
        json.dumps(dict(binding=binding, merchant=merchant, finalInventory=final_inventory, paidCopper=paid),
                   indent=2), encoding="utf-8")
    print(f"Merchant sold one torch for {paid} copper. Evidence in {profile / 'g3-shop-result.json'}")


def check_receipts(quote, insufficient, purchase, stale, paid):
    assert quote["reason"] == "price_quoted" and quote["conditionCount"] == 0
    assert quote["unitPrice"] == purchase["unitPrice"] == paid
    assert (purchase["status"], purchase["reason"]) == ("completed", "purchase_verified"), purchase
    assert (insufficient["status"], insufficient["reason"]) == ("failed", "insufficient_funds")
    assert insufficient["unitPrice"] == paid
    assert (stale["status"], stale["reason"], stale["unitPrice"]) == ("failed", "stale_entity_ref", None)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--profile", type=Path, required=True)
    args = parser.parse_args()
    verify(args.port, args.profile)
