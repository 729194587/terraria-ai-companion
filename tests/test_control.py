import json
import io
import socket
import sys
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from control import Bridge, BridgeError, main


class FakeServer:
    def __init__(self, reject=False, in_world=True, silent=False, silent_hello=False):
        self.reject = reject
        self.in_world = in_world
        self.silent = silent
        self.silent_hello = silent_hello
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
                if self.silent_hello:
                    # Wait for the timed-out client to close without sending hello.
                    connection.recv(1)
                    return
                # Deliberately split the hello across writes.
                connection.sendall(b'{"v":1,"type":"hel')
                connection.sendall(b'lo","instance":"offline","lease_ms":750}\n')
                with connection.makefile("rb") as reader:
                    for line in reader:
                        request = json.loads(line)
                        self.requests.append(request)
                        if self.silent:
                            continue
                        reply = {"v": 1, "id": request["id"], "ok": not self.reject,
                                 "status": "manual_active" if self.reject else "applied",
                                 "mode": "none" if request["cmd"] == "stop" else "external"}
                        if request["cmd"] == "get_state":
                            reply.update(status="state", mode="none", state={
                                "inWorld": self.in_world,
                                "position": {"x": 100.5, "y": 200.0} if self.in_world else None,
                                "velocity": {"x": -1.5, "y": 0.0} if self.in_world else None,
                                "life": 80 if self.in_world else None,
                                "lifeMax": 100 if self.in_world else None})
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

    def test_cli_get_state_world_and_menu(self):
        for in_world in (True, False):
            with self.subTest(in_world=in_world), FakeServer(in_world=in_world) as server:
                with patch("sys.stdout", new_callable=io.StringIO) as output:
                    self.assertEqual(main(["get_state", "--port", str(server.port)]), 0)
                reply = json.loads(output.getvalue())
                self.assertEqual(reply["state"]["inWorld"], in_world)
                self.assertEqual(reply["state"]["life"], 80 if in_world else None)
                self.assertEqual(reply["state"]["lifeMax"], 100 if in_world else None)
                self.assertEqual(reply["state"]["position"], {"x": 100.5, "y": 200.0} if in_world else None)
                self.assertEqual(reply["state"]["velocity"], {"x": -1.5, "y": 0.0} if in_world else None)
            self.assertEqual(server.requests, [{"v": 1, "id": 1, "cmd": "get_state"}])

    def test_cli_request_timeout(self):
        with FakeServer(silent=True) as server, patch("sys.stderr", new_callable=io.StringIO) as errors:
            self.assertEqual(main(["get_state", "--port", str(server.port), "--timeout", "0.1"]), 1)
            self.assertIn("Request timeout for get_state", errors.getvalue())
            self.assertIn("Connected:", errors.getvalue())

    def test_cli_handshake_timeout(self):
        with FakeServer(silent_hello=True) as server, patch("sys.stderr", new_callable=io.StringIO) as errors:
            self.assertEqual(main(["get_state", "--port", str(server.port), "--timeout", "0.1"]), 1)
            self.assertIn("Handshake timeout", errors.getvalue())
            self.assertNotIn("Connected:", errors.getvalue())
            self.assertEqual(server.requests, [])

    def test_cli_connect_timeout(self):
        with patch("control.socket.create_connection", side_effect=TimeoutError), \
                patch("sys.stderr", new_callable=io.StringIO) as errors:
            self.assertEqual(main(["get_state"]), 1)
            self.assertIn("TCP connection timeout", errors.getvalue())
            self.assertNotIn("game update thread", errors.getvalue())

    def test_cli_connection_failure(self):
        with patch("control.socket.create_connection", side_effect=ConnectionRefusedError("refused")), \
                patch("sys.stderr", new_callable=io.StringIO) as errors:
            self.assertEqual(main(["get_state"]), 1)
            self.assertIn("refused", errors.getvalue())

    def test_get_state_requires_snapshot(self):
        with FakeServer() as server, Bridge(server.port) as bridge:
            with patch.object(bridge, "command", return_value={"status": "state"}):
                with self.assertRaisesRegex(BridgeError, "Missing game state"):
                    bridge.get_state()

    def test_invalid_timeout_never_connects(self):
        with patch("control.socket.create_connection") as connect:
            for timeout in ("nan", "inf", "-1", "0"):
                with self.assertRaises(SystemExit):
                    main(["get_state", "--timeout", timeout])
            connect.assert_not_called()

    def test_invalid_duration_never_connects(self):
        with patch("control.socket.create_connection") as connect:
            for duration in ("nan", "inf", "-1", "0", "61"):
                with self.assertRaises(SystemExit):
                    main(["move_right", "--seconds", duration])
            connect.assert_not_called()


if __name__ == "__main__":
    unittest.main()
