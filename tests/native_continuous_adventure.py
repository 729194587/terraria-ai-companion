"""Run daily autonomy on a fresh natural world. Only the initial stance is prescribed."""
import argparse
import asyncio
import gzip
import json
import os
import sys
import time
from pathlib import Path

# A git archive of the comparison revision can supply the production modules.
sys.path.insert(0, str(Path(os.environ.get("ADVENTURE_PYTHON", Path(__file__).resolve().parents[1] / "python")).resolve()))
from agent import Agent
from autonomous_mode import AutonomousMode
from llm import LLMClient, LLMError, load_config
from server_ai import ServerAiBridge
import atomic_operations as atomic
from native_direct_input import wait_file
from native_server_ai_agent import ChatSink

SECONDS = 150
CALLS = 8
GOAL = ("Keep adventuring into unknown terrain. Decide which discoveries to investigate, gather or skip, "
        "then keep exploring. Choose another route if blocked.")


async def verify(args):
    profile = args.profile
    survey = await wait_file(profile / "natural-catalog.json")
    offset = {"west": -145, "east": 165}[args.start]
    column = next(c for c in survey["columns"] if c["x"] == survey["spawnX"] + offset)
    assert survey["seed"] == "20261028" and column["clear"]
    request = profile / "natural-request.tmp"
    request.write_text(json.dumps(dict(caseId="adventure", offsetTiles=offset, surfaceY=column["y"])))
    request.replace(profile / "natural-request.json")
    fixture = await wait_file(profile / "natural-ready.json")
    if args.mechanism:
        from native_autonomy import FixtureModel
        llm = FixtureModel("continue")
        llm.model = "scripted-mechanism"
    else:
        config = load_config(os.environ.get("ADVENTURE_MODEL_CONFIG"))
        llm = LLMClient(config.get("LLM_BASE_URL"), config.get("LLM_API_KEY"), config.get("LLM_MODEL"))
    bridge, control = ServerAiBridge(args.port), ServerAiBridge(args.port)
    agent = Agent(bridge, llm, chat_bridge=ChatSink())
    mode = agent.autonomy = AutonomousMode(agent, control)
    report = dict(start=args.start, fixture=fixture, profile=profile.name, binding=bridge.binding, model=llm.model,
                  secondsLimit=SECONDS, decisionCallLimit=CALLS, httpAttemptLimit=0 if args.mechanism else CALLS, goal=GOAL, calls=[], cycles=[])
    full = gzip.open(profile / "adventure-decisions.jsonl.gz", "wt", encoding="utf-8")
    started = time.monotonic()

    def save():
        (profile / "natural-results.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    async def known():
        data = (await atomic.dispatch(bridge, "query_map", dict(mode="overview")))["data"]
        return round(data["width"] * data["height"] * data["exploredRatio"])

    complete = llm.complete
    async def record(messages, tools):
        if len(report["calls"]) >= CALLS:
            raise LLMError("acceptance model call limit")
        call = dict(index=len(report["calls"])+1, elapsed=round(time.monotonic()-started, 2))
        report["calls"].append(call)
        save()
        full.write(json.dumps(dict(index=call["index"], messages=messages), ensure_ascii=False)+"\n")
        full.flush()
        try:
            reply = await complete(messages, tools)
            call["reply"] = reply
            return reply
        except Exception as error:
            call["error"] = str(error)
            raise
        finally:
            save()

    llm.complete = record
    try:
        report["initialKnown"] = await known()
        await mode.command("on")
        mode.objective = GOAL
        save()
        async with asyncio.timeout(SECONDS):
            while True:
                life = await asyncio.to_thread(control.life_status)
                await agent.observe_life(life, lambda: ServerAiBridge(args.port))
                if mode.worker is None or not mode.worker.done():
                    await mode.pulse(life)
                if mode.worker and mode.worker.done():
                    await mode.worker
                    mode.worker = None
                    owned = (mode.mailbox.records.get(mode.progress.get('actionId'))
                             if mode.mailbox and mode.progress.get('operation') != 'explore' else None)
                    report["cycles"].append(dict(terminal=owned['view'].get('result') if owned else None,
                        elapsed=round(time.monotonic()-started,2), phase=mode.phase,
                        error=mode.error, known=await known(), objective=mode.objective,
                        receipt=json.loads(json.dumps(mode.progress))))
                    save()
                    if len(report["calls"]) >= CALLS or mode.hold or mode.phase in (
                            "no_progress","model_blocked"):
                        report["stop"] = mode.error or ("model_call_limit" if len(report["calls"]) >= CALLS else mode.phase)
                        break
                # Observe completed workers before pulse can replace them.
                if mode.worker:
                    await asyncio.wait({mode.worker}, timeout=.2)
                else:
                    await asyncio.sleep(.1)
    except TimeoutError:
        report["stop"] = "time_limit"
    finally:
        report.setdefault("stop", "exception")
        await mode.command("off")
        report.update(released=(await mode.control_request("status"))["released"], finalKnown=await known(),
                      elapsed=round(time.monotonic()-started, 2))
        save()
        full.close()
        await agent.close()
        control.close()
        bridge.close()
        if not args.mechanism:
            await llm.close()
    (profile / "natural-finish").touch()
    await wait_file(profile / "natural-motion-adventure.json", 30)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--mechanism", action="store_true")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--start", choices=["west","east"], required=True)
    asyncio.run(verify(parser.parse_args()))
