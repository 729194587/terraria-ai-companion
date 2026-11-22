"""Opt-in local diagnostics. Never records HTTP headers or environment files."""
from collections import Counter
from contextvars import ContextVar
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import threading
import uuid


context = ContextVar("agent_trace_context", default={})
MCP_CORRELATION = ('mcp_session_id', 'mcp_request_id', 'mcp_tool_name')


class Trace:
    def __init__(self, path=None, enabled=None, max_bytes=5 * 1024 * 1024):
        self.enabled = os.environ.get("TERRARIA_AGENT_TRACE") == "1" if enabled is None else enabled
        self.session_id = uuid.uuid4().hex
        self.path = Path(path) if path is not None else (Path(__file__).resolve().parents[1]
            / ".local" / "diagnostics" / f"agent-trace-{self.session_id}.jsonl")
        self.max_bytes = max_bytes
        self._lock = threading.Lock()
        self._sequence = 0
        self.write_errors = 0
        self.dropped_records = 0
        self._secrets = set()

    def add_secret(self, value):
        # Only used for redaction; this value is never emitted as a field.
        if value:
            with self._lock:
                self._secrets.add(value)

    def _redact(self, value):
        if isinstance(value, dict):
            return {key: "[REDACTED]" if str(key).lower().replace("-", "_") in
                    ("authorization", "proxy_authorization", "api_key", "llm_api_key", "password", "access_token")
                    else self._redact(item) for key, item in value.items()}
        if isinstance(value, (list, tuple)):
            return [self._redact(item) for item in value]
        if isinstance(value, str):
            for secret in sorted(self._secrets, key=len, reverse=True):
                value = value.replace(secret, "[REDACTED]")
        return value

    def emit(self, event_type, **data):
        if not self.enabled:
            return
        try:
            with self._lock:
                self._sequence += 1
                record = {"type": event_type, "timestamp": datetime.now(timezone.utc).isoformat(),
                          "session_id": self.session_id, "sequence": self._sequence,
                          **context.get(), **data}
                raw = (json.dumps(self._redact(record), ensure_ascii=False) + "\n").encode("utf-8")
                if len(raw) > self.max_bytes:
                    self.dropped_records += 1
                    record = {key: record[key] for key in ("timestamp", "session_id", "sequence",
                              "task_id", "generation", "chat_event_id", "tool_call_id", "model_request_id",
                              "lua_run_id", "lua_call_id", "lua_line", "source_sha256", "connection_id",
                              "request_id", *MCP_CORRELATION) if key in record}
                    raw = (json.dumps(self._redact({**record, "type": "trace_record_dropped",
                        "original_type": event_type, "reason": "event_exceeds_file_limit",
                        "bytes": len(raw)}), ensure_ascii=False) + "\n").encode("utf-8")
                    if len(raw) > self.max_bytes:
                        return
                self.path.parent.mkdir(parents=True, exist_ok=True)
                if self.path.exists() and self.path.stat().st_size + len(raw) > self.max_bytes:
                    self.path.replace(self.path.with_suffix(self.path.suffix + ".1"))
                with self.path.open("ab", buffering=0) as stream:
                    start = stream.tell()
                    try:
                        if stream.write(raw) != len(raw):
                            raise OSError("Short trace write")
                    except Exception:
                        # Best effort rollback so a partial write cannot corrupt the next JSON line.
                        try:
                            stream.truncate(start)
                        except Exception:
                            pass
                        raise
        except Exception:
            self.write_errors += 1
            # Diagnostics must not turn an otherwise successful Agent operation into a failure.
            pass

    def health(self):
        return {'enabled': self.enabled, 'writeErrors': self.write_errors, 'recordsDropped': self.dropped_records}

    def terrain(self, reply):
        if not self.enabled:
            return
        try:
            self.emit("terrain_diagnostic", diagnostic=terrain_diagnostic(reply))
        except Exception:
            self.emit("terrain_diagnostic", diagnostic={"warnings": ["diagnostic_failed"]})


def terrain_diagnostic(reply):
    """Inspect without changing data. Formula mirrors TerrainReader.Read at baseline 1821d94."""
    warnings = []
    state = reply.get("state") if isinstance(reply, dict) else None
    terrain = reply.get("terrain") if isinstance(reply, dict) else None
    if not isinstance(state, dict) or not isinstance(terrain, dict):
        return {"warnings": ["missing_state_or_terrain"]}
    result = {"position": state.get("position"),
              **{key: terrain.get(key) for key in
                 ("playerWidth", "playerHeight", "originX", "originY", "tileSize")},
              "warnings": warnings}
    rows = terrain.get("rows")
    counts = Counter()
    cells = []
    if not isinstance(rows, list):
        warnings.append("rows_not_array")
        result.update(row_count=None, column_counts=[], character_counts={}, special_cells=[])
    else:
        lengths = [len(row) if isinstance(row, str) else None for row in rows]
        result.update(row_count=len(rows), column_counts=lengths)
        if len(rows) != 29 or any(length != 45 for length in lengths):
            warnings.append("expected_45x29_string_grid")
        origin_valid = all(type(terrain.get(key)) is int for key in ("originX", "originY"))
        for row_index, row in enumerate(rows):
            if not isinstance(row, str):
                continue
            counts.update(row)
            for column, char in enumerate(row):
                if char in "h1234":
                    cells.append({"kind": char, "row": row_index, "column": column,
                        "tileX": terrain["originX"] + column if origin_valid else None,
                        "tileY": terrain["originY"] + row_index if origin_valid else None})
        unknown = sorted(set(counts) - set(".#h=1234ab!?"))
        if unknown:
            warnings.append("unrecognized_characters")
        if counts.get("?"):
            warnings.append("unknown_or_unloaded_tiles")
        result.update(character_counts={char: counts[char] for char in ".#h=1234ab!?" + "".join(unknown)},
                      unknown_characters=unknown, special_cells=cells)
    try:
        position = state["position"]
        numbers = [position["x"], position["y"], terrain["playerWidth"], terrain["playerHeight"]]
        if any(type(value) not in (int, float) or not math.isfinite(value) for value in numbers):
            raise ValueError()
        # Deliberately use C#'s fixed 16, 22 and 17; do not infer a new window from malformed rows.
        expected = {"originX": math.floor((position["x"] + terrain["playerWidth"] / 2) / 16) - 22,
                    "originY": math.floor((position["y"] + terrain["playerHeight"]) / 16) - 17}
        matches = all(type(terrain.get(key)) is int and terrain[key] == value for key, value in expected.items())
        result.update(expected_origin=expected, origin_matches=matches)
        if not matches:
            warnings.append("origin_mismatch")
    except (KeyError, TypeError, ValueError, OverflowError):
        result.update(expected_origin=None, origin_matches=None)
        warnings.append("cannot_compute_expected_origin")
    if terrain.get("tileSize") != 16:
        warnings.append("unexpected_tile_size")
    return result


def emit_full(event_type, **data):
    """Mirror opt-in payloads into the existing bounded task bundle, never runtime summaries."""
    trace.emit(event_type, **data)
    if trace.enabled:
        try:
            # Local import avoids the evidence -> tracing dependency at startup.
            import evidence
            evidence.record(event_type, **data)
        except Exception:
            trace.write_errors += 1


trace = Trace()
