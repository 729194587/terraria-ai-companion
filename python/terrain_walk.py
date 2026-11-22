"""Bounded local terrain experiment. No LLM, pathfinding, or external dependencies.

python python/terrain_walk.py --direction right --seconds 20
"""
import argparse
import json
from collections import deque
from dataclasses import dataclass
import math
from pathlib import Path
import sys
import time

from control import Bridge, BridgeError
from spatial import liquid_tiles


class SafetyStop(Exception):
    """Observation or terrain falls outside this small experiment's supported cases."""

    def __init__(self, message, category="terrain", code=None):
        super().__init__(message)
        self.category = category
        self.code = code


def number(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise SafetyStop("invalid numeric observation")
    return value


class View:
    def __init__(self, reply):
        if reply.get("mode") not in ("none", "external"):
            raise SafetyStop("manual control active", "runtime")
        try:
            state, terrain = reply["state"], reply["terrain"]
            if state["inWorld"] is not True or terrain["supported"] is not True:
                raise SafetyStop("player/world outside supported ordinary walking state", "runtime")
            self.x = number(state["position"]["x"])
            self.y = number(state["position"]["y"])
            self.vx = number(state["velocity"]["x"])
            self.vy = number(state["velocity"]["y"])
            self.life = number(state["life"])
            self.width, self.height = terrain["playerWidth"], terrain["playerHeight"]
            self.left, self.top = terrain["originX"], terrain["originY"]
            self.rows = terrain["rows"]
            self.liquids = liquid_tiles(terrain)
            self.grounded = terrain["grounded"]
            if (self.width != 20 or self.height != 42 or terrain["tileSize"] != 16
                    or type(self.left) is not int or type(self.top) is not int
                    or type(self.grounded) is not bool or not isinstance(self.rows, list)
                    or len(self.rows) != 29
                    or any(not isinstance(row, str) or len(row) != 45
                           or set(row) - set(".#=h1234abs!?") for row in self.rows)):
                raise SafetyStop("invalid terrain window", "runtime")
        except (KeyError, TypeError, AttributeError, ValueError) as error:
            raise SafetyStop("missing terrain observation", "runtime") from error
        if self.life <= 0 or abs(self.vx) > 4.1 or abs(self.vy) > 12:
            raise SafetyStop("dead player or speed outside tested walking envelope", "runtime")
        self.bottom = self.y + self.height
        self.supports = {}

    def cell(self, x, y):
        column, row = x - self.left, y - self.top
        if not 0 <= row < len(self.rows) or not 0 <= column < len(self.rows[0]):
            raise SafetyStop("terrain outside observed window")
        cell = self.rows[row][column]
        if cell != "?" and (x, y) in self.liquids:
            cell = "!"
        if cell in "s!?":
            kind = {"s": "untyped slope (requires Bridge 0.1.7+)",
                    "!": "liquid or marked hazard", "?": "unknown terrain"}[cell]
            distance = max(x * 16 - (self.x + self.width), self.x - (x + 1) * 16, 0)
            raise SafetyStop(f"{kind} at tile ({x},{y}), {distance:.0f}px horizontally from player",
                             "immediate_danger")
        return cell

    def clear(self, x, top, bottom):
        # Platforms are one-way: do not treat them as side walls or ceilings.
        for y in range(math.floor(top / 16), math.ceil(bottom / 16)):
            cell = self.cell(x, y)
            if cell in "#h1234":
                left, right = self.span(x)
                low = left if cell == "1" else 16 - right if cell == "2" else 8 if cell == "h" else 0
                high = 16 - left if cell == "3" else right if cell == "4" else 16
                if y * 16 + low < bottom and y * 16 + high > top:
                    raise SafetyStop(f"wall or insufficient headroom at tile ({x},{y}); "
                                     f"body interval [{top:.1f},{bottom:.1f}]")

    def span(self, x):
        left = max(0, self.x - x * 16)
        right = min(16, self.x + self.width - x * 16)
        return (left, right) if left < right else (0, 16)

    def surface(self, x, baseline):
        for y in range(math.floor((baseline - 32) / 16), math.floor((baseline + 16) / 16) + 1):
            cell = self.cell(x, y)
            if cell not in "#h=1234ab":
                continue
            # Use the actual overlap when this column is under the body. Future columns
            # use the whole tile's highest support point for conservative clearance.
            left, right = self.span(x)
            offset = left if cell in "1a" else 16 - right if cell in "2b" else 8 if cell == "h" else 0
            surface = y * 16 + offset
            # A raised platform can be passed from the side; it is not a step to jump over.
            if cell in "=ab" and surface < baseline - 1:
                # Flat elevated platforms can be passed from the side. Sloped platforms
                # are stair surfaces only when their low end joins the current ground.
                if cell == "=" or y * 16 + 16 < baseline - 1:
                    continue
            if baseline - 32 <= surface <= baseline + 16:
                self.supports[x] = (cell, y * 16)
                self.clear(x, surface - self.height, surface)
                return surface
        raise SafetyStop(f"gap or drop deeper than one tile at column {x}; reference={baseline:.1f}",
                         code="no_support")

    def edges(self, x):
        cell, top = self.supports[x]
        if cell in "1a":
            return top, top + 16
        if cell in "2b":
            return top + 16, top
        return (top + (8 if cell == "h" else 0),) * 2

    def check_immediate(self):
        # Body and its contact surface are checked even while airborne. Do not include
        # buried tiles or distant, unrelated parts of the walking forecast here.
        for x in range(math.floor(self.x / 16), math.ceil((self.x + self.width) / 16)):
            for y in range(math.floor(self.y / 16), math.ceil((self.bottom + 2) / 16)):
                self.cell(x, y)

    def front_reference(self, first, direction, baseline):
        # On a descending slope the rear of a 20px body may still support it
        # while its front is already >16px lower along the slope. Bridge that
        # actual footprint using connected edges, not a larger drop allowance.
        start = math.floor(self.x / 16) if direction > 0 else math.ceil((self.x + self.width) / 16) - 1
        reference, connected = baseline, False
        for x in range(start, first, direction):
            try:
                surface = self.surface(x, reference)
            except SafetyStop as error:
                if error.code != "no_support":
                    raise
                connected = False
                continue
            left, right = self.edges(x)
            entry, exit = (left, right) if direction > 0 else (right, left)
            if abs(surface - baseline) <= 2 or (connected and abs(entry - reference) <= 1):
                reference, connected = exit, True
            else:
                connected = False
        return reference if connected else baseline

    def connected_ramp(self, first, count, direction, baseline):
        """Only a continuous 45-degree floor/stair joined to adjacent support is walkable."""
        previous = baseline
        found = False
        for i in range(count):
            x = first + direction * i
            cell, _ = self.supports[x]
            left, right = self.edges(x)
            entry, exit = (left, right) if direction > 0 else (right, left)
            tolerance = 16 if i == 0 and cell in "12ab" else 1
            if abs(entry - previous) > tolerance:
                return False
            found |= cell in "12ab"
            previous = exit
        return found


@dataclass(frozen=True)
class Decision:
    direction: int
    jump: bool
    reason: str


class Walker:
    """One fixed direction, one bounded jump attempt per obstacle; unknown means stop."""
    def __init__(self, direction=1):
        self.direction = direction
        self.baseline = None
        self.jump_started = None
        self.jump_end = None
        self.jump_landing_y = None
        self.jump_columns = []
        self.airborne = False
        self.progress = deque()
        self.life = None
        self.forecast = None

    def diagnostic(self):
        return {**self.__dict__, "progress": list(self.progress)}

    @staticmethod
    def stopping_distance(view):
        # World pixels, 60 game ticks/s. Budget the 250ms RPC cutoff plus one
        # 100ms loop, continued acceleration up to 0.1px/tick² (capped at the
        # supported 4.1px/tick), 16px clearance, and ground deceleration of
        # 0.1px/tick². This is an ordinary-ground envelope, not an engine guarantee.
        reaction_ticks = 60 * 0.35
        speed = min(4.1, abs(view.vx) + 0.1 * reaction_ticks)
        return max(64, 16 + speed * reaction_ticks + speed * speed / (2 * 0.1))

    def decide(self, reply, now):
        view = View(reply)
        if self.life is not None and view.life < self.life:
            raise SafetyStop("life decreased", "immediate_danger")
        self.life = view.life
        view.check_immediate()
        if self.baseline is None and not view.grounded:
            raise SafetyStop("start on supported ground")
        if self.jump_started is None and view.grounded:
            self.baseline = view.bottom
        if self.jump_started is None and abs(view.bottom - self.baseline) > 16:
            raise SafetyStop("unexpected fall or vertical movement")

        front = view.x + view.width if self.direction > 0 else view.x
        first = math.floor(front / 16) if self.direction > 0 else math.ceil(front / 16) - 1
        if self.jump_started is not None:
            # Validate the active action, not a new 17-column walking route. Newly
            # unsafe landing tiles still stop the action; unrelated far walls do not.
            for x in self.jump_columns:
                landing = view.surface(x, self.jump_landing_y)
                if abs(landing - self.jump_landing_y) > 1:
                    raise SafetyStop("jump landing terrain changed", "jump_failed")
            end = self.jump_end + self.direction * view.width
            for x in range(math.floor(min(front, end) / 16), math.ceil(max(front, end) / 16)):
                for y in range(math.floor(view.y / 16), math.ceil(view.bottom / 16)):
                    view.cell(x, y)
            elapsed = now - self.jump_started
            self.airborne |= not view.grounded
            if elapsed > 1.8:
                raise SafetyStop("jump did not finish in time", "jump_failed")
            if view.grounded and elapsed > 0.1 and self.airborne:
                trailing = view.x if self.direction > 0 else view.x + view.width
                if self.direction * (trailing - self.jump_end) < 0:
                    raise SafetyStop("jump landed before clearing obstacle; no retry", "jump_failed")
                if abs(view.bottom - self.jump_landing_y) > 1:
                    raise SafetyStop("jump landed at an unexpected height; no retry", "jump_failed")
                self.jump_started = None
                self.baseline = view.bottom
                self.progress.clear()
                return Decision(self.direction, False, "landed")
            if not self.airborne and elapsed > 0.4:
                raise SafetyStop("jump failed to take off; no retry", "jump_failed")
            return Decision(self.direction, elapsed < 0.2, "jump in progress")

        surfaces = []
        reference = view.front_reference(first, self.direction, self.baseline) if view.grounded else self.baseline
        ramp_reference = reference
        def read_surface(index):
            nonlocal reference
            while len(surfaces) <= index:
                x = first + self.direction * len(surfaces)
                # Follow adjacent support heights rather than the initial foot height.
                for y in range(math.floor((reference - view.height) / 16),
                               math.floor((reference + 32) / 16) + 1):
                    view.cell(x, y)
                try:
                    surface = view.surface(x, reference)
                except SafetyStop as error:
                    # A two-tile obstacle can return to the original ground: it
                    # needs the existing jump planner, not a walking drop allowance.
                    if error.code != "no_support" or not 0 < self.baseline - reference <= 32:
                        raise
                    surface = view.surface(x, self.baseline)
                surfaces.append(surface)
                left, right = view.edges(x)
                reference = right if self.direction > 0 else left
            return surfaces[index]

        budget = self.stopping_distance(view)
        near_count = 0
        self.forecast = None
        for i in range(17):
            x = first + self.direction * i
            edge = (x + (1 if self.direction < 0 else 0)) * 16
            distance = max(0, self.direction * (edge - front))
            try:
                read_surface(i)
            except SafetyStop as error:
                self.forecast = {"distance": distance, "reason": str(error), "budget": budget}
                if distance <= budget:
                    raise SafetyStop(f"{error}; distance={distance:.0f}px, stop budget={budget:.0f}px",
                                     error.category) from error
                break
            if distance <= budget:
                near_count += 1

        walk_ramp = view.connected_ramp(first, near_count, self.direction, ramp_reference)
        raised = [i for i in range(near_count) if surfaces[i] < self.baseline - 1]

        # Lack of forward progress is terminal, not an instruction to repeat jumping forever.
        self.progress.append((now, self.direction * view.x))
        while len(self.progress) > 1 and self.progress[1][0] <= now - 1:
            self.progress.popleft()
        if now - self.progress[0][0] >= 1 and self.direction * view.x - self.progress[0][1] < 4:
            raise SafetyStop("no forward progress for one second")

        if walk_ramp:
            return Decision(self.direction, False, "walk slope")

        if raised:
            start = end = raised[0]
            surface_at = read_surface

            # A newly seen obstacle may straddle the walking lookahead boundary. Use
            # the remaining *observed* tiles to confirm its end and four landing columns;
            # do not confuse a shorter checked prefix with missing terrain data.
            while end - start < 2 and surface_at(end + 1) < self.baseline - 1:
                end += 1
            if end - start < 2:
                # Short obstacle: land back on the original ground after crossing it.
                if any(abs(surface_at(i) - self.baseline) > 1 for i in range(end + 1, end + 5)):
                    raise SafetyStop("no confirmed flat landing after obstacle")
                landing_y = self.baseline
                landing_indices = range(end + 1, end + 5)
                target = (first + self.direction * end) * 16 + (16 if self.direction > 0 else 0)
                jump_reason = "try one low obstacle jump"
                clearance_end = end + 4
            else:
                # A low rise with a flat top is a place to land, not a wall whose
                # entire width must be jumped. Permit one half-block transition.
                landing_start = None
                for candidate in (start, start + 1):
                    level = surface_at(candidate)
                    if (self.baseline - 32 <= level < self.baseline - 1
                            and all(abs(surface_at(i) - level) <= 1 for i in range(candidate, candidate + 4))):
                        landing_start, landing_y = candidate, level
                        break
                if landing_start is None:
                    raise SafetyStop("raised ground has no confirmed flat top landing")
                landing_indices = range(landing_start, landing_start + 4)
                target = (first + self.direction * landing_start) * 16 + (16 if self.direction < 0 else 0)
                jump_reason = "try one low step-up jump"
                clearance_end = landing_start + 3
            edge = (first + self.direction * start) * 16 + (16 if self.direction < 0 else 0)
            distance = self.direction * (edge - front)
            if distance <= 40:
                if not view.grounded:
                    raise SafetyStop("cannot start jump without ground support")
                # Check space for a normal jump, above the current body and obstacle.
                for i in range(-2, clearance_end + 1):
                    view.clear(first + self.direction * i,
                               self.baseline - view.height - 80, self.baseline - 32)
                self.jump_started, self.airborne = now, False
                self.jump_end, self.jump_landing_y = target, landing_y
                self.jump_columns = [first + self.direction * i for i in landing_indices]
                return Decision(self.direction, True, jump_reason)
        return Decision(self.direction, False, "walk")


def run(bridge, direction=1, seconds=20, *, clock=time.monotonic, sleep=time.sleep, log=print,
        failure_log=None):
    walker = Walker(direction)
    deadline = clock() + seconds
    owned = False
    usable = True
    last_reason = None
    reply, before, decision_time = None, None, None
    recent = deque(maxlen=8)
    failure = None

    def stopped(category, reason):
        nonlocal failure
        failure = {"category": category, "reason": reason, "snapshot": reply,
                   "walker_before": before, "decision_time": decision_time,
                   "recent_decisions": list(recent)}
        return f"{category}: {reason}"

    try:
        while clock() < deadline:
            started = clock()
            reply = bridge.get_terrain()
            if reply.get("mode") == "manual":
                owned = False
                return stopped("runtime", "manual control active")
            if clock() - started > 0.25 or clock() >= deadline:
                return stopped("runtime", "observation late or runtime limit reached")
            before, decision_time = walker.diagnostic(), clock()
            decision = walker.decide(reply, decision_time)
            recent.append({"time": decision_time, "state": reply.get("state"),
                           "decision": decision.__dict__, "forecast": walker.forecast})
            bridge.input(decision.direction, decision.jump)
            owned = True  # Only an acknowledged input grants ownership.
            if clock() - started > 0.25:
                return stopped("runtime", "control round trip too slow")
            if decision.reason != last_reason:
                log(f"{decision.reason}; stop budget={walker.stopping_distance(View(reply)):.0f}px; "
                    f"forecast={walker.forecast}")
                last_reason = decision.reason
            sleep(max(0, min(0.1 - (clock() - started), deadline - clock())))
        return "runtime: runtime limit reached"
    except SafetyStop as error:
        return stopped(error.category, str(error))
    except (BridgeError, OSError, ValueError) as error:
        # P and lifecycle changes close the original session. Never reconnect or send stop
        # after a rejected input: explicit stop is allowed to cancel manual mode.
        usable = False
        return stopped("runtime", f"Bridge failure: {error}")
    except KeyboardInterrupt:
        stopped("runtime", "keyboard interrupt")
        raise
    finally:
        try:
            if owned and usable:
                try:
                    bridge.command("stop")
                except (BridgeError, OSError, ValueError):
                    pass
        finally:
            bridge.close()  # EOF releases external input even when a stop cannot be acknowledged.
            # Write only after releasing control. A diagnostic I/O error must not
            # interfere with stop/EOF or obscure the original failure.
            if failure is not None and failure_log is not None:
                try:
                    path = Path(failure_log)
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_text(json.dumps(failure, ensure_ascii=False, indent=2), encoding="utf-8")
                    log(f"Failure snapshot: {path}")
                except OSError as error:
                    log(f"Could not save failure snapshot: {error}")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--direction", choices=("right", "left"), default="right")
    parser.add_argument("--seconds", type=float, default=20)
    parser.add_argument("--port", type=int, default=18777)
    parser.add_argument("--failure-log", default=".local/terrain-walk-failure.json",
                        help="latest failed observation and decision context (overwritten)")
    parser.add_argument("--replay", type=Path, help="re-evaluate a saved failed decision without IPC")
    args = parser.parse_args(argv)
    if not math.isfinite(args.seconds) or not 0 < args.seconds <= 60:
        parser.error("--seconds must be finite and between 0 and 60")
    if not 1 <= args.port <= 65535:
        parser.error("--port must be between 1 and 65535")
    try:
        if args.replay is not None:
            record = json.loads(args.replay.read_text(encoding="utf-8"))
            if record.get("category") == "runtime" or record.get("walker_before") is None:
                print("Runtime interruption; no terrain decision to replay:", record.get("reason"))
                return 0
            walker = Walker()
            walker.__dict__.update(record["walker_before"])
            walker.progress = deque(walker.progress)
            try:
                print(walker.decide(record["snapshot"], record["decision_time"]))
            except SafetyStop as error:
                print(f"Stopped: {error.category}: {error}")
            return 0
        # Keep observation latency below the movement lease; late data never starts a new action.
        bridge = Bridge(args.port, timeout=0.25)
        reason = run(bridge, 1 if args.direction == "right" else -1, args.seconds,
                     failure_log=args.failure_log)
        print("Stopped:", reason)
        return 1 if "Bridge failure:" in reason else 0
    except (BridgeError, OSError, ValueError, KeyError, TypeError) as error:
        print(f"Terrain walk error: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("Interrupted; connection closed.", file=sys.stderr)
        return 130


if __name__ == "__main__":
    sys.exit(main())
