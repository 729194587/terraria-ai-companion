"""Content-free usage accounting; bytes are never treated as tokens."""
from contextvars import ContextVar
from datetime import datetime, timezone
import hashlib
import json
import sys

import agent_trace as tracing


current = ContextVar("task_cost_metrics", default=None)
FIELDS = ("input_tokens", "output_tokens", "cached_input_tokens", "non_cached_input_tokens")
TOOLS = {"get_state", "get_observation", "get_exploration_observation", "move", "stop_movement",
         "execute_lua", "transfer_inventory_item", "swap_inventory_slots", "hold_inventory_item", "get_player_status", "resolve_content", "observe_exploration", "wait",
         "get_player_state", "observe_terrain", "inspect_tile", "scan_tiles", "scan_entities", "inspect_inventory", "get_world_state",
         "mine_wall", "place_wall", "hammer_tile",
         "drop_through_platform", "navigate_to_x", "navigate_to_landing_region", "select_hotbar_slot", "use_item", "mine_tile", "attack_entity", "interact_tile", "place_tile", "stop_action"}


def tool_name(value):
    return value if isinstance(value, str) and value in TOOLS else "unknown"


def usage_counts(usage):
    usage = usage if isinstance(usage, dict) else {}
    def count(value):
        return value if type(value) is int and value >= 0 else None
    details = usage.get("prompt_tokens_details")
    details = details if isinstance(details, dict) else {}
    incoming = count(usage.get("prompt_tokens"))
    outgoing = count(usage.get("completion_tokens"))
    cached = count(details.get("cached_tokens"))
    if cached is None:
        cached = count(usage.get("prompt_cache_hit_tokens"))
    if incoming is not None and cached is not None and cached > incoming:
        cached = None
    return dict(zip(FIELDS, (incoming, outgoing, cached,
        incoming - cached if incoming is not None and cached is not None else None)))


def emit(event, **fields):
    # Only allow accounting metadata here; never supply bodies, arguments or content.
    try:
        record = {"type": event, "timestamp": datetime.now(timezone.utc).isoformat(),
                  "generation": tracing.context.get().get("generation"), **fields}
        print("Cost: " + json.dumps(record, ensure_ascii=True), file=sys.stderr, flush=True)
        tracing.trace.emit(event, **fields)
    except Exception:
        pass  # Telemetry must not interrupt actions or chat.


def record_usage(usage, request_id, elapsed_ms, outcome):
    counts = usage_counts(usage)
    task = current.get()
    if task is not None:
        task.add_usage(counts)
    emit("llm_cost", model_request_id=request_id, outcome=outcome,
         elapsed_ms=round(elapsed_ms, 3), **counts)


class TaskCosts:
    def __init__(self, exposures):
        self.exposures = exposures
        self.model_calls = 0
        self.model_elapsed_ms = 0
        self.totals = {field: 0 for field in FIELDS}
        self.known = {field: 0 for field in FIELDS}
        self.tool_calls = self.tool_result_bytes = 0
        self.lua_api_calls = 0
        self.context_bytes = self.repeated_bytes = 0
        self.observation_bytes = self.repeated_observation_bytes = 0

    def add_usage(self, counts):
        for field, value in counts.items():
            if value is not None:
                self.totals[field] += value
                self.known[field] += 1

    def request(self, messages):
        self.model_calls += 1
        # Identify exact retained strings, not semantic equality or inferred token spans.
        names = {}
        retained = []
        active = set()
        for message in messages:
            for call in message.get("tool_calls") or []:
                names[call["id"]] = call["function"].get("name")
            if message.get("role") != "tool":
                continue
            content = message.get("content", "")
            raw = content.encode("utf-8")
            key = (message.get("tool_call_id"), hashlib.sha256(raw).digest())
            active.add(key)
            prior = self.exposures.get(key, 0)
            self.exposures[key] = prior + 1
            name = names.get(message.get("tool_call_id"))
            name = tool_name(name)
            # An expired placeholder is not another submission of a full observation.
            try:
                value = json.loads(content) if name == "get_observation" else None
                observation = isinstance(value, dict) and isinstance(value.get("observation"), dict)
            except (ValueError, TypeError):
                observation = False
            size = len(raw)
            self.context_bytes += size
            self.repeated_bytes += size if prior else 0
            self.observation_bytes += size if observation else 0
            self.repeated_observation_bytes += size if observation and prior else 0
            retained.append({"name": name, "result_utf8_bytes": size,
                             "prior_submissions": prior, "full_observation": observation})
        for key in list(self.exposures):
            if key not in active:
                del self.exposures[key]
        emit("model_context_cost", request_ordinal=self.model_calls, tool_results=retained)

    def tool(self, name, content):
        size = len(content.encode("utf-8"))
        self.tool_calls += 1
        self.tool_result_bytes += size
        emit("tool_cost", tool_ordinal=self.tool_calls,
             name=tool_name(name), result_utf8_bytes=size)

    def summary(self, outcome, *, task_outcome="unknown", action_outcome=None):
        emit("chat_cost", outcome=outcome, task_outcome=task_outcome,
             action_outcome=action_outcome, model_calls=self.model_calls,
             model_elapsed_ms=round(self.model_elapsed_ms, 3),
             tokens={field: {"known_sum": self.totals[field] if self.known[field] else None,
                            "reported_requests": self.known[field],
                            "unreported_requests": self.model_calls - self.known[field]}
                     for field in FIELDS},
             tool_calls=self.tool_calls, tool_result_utf8_bytes=self.tool_result_bytes,
             lua_api_calls=self.lua_api_calls,
             submitted_tool_utf8_bytes=self.context_bytes,
             repeated_tool_utf8_bytes=self.repeated_bytes,
             submitted_observation_utf8_bytes=self.observation_bytes,
             repeated_observation_utf8_bytes=self.repeated_observation_bytes)
