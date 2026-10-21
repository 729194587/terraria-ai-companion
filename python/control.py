"""Minimal CompanionBridge client. Python 3.10+, standard library only."""
import argparse
from concurrent.futures import Future, TimeoutError as FutureTimeout
import threading
import json
import math
import socket
import sys
import time
import agent_trace as tracing


class BridgeError(Exception):
    pass


class Bridge:
    def __init__(self, port=18777, timeout=2.0, on_event=None):
        self.timeout = timeout
        self.on_event = on_event
        self._lock = threading.Lock()
        self._pending = {}
        self._seen = set()
        self._failure = None
        self._receiver = None
        try:
            self.sock = socket.create_connection(("127.0.0.1", port), timeout=timeout)
        except TimeoutError as error:
            raise BridgeError(f"TCP connection timeout at 127.0.0.1:{port}; check that the intended "
                              "client has CompanionBridge 0.1.5+ loaded and the port is reachable") from error
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.reader = self.sock.makefile("rb")
        self.request_id = 0
        try:
            self.hello = self._read()
            if self.hello.get("v") != 1 or self.hello.get("type") != "hello":
                raise BridgeError("Unsupported Bridge handshake")
        except TimeoutError as error:
            self.close()
            raise BridgeError("Handshake timeout: TCP connected but no Bridge hello received; "
                              "check the loaded Mod version and port owner before diagnosing game pause") from error
        except BaseException:
            self.close()
            raise

        self.sock.settimeout(None)  # Idle event reception has no request deadline.
        self._receiver = threading.Thread(target=self._receive, daemon=True, name="bridge-receive")
        self._receiver.start()

    def _receive(self):
        try:
            while True:
                frame = self._read()
                if frame.get("v") != 1:
                    raise BridgeError("Response version mismatch")
                if frame.get("type") == "chat_message":
                    event_id = frame.get("eventId")
                    if (not isinstance(event_id, str) or not event_id
                            or type(frame.get("senderId")) is not int
                            or not 0 <= frame["senderId"] < 255
                            or not isinstance(frame.get("senderName"), str)
                            or not isinstance(frame.get("text"), str)
                            or type(frame.get("isSelf")) is not bool):
                        raise BridgeError("Malformed chat event")
                    if event_id not in self._seen:
                        self._seen.add(event_id)
                        if not frame["isSelf"] and self.on_event:
                            # Callback must only dispatch, never perform inference here.
                            self.on_event(frame)
                    continue
                with self._lock:
                    future = self._pending.pop(frame.get("id"), None)
                if future is None:
                    raise BridgeError("Response version/id mismatch")
                future.set_result(frame)
        except (OSError, ValueError, BridgeError) as error:
            self._fail(BridgeError(str(error)))

    def _fail(self, error):
        with self._lock:
            self._failure = error
            pending, self._pending = self._pending, {}
        for future in pending.values():
            future.set_exception(error)

    def _read(self):
        line = self.reader.readline(65537)
        if not line:
            raise BridgeError("Bridge closed connection (busy, P override, or world/player changed)")
        if len(line) > 65536 or not line.endswith(b"\n"):
            raise BridgeError("Invalid or oversized response")
        value = json.loads(line)
        if not isinstance(value, dict):
            raise BridgeError("Response must be a JSON object")
        return value

    def command(self, cmd, **parameters):
        future = Future()
        started = time.monotonic()
        request = None
        try:
            with self._lock:
                if self._failure:
                    raise self._failure
                self.request_id += 1
                request = {"v": 1, "id": self.request_id, "cmd": cmd, **parameters}
                self._pending[self.request_id] = future
                tracing.trace.emit("ipc_request", request=request)
                self.sock.sendall((json.dumps(request, ensure_ascii=False) + "\n").encode("utf-8"))
            reply = future.result(timeout=self.timeout)
            tracing.trace.emit("ipc_reply", command=cmd, reply=reply,
                               elapsed_ms=(time.monotonic() - started) * 1000)
        except FutureTimeout as error:
            tracing.trace.emit("ipc_error", command=cmd, request=request, category="timeout",
                               elapsed_ms=(time.monotonic() - started) * 1000)
            self.close()  # No reuse after an ambiguous timeout/late acknowledgement.
            raise BridgeError(f"Request timeout for {cmd}: Bridge handshake completed, but no reply received; "
                              "the game update thread may be paused or stalled. Refocus and retry, "
                              "or increase --timeout") from error
        except OSError as error:
            tracing.trace.emit("ipc_error", command=cmd, request=request, category="network",
                               error_type=type(error).__name__, elapsed_ms=(time.monotonic() - started) * 1000)
            self.close()
            raise BridgeError(str(error)) from error
        except BridgeError as error:
            tracing.trace.emit("ipc_error", command=cmd, request=request, category="bridge",
                               error_type=type(error).__name__, elapsed_ms=(time.monotonic() - started) * 1000)
            raise
        if reply.get("ok") is not True:
            tracing.trace.emit("ipc_error", command=cmd, request=request, category="rejected",
                               reason=reply.get("status", "unknown"),
                               elapsed_ms=(time.monotonic() - started) * 1000)
            raise BridgeError(f"Command rejected: {reply.get('status', 'unknown')}")
        return reply

    def move_right(self, seconds):
        deadline = time.monotonic() + seconds
        first = True
        completed = False
        try:
            while time.monotonic() < deadline:
                reply = self.command("move_right")
                if first:
                    print(f"move_right: {reply['status']}, mode={reply['mode']}", flush=True)
                    first = False
                time.sleep(min(0.1, max(0.0, deadline - time.monotonic())))
            completed = True
        finally:
            # Best effort; EOF and the game-side lease remain the actual safety net.
            try:
                if not first:
                    reply = self.command("stop")
                    print(f"stop: {reply['status']}, mode={reply['mode']}", flush=True)
            except (OSError, BridgeError, ValueError) as error:
                if completed:
                    raise BridgeError(f"Stop not acknowledged; closing connection: {error}") from error

    def get_state(self):
        reply = self.command("get_state")
        state = reply.get("state")
        if reply.get("status") != "state" or not isinstance(state, dict):
            raise BridgeError("Missing game state in response")
        return reply

    def get_terrain(self):
        reply = self.command("get_terrain")
        if reply.get("status") != "terrain" or not isinstance(reply.get("terrain"), dict):
            raise BridgeError("Missing terrain in response (requires CompanionBridge 0.1.6+)")
        return reply

    def input(self, direction, jump=False):
        if type(direction) is not int or direction not in (-1, 0, 1) or type(jump) is not bool:
            raise ValueError("input requires direction=-1/0/1 and boolean jump")
        return self.command("input", direction=direction, jump=jump)

    def get_navigation(self):
        reply = self.command("get_navigation")
        if reply.get("status") != "navigation" or not isinstance(reply.get("navigation"), dict):
            raise BridgeError("Missing navigation feedback (requires CompanionBridge 0.1.8+)")
        return reply

    def terrain_step(self, direction):
        if type(direction) is not int or direction not in (-1, 1):
            raise ValueError("terrain_step requires direction=-1/1")
        return self.command("terrain_step", direction=direction)

    def close(self):
        self._fail(BridgeError("Bridge closed"))
        try:
            self.sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        if self._receiver and threading.current_thread() is not self._receiver:
            self._receiver.join(timeout=1)
        try:
            self.reader.close()
        finally:
            self.sock.close()

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("move_right", "stop", "get_state", "get_terrain"))
    parser.add_argument("--seconds", type=float, default=5.0, help="move duration, 0 < seconds <= 60")
    parser.add_argument("--port", type=int, default=18777)
    parser.add_argument("--timeout", type=float, default=2.0, help="socket timeout in seconds (default: 2)")
    args = parser.parse_args(argv)
    if not math.isfinite(args.seconds) or not 0 < args.seconds <= 60:
        parser.error("--seconds must be finite and between 0 and 60")
    if not 1 <= args.port <= 65535:
        parser.error("--port must be between 1 and 65535")
    if not math.isfinite(args.timeout) or args.timeout <= 0:
        parser.error("--timeout must be finite and positive")
    try:
        with Bridge(args.port, timeout=args.timeout) as bridge:
            print(f"Connected: instance={bridge.hello.get('instance')}, lease={bridge.hello.get('lease_ms')}ms",
                  file=sys.stderr if args.command in ("get_state", "get_terrain") else sys.stdout, flush=True)
            if args.command == "move_right":
                bridge.move_right(args.seconds)
            elif args.command == "get_state":
                print(json.dumps(bridge.get_state(), ensure_ascii=False, indent=2), flush=True)
            elif args.command == "get_terrain":
                print(json.dumps(bridge.get_terrain(), ensure_ascii=False, indent=2), flush=True)
            else:
                print(bridge.command("stop"), flush=True)
        return 0
    except KeyboardInterrupt:
        print("Interrupted; connection closed." if args.command in ("get_state", "get_terrain")
              else "Interrupted; stop attempted and connection closed.", file=sys.stderr)
        return 130
    except (OSError, BridgeError, ValueError) as error:
        print(f"Bridge error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
