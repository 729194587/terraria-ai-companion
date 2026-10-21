import asyncio
import copy
import io
import json
from pathlib import Path
import socket
import sys
import threading
import tempfile
import unittest
from unittest.mock import patch

import httpx

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from agent import Agent, read_state, TOOLS
from control import Bridge, BridgeError
from llm import LLMClient, LLMError, load_config


def event(n, **extra):
    return {"v": 1, "type": "chat_message", "eventId": str(n), "senderId": 0,
            "senderName": "Human", "text": f"hello {n}", "isSelf": False, **extra}


class Game:
    def __init__(self):
        self.sent = []
        self.fail = False

    def command(self, cmd, **params):
        self.sent.append((cmd, params))
        return {"ok": True}

    def get_state(self):
        if self.fail:
            raise BridgeError("fixture read failed")
        return {"state": {"inWorld": True, "position": {"x": 100, "y": 200},
                          "velocity": {"x": 0, "y": 0}, "life": 80, "lifeMax": 100}}

    def get_terrain(self):
        return {**self.get_state(), "terrain": {"originX": 0, "originY": 0, "tileSize": 16,
            "rows": ["." * 45] * 29, "grounded": True, "supported": True,
            "playerWidth": 20, "playerHeight": 42}}


class Model:
    def __init__(self, *answers):
        self.answers = list(answers)
        self.requests = []

    async def complete(self, messages, tools):
        self.requests.append(copy.deepcopy(messages))
        answer = self.answers.pop(0)
        if isinstance(answer, Exception):
            raise answer
        return answer


def final(text="hi"):
    return {"role": "assistant", "content": text}


def tool(name="get_state", args="{}"):
    return {"role": "assistant", "content": None, "tool_calls": [
        {"id": "call_1", "type": "function", "function": {"name": name, "arguments": args}}]}


class AgentTests(unittest.IsolatedAsyncioTestCase):
    async def test_direct_multiturn_and_self_dedup(self):
        game, model = Game(), Model(final("你好"), final("again"))
        agent = Agent(game, model)
        agent.receive(event(1))
        await agent.task
        agent.receive(event(1))
        agent.receive(event(2, isSelf=True))
        self.assertEqual(len(model.requests), 1)
        agent.receive(event(3))
        await agent.task
        self.assertEqual([m["role"] for m in model.requests[1]], ["system", "user", "assistant", "user"])
        self.assertEqual([p["text"] for _, p in game.sent], ["你好", "again"])

    async def test_real_tool_result_format_and_history(self):
        game, model = Game(), Model(tool(), final("80 HP"), final("ok"))
        agent = Agent(game, model)
        agent.receive(event(1))
        await agent.task
        request = model.requests[1]
        self.assertEqual([m["role"] for m in request], ["system", "user", "assistant", "tool"])
        self.assertEqual(request[-1]["tool_call_id"], "call_1")
        result = json.loads(request[-1]["content"])
        self.assertEqual(result["player"]["life"], 80)
        self.assertFalse(result["entities"]["available"])
        self.assertEqual(len(result["terrain"]["rows"]), 29)
        agent.receive(event(2))
        await agent.task
        self.assertEqual(model.requests[2][2:4], request[2:4])

    async def test_read_failure_and_invalid_tool_are_errors(self):
        for call in (tool(), tool("navigate"), tool(args='{"x":1}'), tool(args="not json")):
            game, model = Game(), Model(call, final("cannot read"))
            game.fail = True
            agent = Agent(game, model)
            agent.receive(event(1))
            await agent.task
            result = json.loads(model.requests[1][-1]["content"])
            self.assertFalse(result["valid"])
            self.assertIn("error", result)

    async def test_preemption_late_uncancellable_result_keeps_all_users(self):
        started, release = asyncio.Event(), asyncio.Event()
        class Delayed(Model):
            async def complete(self, messages, tools):
                self.requests.append(copy.deepcopy(messages))
                if len(self.requests) == 1:
                    started.set()
                    try:
                        await release.wait()
                    except asyncio.CancelledError:
                        await release.wait()  # Simulate a transport ignoring cancellation.
                    return final("OLD")
                return final("NEW")
        game, model = Game(), Delayed()
        agent = Agent(game, model)
        agent.receive(event(1))
        await started.wait()
        old = agent.task
        agent.receive(event(2))
        await asyncio.wait_for(agent.task, 1)
        release.set()
        await old
        self.assertEqual([p["text"] for _, p in game.sent], ["NEW"])
        self.assertEqual(sum(m["role"] == "user" for m in agent.history), 2)
        self.assertEqual(sum(m["role"] == "assistant" for m in agent.history), 1)

    async def test_errors_and_budgets_never_send_success(self):
        for answers, models, tools in (([LLMError("HTTP failed")], 4, 3),
                                       ([{"role": "assistant", "content": None}], 4, 3)):
            game, model = Game(), Model(*answers)
            agent = Agent(game, model, models, tools)
            with patch("sys.stderr", new_callable=io.StringIO) as stderr:
                agent.receive(event(1))
                await agent.task
                self.assertIn("Agent error:", stderr.getvalue())
            self.assertEqual(game.sent, [])
            self.assertEqual(sum(m["role"] == "user" for m in agent.history), 1)
            self.assertFalse(any(m["role"] == "assistant" and m.get("content") for m in agent.history))

    async def test_budget_exhaustion_sends_explicit_unconfirmed_result(self):
        for models,tools in ((1,3),(4,0)):
            game=Game();agent=Agent(game,Model(tool()),models,tools)
            agent.receive(event(1));await agent.task
            self.assertEqual(agent.last_movement_result['status'],'budget_exhausted')
            self.assertIn('未确认抵达',game.sent[-1][1]['text'])
            self.assertFalse(any(m['role']=='tool' for m in agent.history))

    async def test_ipc_send_failure_does_not_commit_assistant(self):
        game = Game()
        game.command = lambda *a, **k: (_ for _ in ()).throw(BridgeError("disconnected"))
        agent = Agent(game, Model(final()))
        with patch("sys.stderr", new_callable=io.StringIO) as stderr:
            agent.receive(event(1))
            await agent.task
            self.assertIn("disconnected", stderr.getvalue())
        self.assertEqual(len(agent.history), 2)


