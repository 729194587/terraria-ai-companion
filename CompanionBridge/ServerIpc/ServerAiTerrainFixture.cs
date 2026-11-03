using System;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Enums;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Only called inside the existing isolated action-fixture gate. Geometry and
// initial-state injection are diagnostics, never navigation execution helpers.
internal static class ServerAiTerrainFixture
{
    private static int settle;
    private static object pending;
    private static readonly System.Collections.Generic.List<object> frames=new();
    private static bool recording;
    private static ServerAiNavigation.Run lastRun;
    private static ulong? inputTick;
    private static bool inputLeft,inputRight,inputJump;
    private static string caseName;
    private static string recoveryMode;
    private static bool recoveryFired;
    private static int recoveryLeft,recoveryFloor;
    internal static void RecordInput(Player p)
    {
        if(!Program.LaunchParameters.ContainsKey("-companionterraintest"))return;
        inputTick=Main.GameUpdateCount;
        inputLeft=p.controlLeft;inputRight=p.controlRight;inputJump=p.controlJump;
    }
    internal static void Observe(Player p)
    {
        if(!Program.LaunchParameters.ContainsKey("-companionterraintest")||caseName==null)return;
        bool running=ActorControl.Driver?.Running==true;
        var run=(ActorControl.Driver as ServerAiNavigation)?.Current;
        // A plan can fail in its first update. Still save that real terminal
        // observation even if no earlier frame saw Running=true.
        if(run==null||!running&&!recording&&ReferenceEquals(run,lastRun))return;
        lastRun=run;
        NetworkStateSync.Require(p);
        var o=ServerAiNavigation.Observe(p);
        var nav=run.Snapshot;
        frames.Add(new{tick=Main.GameUpdateCount,x=p.position.X,y=p.position.Y,vx=p.velocity.X,vy=p.velocity.Y,
            o.State.Grounded,o.SafeSupport,o.Settled,left=p.controlLeft,right=p.controlRight,jump=p.controlJump,
            injectedInput=inputTick.HasValue?new{tick=inputTick,left=inputLeft,right=inputRight,jump=inputJump}:null,
            expectedPosition=nav?.ExpectedPosition,expectedVelocity=nav?.ExpectedVelocity,
            action=nav?.Action,frame=nav?.Frame,status=nav?.Status,maxError=nav?.MaxError,
            reason=nav?.Reason,nav?.PlanNumber,nav?.Expansions,nav?.PlanMilliseconds,nav?.ReachedGoal,nav?.TimeBudgetReached,
            nav?.CompletedActions,nav?.CompletedJumps,nav?.RecoveryCount,nav?.RecoveryReplans,
            nav?.RecoveryReason,nav?.RecoveryPosition,nav?.RecoveryInputsReleased,nav?.FailedEdgeCount,run.Updates});
        recording=running;
        if(!running)File.WriteAllText(Path.Combine(Main.SavePath,"terrain-motion-"+caseName+".json"),JsonSerializer.Serialize(frames));
    }
    internal static void Tick(Player p)
    {
        if(!Program.LaunchParameters.ContainsKey("-companionterraintest"))return;
        // Tick -> native Main.Update -> Observe is one synchronous fixture frame.
        // Main.Update increments GameUpdateCount after the movement boundary, so
        // retain the actual sample tick rather than requiring the post-update tick.
        inputTick=null;
        // Terrain-only diagnostic: exclude NPC combat/knockback from these motion scenes.
        // Production navigation keeps its life-loss and velocity guards.
        foreach(var npc in Main.npc)if(npc.active&&!npc.townNPC)npc.active=false;
        // Explicit disturbance inside the existing copied-world/main-thread gate.
        // This is not a production steering or recovery path.
        var active=(ActorControl.Driver as ServerAiNavigation)?.Current;
        if (recoveryMode!=null&&!recoveryFired&&active?.Status=="running"&&active.Updates>=8)
        {
            if(!Program.LaunchParameters.ContainsKey("-companionnavrecoverytest"))
                throw new InvalidOperationException("Recovery disturbance requires its explicit fixture flag");
            NetworkStateSync.Require(p);
            if (recoveryMode is "offset" or "air" or "cancel_air")
            {
                p.position.X-=32;
                if(recoveryMode!="offset")p.position.Y-=160;
            }
            else
            {
                int height=recoveryMode=="high_wall"?12:2;
                for(int x=recoveryLeft+3;x<=recoveryLeft+4;x++)
                    for(int y=recoveryFloor-height;y<recoveryFloor;y++)
                    {var tile=Main.tile[x,y];tile.ClearEverything();tile.HasTile=true;tile.TileType=TileID.Dirt;}
            }
            recoveryFired=true;
            File.WriteAllText(Path.Combine(Main.SavePath,"terrain-recovery-fired.json"),JsonSerializer.Serialize(new
                {recoveryMode,active.ActionId,gameUpdate=Main.GameUpdateCount,x=p.position.X,y=p.position.Y}));
            NetworkStateSync.Full(p);
        }
        string request=Path.Combine(Main.SavePath,"terrain-request.json");
        if(settle>0)
        {
            if(--settle==0)File.WriteAllText(Path.Combine(Main.SavePath,"terrain-ready.json"),JsonSerializer.Serialize(pending));
            return;
        }
        if(!File.Exists(request))return;
        if(ActorControl.Running||ActorItemUse.Running||ActorMining.Running)
            throw new InvalidOperationException("Terrain fixture cannot replace an active action");
        NetworkStateSync.Require(p);
        using var doc=JsonDocument.Parse(File.ReadAllText(request));
        string scene=doc.RootElement.GetProperty("scene").GetString();
        int direction=doc.RootElement.GetProperty("direction").GetInt32();
        if(direction!=1&&direction!=-1)throw new ArgumentException("Invalid terrain direction");
        if(scene is not ("flat" or "step_up" or "step_down" or "slope_up" or "slope_down" or "short_gap" or "low_obstacle" or "wide_gap" or "high_wall" or "platform_up" or "platform_down" or "high_platform"
            or "obstacle_one" or "solid_up_three" or "solid_up_five" or "shaft_up" or "shaft_ceiling" or "alternating" or "no_landing" or "descent_five" or "hazard_target"))
            throw new ArgumentException("Unknown terrain scene");
        recoveryMode=doc.RootElement.TryGetProperty("recovery",out var mode)?mode.GetString():null;
        if(recoveryMode!=null && (!Program.LaunchParameters.ContainsKey("-companionnavrecoverytest")
            || scene!="flat" || direction!=1 || recoveryMode is not ("offset" or "low_wall" or "high_wall" or "air" or "cancel_air")))
            throw new ArgumentException("Invalid isolated navigation recovery scene");
        recoveryFired=false;recoveryLeft=Main.spawnTileX;recoveryFloor=Main.spawnTileY+8;
        caseName=scene+"-"+direction+(recoveryMode==null?"":"-"+recoveryMode);frames.Clear();recording=false;
        inputTick=null;
        lastRun=(ActorControl.Driver as ServerAiNavigation)?.Current;
        int left=Main.spawnTileX, floor=Main.spawnTileY+8;
        for(int tx=left-36;tx<=left+36;tx++)for(int ty=floor-24;ty<=floor+18;ty++)
        {
            int q=direction*(tx-left), surface=floor;
            int slope=0;
            bool platformScene=scene is "platform_up" or "platform_down" or "high_platform";
            if(platformScene)surface=floor+8;
            if(scene=="step_up"&&q>=4)surface--;
            if(scene=="step_down"&&q>=4)surface++;
            if(scene=="slope_up"&&q>=4)
            {surface-=Math.Min(q-3,4);if(q<8)slope=direction==1?2:1;}
            if(scene=="slope_down"&&q>=4)
            {surface+=Math.Min(q-4,4);if(q<8)slope=direction==1?1:2;}
            if(scene=="short_gap"&&q>=4&&q<=6||scene=="wide_gap"&&q>=4&&q<=20)surface+=10;
            if(scene=="low_obstacle"&&q>=4&&q<=5)surface-=2;
            if(scene=="obstacle_one"&&q>=4&&q<=5)surface--;
            if(scene=="solid_up_three"&&q>=4)surface-=3;
            if(scene=="solid_up_five"&&q>=4)surface-=5;
            if(scene=="high_wall"&&q>=4&&q<=6)surface-=12;
            var tile=Main.tile[tx,ty];tile.ClearEverything();
            if(ty>=surface){tile.HasTile=true;tile.TileType=TileID.Dirt;if(ty==surface)tile.Slope=(SlopeType)slope;}
            if(platformScene)
            {
                int top=scene=="platform_down"?floor-4:floor;
                int target=scene=="platform_down"?floor:scene=="high_platform"?floor-12:floor-4;
                if(q<=3&&ty==top||q>=6&&ty==target)
                {tile.HasTile=true;tile.TileType=TileID.Platforms;}
            }
            if(scene is "shaft_up" or "shaft_ceiling")
            {
                if(q is -1 or 2||scene=="shaft_ceiling"&&ty==floor-7)
                {tile.HasTile=true;tile.TileType=TileID.Dirt;}
                else if(q is 0 or 1&&ty==floor-4)
                {tile.HasTile=true;tile.TileType=TileID.Platforms;}
            }
            if(scene=="alternating"&&(q is >=6 and <=11&&ty==floor-4||q is >=14 and <=19&&ty==floor-2)
                ||scene=="descent_five"&&q<=3&&ty==floor-5)
            {tile.HasTile=true;tile.TileType=TileID.Platforms;}
            if(scene=="hazard_target"&&q>=6&&ty==floor-1)
            {tile.LiquidType=LiquidID.Lava;tile.LiquidAmount=255;}
        }
        p.position=new(left*16+(scene is "shaft_up" or "shaft_ceiling"?(direction==1?6:-10):-2),
            (floor-(scene=="platform_down"?4:scene=="descent_five"?5:0))*16-p.height);p.velocity=Vector2.Zero;
        p.controlLeft=p.controlRight=p.controlJump=p.controlDown=p.controlUp=false;
        p.jump=0;p.releaseJump=true;p.fallStart=floor;p.statLife=p.statLifeMax2;
        // Each scene starts with the audited basic profile, independent of the earlier apple test.
        Array.Clear(p.buffType);Array.Clear(p.buffTime);
        pending=new{scene,direction,startX=p.position.X,startY=p.position.Y,targetX=p.position.X+direction*192,
            floor,originTileX=left,world=ProbeSession.WorldSession,generation=ProbeSession.ActorGeneration,slot=p.whoAmI};
        settle=15;
        File.Delete(request);
        NetworkStateSync.Full(p);
    }
}
