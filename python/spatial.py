"""Deterministic spatial annotations of one validated get_terrain snapshot."""
from collections import Counter
import math


def describe_space(state, terrain):
    x, y = state["position"]["x"], state["position"]["y"]
    width, height = terrain["playerWidth"], terrain["playerHeight"]
    if width <= 0 or height <= 0:
        raise ValueError("Player dimensions must be positive")
    size = terrain["tileSize"]
    origin_x, origin_y = terrain["originX"], terrain["originY"]
    rows = terrain["rows"]
    row_count, column_count = len(rows), len(rows[0])
    center_x, center_y = x + width / 2, y + height / 2

    def point(px, py):
        tx, ty = math.floor(px / size), math.floor(py / size)
        column, row = tx - origin_x, ty - origin_y
        return {"worldPixels": {"x": px, "y": py}, "tile": {"x": tx, "y": ty},
                "grid": {"row": row, "column": column},
                "insideGrid": 0 <= row < row_count and 0 <= column < column_count}

    anchors = {"center": point(center_x, center_y), "feet": point(center_x, y + height),
               "head": point(center_x, y)}
    nearby = []
    counts = Counter()
    for row_index, row in enumerate(rows):
        counts.update(row)
        for column, kind in enumerate(row):
            if kind not in "h1234ab=!?":
                continue
            tx, ty = origin_x + column, origin_y + row_index
            px, py = (tx + 0.5) * size, (ty + 0.5) * size
            dx, dy = px - center_x, py - center_y
            distance = math.hypot(dx, dy)
            if distance <= 96:
                nearby.append({"collectorClass": kind, "grid": {"row": row_index, "column": column},
                    "tile": {"x": tx, "y": ty}, "cellCenterWorldPixels": {"x": px, "y": py},
                    "deltaPixels": {"x": dx, "y": dy},
                    "direction": {"horizontal": "left" if dx < 0 else "right" if dx > 0 else "aligned",
                                  "vertical": "above" if dy < 0 else "below" if dy > 0 else "aligned"},
                    "distancePixels": distance, "distanceTiles": distance / size})
    nearby.sort(key=lambda item: (item["distancePixels"], item["grid"]["row"], item["grid"]["column"]))
    nearby_counts = Counter(item["collectorClass"] for item in nearby)
    expected_origin = {"x": anchors["center"]["tile"]["x"] - 22,
                       "y": anchors["feet"]["tile"]["y"] - 17}
    warnings = []
    if expected_origin != {"x": origin_x, "y": origin_y}:
        warnings.append("collector_origin_mismatch; annotations use the actual returned origin")
    if terrain["supported"] is not True:
        warnings.append("unsupported_player_pose; head/feet are geometric top/bottom, not anatomical verification")
    return {
        "source": "computed only from this get_terrain reply's state and terrain",
        "evidence": "Collector collision classifications; not verified visibility, material, passability or surface height.",
        "coordinates": {
            "world": "Pixels; x increases right, y increases down. player.position is collision-box top-left.",
            "tile": "floor(worldPixel / tileSize) on each axis; not screen coordinates.",
            "grid": "Zero-based rows[row][column]; tileX=originX+column, tileY=originY+row.",
            "anchors": "center=box center; head=top center; feet=bottom center. Sprite/pose offsets are not observed.",
            "relative": "World left/right and above/below relative to player center, not facing direction.",
            "distance": "Euclidean tile-cell-center to player-center distance; not collision gap, surface distance or path length."},
        "playerAnchors": anchors,
        "observedBounds": {"tileMin": {"x": origin_x, "y": origin_y},
            "tileMaxInclusive": {"x": origin_x + column_count - 1, "y": origin_y + row_count - 1},
            "rowCount": row_count, "columnCount": column_count},
        "unknown": {"gridUnknownCount": counts["?"],
            "meaning": "? means unknown/unloaded/unsupported, never air. Outside observedBounds is unobserved. "
                       ". means non-colliding, not necessarily empty air. Legend entries are definitions, not detections."},
        "nearbySpecialTiles": {"radiusPixels": 96, "limit": 16, "matchedCount": len(nearby),
            "countsBeforeLimit": {kind: nearby_counts[kind] for kind in "h1234ab=!?"},
            "truncated": len(nearby) > 16, "items": nearby[:16],
            "scope": "Only h/1/2/3/4/a/b/=/!/? cells within radius; omission does not prove absence outside it or after truncation."},
        "expectedCollectorOrigin": expected_origin, "warnings": warnings}
