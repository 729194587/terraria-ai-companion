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
        async def dispatch(bridge, name, args, chat_id, *, lifecycle=None):
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
        result = json.loads(model.requests[-1][-2]["content"])
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
        self.assertIn("out_of_conservative_80_pixel_reach", game.sent[-1][1]["text"])

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
        async def dispatch(*args, **kwargs):
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


class RecordedApproachTests(unittest.TestCase):
    @staticmethod
    def recorded():
        return {"originX":2148,"originY":218,"tileSize":16,
                "rows":["."*45]*14 + ["."*31+"="*12+"."*2] + ["."*45]*2 + ["#"*45]*12}

    def test_both_reported_platform_targets_accept_body_platforms_and_have_tolerance(self):
        import math
        for tile_x, expected_left in [(2179,34793),(2184,34873)]:
            tx,ty=tile_x*16+8,232*16+8
            region=recovery._landing(34722.652,3718,self.recorded(),tx,ty)
            self.assertEqual(expected_left,region["minPlayerLeftX"])
            self.assertEqual(16,region["maxPlayerLeftX"]-region["minPlayerLeftX"])
            self.assertEqual(3760,region["surfaceY"])
            for left in (region["minPlayerLeftX"],region["maxPlayerLeftX"]):
                self.assertLessEqual(math.hypot(left+10-tx,3718+21-ty),72)
        self.assertTrue(34793 <= 34793.766 <= 34809)  # recorded reachable native stop

    def test_leftward_approach_also_has_reachable_band(self):
        import math
        region=recovery._landing(34980,3718,self.recorded(),2179*16+8,232*16+8)
        self.assertEqual(16,region["maxPlayerLeftX"]-region["minPlayerLeftX"])
        for left in (region["minPlayerLeftX"],region["maxPlayerLeftX"]):
            self.assertLessEqual(math.hypot(left+10-(2179*16+8),19),72)

    def test_platform_permission_does_not_allow_solid_unknown_or_hazard_body_cells(self):
        for cell in ("#","?","!","h","1"):
            terrain=self.recorded()
            row=list(terrain["rows"][14]);row[31]=cell;terrain["rows"][14]="".join(row)
            with self.assertRaisesRegex(ValueError,"approach_blocked"):
                recovery._landing(34722.652,3718,terrain,2184*16+8,232*16+8)

    def test_entire_band_not_just_near_edge_needs_safe_support(self):
        terrain=self.recorded()
        # Near edge X=34793 ends at 34813 in column2175. Far edge ends at
        # 34829 in column2176, which also needs verified support.
        row=list(terrain["rows"][17]);row[2176-2148]="?";terrain["rows"][17]="".join(row)
        with self.assertRaisesRegex(ValueError,"no_proven_safe_support"):
            recovery._landing(34722.652,3718,terrain,2179*16+8,232*16+8)
