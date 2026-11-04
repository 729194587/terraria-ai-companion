import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch, AsyncMock
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
import agent
import agent_diagnostics as diagnostics
import agent_trace as tracing
from test_agent import Agent, Game, Model, event, tool, final


class LogTests(unittest.TestCase):
    def test_lua_runtime_log_keeps_summary_without_source_or_return_data(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(diagnostics, 'log', tracing.Trace(enabled=False)):
            path = diagnostics.configure(Path(directory) / 'lua.jsonl')
            diagnostics.tool_result('execute_lua', {'source': 'return "private program"'}, {
                'valid': True, 'status': 'completed', 'programCompleted': True,
                'actionSuccess': None, 'value': {'scene': 'private observation'}, 'calls': 0})
            raw = path.read_text(encoding='utf-8')
            self.assertNotIn('private program', raw)
            self.assertNotIn('private observation', raw)
            self.assertTrue(json.loads(raw)['arguments']['sourceSha256'])
            self.assertTrue(json.loads(raw)['result']['programCompleted'])

    def test_runtime_log_filters_payloads_and_redacts_registered_secrets(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(diagnostics, "log", tracing.Trace(enabled=False)), patch.object(tracing, "trace", tracing.Trace(enabled=False)):
            path = diagnostics.configure(Path(directory)/"run.jsonl")
            tracing.trace.add_secret("test-secret")
            diagnostics.tool_result("mine_tile", {"x":1,"y":2}, {"valid":True, "status":"failed",
                "reason":"root-test-secret", "data":{"inventory":"private"}, "chat":"private"})
            raw = path.read_text(encoding="utf-8")
            self.assertNotIn("test-secret",raw); self.assertNotIn("private",raw)
            self.assertEqual("root-[REDACTED]",json.loads(raw)["result"]["reason"])
            self.assertNotIn("chat_event_id", json.loads(raw))

    def test_recovery_logs_observed_geometry_and_original_target(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(diagnostics, "log", tracing.Trace(enabled=False)):
            path = diagnostics.configure(Path(directory)/"run.jsonl")
            diagnostics.tool_result("observe_terrain", {}, {"valid":True,"state":{"position":{"x":100}},
                "terrain":{"rows":["###"]}}, recovery=True)
            result = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual("mining_recovery_step",result["type"])
            self.assertEqual(["###"],result["result"]["terrain"]["rows"])

    def test_tile_and_underfoot_geometry_are_available_in_runtime_log(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(diagnostics, "log", tracing.Trace(enabled=False)):
            path = diagnostics.configure(Path(directory)/"run.jsonl")
            diagnostics.tool_result("inspect_tile", {"x":2183,"y":233}, {"valid":True,
                "data":{"x":2183,"y":233,"cell":{"knowledge":"read","hasTile":False}},
                "worldSessionId":"world","gameUpdate":12})
            diagnostics.tool_result("get_exploration_observation", {}, {"valid":True,
                "exploration":{"worldSessionId":"world","gameUpdate":13,"player":{"boundsWorldPixels":{"y":3718,"height":42}},
                    "terrain":{"underfoot":{"centerFootTile":{"x":2184,"y":235}}}}})
            records=[json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
            self.assertFalse(records[0]["result"]["data"]["cell"]["hasTile"])
            self.assertEqual(235,records[1]["result"]["footingObservation"]["underfoot"]["centerFootTile"]["y"])

    def test_rotation_and_failed_configuration(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(diagnostics, "log", tracing.Trace(enabled=False)):
            path = diagnostics.configure(Path(directory)/"run.jsonl")
            diagnostics.log.max_bytes = 700
            for _ in range(10): diagnostics.emit("sample", reason="x"*100)
            self.assertTrue(path.with_suffix(".jsonl.1").exists())
            self.assertLessEqual(path.stat().st_size,700)
            with patch("sys.stderr",new_callable=io.StringIO) as output:
                self.assertIsNone(diagnostics.configure(path/"bad.jsonl"))
            self.assertIn("Runtime log unavailable",output.getvalue())

    def test_cli_creates_runtime_log_without_starting_real_agent(self):
        with tempfile.TemporaryDirectory() as directory, patch.object(diagnostics, "log", tracing.Trace(enabled=False)):
            path = Path(directory)/"cli.jsonl"
            with patch("sys.argv",["agent.py","--log-file",str(path)]), patch.object(agent,"run",new_callable=AsyncMock), patch("sys.stderr",new_callable=io.StringIO) as output:
                self.assertEqual(0,agent.main())
            self.assertIn(str(path),output.getvalue())
            self.assertEqual(["session_start","session_end"],[json.loads(s)["type"] for s in path.read_text(encoding="utf-8").splitlines()])


class FailureTests(unittest.IsolatedAsyncioTestCase):
    async def test_blocked_followup_preserves_first_failure_in_reply_and_log(self):
        game = Game(); model = Model(tool("mine_tile",'{"x":1,"y":2}'), tool("use_item"), final("done"))
        async def dispatch(*args, **kwargs):
            return {"valid":True,"status":"failed","reason":"item_aim_requires_default_zoom"}
        with tempfile.TemporaryDirectory() as directory, patch.object(diagnostics,"log",tracing.Trace(enabled=False)):
            path = diagnostics.configure(Path(directory)/"run.jsonl")
            companion = Agent(game,model)
            with patch("atomic_operations.dispatch",dispatch):
                companion.receive(event(1)); await companion.task
            self.assertIn("item_aim_requires_default_zoom",game.sent[-1][1]["text"])
            records = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]
            blocked = next(row['result'] for row in reversed(records) if row['type']=='tool_result' and row['name']=='use_item')
            self.assertTrue(blocked["blocked"])
            self.assertEqual("item_aim_requires_default_zoom",blocked["previousFailure"]["reason"])
