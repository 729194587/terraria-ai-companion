"""Bounded, local runtime diagnostics; no chat, prompts, inventory or HTTP payloads."""
from datetime import datetime
from pathlib import Path
import sys
import agent_trace as tracing

log = tracing.Trace(enabled=False)


def configure(path=None):
    global log
    path = Path(path) if path else (Path(__file__).resolve().parents[1] / ".local" / "diagnostics"
        / ("agent-runtime-" + datetime.now().strftime("%Y%m%d-%H%M%S-%f") + ".jsonl"))
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("ab"):
            pass
    except OSError as error:
        log = tracing.Trace(enabled=False)
        print(f"Runtime log unavailable: {error}", file=sys.stderr, flush=True)
        return None
    log = tracing.Trace(path, enabled=True)
    return path


def emit(event, **data):
    # Reuse secrets registered by LLMClient, even when full tracing is disabled.
    log.emit(event, **tracing.trace._redact(data))


def tool_result(name, arguments, result, *, recovery=False):
    keys = ("valid", "status", "reason", "error", "operation", "actionId", "session",
            "actionGeneration", "controlGeneration", "endEvidenceStatus", "endEvidence",
            "evidence", "recovery", "blocked", "previousFailure")
    summary = {key: result[key] for key in keys if key in result}
    if name == "inspect_tile":
        for key in ("data", "worldSessionId", "gameUpdate"):
            if key in result:
                summary[key] = result[key]
    if name == "get_exploration_observation":
        exploration = result.get("exploration") or {}
        terrain = exploration.get("terrain") or {}
        summary["footingObservation"] = {"worldSessionId": exploration.get("worldSessionId"),
            "gameUpdate": exploration.get("gameUpdate"), "player": exploration.get("player"),
            "underfoot": terrain.get("underfoot")}
    if recovery:
        for key in ("state", "terrain", "data", "worldSessionId", "gameUpdate"):
            if key in result:
                summary[key] = result[key]
    emit("mining_recovery_step" if recovery else "tool_result", name=name,
         arguments=arguments, result=summary)
