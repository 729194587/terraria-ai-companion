using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using CompanionBridge.Motion;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace CompanionBridge.ServerIpc;

public sealed class LoadoutTestPlayer : ModPlayer
{
    public override void PostUpdateEquips()
    {
        if (Player == ServerNavigationLoadoutFixture.JumpActor) Player.jumpSpeedBoost += 3f;
        if (Player == ServerNavigationLoadoutFixture.SpeedActor) Player.moveSpeed += 1f;
        if (Player != ServerNavigationLoadoutFixture.LimitedWingActor) return;
        Player.wingTimeMax = 1;
        // Limit launch resources while preserving native airborne fuel conversion.
        if(Player.velocity.Y==0)Player.wingTime = Math.Min(Player.wingTime, 1);
    }
}

// Runs only in the isolated terrain fixture. Sample the actor even if the
// root task refuses the loadout before creating a movement child.
internal static class ServerNavigationLoadoutFixture
{
    private static string sampleName;
    private static IEnumerator<Route> budgetSearch;
    private static readonly List<double> sliceTimes=new();
    private static bool budgetFinished;
    private static readonly List<object> frames = new();

    private sealed class TestDash(string mode) : INavigationAirAction
    {
        public string Key => "CompanionBridge/TestAirDash"+mode;
        public NavigationAirImpulse Capture(Player player,out int cooldown)
        {
            if(player==dashActor && mode=="capture_fault") {faults++;throw new InvalidOperationException("fixture capture fault");}
            cooldown = (int)Math.Max(0,readyTick-(long)Main.GameUpdateCount);
            return player == dashActor && mode==dashMode ? new(Key,8,-3,100000) : null;
        }
        public void Activate(Player player,int direction)
        {
            if(mode=="activate_fault"){faults++;throw new InvalidOperationException("fixture activation fault");}
            if(player != dashActor || (long)Main.GameUpdateCount < readyTick)
                throw new InvalidOperationException("invalid diagnostic dash activation");
            player.velocity = new Vector2(direction*8,-3);
            readyTick=(long)Main.GameUpdateCount+100000;
            dashCount++;
        }
    }
    private static Player dashActor;
    private static long readyTick;
    private static int dashCount,faults;
    private static string dashMode;
    internal static Player JumpActor {get;private set;}
    internal static Player SpeedActor {get;private set;}
    internal static Player LimitedWingActor { get; private set; }