class LLMTests(unittest.IsolatedAsyncioTestCase):
    async def test_dotenv_and_environment_precedence(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / ".env"
            path.write_text('# local\nLLM_BASE_URL=https://api.deepseek.com\n'
                            'LLM_API_KEY="test-only"\nLLM_MODEL=deepseek-flash\n', encoding="utf-8-sig")
            with patch.dict("os.environ", {}, clear=True):
                self.assertEqual(load_config(path), {"LLM_BASE_URL": "https://api.deepseek.com",
                    "LLM_API_KEY": "test-only", "LLM_MODEL": "deepseek-flash"})
                with patch.dict("os.environ", {"LLM_MODEL": "override"}):
                    self.assertEqual(load_config(path)["LLM_MODEL"], "override")
                self.assertEqual(load_config(Path(directory) / "missing"), {})
                path.write_text('LLM_API_KEY="unclosed', encoding="utf-8")
                with self.assertRaisesRegex(LLMError, "Unclosed"):
                    load_config(path)

    async def test_wire_protocol(self):
        def handler(request):
            self.assertEqual(str(request.url), "https://example.test/v1/chat/completions")
            self.assertEqual(request.headers["Authorization"], "Bearer fake-key")
            data = json.loads(request.content)
            self.assertEqual(data["tools"], TOOLS)
            self.assertEqual(data["model"], "deepseek-flash")
            self.assertEqual(data["thinking"], {"type": "disabled"})
            self.assertNotIn("extra_body", data)
            return httpx.Response(200, json={"choices": [{"finish_reason": "tool_calls", "message": tool()}]})
        client = LLMClient("https://example.test/v1", "fake-key", "deepseek-flash", transport=httpx.MockTransport(handler))
        try:
            self.assertEqual(await client.complete([], TOOLS), tool())
        finally:
            await client.close()

    async def test_non_thinking_tool_round_trip_and_next_user_turn(self):
        requests = []
        def handler(request):
            self.assertEqual(request.headers["Authorization"], "Bearer fake-key")
            data = json.loads(request.content)
            requests.append(data)
            self.assertEqual(data["model"], "deepseek-flash")
            self.assertEqual(data["thinking"], {"type": "disabled"})
            self.assertEqual(data["tools"], TOOLS)
            if len(requests) == 1:
                answer = {**tool(), "reasoning_content": None}
                reason = "tool_calls"
            else:
                messages = data["messages"]
                self.assertEqual(messages[2], tool())
                self.assertEqual(messages[3]["role"], "tool")
                self.assertEqual(messages[3]["tool_call_id"], tool()["tool_calls"][0]["id"])
                result = json.loads(messages[3]["content"])
                self.assertTrue(result["valid"])
                self.assertEqual(result["player"]["life"], 80)
                self.assertTrue(all("reasoning_content" not in message for message in messages))
                if len(requests) == 2:
                    self.assertEqual([m["role"] for m in messages], ["system", "user", "assistant", "tool"])
                    answer = final("80 HP")
                else:
                    self.assertEqual(len(requests), 3)
                    self.assertEqual(messages[4], final("80 HP"))
                    self.assertEqual(messages[5]["role"], "user")
                    answer = final("You asked about my state.")
                reason = "stop"
            return httpx.Response(200, json={"choices": [{"finish_reason": reason, "message": answer}]})
        client = LLMClient("https://example.test/v1", "fake-key", "deepseek-flash",
                           transport=httpx.MockTransport(handler))
        game = Game()
        agent = Agent(game, client)
        try:
            agent.receive(event(1, text="Read your state."))
            await agent.task
            agent.receive(event(2, text="What did I ask?"))
            await agent.task
            self.assertEqual(len(requests), 3)
            self.assertEqual([params["text"] for _, params in game.sent],
                             ["80 HP", "You asked about my state."])
            self.assertEqual(agent.history[2:4], requests[1]["messages"][2:4])
        finally:
            await agent.close()
            await client.close()

    async def test_http_request_cancellation_propagates(self):
        started, cancelled = asyncio.Event(), asyncio.Event()
        async def handler(request):
            self.assertEqual(json.loads(request.content)["thinking"], {"type": "disabled"})
            started.set()
            try:
                await asyncio.Event().wait()
            finally:
                cancelled.set()
        client = LLMClient("https://example.test/v1", "fake-key", "deepseek-flash",
                           transport=httpx.MockTransport(handler))
        task = asyncio.create_task(client.complete([{"role": "user", "content": "hello"}], TOOLS))
        try:
            await asyncio.wait_for(started.wait(), 1)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await asyncio.wait_for(task, 1)
            self.assertTrue(cancelled.is_set())
        finally:
            task.cancel()
            await asyncio.gather(task, return_exceptions=True)
            await client.close()

    async def test_http_timeout_and_bad_responses(self):
        for response in (httpx.Response(401), httpx.Response(200, text="invalid"),
                         httpx.Response(200, json={"choices": []}),
                         httpx.Response(200, json={"choices": [{"message": [], "finish_reason": "stop"}]}),
                         httpx.Response(200, json={"choices": [{"message": final(), "finish_reason": "length"}]}),
                         httpx.ReadTimeout("fixture"), httpx.ConnectError("fixture")):
            def handler(request):
                if isinstance(response, Exception):
                    raise response
                return response
            client = LLMClient("https://example.test/v1", "fake-key", "test", transport=httpx.MockTransport(handler))
            try:
                with self.assertRaises(LLMError):
                    await client.complete([], TOOLS)
            finally:
                await client.close()


class EventWireTests(unittest.TestCase):
    def test_concurrent_commands_route_out_of_order_responses(self):
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        listener.listen()
        port = listener.getsockname()[1]
        errors = []
        def serve():
            try:
                with listener.accept()[0] as connection, connection.makefile("rb") as reader:
                    connection.sendall(b'{"v":1,"type":"hello"}\n')
                    requests = [json.loads(reader.readline()) for _ in range(2)]
                    for request in reversed(requests):
                        connection.sendall((json.dumps({"v": 1, "id": request["id"], "ok": True,
                                                       "status": request["cmd"]}) + "\n").encode())
            except Exception as error:
                errors.append(error)
            finally:
                listener.close()
        server = threading.Thread(target=serve)
        server.start()
        results = {}
        with Bridge(port) as bridge:
            def command(name):
                results[name] = bridge.command(name)["status"]
            threads = [threading.Thread(target=command, args=(name,)) for name in ("get_state", "get_terrain")]
            for thread in threads:
                thread.start()
            for thread in threads:
                thread.join(3)
        server.join(3)
        self.assertEqual(results, {"get_state": "get_state", "get_terrain": "get_terrain"})
        self.assertFalse(errors)

    def test_push_without_request_then_interleaved_replies_and_disconnect(self):
        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        listener.listen()
        received, errors = [], []
        def serve():
            try:
                with listener.accept()[0] as connection, connection.makefile("rb") as reader:
                    frames = [{"v": 1, "type": "hello"}, event(1), event(1), event(2, isSelf=True)]
                    for frame in frames:
                        connection.sendall((json.dumps(frame) + "\n").encode())
                    for _ in range(2):
                        request = json.loads(reader.readline())
                        connection.sendall((json.dumps(event(3 + request["id"])) + "\n").encode())
                        connection.sendall((json.dumps({"v": 1, "id": request["id"], "ok": True}) + "\n").encode())
            except Exception as error:
                errors.append(error)
            finally:
                listener.close()
        thread = threading.Thread(target=serve)
        thread.start()
        pushed = threading.Event()
        def dispatch(frame):
            received.append(frame)
            pushed.set()
        with Bridge(listener.getsockname()[1], on_event=dispatch) as bridge:
            self.assertTrue(pushed.wait(1), "Push must arrive without a command")
            self.assertTrue(bridge.command("get_state")["ok"])
            self.assertTrue(bridge.command("get_terrain")["ok"])
            thread.join(2)
            with self.assertRaises(BridgeError):
                bridge.command("get_state")
        self.assertFalse(errors)
        self.assertEqual([e["eventId"] for e in received], ["1", "4", "5"])


if __name__ == "__main__":
    unittest.main()
