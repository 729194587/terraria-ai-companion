"""Minimal CompanionBridge client. Python 3.10+, standard library only."""
import argparse
from collections import deque
from concurrent.futures import Future, TimeoutError as FutureTimeout
from contextvars import ContextVar
import threading
import json
import math
import socket
import sys
import time
import agent_trace as tracing
from observation import ObservationValidationError, validate_observation

MAX_RESPONSE_BYTES = 16 * 1024 * 1024
MAX_REQUEST_BYTES = 4096
autonomy_version = ContextVar('autonomy_version', default=None)
command_owner = ContextVar('bridge_command_owner', default=None)


# Local-player operations and the TCP hello flags their execution paths require.
# wait is host-local. The terrain reads predate the atomic protocol.
_CLIENT_ATOMIC_GROUPS = {
    (): ('wait', 'observe_terrain', 'get_player_state'),
    ('viewport_observation_v1',): ('observe_exploration',),
    ('item_info_v1',): ('query_item_info',),
    ('item_sources_v1',): ('query_item_sources',),
    ('atomic_operations_v1',): ('get_world_state', 'inspect_tile', 'scan_entities', 'navigate_to_x',
                                'attack_entity', 'mine_tile', 'place_tile', 'use_item', 'stop_action'),
    ('atomic_operations_v1', 'atomic_extensions_v1'): ('inspect_inventory', 'scan_tiles'),
    ('atomic_operations_v1', 'atomic_extensions_v1', 'server_confirmed_interactions_v1'):
        ('mine_wall', 'place_wall', 'hammer_tile', 'interact_tile'),
    ('atomic_operations_v1', 'atomic_foundations_v1'): ('get_player_status', 'resolve_content'),
    ('atomic_operations_v1', 'hotbar_keys_v1'): ('select_hotbar_slot',),
    ('atomic_operations_v1', 'platform_drop_v1'): ('drop_through_platform',),
    ('atomic_operations_v1', 'landing_region_navigation_v1'): ('navigate_to_landing_region',),
    ('atomic_operations_v1', 'inventory_operations_v1'):
        ('inspect_inventory', 'transfer_inventory_item', 'swap_inventory_slots', 'hold_inventory_item'),
}
CLIENT_ATOMIC_OPERATIONS = frozenset().union(*_CLIENT_ATOMIC_GROUPS.values())


def client_atomic_operations(capabilities):
    caps = set(capabilities)
    return frozenset(name for required, names in _CLIENT_ATOMIC_GROUPS.items()
                     if caps.issuperset(required) for name in names)


class BridgeError(Exception):
    def __init__(self, message, diagnostic=None, status=None):
        super().__init__(message)
        self.diagnostic = diagnostic
        self.status = status


class NotAdmittedError(BridgeError):
    """Positive, identity-checked proof that a start request created no action."""


class Bridge:
    def __init__(self, port=18777, timeout=2.0, on_event=None):
        self.timeout = timeout
        self.on_event = on_event
        self._lock = threading.Lock()
        self._pending = {}
        self._seen = set()
        self._game_seen = deque(maxlen=128)
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
            self.supported_atomic_operations = client_atomic_operations(self.hello.get('capabilities', ()))
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
                if frame.get("type") == "autonomy_command":
                    if (not isinstance(frame.get("eventId"), str) or not frame["eventId"]
                            or frame.get("command") not in ("on", "off", "status")):
                        raise BridgeError("Malformed autonomy command")
                    if self.on_event:
                        self.on_event(frame)
                    continue
                if frame.get("type") == "game_message":
                    if (not isinstance(frame.get("eventId"), str) or not frame["eventId"]
                            or not isinstance(frame.get("text"), str) or not frame["text"].strip() or len(frame["text"]) > 1024
                            or frame.get("source") not in ("chat_helper", "main_new_text", "main_new_text_multiline")
                            or type(frame.get("gameUpdate")) is not int or frame["gameUpdate"] < 0
                            or not isinstance(frame.get("receivedAt"), str)
                            or type(frame.get("textTruncated")) is not bool
                            or type(frame.get("droppedBefore")) is not int or frame["droppedBefore"] < 0):
                        raise BridgeError("Malformed game message event")
                    if frame["eventId"] not in self._game_seen:
                        self._game_seen.append(frame["eventId"])
                        if self.on_event:
                            self.on_event(frame)
                    continue
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
                        local_ai = frame['isSelf'] and frame.get('source') == 'local_ai_command'
                        if (not frame["isSelf"] or local_ai) and self.on_event:
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
        line = self.reader.readline(MAX_RESPONSE_BYTES + 1)
        if not line:
            raise BridgeError("Bridge closed connection (busy, P override, or world/player changed)")
        if len(line) > MAX_RESPONSE_BYTES or not line.endswith(b"\n"):
            raise BridgeError("Invalid, truncated or oversized response (max 16 MiB including newline)")
        value = json.loads(line)
        if not isinstance(value, dict):
            raise BridgeError("Response must be a JSON object")
        if value.get("status") == "observation":
            metrics = value.setdefault("transportMetrics", {})
            if not isinstance(metrics, dict):
                raise BridgeError("Malformed observation transport metrics")
            metrics["receivedBytes"] = len(line)
        return value

    def command(self, cmd, **parameters):
        if getattr(self, '_lua_poisoned', False):
            raise BridgeError('Lua action state unresolved; reconnect required')
        if getattr(self, '_lua_owner', None) is not command_owner.get():
            raise BridgeError('Bridge session is owned by a Lua run')
        future = Future()
        started = time.monotonic()
        request = None
        try:
            with self._lock:
                if self._failure:
                    raise self._failure
                self.request_id += 1
                request = {"v": 1, "id": self.request_id, "cmd": cmd, **parameters}
                wire = (json.dumps(request, ensure_ascii=False) + "\n").encode("utf-8")
                if len(wire) - 1 > MAX_REQUEST_BYTES:
                    raise BridgeError("Request exceeds 4096 UTF-8 bytes")
                self._pending[self.request_id] = future
                tracing.trace.emit("ipc_request", request=request)
                self.sock.sendall(wire)
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
            raise BridgeError(f"Command rejected: {reply.get('status', 'unknown')}", status=reply.get("status", "unknown"))
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

    def get_observation(self):
        if "viewport_observation_v1" not in self.hello.get("capabilities", []):
            raise BridgeError("get_observation requires viewport_observation_v1 capability")
        reply = self.command("get_observation")
        if reply.get("status") != "observation":
            raise BridgeError("Missing observation in response")
        try:
            validate_observation(reply.get("observation"))
        except ObservationValidationError as error:
            tracing.trace.emit("observation_validation_failed", diagnostic=error.diagnostic)
            raise BridgeError(str(error), diagnostic=error.diagnostic) from error
        except (ValueError, TypeError, KeyError) as error:
            raise BridgeError(str(error)) from error
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
