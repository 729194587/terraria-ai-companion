"""Run P0 against a copied world and two connected tModLoader wire clients."""
import argparse
import asyncio
import json
from pathlib import Path
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import time

from jsonschema import validate

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))
from atomic_operations import dispatch
from server_ai import ServerAiBridge
from server_ai_contract import api_help


class Client:
    def __init__(self, port, name):
        self.sock = socket.create_connection(("127.0.0.1", port), timeout=10)
        self.stream = self.sock.makefile("rb")
        self.chest = -1
        self.remotes = {}
        self.send(1, self.string("tModLoader v2026.9.3.1!2026.9.3"))
        self.wait(251)
        self.send(251)
        self.slot = self.wait(3)[0]
        self.send(4, bytes([self.slot, 0, 0]) + self.string(name) + bytes(28))
        self.send(16, struct.pack("<Bhh", self.slot, 100, 100))
        self.send(6)
        self.wait(7)
        self.send(8, struct.pack("<ii", -1, -1))
        self.wait(49)
        self.send(12, struct.pack("<BhhihhB", self.slot, -1, -1, 0, 0, 0, 0))
        self.wait(129)

    @staticmethod
    def string(value):
        data = value.encode()
        assert len(data) < 128
        return bytes([len(data)]) + data

    def send(self, packet, body=b""):
        self.sock.sendall(struct.pack("<HB", len(body) + 3, packet) + body)

    def wait(self, packet, match=lambda _: True):
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            header = self.stream.read(2)
            if len(header) != 2:
                raise RuntimeError(f"Disconnected while waiting for packet {packet}")
            data = self.stream.read(struct.unpack("<H", header)[0] - 2)
            kind, body = data[0], data[1:]
            if kind == 2:
                raise RuntimeError(f"Server rejected client: {body!r}")
            if kind == 33:
                self.chest = struct.unpack_from("<h", body)[0]
            if kind == 80:
                slot, index = struct.unpack("<Bh", body)
                self.remotes[slot] = index
            if kind == packet and match(body):
                return body
        raise TimeoutError(f"Missing packet {packet}")

    def expect_chest(self, actor, index):
        self.wait(80, lambda body: struct.unpack("<Bh", body) == (actor, index))

    def close(self):
        self.stream.close()
        self.sock.close()


def wait_for(predicate, seconds=20):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(.05)
    raise TimeoutError("Native test condition timed out")


