import asyncio
import json
from pathlib import Path
import socket
import sys
import threading
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from agent import Agent
from control import Bridge, BridgeError
from test_agent import Game, Model, event, final, tool


def notice(number, text="50 slimes defeated", **extra):
    return {"v": 1, "type": "game_message", "eventId": f"system:{number}", "text": text,
            "source": "chat_helper", "gameUpdate": number, "receivedAt": "fixture-utc",
            "textTruncated": False, "droppedBefore": 0, **extra}


class GameMessageTests(unittest.IsolatedAsyncioTestCase):
    async def test_notices_are_passive_and_used_in_later_chat(self):
        game, model = Game(), Model(final("The notice reported 50 slimes; test drowned."), final("still remember"))
        agent = Agent(game, model)
        try:
            agent.receive(notice(1))
            agent.receive(notice(2, "test drowned"))
            agent.receive(notice(2, "test drowned"))
            await asyncio.sleep(0)
            self.assertIsNone(agent.task)
            self.assertEqual(model.requests, [])
            self.assertEqual(game.sent, [])
            self.assertEqual(agent.generation, 0)
            self.assertEqual(len(agent.history), 1)
            agent.receive(event(1)); await agent.task
            context = json.loads(model.requests[0][2]["content"])
            self.assertEqual([e["text"] for e in context["recentGameEvents"]], ["50 slimes defeated", "test drowned"])
            self.assertEqual(context["coverage"]["evictedCount"], 0)
            self.assertEqual(agent.history[2], model.requests[0][2])
            agent.receive(notice(3, "blood moon"))
            agent.receive(event(2)); await agent.task
            batches = [json.loads(m['content'])['recentGameEvents'] for m in model.requests[1][1:]
                       if 'recentGameEvents' in (m.get('content') or '')]
            self.assertEqual([[e['eventId'] for e in batch] for batch in batches],
                             [['system:1','system:2'], ['system:3']])
        finally:
            await agent.close()

    async def test_burst_is_bounded_without_model_calls(self):
        agent = Agent(Game(), Model())
        try:
            for i in range(1000):
                agent.receive(notice(i, droppedBefore=2 if i == 999 else 0))
            self.assertEqual(len(agent._game_events), 32)
            self.assertEqual(agent._game_events_evicted, 968)
            self.assertEqual(agent._game_events_dropped, 2)
            self.assertEqual(agent.llm.requests, [])
            self.assertEqual(len(agent.history), 1)
            self.assertEqual(agent._seen, set())
            agent.llm = Model(final())
            agent.receive(event(1)); await agent.task
            delivered = json.loads(agent.history[2]['content'])
            self.assertEqual([row['eventId'] for row in delivered['recentGameEvents']],
                             [f'system:{i}' for i in range(968, 1000)])
            self.assertEqual(delivered['coverage']['missedBeforeDelivery'], 968)
        finally:
            await agent.close()

    async def test_new_notice_between_tool_calls_follows_the_completed_batch(self):
        model = Model(tool(), final())
        class GameWithNotice(Game):
            def get_state(self):
                agent.receive(notice(2, 'blood moon'))
                return super().get_state()
        agent = Agent(GameWithNotice(), model)
        try:
            agent.receive(notice(1))
            agent.receive(event(1)); await agent.task
            messages = model.requests[-1]
            self.assertEqual([m['role'] for m in messages],
                             ['system','user','user','assistant','tool','user','user'])
            self.assertEqual([json.loads(messages[i]['content'])['recentGameEvents'][0]['eventId'] for i in (2,5)],
                             ['system:1','system:2'])
            self.assertEqual(messages[5], agent.history[5])
        finally:
            await agent.close()

    async def test_notice_during_active_reply_does_not_cancel_it(self):
        started, release = asyncio.Event(), asyncio.Event()
        class WaitingModel:
            async def complete(self, messages, tools):
                started.set()
                await release.wait()
                return final("fixture answer")
        game = Game(); agent = Agent(game, WaitingModel())
        try:
            agent.receive(event(1)); await started.wait()
            task, generation = agent.task, agent.generation
            agent.receive(notice(1, "test drowned"))
            self.assertIs(agent.task, task)
            self.assertEqual(agent.generation, generation)
            self.assertFalse(task.cancelled())
            release.set(); await task
            self.assertEqual(game.sent[-1][1]["text"], "fixture answer")
        finally:
            release.set(); await agent.close()


class GameMessageWireTests(unittest.TestCase):
    def test_passive_events_interleave_with_chat_and_responses(self):
        listener = socket.socket(); listener.bind(("127.0.0.1", 0)); listener.listen()
        port = listener.getsockname()[1]
        errors, received = [], []
        def serve():
            try:
                with listener, listener.accept()[0] as peer, peer.makefile("rb") as reader:
                    for frame in ({"v": 1, "type": "hello"}, notice(1), notice(1), event(1)):
                        peer.sendall((json.dumps(frame) + "\n").encode())
                    request = json.loads(reader.readline())
                    peer.sendall((json.dumps(notice(2, "test drowned")) + "\n").encode())
                    peer.sendall((json.dumps({"v": 1, "id": request["id"], "ok": True}) + "\n").encode())
                    peer.recv(1)
            except Exception as error:
                errors.append(error)
        thread = threading.Thread(target=serve, daemon=True); thread.start()
        with Bridge(port, on_event=received.append) as bridge:
            self.assertTrue(bridge.command("get_state")["ok"])
        thread.join(2)
        self.assertFalse(errors)
        self.assertFalse(thread.is_alive())
        self.assertEqual([e["type"] for e in received], ["game_message", "chat_message", "game_message"])

    def test_malformed_notice_is_explicit_connection_error(self):
        listener = socket.socket(); listener.bind(("127.0.0.1", 0)); listener.listen()
        port = listener.getsockname()[1]
        def serve():
            with listener, listener.accept()[0] as peer, peer.makefile("rb") as reader:
                peer.sendall(b'{"v":1,"type":"hello"}\n')
                reader.readline()
                peer.sendall((json.dumps(notice(1, gameUpdate=True)) + "\n").encode())
                peer.recv(1)
        thread = threading.Thread(target=serve, daemon=True); thread.start()
        with Bridge(port) as bridge:
            with self.assertRaisesRegex(BridgeError, "Malformed game message event"):
                bridge.command("get_state")
        thread.join(2)
        self.assertFalse(thread.is_alive())
