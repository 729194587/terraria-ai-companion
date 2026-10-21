"""Versioned, bounded observation validation; no game or model connection."""
import math


def _integer(value, minimum=0):
    return type(value) is int and value >= minimum


def _finite(value):
    return type(value) in (int, float) and math.isfinite(value)


def row_indices(row, palette_size, width):
    if not isinstance(row, dict) or not isinstance(row.get("data"), list):
        raise ValueError("Malformed observation tile row")
    data, encoding = row["data"], row.get("encoding")
    if encoding == "indices":
        runs = ((1, index) for index in data)
    elif encoding == "rle" and len(data) % 2 == 0:
        runs = zip(data[::2], data[1::2])
    else:
        raise ValueError("Unsupported observation row encoding")
    count = 0
    for length, index in runs:
        if (not _integer(length, 1) or length > width - count
                or not _integer(index) or index >= palette_size):
            raise ValueError("Invalid observation row count/index")
        count += length
        for _ in range(length):
            yield index
    if count != width:
        raise ValueError("Invalid observation decoded row length")


def validate_observation(value):
    if not isinstance(value, dict) or type(value.get("schemaVersion")) is not int or value["schemaVersion"] != 1:
        raise ValueError("Unsupported observation schema version")
    if (not _integer(value.get("gameUpdate")) or not isinstance(value.get("sampledAt"), str)
            or value.get("phase") != "pre_DoUpdate"
            or value.get("status") not in ("ok", "partial", "stale", "unavailable")
            or not _finite(value.get("samplingMilliseconds")) or value["samplingMilliseconds"] < 0):
        raise ValueError("Malformed observation metadata")
    completeness = value.get("completeness")
    if (not isinstance(completeness, dict) or type(completeness.get("completeWithinScope")) is not bool
            or any(not isinstance(completeness.get(key), list)
                   or any(not isinstance(reason, str) for reason in completeness[key])
                   for key in ("reasons", "limitations"))):
        raise ValueError("Malformed observation completeness")
    if completeness["completeWithinScope"] != (value["status"] == "ok"):
        raise ValueError("Inconsistent observation completeness/status")
    if value["status"] != "unavailable":
        if any(not isinstance(value.get(key), str) or not value[key] for key in ("worldSessionId", "observationId")):
            raise ValueError("Missing observation world/instance identity")
        if not isinstance(value.get("player"), dict) or not isinstance(value.get("environment"), dict):
            raise ValueError("Missing observation player/environment")
    camera = value.get("camera")
    if camera is not None:
        if (not isinstance(camera, dict) or camera.get("status") not in ("recent_render", "stale")
                or not _finite(camera.get("ageMilliseconds")) or camera["ageMilliseconds"] < 0):
            raise ValueError("Malformed observation camera age/status")
        frame = camera.get("frame")
        if (not isinstance(frame, dict) or not _integer(frame.get("renderSequence"), 1)
                or not _integer(frame.get("sourceGameUpdate"))
                or not isinstance(frame.get("worldCorners"), list) or len(frame["worldCorners"]) != 4
                or any(not isinstance(p, dict) or any(not _finite(p.get(axis)) for axis in ("x", "y"))
                       for p in frame["worldCorners"])):
            raise ValueError("Malformed observation camera frame")
    terrain = value.get("terrain")
    if terrain is not None:
        if (not isinstance(terrain, dict) or type(terrain.get("encodingVersion")) is not int
                or terrain["encodingVersion"] != 1 or type(terrain.get("tileSize")) is not int
                or terrain["tileSize"] != 16):
            raise ValueError("Unsupported observation terrain encoding version")
        bounds, palette, rows = terrain.get("bounds"), terrain.get("palette"), terrain.get("rows")
        if (not isinstance(bounds, dict) or any(type(bounds.get(k)) is not int for k in ("x", "y"))
                or not _integer(bounds.get("width"), 1) or not _integer(bounds.get("height"), 1)
                or bounds["width"] * bounds["height"] > 131072
                or not isinstance(palette, list) or not palette or not isinstance(rows, list)
                or len(rows) != bounds["height"] or not isinstance(terrain.get("types"), list)):
            raise ValueError("Malformed observation terrain bounds/palette")
        for cell in palette:
            if (not isinstance(cell, dict) or cell.get("knowledge") not in ("read", "out_of_world", "unloaded")
                    or type(cell.get("hasTile")) is not bool
                    or cell.get("collisionKnowledge") not in ("known", "unknown_mod_hooks")):
                raise ValueError("Malformed observation tile cell")
        unknown = uninterpreted = 0
        for row in rows:
            for index in row_indices(row, len(palette), bounds["width"]):
                unknown += palette[index]["knowledge"] != "read"
                uninterpreted += palette[index]["collisionKnowledge"] != "known"
        if (type(terrain.get("unknownCount")) is not int or terrain["unknownCount"] != unknown
                or type(terrain.get("uninterpretedCount")) is not int or terrain["uninterpretedCount"] != uninterpreted):
            raise ValueError("Inconsistent observation terrain coverage")
    entities = value.get("entities")
    if entities is not None:
        if not isinstance(entities, dict) or not isinstance(entities.get("items"), list):
            raise ValueError("Malformed observation entities")
        coverage = entities.get("coverage")
        if (not isinstance(coverage, dict)
                or any(not _integer(coverage.get(k)) for k in ("matchedCount", "returnedCount", "omittedCount"))
                or coverage["returnedCount"] != len(entities["items"])
                or coverage["matchedCount"] != coverage["returnedCount"] + coverage["omittedCount"]
                or coverage["omittedCount"] and completeness["completeWithinScope"]):
            raise ValueError("Inconsistent observation entity coverage")
        keys = set()
        for entity in entities["items"]:
            if (not isinstance(entity, dict) or entity.get("kind") not in ("player", "npc", "item", "projectile")
                    or not isinstance(entity.get("instanceKey"), str) or entity["instanceKey"] in keys
                    or entity.get("sampleGameUpdate") != value["gameUpdate"] or entity.get("inViewport") is not True):
                raise ValueError("Malformed observation entity identity/sample")
            keys.add(entity["instanceKey"])
            for field, axes in (("boundsWorldPixels", ("x", "y", "width", "height")),
                                ("velocityPixelsPerTick", ("x", "y"))):
                vector = entity.get(field)
                if not isinstance(vector, dict) or any(not _finite(vector.get(axis)) for axis in axes):
                    raise ValueError("Malformed observation entity geometry")
    if value["status"] == "ok" and (camera is None or camera["status"] != "recent_render"
                                     or terrain is None or entities is None
                                     or terrain["unknownCount"] or terrain["uninterpretedCount"]):
        raise ValueError("Incomplete observation marked complete")
    return value