async def verify(profile, bridge, first, second):
    ready = json.loads((profile / "container-ready.json").read_text())
    x, y, index, other = (ready[key] for key in ("x", "y", "index", "other"))
    actor = bridge.binding["slot"]
    report = {"clients": [first.slot, second.slot], "actorSlot": actor, "cases": {}}
    cases = report["cases"]
    report["sync"] = []

    async def api(name, arguments=None, *, status="completed", reason=None):
        result = await dispatch(bridge, name, arguments or {})
        help_result = api_help(bridge, {"name": name})
        assert help_result["status"] == "ok", help_result
        validate(result, help_result["returnsSchema"])
        actual = result.get("status", result.get("data", {}).get("status"))
        assert actual == status, result
        if reason:
            assert result.get("reason", result.get("data", {}).get("reason")) == reason, result
        cases.setdefault(name, []).append(result)
        return result

    async def open_at(tx, ty=y, **expected):
        return await api("open_container", {"x": tx, "y": ty}, **expected)

    async def inspect():
        return (await api("inspect_container", status="open"))["data"]["container"]

    async def close(token, **expected):
        return await api("close_container", {"containerToken": token}, **expected)

    def sync(chest):
        first.expect_chest(actor, chest)
        second.expect_chest(actor, chest)
        report["sync"].append({"chestIndex": chest, "receivedBy": [first.slot, second.slot]})

    def fixture(command):
        target = profile / "container-request"
        response = profile / "container-state.json"
        response.unlink(missing_ok=True)
        pending = profile / "container-request.tmp"
        pending.write_text(command)
        pending.replace(target)
        def read():
            try:
                data = json.loads(response.read_text())
                return data if data["command"] == command else None
            except (FileNotFoundError, json.JSONDecodeError):
                return None
        return wait_for(read)

    first.send(31, struct.pack("<hh", x + 4, y))
    first.wait(33)
    second.expect_chest(first.slot, other)
    await open_at(x + 4, status="failed", reason="container_in_use")
    await open_at(x + 8, status="failed", reason="container_locked")
    await open_at(x + 20, status="failed", reason="out_of_native_interaction_range")
    await open_at(x + 1, y + 1)
    sync(index)
    second.send(31, struct.pack("<hh", x, y))
    second.send(31, struct.pack("<hh", x + 20, y))
    second.wait(33)
    second_chest = second.chest
    assert second_chest >= 0 and second_chest != index
    assert first.chest == other
    baseline = fixture("snapshot")
    assert {row["slot"]: row["chest"] for row in baseline["clients"]} == {
        first.slot: other, second.slot: second_chest}
    assert baseline["actorChest"] == index
    report["baseline"] = baseline

    old = await inspect()
    new = await inspect()
    assert (new["x"], new["y"], new["typeId"], new["chestIndex"]) == (x, y, 21, index)
    assert new["slots"][0]["typeId"] == 9 and new["slots"][0]["stack"] == 7
    assert old["containerToken"] != new["containerToken"]
    assert old["slots"][0]["slotToken"] != new["slots"][0]["slotToken"]
    await close(old["containerToken"], status="failed", reason="stale_container_token")
    await close(new["containerToken"])
    sync(-1)
    await api("inspect_container", status="unavailable")

    await open_at(x)
    sync(index)
    before_replacement = await inspect()
    fixture("replace")
    sync(-1)
    await open_at(x)
    sync(index)
    after_replacement = await inspect()
    assert after_replacement["chestIndex"] == before_replacement["chestIndex"]
    await close(before_replacement["containerToken"], status="failed", reason="stale_container_token")
    assert fixture("snapshot")["actorChest"] == index
    fixture("move-away")
    sync(-1)
    await api("inspect_container", status="unavailable")
    fixture("move-back")
    await open_at(x)
    sync(index)
    fixture("missing")
    sync(-1)
    await open_at(x, status="failed", reason="container_record_missing")
    fixture("replace")
    await open_at(x)
    sync(index)
    fixture("destroy")
    sync(-1)
    await api("inspect_container", status="unavailable")
    report["afterCleanup"] = fixture("snapshot")
    assert report["afterCleanup"]["clients"] == baseline["clients"]

    first.send(33, struct.pack("<hhhB", -1, 0, 0, 0))
    wait_for(lambda: fixture("snapshot")["clients"][0]["chest"] == -1)
    await open_at(x + 4)
    sync(other)
    assert (await inspect())["typeId"] == 467
    fixture("invalidate")
    sync(-1)
    await api("inspect_container", status="unavailable")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine", type=Path, default=Path("D:/steam/steamapps/common/tModLoader"))
    parser.add_argument("--world", required=True, type=Path)
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--port", default=27941, type=int)
    parser.add_argument("--ipc-port", default=27942, type=int)
    args = parser.parse_args()
    args.engine = args.engine.resolve()
    if args.port == args.ipc_port:
        parser.error("Game and IPC ports must be distinct.")
    for port in (args.port, args.ipc_port):
        with socket.socket() as probe:
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
            probe.bind(("127.0.0.1", port))
    base = ROOT / ".local"
    base.mkdir(exist_ok=True)
    profile = Path(tempfile.mkdtemp(prefix="container-native-", dir=base))
    print(f"Native test profile: {profile}", flush=True)
    mods = profile / "Mods"
    mods.mkdir()
    (mods / "enabled.json").write_text('["CompanionBridge"]')
    shutil.copyfile(args.package, mods / "CompanionBridge.tmod")
    world = profile / "test.wld"
    shutil.copyfile(args.world, world)
    (profile / "isolated-container-fixture").touch()
    clients = []
    bridge = None
    with (profile / "server.log").open("w") as log:
        process = subprocess.Popen(
            [str(args.engine / "dotnet/dotnet.exe"), "exec", "--additionalprobingpath", str(args.engine / "Libraries"), "tModLoader.dll", "-server",
             "-world", str(world), "-port", str(args.port), "-tmlsavedirectory", str(profile),
             "-modpath", str(mods), "-companionserverai", "-companionaiport", str(args.ipc_port),
             "-companionaiipctest", "-companioncontainertest"],
            cwd=args.engine, stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        try:
            def ready():
                if process.poll() is not None:
                    raise RuntimeError(f"Dedicated server exited: {process.returncode}")
                return (profile / "container-ready.json").exists()
            wait_for(ready, 90)
            clients.append(Client(args.port, "ChestObserverA"))
            clients.append(Client(args.port, "ChestObserverB"))
            bridge = ServerAiBridge(port=args.ipc_port)
            report = asyncio.run(verify(profile, bridge, *clients))
            report["result"] = "passed"
            report["engineVersion"] = "2026.9.3.1"
            (profile / "container-results.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
            print(f"Passed with two joined wire clients. Evidence: {profile / 'container-results.json'}")
        finally:
            if bridge:
                bridge.close()
            for client in clients:
                client.close()
            (profile / "g81-test-stop").touch()
            try:
                process.wait(timeout=12)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


if __name__ == "__main__":
    main()