    internal static void Prepare(Player player, string loadout, string scene, int column, int floor,int shelfHeight)
    {
        JumpActor = loadout == "boost_jump" ? player : null;
        SpeedActor = loadout == "boost_run" ? player : null;
        faults=0;dashMode=loadout is "activate_fault" or "capture_fault"?loadout:"";
        dashActor = loadout is "dash" or "cooling_dash" or "activate_fault" or "capture_fault" ? player : null;
        readyTick = loadout == "cooling_dash" ? (long)Main.GameUpdateCount + 100000 : 0;
        budgetSearch?.Dispose();
        budgetSearch=null;
        budgetFinished=false;
        sliceTimes.Clear();
        dashCount = 0;
        if (dashActor != null) ModContent.GetInstance<CompanionBridge>().Call("RegisterNavigationAirAction", new TestDash(dashMode));
        LimitedWingActor = loadout.StartsWith("limited_") ? player : null;
        loadout = loadout.Replace("limited_", "");
        int bottleItem = loadout switch {
            "sand" => ItemID.SandstorminaBottle, "blizzard"=>ItemID.BlizzardinaBottle,
            "balloon"=>ItemID.ShinyRedBalloon, "cloud_balloon"=>ItemID.CloudinaBalloon,
            "sand_balloon"=>ItemID.SandstorminaBalloon, "blizzard_balloon"=>ItemID.BlizzardinaBalloon,
            "bundle" or "bundle_angel" or "shaft_bundle"=>ItemID.BundleofBalloons, "separate"=>ItemID.SandstorminaBottle,
            "duplicate_cloud"=>ItemID.CloudinaBottle, _=>0};
        if (bottleItem==0 && loadout is not ("shaft_rope" or "shaft_platform" or "shaft_blocked" or "shaft_bundle" or "basic" or "cloud" or "cloud_angel" or "angel_hook" or "angel_rocket_hook" or "angel" or "hook" or "boost_run" or "boost_jump" or "dash" or "cooling_dash" or "capture_fault" or "activate_fault"))
            throw new ArgumentException("Unknown navigation test loadout");
        foreach (var item in player.inventory) item.TurnToAir();
        foreach (var item in player.armor) item.TurnToAir();
        foreach (var item in player.miscEquips) item.TurnToAir();
        if(bottleItem!=0) player.armor[3].SetDefaults(bottleItem);
        if(loadout=="separate")
        {
            player.armor[4].SetDefaults(ItemID.BlizzardinaBottle);
            player.armor[5].SetDefaults(ItemID.CloudinaBottle);
            player.armor[6].SetDefaults(ItemID.ShinyRedBalloon);
        }
        if(loadout=="duplicate_cloud") player.armor[4].SetDefaults(ItemID.CloudinaBalloon);
        if (loadout is "cloud" or "cloud_angel") player.armor[3].SetDefaults(ItemID.CloudinaBottle);
        if (loadout is "angel" or "cloud_angel" or "angel_hook" or "angel_rocket_hook" or "bundle_angel") player.armor[4].SetDefaults(ItemID.AngelWings);
        if(loadout=="angel_rocket_hook")player.armor[5].SetDefaults(ItemID.RocketBoots);
        if (loadout is "hook" or "angel_hook" or "angel_rocket_hook") player.miscEquips[4].SetDefaults(ItemID.GrapplingHook);
        if (scene == "frontier_nohook")
        {
            player.position = new Vector2((column + 5) * 16 - 2, (floor + 10) * 16 - player.height);
            player.fallStart = floor + 10;
        }
        if(loadout is "shaft_rope" or "shaft_blocked" or "shaft_bundle")
        {
            for(int row=floor-16;row<floor;row++)
            {
                var tile=Main.tile[column+4,row];
                tile.HasTile=true;tile.TileType=TileID.Rope;
            }
        }
        if(loadout=="shaft_blocked")
        {
            var ceiling=Main.tile[column+4,floor-14];
            ceiling.HasTile=true;ceiling.TileType=TileID.Dirt;
        }
        if(loadout is "shaft_platform" or "shaft_blocked" or "shaft_bundle")
        {
            player.inventory[0].SetDefaults(ItemID.WoodPlatform);
            player.inventory[0].stack=16;
        }
        if(scene=="wide_gap")
        {
            // Crossing must clear the gap. Prevent a ground-only detour along the bottom.
            for(int x=column+15;x<=column+20;x++)
                for(int y=floor;y<floor+10;y++)
                {
                    var ground=Main.tile[x,y];ground.HasTile=true;ground.TileType=TileID.Dirt;
                }
            for(int x=column+4;x<=column+14;x++)
            {
                var tile = Main.tile[x,floor+9];
                tile.TileType = TileID.Spikes;
                tile.HasTile = true;
            }
        }
        if(scene=="flat" && loadout is "bundle" or "bundle_angel" && shelfHeight==0)
        {
            for(int x=column-2;x<=column+110;x++)
                for(int y=floor-80;y<=floor;y++)
                {
                    var tile=Main.tile[x,y];tile.ClearEverything();
                    if(y==floor){tile.HasTile=true;tile.TileType=TileID.Dirt;}
                }
        }
        if(shelfHeight>0)
        {
            if(shelfHeight>64 || scene!="flat")throw new ArgumentException("Invalid bottle shelf");
            for(int x=column-22;x<=column+22;x++)
                for(int y=floor-70;y<floor;y++)
                {
                    var tile=Main.tile[x,y];tile.ClearEverything();
                    if(y==floor-shelfHeight && Math.Abs(x-column)<=9)
                    {tile.HasTile=true;tile.TileType=TileID.Platforms;}
                }
            scene="bottle"+shelfHeight;
        }
        sampleName = scene + "-" + (LimitedWingActor == null ? "" : "limited_") + loadout;
        frames.Clear();
    }

    internal static void Observe(Player player, bool horizontalInput, bool jumpInput)
    {
        if (sampleName == null) return;
        if(!budgetFinished && sampleName is "flat-bundle" or "flat-bundle_angel" && frames.Count>=15)
        {
            if(budgetSearch==null)
            {
                var pose=NativeMotionWorld.Observe(player);
                var ph=NativeNavigation.Parameters(player);
                // Reject every completed edge so running jumps exhaust every runway,
                // chain length and delay. Wings also evaluate their braking alternatives.
                var task=new NavigationTask{AllowedPose=_=>false};
                budgetSearch=task.TravelCandidates(pose,LandingRegion.AtDestination(pose.Px+430,pose.Py+42),
                    ph,new NativeMotionWorld(pose)).GetEnumerator();
            }
            long started=Stopwatch.GetTimestamp();
            budgetFinished=NavigationTask.AdvanceTravel(budgetSearch,out _);
            sliceTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if(budgetFinished)
            {
                budgetSearch.Dispose();budgetSearch=null;
                File.WriteAllText(Path.Combine(Main.SavePath,sampleName+"-budget.json"),JsonSerializer.Serialize(sliceTimes));
            }
        }
        ref var cloud = ref player.GetJumpState(ExtraJump.CloudInABottle);
        frames.Add(new { tick = Main.GameUpdateCount, x = player.position.X, feetY = player.position.Y + player.height,
            player.pulley, platforms=player.inventory[0].type==ItemID.WoodPlatform?player.inventory[0].stack:0,
            sand=player.GetJumpState(ExtraJump.SandstormInABottle).Active,
            blizzard=player.GetJumpState(ExtraJump.BlizzardInABottle).Active,
            faults,dashCount, horizontalInput, jumpInput, player.grapCount,
            cloud.Enabled, cloud.Active, player.wingsLogic, player.wingTime, player.wingTimeMax,
            basicHook = player.miscEquips[4].type == ItemID.GrapplingHook });
        string stop = Path.Combine(Main.SavePath, "loadout-finish");
        if (!File.Exists(stop)) return;
        File.Delete(stop);
        File.WriteAllText(Path.Combine(Main.SavePath, sampleName + "-motion.json"), JsonSerializer.Serialize(frames));
        sampleName = null;
        LimitedWingActor = JumpActor = SpeedActor = dashActor = null;
        frames.Clear();
    }
}
