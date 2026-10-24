import asyncio
import copy
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
import agent_trace as tracing
from agent import Agent, TOOLS
from llm import LLMClient, LLMError
from control import Bridge, BridgeError
from test_agent import Game, Model, event, tool, final
from test_control import FakeServer


class TraceTests(unittest.TestCase):
    def test_oversized_event_retained_fields_are_redacted(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = tracing.Trace(Path(directory) / "trace.jsonl", enabled=True, max_bytes=800)
            key = "configured-test-api-key"
            trace.add_secret(key)
            token = tracing.context.set({"generation": 7, "chat_event_id": f"chat-{key}",
                "tool_call_id": f"tool-{key}", "model_request_id": f"model-{key}"})
            try:
                trace.emit(f"event-{key}", payload="x" * 5000)
            finally:
                tracing.context.reset(token)
            raw = trace.path.read_text(encoding="utf-8")
            self.assertNotIn(key, raw)
            record = json.loads(raw)
            self.assertEqual(record["type"], "trace_record_dropped")
            self.assertEqual(record["generation"], 7)
            self.assertEqual(record["chat_event_id"], "chat-[REDACTED]")
            self.assertEqual(record["tool_call_id"], "tool-[REDACTED]")
            self.assertEqual(record["model_request_id"], "model-[REDACTED]")
            self.assertEqual(record["original_type"], "event-[REDACTED]")
            self.assertLessEqual(len(raw.encode("utf-8")), 800)

    def test_opt_in_default_and_json_lines_rotation(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "trace.jsonl"
            with patch.dict(os.environ, {}, clear=True):
                disabled = tracing.Trace(path)
            disabled.emit("private", messages=["private-chat"], rows=["h"])
            self.assertFalse(path.exists())
            with patch.dict(os.environ, {"TERRARIA_AGENT_TRACE": "1"}):
                enabled = tracing.Trace(path, max_bytes=800)
            self.assertTrue(enabled.enabled)
            for _ in range(10):
                enabled.emit("sample", value="x" * 100)
            enabled.emit("oversized", value="x" * 2000)
            files = list(Path(directory).iterdir())
            self.assertEqual(len(files), 2)
            for file in files:
                self.assertLessEqual(file.stat().st_size, 800)
                for line in file.read_text(encoding="utf-8").splitlines():
                    parsed = json.loads(line)
                    self.assertIn("timestamp", parsed)
                    self.assertEqual(parsed["session_id"], enabled.session_id)
            self.assertIn("trace_record_dropped", path.read_text(encoding="utf-8"))

    def test_half_blocks_slopes_origin_and_input_unchanged(self):
        reply = Game().get_terrain()
        reply["state"]["position"] = {"x": 1600, "y": 1558}
        terrain = reply["terrain"]
        terrain.update(originX=78, originY=83)
        rows = [list(row) for row in terrain["rows"]]
        for column, kind in enumerate("h1234"):
            rows[2][column + 3] = kind
        terrain["rows"] = ["".join(row) for row in rows]
        original = copy.deepcopy(reply)
        diagnostic = tracing.terrain_diagnostic(reply)
        self.assertEqual(diagnostic["expected_origin"], {"originX": 78, "originY": 83})
        self.assertTrue(diagnostic["origin_matches"])
        self.assertEqual(diagnostic["row_count"], 29)
        self.assertEqual(diagnostic["column_counts"], [45] * 29)
        self.assertEqual(diagnostic["special_cells"], [
            {"kind": kind, "row": 2, "column": column + 3, "tileX": 81 + column, "tileY": 85}
            for column, kind in enumerate("h1234")])
        self.assertEqual(diagnostic["character_counts"]["h"], 1)
        self.assertEqual(reply, original)
        terrain["originX"] += 1
        original = copy.deepcopy(reply)
        diagnostic = tracing.terrain_diagnostic(reply)
        self.assertFalse(diagnostic["origin_matches"])
        self.assertIn("origin_mismatch", diagnostic["warnings"])
        self.assertEqual(reply, original)
        for x, y, expected in ((1589.9, 1557.9, {"originX": 77, "originY": 82}),
                               (-11, -43, {"originX": -23, "originY": -18})):
            reply["state"]["position"] = {"x": x, "y": y}
            self.assertEqual(tracing.terrain_diagnostic(reply)["expected_origin"], expected)

    def test_bad_grid_and_unknown_values_warn(self):
        reply = Game().get_terrain()
        reply["terrain"]["rows"] = ["h?Z", None]
        original = copy.deepcopy(reply)
        diagnostic = tracing.terrain_diagnostic(reply)
        self.assertEqual(diagnostic["column_counts"], [3, None])
        self.assertEqual(diagnostic["unknown_characters"], ["Z"])
        self.assertIn("expected_45x29_string_grid", diagnostic["warnings"])
        self.assertIn("unknown_or_unloaded_tiles", diagnostic["warnings"])
        self.assertIn("unrecognized_characters", diagnostic["warnings"])
        self.assertEqual(diagnostic["character_counts"]["1"], 0)
        self.assertEqual(reply, original)


class TraceIntegrationTests(unittest.IsolatedAsyncioTestCase):
    async def test_unexpected_exception_records_failure_and_propagates(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = tracing.Trace(Path(directory) / "trace.jsonl", enabled=True)
            error = RuntimeError("unexpected fixture failure")
            game = Game()
            with patch.object(tracing, "trace", trace):
                agent = Agent(game, Model(error))
                try:
                    agent.receive(event(1))
                    with self.assertRaises(RuntimeError) as raised:
                        await agent.task
                    self.assertIs(raised.exception, error)
                finally:
                    await agent.close()
            records = [json.loads(line) for line in trace.path.read_text(encoding="utf-8").splitlines()]
            failed = [r for r in records if r["type"] == "agent_failed"]
            ended = [r for r in records if r["type"] == "agent_end"]
            self.assertEqual(len(failed), 1)
            self.assertEqual(failed[0]["error_type"], "RuntimeError")
            self.assertEqual(len(ended), 1)
            self.assertEqual(ended[0]["outcome"], "failed")
            self.assertEqual(ended[0]["generation"], 1)
            self.assertEqual(ended[0]["chat_event_id"], "1")
            self.assertEqual(game.sent, [])

    async def test_rejected_ipc_records_raw_reply_and_error(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = tracing.Trace(Path(directory) / "trace.jsonl", enabled=True)
            with patch.object(tracing, "trace", trace), FakeServer(reject=True) as server:
                with Bridge(server.port) as bridge:
                    with self.assertRaisesRegex(BridgeError, "Command rejected: manual_active"):
                        await asyncio.to_thread(bridge.command, "move_right")
            records = [json.loads(line) for line in trace.path.read_text(encoding="utf-8").splitlines()]
            self.assertEqual([r["type"] for r in records], ["ipc_request", "ipc_reply", "ipc_error"])
            request, reply, error = records
            self.assertFalse(reply["reply"]["ok"])
            self.assertEqual(reply["reply"]["status"], "manual_active")
            self.assertEqual(error["category"], "rejected")
            self.assertEqual(error["reason"], "manual_active")
            self.assertEqual(error["request"], request["request"])
            self.assertEqual(error["request"]["id"], reply["reply"]["id"])

    async def test_complete_real_bridge_and_mock_model_trace(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = tracing.Trace(Path(directory) / "trace.jsonl", enabled=True)
            requests = []
            def handler(request):
                requests.append(json.loads(request.content))
                return httpx.Response(200, json={"choices": [{
                    "finish_reason": "tool_calls" if len(requests) == 1 else "stop",
                    "message": tool() if len(requests) == 1 else final("80 HP")} ]})
            with patch.object(tracing, "trace", trace), FakeServer() as server:
                client = LLMClient("https://example.test", "trace-test-key", "deepseek-flash",
                                   transport=httpx.MockTransport(handler))
                bridge = Bridge(server.port)
                agent = Agent(bridge, client)
                try:
                    agent.receive(event(1, text="private-chat trace-test-key"))
                    await agent.task
                finally:
                    await agent.close()
                    bridge.close()
                    await client.close()
            raw = trace.path.read_text(encoding="utf-8")
            self.assertNotIn("trace-test-key", raw)
            self.assertNotIn("Authorization", raw)
            self.assertNotIn("Bearer", raw)
            self.assertIn("[REDACTED]", raw)
            records = [json.loads(line) for line in raw.splitlines()]
            types = [record["type"] for record in records]
            for kind in ("chat_received", "agent_start", "llm_request", "llm_response", "llm_return",
                         "ipc_request", "ipc_reply", "tool_start", "tool_result", "terrain_diagnostic",
                         "tool_message", "reply_prepared", "send_chat_start", "send_chat_result", "agent_end"):
                self.assertIn(kind, types)
            for record in records:
                self.assertEqual(record["generation"], 1)
                self.assertEqual(record["chat_event_id"], "1")
            ipc = [record for record in records if record["type"] == "ipc_reply"]
            self.assertEqual([r["command"] for r in ipc], ["get_state", "get_terrain", "send_chat"])
            self.assertTrue(all("id" in r["reply"] for r in ipc))
            terrain_reply = ipc[1]
            self.assertEqual(terrain_reply["tool_call_id"], "call_1")
            result = next(r["result"] for r in records if r["type"] == "tool_result")
            self.assertEqual(result["terrain"], terrain_reply["reply"]["terrain"])
            message = next(r["message"] for r in records if r["type"] == "tool_message")
            self.assertEqual(json.loads(message["content"]), result)
            model_requests = [r for r in records if r["type"] == "llm_request"]
            self.assertEqual(model_requests[1]["body"]["messages"][-2], message)
            self.assertEqual(model_requests[1]["body"], requests[1] | {
                "messages": trace._redact(requests[1]["messages"])})
            self.assertEqual(model_requests[0]["body"]["thinking"], {"type": "disabled"})
            self.assertEqual([r["model_request_id"] for r in model_requests], [1, 2])

    async def test_disabled_trace_and_write_failure_preserve_dialogue(self):
        with tempfile.TemporaryDirectory() as directory:
            blocked = Path(directory) / "blocked"
            blocked.write_text("file, not directory", encoding="utf-8")
            for enabled in (False, True):
                trace = tracing.Trace(blocked / "trace.jsonl", enabled=enabled)
                game = Game()
                agent = Agent(game, Model(tool(), final("reply-private")))
                with patch.object(tracing, "trace", trace), patch("sys.stderr", new_callable=io.StringIO) as output:
                    agent.receive(event(1, text="chat-private"))
                    await agent.task
                    await agent.close()
                self.assertEqual(game.sent[0][1]["text"], "reply-private")
                self.assertNotIn("chat-private", output.getvalue())
                self.assertNotIn("reply-private", output.getvalue())
                self.assertNotIn("rows", output.getvalue())
                self.assertFalse(trace.path.exists())

    async def test_invalid_terrain_is_diagnosed_before_tool_validation(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = tracing.Trace(Path(directory) / "trace.jsonl", enabled=True)
            game = Game()
            reply = game.get_terrain()
            reply["terrain"]["rows"][0] = "?Z"
            original = copy.deepcopy(reply)
            game.get_terrain = lambda: reply
            with patch.object(tracing, "trace", trace):
                agent = Agent(game, Model(tool(), final("Cannot read terrain.")))
                try:
                    agent.receive(event(1))
                    await agent.task
                finally:
                    await agent.close()
            records = [json.loads(line) for line in trace.path.read_text(encoding="utf-8").splitlines()]
            diagnostic = next(r for r in records if r["type"] == "terrain_diagnostic")
            self.assertEqual(diagnostic["tool_call_id"], "call_1")
            self.assertIn("unrecognized_characters", diagnostic["diagnostic"]["warnings"])
            result = next(r["result"] for r in records if r["type"] == "tool_result")
            self.assertFalse(result["valid"])
            self.assertEqual(reply, original)

    async def test_cancel_and_late_result_discard_trace(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = tracing.Trace(Path(directory) / "trace.jsonl", enabled=True)
            started, release = asyncio.Event(), asyncio.Event()
            class LateModel(Model):
                async def complete(self, messages, tools):
                    self.requests.append(copy.deepcopy(messages))
                    if len(self.requests) == 1:
                        started.set()
                        try:
                            await release.wait()
                        except asyncio.CancelledError:
                            await release.wait()
                        return final("OLD")
                    return final("NEW")
            game = Game()
            with patch.object(tracing, "trace", trace):
                agent = Agent(game, LateModel())
                try:
                    agent.receive(event(1))
                    await asyncio.wait_for(started.wait(), 1)
                    old = agent.task
                    agent.receive(event(2))
                    await asyncio.wait_for(agent.task, 1)
                    release.set()
                    await old
                finally:
                    release.set()
                    await agent.close()
            self.assertEqual([params["text"] for _, params in game.sent], ["NEW"])
            self.assertEqual(sum(m["role"] == "user" for m in agent.history), 2)
            records = [json.loads(line) for line in trace.path.read_text(encoding="utf-8").splitlines()]
            discarded = next(r for r in records if r["type"] == "agent_result_discarded")
            self.assertEqual(discarded["generation"], 1)
            self.assertEqual(discarded["reason"], "newer_generation_after_llm")

    async def test_http_errors_and_cancellation_trace(self):
        with tempfile.TemporaryDirectory() as directory:
            trace = tracing.Trace(Path(directory) / "trace.jsonl", enabled=True)
            for response in (httpx.Response(401, text="private error body"), httpx.ReadTimeout("secret"),
                             httpx.ConnectError("secret"), httpx.Response(200, text="bad json")):
                def handler(request):
                    if isinstance(response, Exception):
                        raise response
                    return response
                with patch.object(tracing, "trace", trace):
                    client = LLMClient("https://example.test", "fake-key", "deepseek-flash",
                                       transport=httpx.MockTransport(handler))
                    try:
                        with self.assertRaises(LLMError):
                            await client.complete([], TOOLS)
                    finally:
                        await client.close()
            started = asyncio.Event()
            async def waiting(request):
                started.set()
                await asyncio.Event().wait()
            with patch.object(tracing, "trace", trace):
                client = LLMClient("https://example.test", "fake-key", "deepseek-flash",
                                   transport=httpx.MockTransport(waiting))
                agent = Agent(Game(), client)
                try:
                    agent.receive(event(1))
                    await asyncio.wait_for(started.wait(), 1)
                finally:
                    await agent.close()
                    await client.close()
            raw = trace.path.read_text(encoding="utf-8")
            records = [json.loads(line) for line in raw.splitlines()]
            self.assertEqual({r["category"] for r in records if r["type"] == "llm_error"},
                             {"http", "timeout", "network", "invalid_response"})
            self.assertIn("llm_cancelled", [r["type"] for r in records])
            self.assertIn("agent_cancelled", [r["type"] for r in records])
            self.assertNotIn("private error body", raw)
            self.assertNotIn("secret", raw)


if __name__ == "__main__":
    unittest.main()
