using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Opt-in generated/copied-world diagnostic. NEVER writes Tile or builds terrain.
internal static class ServerAiNaturalFixture
{
    private static readonly MethodInfo update=typeof(Main).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic,
        null,new[]{typeof(GameTime)},null);
    private static readonly List<object> frames=new();
    private static string caseId;
    private static int settle;
    private static object pending, initialNavigation;
    private static bool failed;
    private static string FileAt(string name)=>Path.Combine(Main.SavePath,name);
    private static bool Enabled=>Program.LaunchParameters.ContainsKey("-companionnaturaltest")
        &&Program.LaunchParameters.ContainsKey("-companionaiipctest")&&File.Exists(FileAt("isolated-natural-world"));
    internal static void Reset(){caseId=null;settle=0;pending=initialNavigation=null;failed=false;frames.Clear();}
    internal static void Tick()
    {
        if(!Enabled||failed||Netplay.HasClients||!Program.IsMainThread||ProbeSession.Actor==null)return;
        try
        {
            if(Program.LaunchParameters.ContainsKey("-companionactionstest"))throw new InvalidOperationException("Natural and constructed fixtures cannot mix");
            lock(ProbeSession.Reservation.Gate)
            {
                var p=ProbeSession.Actor;NetworkStateSync.Require(p);
                string catalog=FileAt("natural-catalog.json");
                if(!File.Exists(catalog))
                {
                    var columns=new List<object>();
                    for(int cx=Math.Max(32,Main.spawnTileX-512);cx<=Math.Min(Main.maxTilesX-32,Main.spawnTileX+512);cx++)
                        for(int cy=40;cy<Math.Min(Main.maxTilesY-40,(int)Main.worldSurface+100);cy++)
                            if(WorldGen.SolidTile(cx,cy))
                            {
                                var tile=Main.tile[cx,cy];
                                columns.Add(new{x=cx,y=cy,type=tile.TileType,slope=(byte)tile.Slope,half=tile.IsHalfBlock});break;
                            }
                    File.WriteAllText(catalog,JsonSerializer.Serialize(new{seed=Main.ActiveWorldFileData.SeedText,spawnX=Main.spawnTileX,
                        spawnY=Main.spawnTileY,columns,scope="read-only natural surface survey; not path/reachability evidence"}));
                }
                // Navigation/perception diagnostic, not a combat acceptance test.
                foreach(var npc in Main.npc)if(npc.active&&!npc.townNPC)npc.active=false;
                string request=FileAt("natural-request.json");
                if(File.Exists(request))
                {
                    if(ActorControl.Running||ActorItemUse.Running||ActorMining.Running)throw new InvalidOperationException("Natural fixture cannot reset an active action");
                    using var doc=JsonDocument.Parse(File.ReadAllText(request));
                    caseId=doc.RootElement.GetProperty("caseId").GetString();
                    if(string.IsNullOrEmpty(caseId)||caseId.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'))throw new ArgumentException("Invalid diagnostic case ID");
                    int offset=doc.RootElement.GetProperty("offsetTiles").GetInt32();
                    if(Math.Abs(offset)>512)throw new ArgumentException("Natural start offset exceeds diagnostic scope");
                    int tx=Main.spawnTileX+offset, surface=-1;
                    if(tx<32||tx>Main.maxTilesX-32)throw new ArgumentException("Natural start outside world");
                    for(int ty=40;ty<Math.Min(Main.maxTilesY-40,(int)Main.worldSurface+100);ty++)
                    {
                        if(!WorldGen.SolidTile(tx,ty))continue;
                        var pos=new Vector2(tx*16,ty*16-p.height);
                        if(Collision.SolidCollision(pos,p.width,p.height))continue;
                        surface=ty;break;
                    }
                    if(surface<0)throw new InvalidOperationException("No unmodified natural start surface");
                    // Scene initialization only; every navigation thereafter uses native inputs.
                    p.position=new(tx*16,surface*16-p.height);p.velocity=Vector2.Zero;
                    p.controlLeft=p.controlRight=p.controlJump=p.controlDown=p.controlUp=p.controlUseItem=p.controlUseTile=false;
                    p.jump=0;p.releaseJump=true;p.fallStart=surface;p.statLife=p.statLifeMax2;
                    Array.Clear(p.buffType);Array.Clear(p.buffTime);
                    frames.Clear();settle=30;initialNavigation=(ActorControl.Driver as ServerAiNavigation)?.Current;
                    pending=new{caseId,offsetTiles=offset,surface,spawnX=Main.spawnTileX,spawnY=Main.spawnTileY,
                        seed=Main.ActiveWorldFileData.SeedText,worldWidth=Main.maxTilesX,worldHeight=Main.maxTilesY,
                        world=ProbeSession.WorldSession,generation=ProbeSession.ActorGeneration,slot=p.whoAmI,
                        terrainWrites=false,initialStateInjection=true,npcCombat="excluded"};
                    File.Delete(request);NetworkStateSync.Full(p);
                }
            }
            long updateStart=System.Diagnostics.Stopwatch.GetTimestamp();
            update.Invoke(Main.instance,new object[]{new GameTime()});
            double updateMilliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds;
            lock(ProbeSession.Reservation.Gate)
            {
                var p=ProbeSession.Actor;NetworkStateSync.Require(p);
                var o=ServerAiNavigation.Observe(p);
                if(settle>0&&--settle==0)
                    File.WriteAllText(FileAt("natural-ready.json"),JsonSerializer.Serialize(new{fixture=pending,
                        x=p.position.X,y=p.position.Y,o.State.Grounded,o.SafeSupport,o.Settled,gameUpdate=Main.GameUpdateCount}));
                if(caseId!=null&&settle==0)
                {
                    if(frames.Count>=12000)throw new InvalidOperationException("Natural trace budget exhausted");
                    var run=(ActorControl.Driver as ServerAiNavigation)?.Current;
                    var nav=ReferenceEquals(run,initialNavigation)?null:run?.Snapshot;
                    frames.Add(new{tick=Main.GameUpdateCount,x=p.position.X,y=p.position.Y,vx=p.velocity.X,vy=p.velocity.Y,
                        o.State.Grounded,o.SafeSupport,o.Settled,left=p.controlLeft,right=p.controlRight,jump=p.controlJump,
                        status=nav?.Status,action=nav?.Action,frame=nav?.Frame,expectedPosition=nav?.ExpectedPosition,
                        expectedVelocity=nav?.ExpectedVelocity,maxError=nav?.MaxError,gameUpdateMilliseconds=updateMilliseconds,navigation=nav});
                }
                string finish=FileAt("natural-finish");
                if(File.Exists(finish)&&caseId!=null)
                {
                    if(ActorControl.Running)throw new InvalidOperationException("Cannot finish a running natural action");
                    File.WriteAllText(FileAt("natural-motion-"+caseId+".json"),JsonSerializer.Serialize(frames));
                    File.Delete(finish);caseId=null;
                }
            }
        }
        catch(Exception e)
        {
            failed=true;File.WriteAllText(FileAt("natural-fixture-failure.txt"),e.ToString());
            ProbeSession.Quarantine("Natural diagnostic failed: "+e.Message);
        }
    }
}
