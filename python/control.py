"""Minimal CompanionBridge client. Python 3.10+, standard library only."""
import argparse
import json
import math
import socket
import sys
import time


class BridgeError(Exception):
    pass


class Bridge:
    def __init__(self, port=18777, timeout=2.0):
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

    def _read(self):
        line = self.reader.readline(4097)
        if not line:
            raise BridgeError("Bridge closed connection (busy, P override, or world/player changed)")
        if len(line) > 4096 or not line.endswith(b"\n"):
            raise BridgeError("Invalid or oversized response")
        value = json.loads(line)
        if not isinstance(value, dict):
            raise BridgeError("Response must be a JSON object")
        return value

    def command(self, cmd, **parameters):
        self.request_id += 1
        request = {"v": 1, "id": self.request_id, "cmd": cmd}
        request.update(parameters)
        try:
            self.sock.sendall((json.dumps(request) + "\n").encode("utf-8"))
            reply = self._read()
        except TimeoutError as error:
            raise BridgeError(f"Request timeout for {cmd}: Bridge handshake completed, but no reply received; "
                              "the game update thread may be paused or stalled. Refocus and retry, "
                              "or increase --timeout") from error
        if reply.get("v") != 1 or reply.get("id") != self.request_id:
            raise BridgeError("Response version/id mismatch")
        if reply.get("ok") is not True:
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
