"""Exploration-only projection of one validated M1.9 snapshot; no extra game reads."""
import copy
import math

from observation import row_indices, validate_observation


# Omitted region fields have these declared values, never inferred for unread cells.
CELL_DEFAULTS = dict(hasTile=False, halfBlock=False, slope=0, solid=False, solidTop=False,
                     actuated=False, actuator=False, wireMask=0, liquidType=0, liquidAmount=0,
                     touchDamage=0, hot=False, bleeding=False, destroyOnTouch=False, boulder=False)
PLAYER_FIELDS = ("id", "positionWorldPixels", "boundsWorldPixels", "velocityPixelsPerTick",
                 "life", "lifeMax", "dead", "ghost", "mount")
MOVEMENT_FIELDS = ("gravDir", "wet", "lavaWet", "honeyWet", "shimmerWet", "pulley", "grapCount",
                   "frozen", "webbed", "stoned", "sloping", "stairFall", "jump", "controlDown", "controlJump",
                   "runAcceleration", "maxRunSpeed", "gravity", "maxFallSpeed", "contactStatus")


def _state(cell):
    state = {"knowledge": cell["knowledge"]}
    if cell["knowledge"] != "read":
        return state  # Default collision/liquid values are NOT applicable here.
    state["collisionKnowledge"] = cell["collisionKnowledge"]
    for field, default in CELL_DEFAULTS.items():
        if field not in cell:
            raise ValueError("Exploration source missing tile property: " + field)
        if cell[field] != default:
            state[field] = cell[field]
    if cell["collisionKnowledge"] != "known":
        state["typeId"] = cell.get("tileType")
        state["frame"] = [cell.get("frameX"), cell.get("frameY")]
    return state


def _terrain(terrain, player):
    if terrain is None:
        return None
    bounds = terrain["bounds"]
    palette = terrain["palette"]
    states = [_state(cell) for cell in palette]
    # Merge equal semantic horizontal spans vertically. No palette indices or cell grid is returned.
    regions, active, rows = [], {}, []
    for offset, row in enumerate(terrain["rows"]):
        raw = list(row_indices(row, len(palette), bounds["width"]))
        rows.append(raw)
        spans = []
        start = 0
        while start < len(raw):
            end = start + 1
            while end < len(raw) and states[raw[end]] == states[raw[start]]:
                end += 1
            spans.append((bounds["x"] + start, end - start, states[raw[start]]))
            start = end
        current = {}
        for x, width, state in spans:
            key = (x, width, tuple((k, tuple(v) if isinstance(v, list) else v) for k, v in state.items()))
            if key in active:
                region = active[key]
                region[3] += 1
            else:
                region = [x, bounds["y"] + offset, width, 1, state]
                regions.append(region)
            current[key] = region
        active = current
    support = []
    box = player.get("boundsWorldPixels") if player else None
    if box:
        feet_y = math.floor((box["y"] + box["height"]) / terrain["tileSize"])
        for column in range(bounds["width"]):
            ground, status = None, "no_support_observed_to_viewport_bottom"
            if player.get("movement", {}).get("gravDir") != 1:
                status = "unsupported_or_unknown_gravity"
            elif not bounds["y"] <= feet_y < bounds["y"] + bounds["height"]:
                status = "feet_outside_sample"
            else:
                for y in range(feet_y - bounds["y"], bounds["height"]):
                    cell = palette[rows[y][column]]
                    if cell["knowledge"] != "read" or cell["collisionKnowledge"] != "known":
                        status = "unknown_before_support"
                        break
                    if not cell["actuated"] and (cell["solid"] or cell["solidTop"]):
                        ground, status = bounds["y"] + y, "potential_support_tile"
                        break
            x = bounds["x"] + column
            if support and support[-1][2:] == [ground, status]:
                support[-1][1] += 1
            else:
                support.append([x, 1, ground, status])
    underfoot = None
    if box:
        center_x = box["x"] + box["width"] / 2
        feet = box["y"] + box["height"]
        feet_row = math.floor(feet / terrain["tileSize"])
        first_x = math.floor(box["x"] / terrain["tileSize"])
        last_x = math.ceil((box["x"] + box["width"]) / terrain["tileSize"]) - 1
        candidates = []
        normal_gravity = player.get("movement", {}).get("gravDir") == 1
        if normal_gravity:
            for x in range(first_x, min(last_x + 1, first_x + 8)):
                cx, cy = x - bounds["x"], feet_row - bounds["y"]
                entry = {"x": x, "y": feet_row}
                if 0 <= cx < bounds["width"] and 0 <= cy < bounds["height"]:
                    raw = palette[rows[cy][cx]]
                    entry["stateOverrides"] = _state(raw)
                    if raw["knowledge"] == "read":
                        entry.update(hasTile=raw["hasTile"], tileType=raw.get("tileType"))
                else:
                    entry["stateOverrides"] = {"knowledge": "outside_observation"}
                candidates.append(entry)
        underfoot = {"feetWorldPixels": {"x": center_x, "y": feet},
            "centerFootTile": {"x": math.floor(center_x / terrain["tileSize"]), "y": feet_row},
            "status": "geometric_candidates" if normal_gravity else "unsupported_or_unknown_gravity",
            "candidates": candidates, "truncated": normal_gravity and last_x - first_x + 1 > 8,
            "interpretation": "Same-snapshot cells immediately at collision-box feet, not body/center tiles. "
                "Not proof of ground contact, support, material safety or a safe fall after removal. "
                "Re-observe and inspect_tile before mining; empty/unknown candidates are not mining targets."}
    return {"tileSize": terrain["tileSize"], "bounds": copy.deepcopy(bounds),
            "encoding": "semantic_rectangles_v1", "readCellDefaults": dict(CELL_DEFAULTS),
            "regionFields": ["tileX", "tileY", "width", "height", "stateOverrides"],
            "regions": regions, "unknownCount": terrain["unknownCount"],
            "uninterpretedCount": terrain["uninterpretedCount"],
            "supportColumnFields": ["tileX", "width", "supportTileY", "status"],
            "supportColumns": support, "underfoot": underfoot,
            "interpretation": "Regions partition all sampled cells. Defaults apply only to knowledge=read. "
                "SolidTop is one-way; actuated tiles do not block. Shape, liquid amount/type and hazards are independent. "
                "Support columns start at box bottom, scan down: geometric candidates, not reachable floors or safe paths; "
                "differences expose potential drops. Slopes/half blocks need region shape; gravity inversion needs full observation. "
                "Outside bounds is unobserved. Walls/paint/ordinary frames and non-collision material identities are omitted; "
                "Mod hooks remain unknown, even with raw type/frame."}


