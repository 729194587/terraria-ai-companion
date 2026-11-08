"""One bounded Agent-side recovery; never moves inside the mining adapter.

Only a fully observed level corridor without solid body obstructions is accepted. Navigation
still plans and verifies the actual landing; unknown or complex routes stop.
"""
import math
import atomic_operations as atomic
from spatial import liquid_tiles


def eligible(result):
    end = result.get("endEvidence")
    return (result.get("valid") is True and result.get("status") == "failed"
            and result.get("reason") == "out_of_conservative_80_pixel_reach"
            and isinstance(end, dict) and bool(end.get("worldSessionId")))


def _player(reply):
    p, t = reply["state"], reply["terrain"]
    pos, vel = p["position"], p["velocity"]
    values = [pos["x"], pos["y"], vel["x"], vel["y"], t["playerWidth"], t["playerHeight"]]
    if (reply.get("valid") is not True or p.get("inWorld") is not True or not p.get("life", 0) > 0
            or t.get("grounded") is not True or t.get("supported") is not True
            or any(type(v) not in (int, float) or not math.isfinite(v) for v in values)
            or abs(vel["x"]) > .1 or abs(vel["y"]) > .1
            or (t["playerWidth"], t["playerHeight"]) != (20, 42)):
        raise ValueError("player_not_safe_settled_supported")
    return pos["x"], pos["y"], t


def _tile(reply, args, world, update, tile_type):
    data = reply.get("data", {})
    cell = data.get("cell", {})
    if (reply.get("valid") is not True or reply.get("worldSessionId") != world
            or type(reply.get("gameUpdate")) is not int or reply["gameUpdate"] < update
            or data.get("x") != args["x"] or data.get("y") != args["y"]
            or cell.get("knowledge") != "read" or cell.get("hasTile") is not True
            or cell.get("tileType") != tile_type or cell.get("collisionKnowledge") != "known"
            or cell.get("liquidAmount", 0) != 0
            or any(cell.get(k, False) for k in ("hot", "bleeding", "destroyOnTouch", "boulder"))
            or cell.get("touchDamage", 0) != 0):
        raise ValueError("target_changed_unknown_or_unsafe")


def _landing(x, y, t, tx, ty):
    # A flat one-way platform is not a solid side/body obstruction. Native
    # navigation validates its actual collision; solid/unsafe/unknown cells stop.
    # Verify the whole landing band and route, not only one selected point.
    feet = y + 42
    if abs(feet / 16 - round(feet / 16)) > .001 or t.get("tileSize") != 16:
        raise ValueError("no_proven_level_approach")
    rows, ox, oy = t["rows"], t["originX"], t["originY"]
    liquids = liquid_tiles(t)
    def cell(cx, cy):
        ix, iy = cx - ox, cy - oy
        if (cx, cy) in liquids:
            return "!"
        return rows[iy][ix] if 0 <= iy < len(rows) and 0 <= ix < len(rows[iy]) else "?"
    dy = y + 21 - ty
    if abs(dy) >= 72:
        raise ValueError("target_needs_unproven_vertical_route")
    dx = math.sqrt(72 ** 2 - dy ** 2)
    direction = 1 if tx > x + 10 else -1
    candidate = tx - direction * dx - 10
    candidate = math.ceil(candidate) if direction > 0 else math.floor(candidate)
    # A point goal usually cannot be hit by discrete native acceleration/braking.
    # Use up to one tile of tolerance, entirely inside the 72px reach circle.
    if direction > 0:
        lower, upper = candidate, min(candidate + 16, math.floor(tx + dx - 10))
    else:
        lower, upper = max(candidate - 16, math.ceil(tx - dx - 10)), candidate
    if upper - lower < 4:
        raise ValueError("no_usable_landing_region")
    if max(abs(lower - x), abs(upper - x)) > 640:
        raise ValueError("approach_outside_local_scope")
    left, right = min(x, lower), max(x, upper) + 20
    for cx in range(math.floor(left / 16), math.floor((right - .01) / 16) + 1):
        if cell(cx, round(feet / 16)) not in ("#", "="):
            raise ValueError("no_proven_safe_support")
        for cy in range(math.floor(y / 16), math.floor((feet - .01) / 16) + 1):
            if cell(cx, cy) not in (".", "="):
                raise ValueError("approach_blocked_hazardous_or_unknown")
    return {"minPlayerLeftX": lower, "maxPlayerLeftX": upper, "surfaceY": feet}


async def recover(args, failure, invoke):
    """invoke uses normal atomic dispatch/session/cancel/lease protections; <=6 calls."""
    steps = []
    async def call(name, arguments):
        result = await invoke(name, arguments)
        steps.append({"operation": name, "status": result.get("status"), "reason": result.get("reason")})
        return result
    def stopped(reason):
        return {**failure, "reason": "mining_recovery_stopped: " + reason,
                "recovery": {"attempted": True, "retryCount": 0, "steps": steps}}
    try:
        atomic.validate("mine_tile", args)
        end, evidence = failure["endEvidence"], failure.get("evidence") or {}
        tile_type = evidence.get("targetTileType")
        if (type(tile_type) is not int or tile_type < 0
                or evidence.get("targetTileX") != args["x"] or evidence.get("targetTileY") != args["y"]):
            return stopped("missing_original_target_evidence")
        world, update = end["worldSessionId"], end["gameUpdate"]
        before = await call("observe_terrain", {})
        x, y, terrain = _player(before)
        target = await call("inspect_tile", args)
        _tile(target, args, world, update, tile_type)
        tx, ty = args["x"] * 16 + 8, args["y"] * 16 + 8
        landing = _landing(x, y, terrain, tx, ty)
        nav = await call("navigate_to_landing_region", landing)
        if nav.get("valid") is not True or nav.get("status") != "completed":
            return {**nav, "recovery": {"attempted": True, "retryCount": 0, "steps": steps}}
        nav_end = nav.get("endEvidence") or {}
        if nav_end.get("worldSessionId") != world:
            return stopped("world_changed_or_missing_navigation_release")
        after = await call("observe_terrain", {})
        x, y, _ = _player(after)
        target = await call("inspect_tile", args)
        _tile(target, args, world, nav_end["gameUpdate"], tile_type)
        if math.hypot(x + 10 - tx, y + 21 - ty) > 80:
            return stopped("still_out_of_reach")
        retry = await call("mine_tile", args)
        return {**retry, "recovery": {"attempted": True, "retryCount": 1, "steps": steps}}
    except (ValueError, KeyError, TypeError, IndexError) as error:
        return stopped(str(error))
