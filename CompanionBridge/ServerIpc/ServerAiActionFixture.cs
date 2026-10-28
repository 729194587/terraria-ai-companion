using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Explicit isolated-server diagnostic, never enabled by Host & Play profiles.
// Drives whole native Game.Update frames because no-client servers otherwise pause.
internal static class ServerAiActionFixture
{
    private static bool initialized, failed, combatIsolated;
    private static int x, y;
    private static object observedDrop;
    private static NPC perceptionNpc;
    private static Vector2 perceptionPosition;
    private static readonly MethodInfo update = typeof(Main).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic,
        null, new[]{typeof(GameTime)}, null);
    internal static bool Enabled => Program.LaunchParameters.ContainsKey("-companionactionstest")
        && Program.LaunchParameters.ContainsKey("-companionaiipctest")
        && File.Exists(Path.Combine(Main.SavePath,"isolated-action-fixture"));
    internal static void Tick()
    {
        if(!Enabled||failed||Netplay.HasClients||!Program.IsMainThread||ProbeSession.Actor==null)return;
        try
        {
            lock(ProbeSession.Reservation.Gate)
            {
                var p=ProbeSession.Actor;NetworkStateSync.Require(p);
                if(!initialized)
                {
                    if(update==null)throw new MissingMethodException("Native Main.Update(GameTime) unavailable");
                    int left=Main.spawnTileX, floor=Main.spawnTileY+8;
                    for(int tx=left-22;tx<=left+24;tx++)for(int ty=floor-14;ty<=floor+2;ty++)
                    {
                        var tile=Main.tile[tx,ty];tile.ClearEverything();
                        if(ty>=floor){tile.HasTile=true;tile.TileType=TileID.Dirt;}
                    }
                    p.position=new(left*16,floor*16-p.height);p.velocity=Vector2.Zero;
                    p.statLife=80;
                    p.inventory[0].SetDefaults(ItemID.WoodenSword);
                    p.inventory[1].SetDefaults(ItemID.CopperPickaxe);
                    p.inventory[2].SetDefaults(ItemID.Apple);p.inventory[2].stack=2;
                    x=left+8;y=floor-2;
                    for(int tx=x;tx<=x+1;tx++){var tile=Main.tile[tx,y];tile.HasTile=true;tile.TileType=TileID.Stone;}
                    var cancelTile=Main.tile[x,y-1];cancelTile.HasTile=true;cancelTile.TileType=TileID.Stone;
                    if(Program.LaunchParameters.ContainsKey("-companionperceptiontest"))
                    {
                        // Explicit extra flag inside the existing copied-world fixture only.
                        int npc=NPC.NewNPC(p.GetSource_Misc("G85PerceptionFixture"),left*16+240,floor*16-48,NPCID.BlueSlime);
                        Main.npc[npc].velocity=Vector2.Zero;
                        // Presence/classification fixture, not a combat scenario. Keep
                        // native type/damage, but prevent AI/RNG from attacking during reads.
                        Main.npc[npc].aiStyle=-1;Main.npc[npc].noGravity=true;
                        perceptionNpc=Main.npc[npc];perceptionPosition=perceptionNpc.position;
                        Item.NewItem(p.GetSource_Misc("G85PerceptionFixture"),left*16+240,floor*16-32,16,16,ItemID.StoneBlock,3);
                    }
                    if(Program.LaunchParameters.ContainsKey("-companionplacementtest"))
                    {
                        // Diagnostic materials only; normal AI inventories are never seeded.
                        p.inventory[4].SetDefaults(ItemID.Wood);p.inventory[4].stack=4;
                        p.inventory[5].SetDefaults(ItemID.StoneBlock);p.inventory[5].stack=4;
                        p.inventory[6].SetDefaults(ItemID.DirtBlock);p.inventory[6].stack=4;
                    }
                    if(Program.LaunchParameters.ContainsKey("-companionlandingtest"))ServerAiLandingFixture.Run(p);
                    if(Program.LaunchParameters.ContainsKey("-companionsemantictest"))
                    {
                        // Only this explicitly marked copied-world diagnostic may create fixtures.
                        int treeX=left-4;
                        for(int tx=treeX-3;tx<=treeX+3;tx++)for(int ty=floor-38;ty<=floor;ty++)
                        {
                            var tile=Main.tile[tx,ty];tile.ClearEverything();
                            if(ty==floor){tile.HasTile=true;tile.TileType=TileID.Grass;}
                        }
                        if(!WorldGen.GrowTree(treeX,floor))throw new InvalidOperationException("Semantic native tree failed to grow");
                        p.inventory[3].SetDefaults(ItemID.CopperAxe);
                        for(int tx=left-10;tx<=left-9;tx++)
                        {var tile=Main.tile[tx,floor+1];tile.HasTile=true;tile.TileType=TileID.Copper;}
                        var diagonal=Main.tile[left-8,floor+2];diagonal.HasTile=true;diagonal.TileType=TileID.Copper;
                    }
                    if(Program.LaunchParameters.ContainsKey("-companionscenetest"))ServerSceneFixture.Prepare(p,left,floor);
                    initialized=true;NetworkStateSync.Full(p);
                    File.WriteAllText(Path.Combine(Main.SavePath,"action-fixture.json"),JsonSerializer.Serialize(new{targetX=p.position.X+64,mineX=x,mineY=y,
                        initialLife=p.statLife,initialAppleStack=2,perceptionNpcSlot=perceptionNpc?.whoAmI,mode="isolated copied world; diagnostic native Main.Update frames; no clients"}));
                }
            }
            // The copied-world perception fixture tests NPC presence, not AI/combat.
            // Keep only that NPC in its explicit static pose during reads; exclude
            // ambient NPCs. After the read phase, retire it too. Never rewrite Player
            // position/velocity, grant immunity, or interfere with native reach checks.
            lock(ProbeSession.Reservation.Gate)
            {
                string isolation=Path.Combine(Main.SavePath,"action-fixture-no-combat");
                bool isolate=combatIsolated||File.Exists(isolation);
                var p=ProbeSession.Actor;NetworkStateSync.Require(p);
                if(isolate&&!combatIsolated&&(ActorControl.Running||ActorItemUse.Running||ActorMining.Running))
                    throw new InvalidOperationException("Cannot isolate combat during an active action");
                var removed=Main.npc.Where(n=>n.active&&!n.townNPC&&(isolate||!ReferenceEquals(n,perceptionNpc)))
                    .Select(n=>new{slot=n.whoAmI,n.type,n.damage,n.aiStyle,n.noGravity,x=n.position.X,y=n.position.Y}).ToArray();
                foreach(var npc in Main.npc)if(npc.active&&!npc.townNPC)
                {
                    if(isolate||!ReferenceEquals(npc,perceptionNpc))npc.active=false;
                    else
                    {
                        if(npc.type!=NPCID.BlueSlime)throw new InvalidOperationException("Perception NPC fixture replaced");
                        npc.position=perceptionPosition;npc.velocity=Vector2.Zero;npc.aiStyle=-1;npc.noGravity=true;
                    }
                }
                if(removed.Length>0)ProbeSession.Log("action-fixture-npc-removed",new{isolate,removed});
                if(isolate&&!combatIsolated)
                {
                    combatIsolated=true;
                    var state=new{world=ProbeSession.WorldSession,generation=ProbeSession.ActorGeneration,slot=p.whoAmI,
                        gameUpdate=Main.GameUpdateCount,removed,x=p.position.X,y=p.position.Y,vx=p.velocity.X,vy=p.velocity.Y,
                        p.statLife,selectedSlot=p.selectedItem,itemType=p.HeldItem.type,pick=p.HeldItem.pick,
                        tileBoost=p.HeldItem.tileBoost,Player.tileRangeX,Player.tileRangeY,
                        buffs=p.buffType.Where(t=>t!=0).ToArray()};
                    ProbeSession.Log("action-fixture-combat-isolated",state);
                    File.WriteAllText(isolation+".json",JsonSerializer.Serialize(state));
                }
            }
            // Same virtual native method invoked by DedServ_PostModLoad (IL_0dcb).
            // Does not call Player.Update, spoof identity, or change Netplay.HasClients.
            lock(ProbeSession.Reservation.Gate)ServerAiTerrainFixture.Tick(ProbeSession.Actor);
            update.Invoke(Main.instance,new object[]{new GameTime()});
            lock(ProbeSession.Reservation.Gate)ServerAiTerrainFixture.Observe(ProbeSession.Actor);
            var current=ActorMining.Current;
            object[] Drops()=>Main.item.Where(i=>i.active&&i.type==ItemID.StoneBlock&&Vector2.Distance(i.Center,new(x*16+8,y*16+8))<160)
                .Select(i=>(object)new{type=i.type,i.stack,x=i.position.X,y=i.position.Y}).ToArray();
            // Observe at the destruction frame, before later idle native pickup
            // can legitimately move/consume the world item. Never suppress pickup.
            if(observedDrop==null&&current?.Destroyed==true&&current.X==x&&current.Y==y&&!Main.tile[x,y].HasTile)
            {
                var drops=Drops();
                if(drops.Length>0)observedDrop=new{gameUpdate=Main.GameUpdateCount,items=drops,nativeActionId=current.Id};
            }
            if(File.Exists(Path.Combine(Main.SavePath,"g83-test-snapshot")))
            {
                var p=ProbeSession.Actor;NetworkStateSync.Require(p);
                File.WriteAllText(Path.Combine(Main.SavePath,"action-world-evidence.json"),JsonSerializer.Serialize(new{
                    gameUpdate=Main.GameUpdateCount,targetAbsent=!Main.tile[x,y].HasTile,neighborPresent=Main.tile[x+1,y].HasTile,
                    cancellationTargetPresent=Main.tile[x,y-1].HasTile,
                    p.statLife,appleStack=p.inventory[2].stack,pickStack=p.inventory[1].stack,x=p.position.X,y=p.position.Y,drops=Drops(),observedDrop,
                    stoneInInventory=p.inventory.Where(i=>!i.IsAir&&i.type==ItemID.StoneBlock).Sum(i=>i.stack),
                    controlsReleased=!p.controlLeft&&!p.controlRight&&!p.controlJump&&!p.controlUseItem&&!p.channel,
                    networkFull=NetworkStateSync.FullSnapshots,networkControls=NetworkStateSync.Controls}));
                File.Delete(Path.Combine(Main.SavePath,"g83-test-snapshot"));
            }
        }
        catch(Exception e)
        {
            failed=true;
            File.WriteAllText(Path.Combine(Main.SavePath,"action-fixture-failure.txt"),e.ToString());
            ProbeSession.Quarantine("Isolated action fixture failed: "+e.Message);
        }
    }
    internal static void Reset(){initialized=failed=combatIsolated=false;observedDrop=null;perceptionNpc=null;perceptionPosition=default;}
}
