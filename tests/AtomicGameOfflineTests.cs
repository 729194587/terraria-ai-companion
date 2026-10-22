using System;
using System.Diagnostics;
using System.Text.Json;
using CompanionBridge;
using CompanionBridge.Common.Systems;
using Terraria;

public static class AtomicGameOfflineTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run()
    {
        var controls = CompanionBridge.CompanionBridge.Controls;
        if (controls.SessionId != 0) controls.EndSession(controls.SessionId);
        controls.Reset(); int session = controls.BeginSession(), id = 0;
        Main.myPlayer = 0; Main.netMode = 0;
        var player = Main.player[0] = new Player { position = new(88, 88) };
        MovementSnapshot Start(string operation, string args = "{}")
        {
            controls.Reset();
            player.itemAnimation = player.itemTime = 0; player.selectedItem = 0;
            player.inventory[0] = new Item { pick = 50, createTile = operation == "attack_entity" ? -1 : 2, consumable = true };
            var request = new ControlRequest(++id, "start_atomic", ExpectedGeneration: controls.Generation,
                Operation: operation, Arguments: JsonDocument.Parse(args).RootElement.Clone());
            var pending = controls.Enqueue(session, request, Stopwatch.GetTimestamp());
            controls.Update(true, Stopwatch.GetTimestamp(), new(true, new(88,88), new(0,0),100,100), worldSessionId: "hook-test");
            Check(pending.Result.Ok, "Fixture start accepted");
            AtomicOperations.Before(player, request);
            return controls.AtomicAction;
        }
        var action = Start("select_hotbar_slot", "{\"slot\":3}");
        Check(action.Completed && player.selectedItem == 3, "Selection verified against actual player field");
        action = Start("mine_tile", "{\"x\":99,\"y\":99}");
        Check(action.Status == "failed" && !controls.IsMoving, "World boundary rejected at execution");
        Main.tile[20,20] = new Tile { HasTile = true, TileType = 2 };
        action = Start("mine_tile", "{\"x\":20,\"y\":20}");
        Check(action.Status == "failed" && action.Reason.Contains("reach"), "No remote mining");
        Main.tile[5,5] = new Tile { HasTile = true, TileType = 2 };
        action = Start("mine_tile", "{\"x\":5,\"y\":5}");
        Check(action.Status == "running" && player.controlUseItem, "Mining waits for native effect");
        var request = controls.Input.Atomic;
        Main.mouseX = 123; Main.mouseY = 234; Player.tileTargetX = 99; Player.tileTargetY = 98;
        AtomicOperations.Aim(player);
        Check(Player.tileTargetX == 5 && Main.mouseX == 88, "ItemCheck receives exact tile aim");
        AtomicOperations.RestoreAim();
        Check(Main.mouseX == 123 && Main.mouseY == 234 && Player.tileTargetX == 99 && Player.tileTargetY == 98, "Global cursor restored");
        AtomicOperations.After(player);
        Check(!player.controlUseItem && controls.AtomicAction.Status == "running", "Input submission is never mining completion");
        Main.tile[5,5].HasTile = false;
        AtomicOperations.Before(player, request);
        Check(controls.AtomicAction.Completed && controls.AtomicAction.Evidence != null, "Mining requires tile disappearance");
        action = Start("place_tile", "{\"x\":5,\"y\":5}"); request = controls.Input.Atomic;
        Main.tile[5,5] = new Tile { HasTile = true, TileType = 2 };
        AtomicOperations.Before(player, request);
        Check(controls.AtomicAction.Status == "failed", "Tile appearance without inventory consumption cannot prove placement");
        Main.tile[5,5].HasTile = false;
        Start("place_tile", "{\"x\":5,\"y\":5}"); request = controls.Input.Atomic;
        Main.tile[5,5] = new Tile { HasTile = true, TileType = 2 }; player.inventory[0].stack--;
        AtomicOperations.Before(player, request);
        Check(controls.AtomicAction.Completed, "Expected type plus consumption verifies placement");
        action = Start("attack_entity", "{\"entityRef\":\"stale\"}");
        Check(action.Status == "failed" && action.Reason.Contains("reference"), "Arbitrary entity reference rejected");
        var npc = Main.npc[0] = new NPC { whoAmI = 0, position = new(100,88) };
        var identity = npc.GetGlobalNPC<AtomicNpcIdentity>();
        Check(((AtomicNpcIdentity)identity.Clone(npc, new NPC())).Token != identity.Token, "NPC clone gets a fresh instance identity");
        string reference = "hook-test:npc:0:" + npc.GetGlobalNPC<AtomicNpcIdentity>().Token;
        Start("attack_entity", JsonSerializer.Serialize(new { entityRef = reference })); request = controls.Input.Atomic;
        Main.npc[0] = new NPC { whoAmI = 0, position = new(100,88) };
        // Old instance stays alive as an object; it is no longer the entity in this slot.
        AtomicOperations.Before(player, request);
        Check(controls.AtomicAction.Status == "failed", "Reused NPC slot must invalidate old instance");
        npc = Main.npc[0];
        reference = "hook-test:npc:0:" + npc.GetGlobalNPC<AtomicNpcIdentity>().Token;
        Start("attack_entity", JsonSerializer.Serialize(new { entityRef = reference })); request = controls.Input.Atomic;
        npc.active = false;
        AtomicOperations.Before(player, request);
        Check(controls.AtomicAction.Status == "failed", "Despawn without damage is never attack success");
        npc.active = true;
        Start("attack_entity", JsonSerializer.Serialize(new { entityRef = reference })); request = controls.Input.Atomic;
        npc.life--;
        AtomicOperations.Before(player, request);
        Check(controls.AtomicAction.Completed, "Attack requires observed life decrease");
        Start("use_item");
        controls.RevokeControl();
        AtomicOperations.Aim(player);
        Check(!player.controlUseItem, "Revocation between injection and ItemCheck blocks late item use");
        Main.tile[5,5] = new Tile { HasTile = true, TileType = 10 };
        action = Start("interact_tile", "{\"x\":5,\"y\":5}");
        Check(action.Completed && Main.tile[5,5].TileType == 11, "Supported door checks resulting type");
        Main.netMode = 1;
        action = Start("interact_tile", "{\"x\":5,\"y\":5}");
        Check(action.Status == "failed" && action.Reason.Contains("multiplayer"), "Unsupported multiplayer interaction explicit");
        Main.netMode = 0;
        Start("use_item"); request = controls.Input.Atomic;
        Check(controls.AtomicAction.Status == "running", "Use waits for animation");
        player.itemAnimation = 1; AtomicOperations.After(player); player.itemAnimation = 0;
        AtomicOperations.Before(player, request);
        Check(controls.AtomicAction.Completed, "Generic use verifies native animation cycle only");
        controls.EndSession(session); controls.Reset();
        Console.WriteLine("PASS: actual atomic adapter on game API stubs: reach/bounds, selection, native-use cycle, tile/consumption effects, stale NPCs, doors and cursor restoration (no real game physics)");
    }
}
