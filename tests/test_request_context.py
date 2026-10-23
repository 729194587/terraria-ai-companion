import asyncio
import copy
import json
import unittest
from unittest.mock import patch

from test_agent import Agent, Game, Model, event, final, tool, LLMError
from test_game_messages import notice
import cost_metrics
from agent import history_receipts


class RequestContextTests(unittest.IsolatedAsyncioTestCase):
    async def test_existing_history_is_not_rewritten(self):
        agent = Agent(Game(), Model(final()))
        batch = [tool("get_observation"), {"role": "tool", "tool_call_id": "call_1",
                 "content": json.dumps({"observation": {"large": "x" * 10000}})}]
        agent.history.extend(batch)
        before = copy.deepcopy(agent.history)
        agent.receive(event(1))
        self.assertEqual(agent.history[:-1], before)
        await agent.task
        self.assertEqual(agent.history[:len(before)], before)
        self.assertEqual(batch[1]["content"], before[-1]["content"])

    async def test_full_working_results_compact_receipts_and_actual_costs(self):
        for name, key, reader in (
                ("get_observation", "observation", "read_observation"),
                ("get_exploration_observation", "exploration", "read_exploration_observation")):
            with self.subTest(name=name):
                payload = {"valid": True, key: {"status": "ok", "large": "x" * 10000}}
                model = Model(tool(name), tool("get_state"), final(), final())
                agent = Agent(Game(), model)
                records = []
                with patch("agent." + reader, return_value=payload), patch.object(
                        cost_metrics, "emit", side_effect=lambda kind, **data: records.append((kind, data))):
                    agent.receive(event(1))
                    await agent.task
                    persisted = copy.deepcopy(agent.history)
                    agent.receive(event(2))
                    await agent.task
                for request in model.requests[1:3]:
                    result = next(m for m in request if m["role"] == "tool")
                    self.assertEqual(json.loads(result["content"])[key], payload[key])
                self.assertEqual(agent.history[:len(persisted)], persisted)
                old = next(m for m in model.requests[3] if m["role"] == "tool")
                receipt = json.loads(old["content"])
                self.assertTrue(receipt["expired"])
                self.assertIn(name, receipt["reason"])
                self.assertNotIn(key, receipt)
                for request in model.requests[1:]:
                    # Every persisted call still has its result in the same order.
                    calls = [c["id"] for m in request for c in m.get("tool_calls", [])]
                    results = [m["tool_call_id"] for m in request if m["role"] == "tool"]
                    self.assertEqual(calls, results)
                costs = [d for k, d in records if k == "model_context_cost"]
                for request, measured in zip(model.requests, costs):
                    self.assertEqual(
                        [len(m["content"].encode("utf-8")) for m in request if m["role"] == "tool"],
                        [t["result_utf8_bytes"] for t in measured["tool_results"]])
                self.assertFalse(costs[-1]["tool_results"][0]["full_observation"])

    async def test_events_preserve_prefix_and_never_persist_across_outcomes(self):
        for mode in ("normal", "failed", "budget", "cancelled"):
            with self.subTest(mode=mode):
                entered = asyncio.Event()
                class Waiting(Model):
                    async def complete(self, messages, tools):
                        self.requests.append(copy.deepcopy(messages))
                        entered.set()
                        await asyncio.Future()
                model = (Waiting() if mode == "cancelled" else
                         Model(LLMError("fixture")) if mode == "failed" else
                         Model(tool()) if mode == "budget" else Model(tool(), final()))
                agent = Agent(Game(), model, max_model_calls=1 if mode == "budget" else 8)
                agent.history.extend([{"role": "user", "content": '{"text":"previous"}'}, final()])
                before = copy.deepcopy(agent.history)
                agent.receive(notice(1, droppedBefore=2))
                agent.receive(notice(1, droppedBefore=2))
                agent.receive(event(1))
                if mode == "cancelled":
                    await entered.wait()
                    await agent.close()
                else:
                    await agent.task
                self.assertEqual(agent.history[:len(before)], before)
                for request in model.requests:
                    self.assertEqual(request[:len(before)], before)
                    user = json.loads(request[len(before)]["content"])
                    self.assertEqual(len(user["recentGameEvents"]), 1)
                    self.assertEqual(user["coverage"]["serverDroppedCount"], 2)
                    self.assertEqual(user["text"], "hello 1")
                for message in agent.history[1:]:
                    self.assertNotIn("recentGameEvents", message.get("content") or "")


class ReceiptTests(unittest.TestCase):
    def test_only_known_reader_and_expected_payload_are_compacted(self):
        for name, content in (
                ("get_state", '{"observation":{}}'),
                ("get_observation", '{"error":"failed","valid":false}'),
                ("get_observation", '{"observation":"malformed"}'),
                ("get_observation", "invalid json"),
                ("inspect_tile", '{"exploration":{}}')):
            batch = [tool(name), {"role": "tool", "tool_call_id": "call_1", "content": content}]
            before = copy.deepcopy(batch)
            self.assertEqual(history_receipts(batch), before)
            self.assertEqual(batch, before)


    def assert_observation_receipts(self, name, key):
        for result in (
                {"valid": False, "reason": "snapshot failed", key: {"status": "ok"}},
                {"valid": False, key: {"status": "unavailable", "reason": "not in world"}},
                {"valid": True, key: {"status": "unavailable", "reason": "not in world"}},
                {"valid": 1, key: {"status": "ok"}},
                {key: {"status": "ok"}},
                {"valid": True, "error": "read failed", key: {"status": "ok"}},
                {"valid": True, key: {}},
                {"valid": True, key: {"status": "unexpected"}},
                {"valid": True, key: []},
                {"valid": True, key: None}):
            with self.subTest(result=result):
                batch = [tool(name), {"role": "tool", "tool_call_id": "call_1",
                                      "content": json.dumps(result)}]
                before = copy.deepcopy(batch)
                self.assertEqual(history_receipts(batch), before)
                self.assertEqual(batch, before)
        for status in ("ok", "partial", "stale"):
            batch = [tool(name), {"role": "tool", "tool_call_id": "call_1",
                                  "content": json.dumps({"valid": True, key: {"status": status}})}]
            before = copy.deepcopy(batch)
            persisted = history_receipts(batch)
            self.assertTrue(json.loads(persisted[-1]["content"])["expired"])
            self.assertEqual(batch, before)
            self.assertEqual(persisted[0], batch[0])
            self.assertEqual(persisted[-1]["tool_call_id"], "call_1")

    def test_invalid_full_observation_with_dictionary_is_preserved(self):
        self.assert_observation_receipts("get_observation", "observation")

    def test_invalid_exploration_with_dictionary_is_preserved(self):
        self.assert_observation_receipts("get_exploration_observation", "exploration")
