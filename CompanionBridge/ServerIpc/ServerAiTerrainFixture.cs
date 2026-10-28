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
    private static string caseName;
    internal static void Observe(Player p)
    {
        if(!Program.LaunchParameters.ContainsKey("-companionterraintest")||caseName==null)return;
        bool running=ActorControl.Driver?.Running==true;
        if(!running&&!recording)return;
        NetworkStateSync.Require(p);
        var o=ServerAiNavigation.Observe(p);
        var nav=(ActorControl.Driver as ServerAiNavigation)?.Current?.Snapshot;
        frames.Add(new{tick=Main.GameUpdateCount,x=p.position.X,y=p.position.Y,vx=p.velocity.X,vy=p.velocity.Y,
            o.State.Grounded,o.SafeSupport,o.Settled,left=p.controlLeft,right=p.controlRight,jump=p.controlJump,
            expectedPosition=nav?.ExpectedPosition,expectedVelocity=nav?.ExpectedVelocity,
            action=nav?.Action,frame=nav?.Frame,status=nav?.Status,maxError=nav?.MaxError});
        recording=running;
        if(!running)File.WriteAllText(Path.Combine(Main.SavePath,"terrain-motion-"+caseName+".json"),JsonSerializer.Serialize(frames));
    }
    internal static void Tick(Player p)
    {
        if(!Program.LaunchParameters.ContainsKey("-companionterraintest"))return;
        // Terrain-only diagnostic: exclude NPC combat/knockback from these motion scenes.
        // Production navigation keeps its life-loss and velocity guards.
        foreach(var npc in Main.npc)if(npc.active&&!npc.townNPC)npc.active=false;
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
        if(scene is not ("flat" or "step_up" or "step_down" or "slope_up" or "slope_down" or "short_gap" or "low_obstacle" or "wide_gap" or "high_wall" or "platform_up" or "platform_down" or "high_platform"))
            throw new ArgumentException("Unknown terrain scene");
        caseName=scene+"-"+direction;frames.Clear();recording=false;
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
        }
        p.position=new(left*16-2,(floor-(scene=="platform_down"?4:0))*16-p.height);p.velocity=Vector2.Zero;
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
