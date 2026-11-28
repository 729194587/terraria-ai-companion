using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using CompanionBridge.Motion;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
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
    private static bool contactInjected;
    private static object pending, initialNavigation;
    private static bool failed, journey;
    private enum FlightStage { Ground, Primary, Extra, Wing }
    private static FlightStage stage;
    private static bool grounded,held,commandRight,commandJump;
    private static int flightGoal,flightTicks,stalled;
    private static float furthest;

    internal static void SampleTravelInput(Player p)
    {
        if(caseId==null || !ReferenceEquals(p,ProbeSession.Actor))return;
        if(flightGoal>0 && settle==0 && !failed)
        {
            bool jump=true;
            if(grounded)
            {
                jump=!held;
                stage=jump?FlightStage.Primary:FlightStage.Ground;
            }
            else if(stage==FlightStage.Primary && p.jump<=0)
            {
                jump=false;stage=FlightStage.Extra;
            }
            else if(stage==FlightStage.Extra && held && p.jump<=0 && !p.GetJumpState(ExtraJump.CloudInABottle).Active)
            {
                jump=false;stage=FlightStage.Wing;
            }
            p.controlLeft=false;p.controlRight=true;p.controlJump=held=jump;
            flightTicks++;
        }
        commandRight=p.controlRight;commandJump=p.controlJump;
    }
    private static string FileAt(string name)=>Path.Combine(Main.SavePath,name);
    private static bool Enabled=>Program.LaunchParameters.ContainsKey("-companionnaturaltest")
        &&Program.LaunchParameters.ContainsKey("-companionaiipctest")&&File.Exists(FileAt("isolated-natural-world"));
    // Only the test survey sees this cross-section. A floating island can be the
    // first solid tile from the sky but is a different destination from the ground below.
    private static HashSet<Point> GroundComponent(int left,int right,int bottom)
    {
        var ground=new HashSet<Point>();
        var frontier=new Queue<Point>();
        void Add(int x,int y)
        {
            if(x<left||x>right||y<40||y>bottom)return;
            var tile=Main.tile[x,y];
            if(!tile.HasTile||tile.IsActuated||!Main.tileSolid[tile.TileType]||Main.tileSolidTop[tile.TileType])return;
            var cell=new Point(x,y);
            if(ground.Add(cell))frontier.Enqueue(cell);
        }
        for(int x=left;x<=right;x++)Add(x,bottom);
        while(frontier.TryDequeue(out var cell))
        {
            Add(cell.X-1,cell.Y);Add(cell.X+1,cell.Y);
            Add(cell.X,cell.Y-1);Add(cell.X,cell.Y+1);
        }
        return ground;
    }
    internal static void Reset(){caseId=null;settle=0;pending=initialNavigation=null;failed=false;frames.Clear();}
    internal static void Tick(bool live=false)
    {
        if(!Enabled||failed||live!=Netplay.HasClients||live&&!Program.LaunchParameters.ContainsKey("-companionwatchtest")||!Program.IsMainThread||ProbeSession.Actor==null)return;
        try
        {
            if(Program.LaunchParameters.ContainsKey("-companionactionstest"))throw new InvalidOperationException("Natural and constructed fixtures cannot mix");
            // Field comparison uses the normal spawn, hostiles, survival and world time.
            // Only supply the whole game frames a headless, clientless server lacks.
            if(Program.LaunchParameters.ContainsKey("-companionexplorationfield"))
            {
                string manifest=FileAt("exploration-field.json");
                if(!File.Exists(manifest))
                {
                    var p=ProbeSession.Actor;
                    // Normalize the three ordinary starter tool prefixes in both fresh profiles.
                    foreach(var item in p.inventory.Take(3))
                        if(!item.IsAir)item.SetDefaults(item.type);
                    File.WriteAllText(manifest,JsonSerializer.Serialize(new{
                        seed=Main.ActiveWorldFileData.SeedText,spawnX=Main.spawnTileX,spawnY=Main.spawnTileY,
                        starter=p.inventory.Take(3).Select(i=>new{i.type,i.stack,i.prefix}),
                        hazards="normal",terrainEdits=false,teleported=false}));
                }
                if(!live)update.Invoke(Main.instance,new object[]{new GameTime()});
                return;
            }
            lock(ProbeSession.Reservation.Gate)
            {
                var p=ProbeSession.Actor;NetworkStateSync.Require(p);
                string catalog=FileAt("natural-catalog.json");
                if(!File.Exists(catalog))
                {
                    var columns=new List<object>();
                    int left=Math.Max(32,Main.spawnTileX-512),right=Math.Min(Main.maxTilesX-32,Main.spawnTileX+(Program.LaunchParameters.ContainsKey("-companiontraversetest")?768:512));
                    var ground=Program.LaunchParameters.ContainsKey("-companionjourneytest")
                        ?GroundComponent(left,right,Math.Min(Main.maxTilesY-40,(int)Main.worldSurface+100)):null;
                    for(int cx=left;cx<=right;cx++)
                        for(int cy=40;cy<Math.Min(Main.maxTilesY-40,(int)Main.worldSurface+100);cy++)
                            if(ground?.Contains(new Point(cx,cy))??WorldGen.SolidTile(cx,cy))
                            {
                                var tile=Main.tile[cx,cy];
                                var pos=new Vector2(cx*16,cy*16-p.height);
                                bool clear=!Collision.SolidCollision(pos,p.width,p.height)
                                    &&Main.tile[cx,cy-1].LiquidAmount==0;
                                columns.Add(new{x=cx,y=cy,type=tile.TileType,slope=(byte)tile.Slope,half=tile.IsHalfBlock,clear});break;
                            }
                    File.WriteAllText(catalog,JsonSerializer.Serialize(new{seed=Main.ActiveWorldFileData.SeedText,spawnX=Main.spawnTileX,
                        spawnY=Main.spawnTileY,columns,scope="read-only natural surface survey; not path/reachability evidence"}));
                }
                // Navigation/perception diagnostic, not a combat acceptance test.
                foreach(var npc in Main.npc)if(npc.active&&!npc.townNPC)
                {
                    npc.active=false;
                    if(live)NetMessage.SendData(MessageID.SyncNPC,number:npc.whoAmI);
                }
                if(Program.LaunchParameters.ContainsKey("-companionnaturalminingtest")&&!File.Exists(FileAt("natural-veins.json")))
                    File.WriteAllText(FileAt("natural-veins.json"),JsonSerializer.Serialize(NaturalMiningSurvey.Find()));
                string request=FileAt("natural-request.json");
                if(File.Exists(request))
                {
                    if(ActorControl.Running||ActorItemUse.Running||ActorMining.Running)throw new InvalidOperationException("Natural fixture cannot reset an active action");
                    using var doc=JsonDocument.Parse(File.ReadAllText(request));
                    caseId=doc.RootElement.GetProperty("caseId").GetString();
                    if(string.IsNullOrEmpty(caseId)||caseId.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'))throw new ArgumentException("Invalid diagnostic case ID");
                    int offset=doc.RootElement.GetProperty("offsetTiles").GetInt32();
                    if(Math.Abs(offset)>512 && !((caseId is "local-replay" or "slope-entry" or "slope-approach") && offset<=768 && offset>512 && Program.LaunchParameters.ContainsKey("-companiontraversetest")))throw new ArgumentException("Natural start offset exceeds diagnostic scope");
                    int tx=Main.spawnTileX+offset, surface=-1;
                    if(tx<32||tx>Main.maxTilesX-32)throw new ArgumentException("Natural start outside world");
                    journey=Program.LaunchParameters.ContainsKey("-companionjourneytest");
                    bool fixedSurface=journey||Program.LaunchParameters.ContainsKey("-companionnaturalminingtest")&&doc.RootElement.TryGetProperty("surfaceY",out _);
                    int firstY=fixedSurface?doc.RootElement.GetProperty("surfaceY").GetInt32():40;
                    int limit=fixedSurface?firstY+1:Math.Min(Main.maxTilesY-40,(int)Main.worldSurface+100);
                    if(firstY<40||limit>Main.maxTilesY-40)throw new ArgumentException("Invalid start surface");
                    float startX=tx*16;
                    if(fixedSurface && doc.RootElement.TryGetProperty("startPixelX",out var pixel))
                    {
                        startX=pixel.GetSingle();
                        if((int)Math.Floor(startX/16)!=tx)throw new ArgumentException("Replay start column mismatch");
                    }
                    for(int ty=firstY;ty<limit;ty++)
                    {
                        if(!WorldGen.SolidTile(tx,ty)&&!(journey&&WorldGen.SolidTile(tx+1,ty))
                            &&!(journey && doc.RootElement.TryGetProperty("startPixelX",out _)
                                && (Main.tile[tx,ty].HasUnactuatedTile || Main.tile[tx+1,ty].HasUnactuatedTile)))continue;
                        var pos=new Vector2(startX,ty*16-p.height);
                        if(Collision.SolidCollision(pos,p.width,p.height))continue;
                        surface=ty;break;
                    }
                    bool bankReplay=caseId=="bank-replay" && journey;
                    if(bankReplay)surface=firstY;
                    if(surface<0)throw new InvalidOperationException("No unmodified natural start surface");
                    // Scene initialization only; every navigation thereafter uses native inputs.
                    p.position=new(startX,surface*16-p.height);p.velocity=Vector2.Zero;
                    if(bankReplay)p.position.Y=doc.RootElement.GetProperty("startFeetY").GetSingle()-p.height;
                    p.controlLeft=p.controlRight=p.controlJump=p.controlDown=p.controlUp=p.controlUseItem=p.controlUseTile=false;
                    p.jump=0;p.releaseJump=true;p.fallStart=surface;p.statLife=p.statLifeMax2;
                    Array.Clear(p.buffType);Array.Clear(p.buffTime);
                    if(caseId=="change-stance"&&Program.LaunchParameters.ContainsKey("-companionnaturalminingtest"))
                    {
                        // Fresh native characters roll starter prefixes. Fix the comparison loadout.
                        p.inventory[0].SetDefaults(ItemID.CopperShortsword);
                        p.inventory[1].SetDefaults(ItemID.CopperPickaxe);
                        p.inventory[2].SetDefaults(ItemID.CopperAxe);
                        p.selectedItem=0;
                    }
                    if(journey)
                    {
                        foreach(var item in p.inventory.Concat(p.armor).Concat(p.miscEquips))item.TurnToAir();
                        p.inventory[0].SetDefaults(ItemID.CopperPickaxe);
                        p.inventory[1].SetDefaults(ItemID.CopperAxe);
                        p.inventory[2].SetDefaults(ItemID.WoodPlatform);p.inventory[2].stack=caseId=="east-wall"?4:60;
                        if(bankReplay)p.inventory[2].TurnToAir();
                        if(caseId=="death-feedback")
                        {
                            p.inventory[50]=new Item(ItemID.GoldCoin,4);
                            p.inventory[51]=new Item(ItemID.SilverCoin,20);
                        }
                        if(caseId is "death-feedback" or "death-stop")p.bank.item[0]=new Item(ItemID.PlatinumCoin,1);
                        p.miscEquips[4].SetDefaults(ItemID.GrapplingHook);
                        if(Program.LaunchParameters.TryGetValue("-companionjourneyloadout",out var loadout))
                        {
                            if(loadout is "cloud" or "cloud_angel")p.armor[3].SetDefaults(ItemID.CloudinaBottle);
                            if(loadout is "bundle" or "bundle_hermes")p.armor[3].SetDefaults(ItemID.BundleofBalloons);
                            if(loadout is "hermes" or "bundle_hermes")p.armor[4].SetDefaults(ItemID.HermesBoots);
                            if(loadout=="sand")p.armor[3].SetDefaults(ItemID.SandstorminaBottle);
                            if(loadout=="blizzard")p.armor[3].SetDefaults(ItemID.BlizzardinaBottle);
                            if(loadout is "angel" or "cloud_angel")p.armor[4].SetDefaults(ItemID.AngelWings);
                        }
                        p.selectedItem=0;
                    }
                    contactInjected=false;frames.Clear();settle=30;held=false;stage=FlightStage.Ground;flightTicks=stalled=0;furthest=p.position.X;
                    flightGoal=journey && caseId=="traverse" && File.Exists(FileAt("flight-reference")) ? doc.RootElement.GetProperty("flightGoalX").GetInt32():0;initialNavigation=(ActorControl.Driver as ServerAiNavigation)?.Current;
                    pending=new{caseId,offsetTiles=offset,surface,spawnX=Main.spawnTileX,spawnY=Main.spawnTileY,
                        seed=Main.ActiveWorldFileData.SeedText,worldWidth=Main.maxTilesX,worldHeight=Main.maxTilesY,
                        world=ProbeSession.WorldSession,generation=ProbeSession.ActorGeneration,slot=p.whoAmI,
                        terrainWrites=false,initialStateInjection=true,npcCombat=caseId.StartsWith("contact-")?"single controlled zombie contact":"excluded"};
                    File.Delete(request);NetworkStateSync.Full(p);
                }
            }
            long updateStart=System.Diagnostics.Stopwatch.GetTimestamp();
            if(!live)update.Invoke(Main.instance,new object[]{new GameTime()});
            double updateMilliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds;
            lock(ProbeSession.Reservation.Gate)
            {
                var p=ProbeSession.Actor;NetworkStateSync.Require(p);
                var o=ServerAiNavigation.Observe(p);
                if(settle>0&&--settle==0)
                {
                    if(journey && File.Exists(FileAt("natural-probe-direction")))
                    {
                        int dir=int.Parse(File.ReadAllText(FileAt("natural-probe-direction")));
                        var ph=NativeNavigation.Parameters(p);
                        var world=new NativeMotionWorld(o.State);
                        int row=(int)Math.Round(p.Bottom.Y/16),cx=(int)(p.Center.X/16);
                        if(caseId=="local-replay")
                        {
                            var grid=Enumerable.Range(row-35,61).Select(y=>new string(Enumerable.Range(cx-40,100)
                                .Select(x=>global::CompanionBridge.Common.Systems.TerrainReader.ReadDryCell(x,y)).ToArray()));
                            File.WriteAllLines(FileAt("local-geometry.txt"),grid);
                        }
                        if(caseId=="slope-approach")
                        {
                            var launches=from action in SurfaceNavigator.UphillLaunches(o.State,dir,ph)
                                where action.Hold>0 && action.ImpulseFrame<0
                                let edge=PhysicsSimulator.Predict(o.State,dir,action.Hold,ph,world,rollingJump:true,
                                    extraJumpFrame:action.ExtraFrame,extraCount:action.ExtraCount,extraHoldLimit:action.ExtraHold,
                                    airStartFrame:action.AirStart,airHoldFrames:action.AirHold)
                                select new{action,edge.Safe,edge.Reason,x=edge.Landing.Px,feet=edge.Landing.Py+42,frames=edge.Frames.Count};
                            File.WriteAllText(FileAt("natural-launches.json"),JsonSerializer.Serialize(launches));
                        }
                        var shore=from step in Enumerable.Range(-2,21)
                                  let tx=cx+step*dir
                                  select new{x=tx,cells=Enumerable.Range(row-4,10).Select(y=>{
                                      var tile=Main.tile[tx,y];
                                      var cell=global::CompanionBridge.Common.Systems.TerrainReader.ReadEnvironment(tx,y);
                                      return new{y,cell.Shape,liquid=tile.LiquidAmount,type=tile.TileType};})};
                        var identity=new AiReadOnlyProtocol.Identity(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,null);
                        var edits=new NativeNavigationEdits(identity,o.State,rolling:true);
                        var view=new LocalTerrainView(edits.Environment);
                        var platform=edits.Materials.FirstOrDefault(m=>m.Shape=='=');
                        var checks=Enumerable.Range(-2,7).Select(i=>{
                            int x=cx+i*dir;
                            return new{x,deck=LocalEditGeometry.WaterDeckCell(view,x,row),
                                place=platform!=null&&edits.Place(o.State,x,row,platform,view)};
                        });
                        File.WriteAllText(FileAt("natural-shore-"+caseId+".json"),JsonSerializer.Serialize(new{row,cx,dir,shore,checks}));
                        var probes=from hold in new[]{0,6,15}
                                   from stride in (hold==0?new[]{6,24,60,100}:new[]{24})
                                   let edge=PhysicsSimulator.Predict(o.State,dir,hold,ph,world,walkFrames:stride)
                                   select new{hold,stride,edge.Safe,edge.Reason,dx=edge.Landing.Px-o.State.Px,dy=edge.Landing.Py-o.State.Py};
                        File.WriteAllText(FileAt("natural-probes-"+caseId+".json"),JsonSerializer.Serialize(probes));
                    }
                    File.WriteAllText(FileAt("natural-ready.json"),JsonSerializer.Serialize(new{fixture=pending,
                        x=p.position.X,y=p.position.Y,cloudEnabled=p.GetJumpState(ExtraJump.CloudInABottle).Enabled,cloudActive=p.GetJumpState(ExtraJump.CloudInABottle).Active,
                        p.wingsLogic,p.wingTime,p.wingTimeMax,o.State.Grounded,o.SafeSupport,o.Settled,gameUpdate=Main.GameUpdateCount}));
                }
                if(caseId!=null&&settle==0)
                {
                    if(frames.Count>=(caseId=="traverse"?42000:12000))throw new InvalidOperationException("Natural trace budget exhausted");
                    var run=(ActorControl.Driver as ServerAiNavigation)?.Current;
                    var nav=ReferenceEquals(run,initialNavigation)?null:run?.Snapshot;
                    if(caseId is ("news-receive" or "news-change") && !contactInjected && File.Exists(FileAt("news-emit"))
                        && run?.Status=="running" && Math.Abs(p.position.X-run.StartX)>32)
                    {
                        var warning=Terraria.Localization.NetworkText.FromKey("LegacyMisc.9");
                        Terraria.Chat.ChatHelper.SendChatMessageToClient(Terraria.Localization.NetworkText.FromLiteral("other-player-only"),Color.White,0);
                        Terraria.Chat.ChatHelper.BroadcastChatMessage(Terraria.Localization.NetworkText.FromLiteral("excluded-ai"),Color.White,p.whoAmI);
                        Terraria.Chat.ChatHelper.SendChatMessageToClient(Terraria.Localization.NetworkText.FromLiteral("private-to-ai"),Color.White,p.whoAmI);
                        Terraria.Chat.ChatHelper.BroadcastChatMessage(warning,Color.White);
                        contactInjected=true;
                    }
                    bool lethal=caseId is "death-feedback" or "death-stop";
                    if(!contactInjected && (lethal ? ServerAiSystem.AgentActive && File.Exists(FileAt("death-encounter"))
                        : caseId is ("contact-light" or "contact-heavy") && run?.Status=="running" && Math.Abs(p.position.X-run.StartX)>32))
                    {
                        // Exactly one encounter. Native NPC collision owns damage, immunity and knockback.
                        int slot=NPC.NewNPC(new Terraria.DataStructures.EntitySource_Misc("travel-contact"),(int)p.Center.X,(int)p.Center.Y,NPCID.Zombie);
                        if(slot>=Main.maxNPCs)throw new InvalidOperationException("Diagnostic NPC slot unavailable");
                        var enemy=Main.npc[slot];
                        try
                        {
                            enemy.position=p.position;enemy.target=p.whoAmI;
                            enemy.damage=lethal?1000:caseId=="contact-light"?8:60;
                            var collision=typeof(Player).GetMethod("Update_NPCCollision",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
                            int before=p.statLife;
                            long GroundCoins()=>Utils.CoinsCount(out _,Main.item.Where(i=>i.active).ToArray());
                            long groundBefore=lethal?GroundCoins():0;
                            NativeSurvival.Run(p,()=>collision.Invoke(p,null));
                            if(lethal)
                            {
                                long dropped=GroundCoins()-groundBefore;
                                if(dropped!=NativeSurvival.LastDeath.coinsLostCopper || p.bank.item[0].type!=ItemID.PlatinumCoin || p.bank.item[0].stack!=1)
                                    throw new InvalidOperationException("Death loss disagrees with native dropped coins or bank changed");
                            }
                            if(p.statLife>=before)throw new InvalidOperationException("Diagnostic contact did not deal native damage");
                            contactInjected=true;
                        }
                        finally{enemy.active=false;}
                    }
                    grounded=o.State.Grounded;
                    if(flightGoal>0 && flightTicks>0)
                    {
                        if(p.position.X>furthest+1){furthest=p.position.X;stalled=0;}else stalled++;
                        string stop=p.dead?"dead":p.Right.X>=flightGoal?"crossed_target_x":stalled>=1800?"no_forward_progress":
                            flightTicks>=30000?"tick_limit":null;
                        if(stop!=null)
                        {
                            File.WriteAllText(FileAt("flight-stop.json"),JsonSerializer.Serialize(new{stop,flightTicks,furthest,x=p.position.X,feet=p.Bottom.Y}));
                            flightGoal=0;p.controlRight=p.controlJump=false;
                        }
                    }
                    frames.Add(new{tick=Main.GameUpdateCount,x=p.position.X,y=p.position.Y,vx=p.velocity.X,vy=p.velocity.Y,
                        hermesEquipped=p.armor[4].type==ItemID.HermesBoots,
                        cloudEnabled=p.GetJumpState(ExtraJump.CloudInABottle).Enabled,
                        sandEnabled=p.GetJumpState(ExtraJump.SandstormInABottle).Enabled,sandActive=p.GetJumpState(ExtraJump.SandstormInABottle).Active,
                        blizzardEnabled=p.GetJumpState(ExtraJump.BlizzardInABottle).Enabled,blizzardActive=p.GetJumpState(ExtraJump.BlizzardInABottle).Active,
                        cloudActive=p.GetJumpState(ExtraJump.CloudInABottle).Active,p.wingsLogic,p.wingTime,p.wingTimeMax,
                        o.State.Grounded,o.SafeSupport,o.Settled,p.wet,life=p.statLife,platformStock=journey?p.inventory.Where(i=>i.type==ItemID.WoodPlatform).Sum(i=>i.stack):0,left=p.controlLeft,right=p.controlRight,jump=p.controlJump,
                        commandRight,commandJump,flightTicks,reason=nav?.Reason,searchExit=nav?.SearchExit,planNumber=nav?.PlanNumber,planKind=nav?.PlanKind,status=nav?.Status,action=nav?.Action,frame=nav?.Frame,expectedPosition=nav?.ExpectedPosition,
                        expectedVelocity=nav?.ExpectedVelocity,maxError=nav?.MaxError,gameUpdateMilliseconds=live?(double?)null:updateMilliseconds,hookCount=p.grapCount,navigation=journey?null:nav});
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
