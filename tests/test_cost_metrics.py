import asyncio
import io
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
import cost_metrics as metrics
from llm import LLMClient, LLMError
from agent import Agent
from test_agent import Game, Model, event, final, tool


class UsageTests(unittest.TestCase):
    def test_missing_partial_and_invalid_usage(self):
        for value in (None, [], {}, {"prompt_tokens": True, "completion_tokens": -1}):
            self.assertTrue(all(v is None for v in metrics.usage_counts(value).values()))
        value = metrics.usage_counts({"prompt_tokens": 100, "completion_tokens": 10})
        self.assertEqual(value["input_tokens"], 100)
        self.assertIsNone(value["cached_input_tokens"])
        self.assertIsNone(value["non_cached_input_tokens"])
        value = metrics.usage_counts({"prompt_tokens": 100,
            "prompt_tokens_details": {"cached_tokens": 120}})
        self.assertIsNone(value["cached_input_tokens"])
        self.assertEqual(metrics.usage_counts({"prompt_tokens": 100,
            "prompt_cache_hit_tokens": 80})["non_cached_input_tokens"], 20)

    def test_accumulation_and_repeat_submissions(self):
        records = []
        exposures = {}
        costs = metrics.TaskCosts(exposures)
        content = json.dumps({"observation": {"label": "水"}}, ensure_ascii=False)
        messages = [tool("get_observation"),
                    {"role": "tool", "tool_call_id": "call_1", "content": content}]
        size = len(content.encode("utf-8"))
        with patch.object(metrics, "emit", side_effect=lambda event_type, **data: records.append((event_type, data))):
            costs.tool("get_observation", content)
            costs.request(messages)
            costs.request(messages)
            costs.add_usage(metrics.usage_counts({"prompt_tokens": 100, "completion_tokens": 10,
                "prompt_tokens_details": {"cached_tokens": 60}}))
            costs.add_usage(metrics.usage_counts(None))
            costs.summary("completed")
            # Cross-chat retained results still count, but expired placeholders do not.
            other = metrics.TaskCosts(exposures)
            other.request(messages)
            messages[-1]["content"] = '{"expired": true}'
            other.request(messages)
        summary = records[3][1]
        self.assertEqual(summary["model_calls"], 2)
        self.assertEqual(summary["tool_result_utf8_bytes"], size)
        self.assertEqual(summary["submitted_observation_utf8_bytes"], size * 2)
        self.assertEqual(summary["repeated_observation_utf8_bytes"], size)
        self.assertEqual(summary["tokens"]["non_cached_input_tokens"],
                         {"known_sum": 40, "reported_requests": 1, "unreported_requests": 1})
        self.assertEqual(other.repeated_observation_bytes, size)
        self.assertEqual(records[-2][1]["tool_results"][0]["prior_submissions"], 2)
        self.assertFalse(records[-1][1]["tool_results"][0]["full_observation"])

    def test_default_logs_contain_only_metrics(self):
        stream = io.StringIO()
        with patch("sys.stderr", stream):
            costs = metrics.TaskCosts({})
            costs.tool("get_observation", '{"secret": "private scene"}')
            costs.tool({"malformed": "private name"}, '{}')
            costs.summary("cancelled")
        output = stream.getvalue()
        self.assertNotIn("private scene", output)
        self.assertNotIn("secret", output)
        self.assertNotIn("private name", output)
        for line in output.splitlines():
            self.assertIsInstance(json.loads(line.removeprefix("Cost: ")), dict)


class IntegrationTests(unittest.IsolatedAsyncioTestCase):
    async def test_api_usage_and_failures_without_changing_returned_message(self):
        records = []
        async def respond(request):
            return httpx.Response(200, json={"choices": [{"message": final(), "finish_reason": "stop"}],
                "usage": {"prompt_tokens": 50, "completion_tokens": 5,
                          "prompt_tokens_details": {"cached_tokens": 30}}})
        client = LLMClient("https://example.com/v1", "not-a-real-key", "fixture",
                           transport=httpx.MockTransport(respond))
        costs = metrics.TaskCosts({})
        token = metrics.current.set(costs)
        try:
            with patch.object(metrics, "emit", side_effect=lambda event_type, **data: records.append((event_type, data))):
                self.assertEqual(await client.complete([], []), final())
            self.assertEqual(costs.totals["input_tokens"], 50)
            self.assertEqual(costs.totals["cached_input_tokens"], 30)
            self.assertGreaterEqual(records[0][1]["elapsed_ms"], 0)
            self.assertNotIn("not-a-real-key", json.dumps(records))
        finally:
            metrics.current.reset(token)
            await client.close()

    async def test_missing_usage_http_error_invalid_response_and_cancellation(self):
        for mode in ("missing", "http", "invalid", "cancelled"):
            async def respond(request):
                if mode == "cancelled":
                    raise asyncio.CancelledError()
                if mode == "http":
                    return httpx.Response(503)
                return httpx.Response(200, json={"choices": []} if mode == "invalid" else
                    {"choices": [{"message": final(), "finish_reason": "stop"}]})
            client = LLMClient("https://example.com/v1", "fixture", "fixture",
                               transport=httpx.MockTransport(respond))
            try:
                with patch.object(metrics, "emit") as log:
                    if mode == "missing":
                        await client.complete([], [])
                    else:
                        with self.assertRaises(asyncio.CancelledError if mode == "cancelled" else LLMError):
                            await client.complete([], [])
                data = log.call_args.kwargs
                self.assertIsNone(data["input_tokens"])
                self.assertEqual(data["outcome"], "completed" if mode == "missing" else
                                 "cancelled" if mode == "cancelled" else "failed")
            finally:
                await client.close()

    async def test_agent_counts_requests_tools_and_history_without_behavior_change(self):
        model = Model(tool(), final("ok"), final("again"))
        agent = Agent(Game(), model)
        records = []
        with patch.object(metrics, "emit", side_effect=lambda event_type, **data: records.append((event_type, data))):
            agent.receive(event(1))
            await agent.task
            agent.receive(event(2))
            await agent.task
        summaries = [data for name, data in records if name == "chat_cost"]
        self.assertEqual([s["model_calls"] for s in summaries], [2, 1])
        self.assertEqual([s["tool_calls"] for s in summaries], [1, 0])
        size = len(model.requests[1][-1]["content"].encode("utf-8"))
        self.assertEqual(summaries[0]["tool_result_utf8_bytes"], size)
        self.assertEqual(summaries[1]["repeated_tool_utf8_bytes"], size)
        self.assertIsNone(summaries[0]["tokens"]["input_tokens"]["known_sum"])
        self.assertEqual(len(agent.bridge.sent), 2)


if __name__ == "__main__":
    unittest.main()

