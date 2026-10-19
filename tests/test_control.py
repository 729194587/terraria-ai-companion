import json
import socket
import sys
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from control import Bridge, BridgeError, main


class FakeServer:
    def __init__(self, reject=False):
        self.reject = reject
        self.requests = []
        self.error = None
        self.listener = socket.socket()
        self.listener.bind(("127.0.0.1", 0))
        self.listener.listen(1)
        self.listener.settimeout(3)
        self.port = self.listener.getsockname()[1]
        self.thread = threading.Thread(target=self.run, daemon=True)

    def __enter__(self):
        self.thread.start()
        return self

    def run(self):
        try:
            with self.listener.accept()[0] as connection:
                connection.settimeout(3)
                # Deliberately split the hello across writes.
                connection.sendall(b'{"v":1,"type":"hel')
                connection.sendall(b'lo","instance":"offline","lease_ms":750}\n')
                with connection.makefile("rb") as reader:
                    for line in reader:
                        request = json.loads(line)
                        self.requests.append(request)
                        reply = {"v": 1, "id": request["id"], "ok": not self.reject,
                                 "status": "manual_active" if self.reject else "applied",
                                 "mode": "none" if request["cmd"] == "stop" else "external"}
                        connection.sendall((json.dumps(reply) + "\n").encode())
        except Exception as error:
            self.error = error
        finally:
            self.listener.close()

    def __exit__(self, *args):
        self.thread.join(4)
        if self.thread.is_alive():
            raise AssertionError("Fake server did not finish")
        if self.error:
            raise self.error


class ControlTests(unittest.TestCase):
    def test_heartbeat_then_explicit_stop(self):
        with FakeServer() as server:
            with Bridge(server.port) as bridge:
                bridge.move_right(0.25)
        self.assertGreaterEqual(len(server.requests), 3)
        self.assertEqual(server.requests[-1]["cmd"], "stop")
        self.assertTrue(all(r["cmd"] == "move_right" for r in server.requests[:-1]))
        self.assertEqual([r["id"] for r in server.requests], list(range(1, len(server.requests) + 1)))

    def test_rejected_control_is_not_reported_as_applied(self):
        with FakeServer(reject=True) as server:
            with Bridge(server.port) as bridge:
                with self.assertRaisesRegex(BridgeError, "manual_active"):
                    bridge.move_right(0.25)
        self.assertEqual([r["cmd"] for r in server.requests], ["move_right"])

    def test_cli_stop(self):
        with FakeServer() as server:
            self.assertEqual(main(["stop", "--port", str(server.port)]), 0)
        self.assertEqual(server.requests[0]["cmd"], "stop")

    def test_invalid_duration_never_connects(self):
        with patch("control.socket.create_connection") as connect:
            for duration in ("nan", "inf", "-1", "0", "61"):
                with self.assertRaises(SystemExit):
                    main(["move_right", "--seconds", duration])
            connect.assert_not_called()


if __name__ == "__main__":
    unittest.main()