def _entities(value):
    if value is None:
        return None
    items = []
    for entity in value["items"]:
        fields = ("kind", "instanceKey", "slot", "typeId", "contentKey", "name", "boundsWorldPixels",
                  "velocityPixelsPerTick", "life", "lifeMax", "classification", "classificationBasis")
        entry = {k: copy.deepcopy(entity[k]) for k in fields if k in entity and entity[k] is not None}
        details = entity.get("details", {})
        keep = {"player": ("dead", "ghost", "hostile", "team", "isLocal"),
                "npc": ("evidence", "hide", "dontTakeDamage", "realLife"),
                "item": ("stack", "prefix", "favorited", "customState"),
                "projectile": ("owner", "identity", "friendly", "hostile", "damage", "timeLeft", "hide")}
        entry["details"] = {k: copy.deepcopy(details[k]) for k in keep[entity["kind"]] if k in details}
        items.append(entry)
    return {"items": items, "coverage": copy.deepcopy(value["coverage"]),
            "source": "client_known", "sample": "all entries use observation gameUpdate and inViewport=true",
            "visibility": "geometric intersection only; lighting/occlusion/invisibility not evaluated",
            "lifeSemantics": "omitted for items/projectiles: not applicable; absent for other kinds: unknown"}


def _investigate(value):
    if value is None:
        return None
    groups = {}
    for item in value["items"]:
        key = (item["category"], item["typeId"], item["contentKey"], item["interactionKnowledge"], item["contents"])
        groups.setdefault(key, []).append((item["anchorTile"]["x"], item["anchorTile"]["y"]))
    output = []
    for (category, type_id, content_key, knowledge, contents), anchors in groups.items():
        runs = []
        for x, y in sorted(anchors, key=lambda point: (point[1], point[0])):
            if runs and runs[-1][1] == y and runs[-1][0] + runs[-1][2] == x:
                runs[-1][2] += 1
            else:
                runs.append([x, y, 1])
        output.append({"category": category, "typeId": type_id, "contentKey": content_key,
                       "interactionKnowledge": knowledge, "contents": contents, "count": len(anchors),
                       "anchorRuns": runs})
    return {"status": value["status"], "contentsStatus": value["contentsStatus"],
            "candidateCount": len(value["items"]), "returnedCount": len(value["items"]), "omittedCount": 0,
            "anchorRunFields": ["tileX", "tileY", "horizontalCount"], "groups": output,
            "interpretation": "Candidates for investigation, not confirmed usable objects. Frame-important trees/plants "
                "may be included; anchors need not represent entire trees. No chest contents read."}


def project_observation(observation):
    """Pure projection; preserve freshness/coverage, never infer safety from omitted detail."""
    validate_observation(observation)
    player = observation.get("player")
    minimal = None if player is None else {k: copy.deepcopy(player[k]) for k in PLAYER_FIELDS if k in player}
    if minimal is not None:
        minimal["movement"] = {k: copy.deepcopy(player.get("movement", {})[k]) for k in MOVEMENT_FIELDS
                               if k in player.get("movement", {})}
    result = {k: copy.deepcopy(observation.get(k)) for k in
              ("worldSessionId", "observationId", "gameUpdate", "sampledAt", "phase", "status", "camera", "completeness")}
    result.update(schemaVersion=1, sourceSchemaVersion=observation["schemaVersion"],
                  scope="exploration projection of client-known viewport, not whole world or pathfinding",
                  coordinates="world pixels; tile=16px; bounds top-left; +X right,+Y down; velocity pixels/update",
                  player=minimal, terrain=_terrain(observation.get("terrain"), player),
                  entities=_entities(observation.get("entities")),
                  investigate=_investigate(observation.get("interactables")),
                  exclusions=["inventory/equipment/buffs", "detailed environment", "raw tile palette/grid",
                              "walls/paint/ordinary tile frames/materials except underfoot tile IDs", "mod interaction interpretation"])
    return result


def read_exploration_observation(bridge):
    from datetime import datetime, timezone
    reply = bridge.get_observation()  # Existing on-demand game-thread snapshot + strict validation.
    result = project_observation(reply["observation"])
    return {"valid": result["status"] != "unavailable", "exploration": result,
            "controlGeneration": reply.get("controlGeneration"),
            "receivedAt": datetime.now(timezone.utc).isoformat()}
