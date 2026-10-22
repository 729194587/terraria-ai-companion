import copy
import json
import unittest
from unittest.mock import patch
from test_agent import Agent, Game, Model, event, final, tool
import mining_recovery as recovery


def failure(status="failed", reason="out_of_conservative_80_pixel_reach"):
    return {"valid": True, "status": status, "reason": reason, "operation": "mine_tile",
            "evidence": {"targetTileX": 14, "targetTileY": 8, "targetTileType": 19},
            "endEvidence": {"worldSessionId": "world", "gameUpdate": 1}}


def terrain(x=100):
    return {"valid": True, "state": {"inWorld": True, "life": 100,
            "position": {"x": x, "y": 118}, "velocity": {"x": 0, "y": 0}},
            "terrain": {"originX": 0, "originY": 0, "tileSize": 16,
                        "rows": ["." * 45] * 10 + ["#" * 45],
                        "grounded": True, "supported": True, "playerWidth": 20, "playerHeight": 42}}


def tile():
    return {"valid": True, "worldSessionId": "world", "gameUpdate": 10,
            "data": {"x": 14, "y": 8, "cell": {"knowledge": "read", "hasTile": True,
                "tileType": 19, "collisionKnowledge": "known", "liquidAmount": 0}}}


class RecoveryTests(unittest.IsolatedAsyncioTestCase):
    async def run_agent(self, replies, *, first=None, extra=None):
        game = Game()
        calls = []
        first = first or failure()
        results = iter([first, *replies])
        async def dispatch(bridge, name, args, chat_id):
            calls.append((name, copy.deepcopy(args)))
            return copy.deepcopy(next(results))
        answers = [tool("mine_tile", '{"x":14,"y":8}')]
        if extra:
            answers.append(extra)
        answers.append(final("完成了"))
        model = Model(*answers)
        agent = Agent(game, model)
        with patch("atomic_operations.dispatch", dispatch):
            agent.receive(event(1))
            await agent.task
        return calls, game, model

    async def test_agent_observes_navigates_reobserves_and_retries_original_once(self):
        nav = {"valid": True, "status": "completed", "endEvidence": {"worldSessionId": "world", "gameUpdate": 5}}
        retry = {"valid": True, "status": "completed", "reason": "tile_removed"}
        calls, game, model = await self.run_agent([terrain(), tile(), nav, terrain(156), tile(), retry])
        self.assertEqual(["mine_tile", "observe_terrain", "inspect_tile", "navigate_to_landing_region",
                          "observe_terrain", "inspect_tile", "mine_tile"], [n for n, _ in calls])
        self.assertEqual(calls[0], calls[-1])
        result = json.loads(model.requests[-1][-1]["content"])
        self.assertEqual(1, result["recovery"]["retryCount"])
        self.assertEqual("completed", result["status"])

    async def test_no_route_changed_target_or_navigation_failure_stops(self):
        unsafe = terrain(); unsafe["terrain"]["rows"][10] = "?" * 45
        changed = tile(); changed["data"]["cell"]["tileType"] = 2
        for replies in ([unsafe, tile()], [terrain(), changed],
                        [terrain(), tile(), {"valid": True, "status": "timeout", "reason": "no_path"}]):
            calls, game, _ = await self.run_agent(replies)
            self.assertEqual(1, sum(n == "mine_tile" for n, _ in calls))
            self.assertIn("行动未完成", game.sent[-1][1]["text"])

    async def test_retry_failure_cannot_start_second_recovery_or_another_action(self):
        nav = {"valid": True, "status": "completed", "endEvidence": {"worldSessionId": "world", "gameUpdate": 5}}
        calls, game, _ = await self.run_agent([terrain(), tile(), nav, terrain(156), tile(), failure()],
                                            extra=tool("mine_tile", '{"x":14,"y":8}'))
        self.assertEqual(2, sum(n == "mine_tile" for n, _ in calls))
        self.assertIn("previous_atomic_action_failed", game.sent[-1][1]["text"])

    async def test_post_navigation_revalidates_world_distance_and_target(self):
        nav = {"valid": True, "status": "completed", "endEvidence": {"worldSessionId": "world", "gameUpdate": 5}}
        changed = tile(); changed["data"]["cell"]["hasTile"] = False
        other_world = tile(); other_world["worldSessionId"] = "other"
        for after, target in [(terrain(), tile()), (terrain(156), changed), (terrain(156), other_world)]:
            calls, _, _ = await self.run_agent([terrain(), tile(), nav, after, target])
            self.assertEqual(1, sum(n == "mine_tile" for n, _ in calls))

    async def test_other_failures_timeout_cancel_unknown_never_recover(self):
        for first in (failure("timeout"), failure("cancelled"), failure("unknown"),
                      failure(reason="item_aim_requires_default_zoom")):
            calls, _, _ = await self.run_agent([], first=first, extra=tool("use_item"))
            self.assertEqual(["mine_tile"], [n for n, _ in calls])

    async def test_use_cycle_cannot_be_reported_as_successful_summon(self):
        game = Game()
        model = Model(tool("use_item"), final("雀杖已经成功召唤！"))
        async def dispatch(*args):
            return {"valid": True, "status": "completed", "reason": "native_item_use_cycle_finished",
                    "evidence": {"effectStatus": "unconfirmed"}}
        agent = Agent(game, model)
        with patch("atomic_operations.dispatch", dispatch):
            agent.receive(event(1)); await agent.task
        self.assertIn("未确认", game.sent[-1][1]["text"])
        self.assertNotIn("已经成功召唤", game.sent[-1][1]["text"])

    async def test_recovery_cancellation_propagates(self):
        import asyncio
        async def cancelled(*args):
            raise asyncio.CancelledError()
        with self.assertRaises(asyncio.CancelledError):
            await recovery.recover({"x":14,"y":8}, failure(), cancelled)
