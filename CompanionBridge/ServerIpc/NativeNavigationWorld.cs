using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;
using Terraria.ID;
using Microsoft.Xna.Framework;
using CompanionBridge.Motion;
using CompanionBridge.Common.Systems;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal sealed class NativeNavigationWorld : INavigationWorld
{
    private readonly AiReadOnlyProtocol.Identity identity;
    private readonly Player player;
    private readonly JsonElement target;
    private readonly float x,y;
    private readonly LandingRegion destination;
    private readonly int initialLife;
    private NativeNavigationEdits edits;
    private readonly NavigationKnowledge knowledge;
    private LocalEditEvaluation evaluation;
    private SurfaceNavigator movement;
    private LocalEditPlan selected;
    private bool ready,forced,preferDryBank;
    private PhysicsState plannedStart;
    private PhysicsParameters plannedPhysics;
    private long plannedTerrain,plannedInventory;
    private string planningDetail;
    private bool liquidBoundary;
    private (int X,int Y,int Side)? bridgeTip, pendingTip;
    private NavigationStep bridgeStep;
    private WallPlatform wallPlatform;
    private bool wallPlacementIssued;
    private sealed record WallPlatform(int X,int Y,int Side,int Slot,int ItemType,int JumpFrames);
    private readonly HashSet<(int,int)> triedWallPlatforms=new();
    internal bool ContinuingSpecialRoute=>wallPlatform!=null||bridgeTip.HasValue||bankCrossing!=null;
    public bool CanBuildWallStep=>PlanWallPlatform()!=null;
    private readonly HashSet<(int X,int Row)> attemptedBridgeStarts=new();
    private readonly Queue<object> decisions=new();
    public bool PlanningPending{get;private set;}
    public AiAtomicActions.NativeResult PlanningFailure{get;private set;}
    public object PlanningEvidence{get;private set;}

    internal NativeNavigationWorld(AiReadOnlyProtocol.Identity identity,JsonElement target,NavigationKnowledge knowledge=null)
    {
        this.knowledge=knowledge??new();
        this.identity=identity;
        this.target=target.Clone();
        player=(Player)identity.Actor;
        initialLife=player.statLife;
        x=target.GetProperty("x").GetSingle();
        y=target.GetProperty("y").GetSingle();
        destination=LandingRegion.AtDestination(x,y);
        this.knowledge.ShoreGoal=destination;
    }

    public object Target=>target;
    public bool ReplanAfterChange=>true;
    public bool CanInspectWaterBoundary
    {
        get
        {
            var p=ServerAiNavigation.Observe(player);
            return p.Settled&&new NativeMotionWorld(p.State).WaterAhead(p.State,Math.Sign(x-player.Center.X));
        }
    }
    public string ProgressKey=>CheckpointKey(player);
    internal static string CheckpointKey(Player actor)
    {
        var pose=NativeMotionWorld.Observe(actor);
        return JsonSerializer.Serialize(new{pose,terrain=new NativeMotionWorld(pose).TerrainSignature(pose)});
    }
    public string NoRouteReason=>edits.SearchFailure??(liquidBoundary
        ?"navigation_liquid_local_frontier_exhausted":"navigation_local_frontier_exhausted");
    public string SearchDetail=>planningDetail??edits?.SearchDetail;
    public object OpeningEvidence=>edits?.OpeningEvidence;
    public object ArrivalEvidence{get;private set;}
    public string InterruptionReason()=>CheckHazards(identity,initialLife,travelling:true);

    internal static string CheckHazards(AiReadOnlyProtocol.Identity identity,int initialLife,bool travelling=false)
    {
        var player=(Player)identity.Actor;
        ServerAiNativeActions.Require(identity);
        if(travelling?NativeNavigation.ExceedsTravelBudget(player,initialLife):player.statLife<initialLife)return "task_interrupt_health_decreased";
        if(player.lavaWet)return "task_interrupt_lava_contact";
        // Bounded native safety check, on the game thread; no model/TCP wait.
        foreach(var npc in Main.npc)
        {
            if(!npc.active||npc.life<=0||npc.friendly||npc.townNPC||npc.damage<=0)continue;
            if(NativeCombatWorld.ContactTick(player,npc,player.velocity.X) is not int contact)continue;
            if(contact>1 && (!npc.HasValidTarget||npc.target!=player.whoAmI))continue;
            if(contact>0 && !npc.noTileCollide && !Collision.CanHitLine(npc.position,npc.width,npc.height,player.position,player.width,player.height))continue;
            int cooldown=-1;
            if((!travelling||npc.boss||NativeNavigation.ExceedsTravelBudget(player,initialLife,npc.damage))
                &&NPCLoader.CanHitPlayer(npc,player,ref cooldown))return "task_interrupt_nearby_hostile";
        }
        return null;
    }

    public string Prepare()
    {
        if(!WorldGen.InWorld((int)(x/16),(int)(y/16),2))return "invalid_navigation_destination";
        if(!GameNavigation.Ordinary(player))return "unsupported_navigation_profile";
        edits=new(identity,ServerAiNavigation.Observe(player).State,(x,y)=>identity.Autonomy!=null&&AutonomyPolicy.ProtectedTile(x,y),rolling:true);
        return null;
    }

    public string Revalidate()=>null;

    public bool Arrived()
    {
        var observed=ServerAiNavigation.Observe(player);
        if(!observed.Settled||!destination.Arrived(observed.State,new NativeMotionWorld(observed.State)))
            return false;
        var pose=observed.State;
        ArrivalEvidence=new{target=Target,x=pose.Px,y=pose.Py,feetY=pose.Py+PhysicsSimulator.PlayerH,
            arrived=true,grounded=pose.Grounded,safeSupport=observed.SafeSupport,settled=observed.Settled};
        return true;
    }

    public NavigationStep FindRopeRoute()
    {
        var pose=ServerAiNavigation.Observe(player);
        float rise=player.Bottom.Y-y;
        if(!pose.Settled || rise<=0 || rise>320 || y%16!=0)return null;
        var physics=NativeNavigation.Parameters(player);
        if(rise<=physics.RiseBound(pose.State.Air?.WingTime??0))return null;
        int from=(int)(player.Center.X/16), side=Math.Sign(x-player.Center.X);
        int head=ServerAiRope.Head(player.position.Y), row=(int)(y/16);
        int floor=(int)Math.Round(player.Bottom.Y/16);
        if(side==0 || Math.Abs(player.Bottom.Y-floor*16)>.5f)return null;
        // Approach only across a continuous dry floor with standing clearance.
        // The movement child still validates and executes the complete approach.
        for(int distance=0;distance<=6;distance++)
        {
            int col=from+side*distance;
            if(!WorldGen.InWorld(col,floor,12) || TerrainReader.ReadDryCell(col,floor)!='#')break;
            bool clear=true;
            for(int rowAbove=1;rowAbove<=3;rowAbove++)
                if(TerrainReader.ReadDryCell(col,floor-rowAbove)!='.')clear=false;
            if(!clear)break;
            if(!ServerAiRope.Rope(col,head))continue;
            int exitCol=col+side*2;
            if(side*(x-exitCol*16-8)<-10 || !ServerAiRope.SideExitClear(col,row,side))continue;
            int top=ServerAiRope.Head(y-32-player.height);
            bool continuous=true;
            for(int at=top;at<=head;at++)
                if(!ServerAiRope.Rope(col,at)){continuous=false;break;}
            if(!continuous)continue;
            if(distance!=0)
                return new("navigate_to_landing_region",RegionArguments(new(col*16-5,col*16+1,floor*16)));
            return new("move_on_rope",JsonSerializer.SerializeToElement(new{ropeX=col,targetFeetY=y-32,
                finish="hold",maxUpdates=300,ledgeDirection=side}));
        }
        return null;
    }

    public JsonElement? FindApproach()
    {
        ReleasePlanning();
        if(bankCrossing is {} crossing)
        {
            bankCrossing=null;bridgeTip=null;
            knowledge.Bind(player,identity.World,identity.Generation,identity.Slot);
            knowledge.BankCrossing=crossing;
            return RegionArguments(crossing.Goal);
        }
        return RegionArguments(destination);
    }

    public NavigationStep FindOpening(int remainingTiles)
    {
        if(wallPlatform!=null && !wallPlacementIssued)
        {
            if(ValidateWallPlacement()!=null)
            {
                wallPlatform=null; planningDetail="jump did not clear wall platform placement";return null;
            }
            wallPlacementIssued=true;
            var step=wallPlatform;
            planningDetail="place wall platform below airborne player";
            return new("place_tile",JsonSerializer.SerializeToElement(new{x=step.X,y=step.Y,plannedPlacementSlot=step.Slot}));
        }
        if(PlanWallPlatform() is {} launch)
        {
            wallPlatform=launch;
            triedWallPlatforms.Add((launch.X,launch.Y));
            planningDetail="jump vertically to place a wall platform";
            return new("move_input",JsonSerializer.SerializeToElement(new{horizontal="none",vertical="none",
                jumpHoldUpdates=launch.JumpFrames,maxUpdates=launch.JumpFrames}));
        }
        if(bridgeStep is {} continuation)
        {
            bridgeStep=null;
            return TrackPlacement(continuation);
        }
        Evaluate(remainingTiles,ready?forced:true);
        if(PlanningPending||PlanningFailure!=null)return null;
        var plan=selected;
        ReleasePlanning();
        return TrackPlacement(plan!=null?edits.ArmPlan(plan.Actions):BridgeStart());
    }

    private NavigationStep TrackPlacement(NavigationStep step)
    {
        pendingTip=null;
        if(step?.Operation=="place_tile"&&step.Arguments.TryGetProperty("waterBridgeDirection",out var dir)
            &&dir.GetInt32() is -1 or 1)
            pendingTip=(step.Arguments.GetProperty("x").GetInt32(),step.Arguments.GetProperty("y").GetInt32(),dir.GetInt32());
        return step;
    }

    private NavigationStep ContinueBridge()
    {
        if(bridgeTip is not {} tip)return null;
        var stance=ServerAiNavigation.Observe(player);
        var material=edits.Materials.FirstOrDefault(m=>m.Shape=='='&&m.Count>0);
        var view=new LocalTerrainView(edits.Environment);
        int next=tip.X+tip.Side;
        if(stance.Settled&&material!=null&&view.Cell(tip.X,tip.Y)=='='
            &&(x-tip.X*16-8)*tip.Side>0&&LocalEditGeometry.WaterDeckCell(view,next,tip.Y))
        {
            var pose=stance.State;
            float tipLeft=tip.X*16-2;
            bool launchFromTip=(pose.ExtraAvailable || pose.Air?.WingTime>0) && tip.Side*(tipLeft-pose.Px)>=32;
            if(launchFromTip || Math.Abs(next*16+8-player.Center.X)>Player.tileRangeX*16
                ||!player.InInteractionRange(next,tip.Y,Terraria.DataStructures.TileReachCheckSettings.Simple)
                ||Math.Abs(pose.Py+42-tip.Y*16)>LandingRegion.FeetTolerance)
            {
                var onDeck=new PhysicsState(tipLeft,tip.Y*16-42,0,0,true);
                if(new NativeMotionWorld(pose).SafeLanding(onDeck))
                    return new("navigate_to_landing_region",RegionArguments(new(tipLeft-2,tipLeft+2,tip.Y*16)));
            }
            else if(edits.Place(pose,next,tip.Y,material,view))
            {
                var after=view.Fork();
                var landing=new PhysicsState(next*16-2,tip.Y*16-42,0,0,true);
                if(after.Set(next,tip.Y,'=',material.Type)&&LocalEditGeometry.LandingBand(landing,
                    (tx,ty)=>LocalEditGeometry.LandingCell(after,tx,ty,tip.Y)))
                {
                    var step=edits.Arm(new(LocalEditKind.Place,pose,pose,next,tip.Y,material.Slot,material.Type,'=',tip.Side));
                    if(step!=null)return step;
                }
            }
        }
        bridgeTip=null;
        return null;
    }

    private NavigationStep BridgeStart(bool placeOnly=false)
    {
        var observed=ServerAiNavigation.Observe(player);
        var material=edits.Materials.FirstOrDefault(m=>m.Shape=='='&&m.Count>0);
        if(!observed.Settled||material==null)return null;
        var pose=observed.State;
        int side=Math.Sign(Math.Clamp(pose.Px,destination.MinPlayerLeftX,destination.MaxPlayerLeftX)-pose.Px);
        if(side==0)return null;
        int cx=FallSafety.Row(pose.Px+10),row=FallSafety.Row(pose.Py+42);
        var view=new LocalTerrainView(edits.Environment);
        var world=new NativeMotionWorld(pose);
        LandingRegion best=null;
        float distance=float.MaxValue;
        (int X,int Row) key=default;
        // Search both ways: the exploration stride may already have walked
        // below the waterline. A new deck starts at an attached, level bank.
        for(int offset=-12;offset<=12;offset++)
            for(int y=row-5;y<=row+3;y++)
            {
                int bank=cx+offset,deck=bank+side;
                if(!LocalEditGeometry.WaterDeckCell(view,deck,y))continue;
                // A player can attach the first platform from a nearby sloping
                // shore. Verify the resulting deck support before spending it.
                if(Math.Abs(offset)<=3&&side*(deck-cx)>=-1&&Math.Abs(y*16-pose.Py-42)<=32
                    &&edits.Place(pose,deck,y,material,view))
                {
                    var after=view.Fork();
                    var landing=new PhysicsState(deck*16-2,y*16-42,0,0,true);
                    if(after.Set(deck,y,'=',material.Type)&&LocalEditGeometry.LandingBand(landing,
                        (x,ty)=>LocalEditGeometry.LandingCell(after,x,ty,y)))
                    {
                        var placement=edits.Arm(new(LocalEditKind.Place,pose,pose,deck,y,material.Slot,material.Type,'=',side));
                        if(placement!=null){planningDetail="attach reachable shoreline platform";return placement;}
                    }
                }
                if(placeOnly||view.Cell(bank,y) is not ('#' or '=')||attemptedBridgeStarts.Contains((bank,y)))continue;
                for(int overhang=0;overhang<=8;overhang+=4)
                {
                    var start=new PhysicsState(bank*16-2+side*overhang,y*16-42,0,0,true);
                    if(!world.SafeLanding(start)||!LocalEditGeometry.LandingBand(start,
                        (x,ty)=>LocalEditGeometry.LandingCell(view,x,ty,y)))continue;
                    var region=new LandingRegion(start.Px-2,start.Px+2,y*16);
                    float cost=region.Distance(pose);
                    if(cost<=2||cost>=distance)continue;
                    best=region;key=(bank,y);distance=cost;
                }
            }
        if(best==null)return null;
        attemptedBridgeStarts.Add(key);
        planningDetail="move to level shoreline support to start a horizontal platform deck";
        return new("navigate_to_landing_region",RegionArguments(best));
    }

    private WallPlatform PlanWallPlatform()
    {
        var pose=ServerAiNavigation.Observe(player);
        int side=Math.Sign(x-player.Center.X);
        if(side==0 || !pose.Settled || pose.State.Wet || !player.releaseJump)return null;
        int col=(int)(player.Center.X/16),feet=(int)Math.Round(player.Bottom.Y/16);
        int wall=col+side;
        if(!WorldGen.InWorld(wall,feet,12))return null;
        // Require a continuous face higher than a normal step or short hop.
        for(int rise=1;rise<=5;rise++)
            if(!WorldGen.SolidTile(wall,feet-rise))return null;
        var material=edits.Materials.FirstOrDefault(m=>m.Type==TileID.Platforms);
        if(material==null)return null;
        var ph=NativeNavigation.Parameters(player);
        var world=new NativeMotionWorld(pose.State);
        var jump=PhysicsSimulator.Predict(pose.State,side,ph.JumpHeight,ph,world);
        if(jump.Safe && side*(jump.Landing.Px+PhysicsSimulator.PlayerW/2-(wall*16+8))>=0
            && jump.Landing.Py<pose.State.Py-16)return null;
        var predicted=pose.State;
        WallPlatform best=null;
        try
        {
            for(int frame=1;frame<=ph.JumpHeight && frame<=30;frame++)
            {
                var next=PhysicsSimulator.Step(predicted,new PhysicsInput(0,true),ph,world);
                if(!world.SafeSweep(predicted,next,out _) || next.Py>=predicted.Py)break;
                predicted=next;
                int row=(int)Math.Ceiling((next.Py+PhysicsSimulator.PlayerH+4)/16);
                if(feet-row<2 || triedWallPlatforms.Contains((col,row)) || Main.tile[col,row].HasTile
                    || Main.tile[col,row].LiquidAmount!=0 || !WorldGen.SolidTile(wall,row))continue;
                best=new(col,row,side,material.Slot,player.inventory[material.Slot].type,frame);
            }
        }
        catch(UnsafeMotionException){return null;}
        return best;
    }

    private string ValidateWallPlacement()
    {
        var step=wallPlatform;
        var item=player.inventory[step.Slot];
        return item.type==step.ItemType && item.createTile==TileID.Platforms && item.stack>0
            && player.Bottom.Y<=step.Y*16 && player.Right.X>step.X*16 && player.Left.X<(step.X+1)*16
            && !Main.tile[step.X,step.Y].HasTile && Main.tile[step.X,step.Y].LiquidAmount==0
            && WorldGen.SolidTile(step.X+step.Side,step.Y)
            && ActorRope.InReach(player,item,step.X,step.Y)?null:"navigation_wall_platform_changed";
    }

    private BankAirSearch bankSearch;
    private readonly Queue<object> bankDecisions=new();
    public object BankAirEvidence=>bankDecisions.Count==0?null:bankDecisions.ToArray();
    private NavigationQuote bankCrossing;

    // Evaluate dry trajectories on the actual tiles. Landing on a safe island or
    // existing deck counts as progress even when the final goal is far inland.
    private sealed class BankAirSearch
    {
        internal readonly PhysicsState Start;
        internal readonly PhysicsParameters Physics;
        internal readonly long Terrain;
        private readonly IEnumerator<LaunchInput> launches;
        private Prediction best;
        private float bestRate;
        private long bestTerrain;
        private readonly Dictionary<string,float> furthest=new();
        private int trials;
        private readonly Dictionary<string,int> rejected=new();
        internal bool Finished {get;private set;}
        internal object Evidence=>new{start=Start,trials,rejected,furthest,landing=best?.Landing,frames=best?.ExecuteFrames,
            extra=best?.ExtraJumpFrame,wing=best?.AirStartFrame};

        internal BankAirSearch(PhysicsState start,PhysicsParameters physics,long terrain,int side)
        {
            Start=start;Physics=physics;Terrain=terrain;
            launches=Candidates(start,physics,side).GetEnumerator();
        }

        private static IEnumerable<LaunchInput> Candidates(PhysicsState start,PhysicsParameters ph,int side)
        {
            foreach(var launch in ph.Launches(start,side,4))
            {
                if(launch.Hold==0 || launch.ImpulseFrame>=0)continue;
                yield return launch;
                // Coarse flight candidates jump from 64 updates to a full tank.
                // Sample intermediate release times for far-bank landings.
                if(launch.AirStart>=0 && launch.AirHold==64 && start.Air.WingTime>64)
                    for(int duration=72;duration<Math.Min(start.Air.WingTime,120);duration+=8)
                        yield return launch with{AirHold=duration};
                if(launch.ExtraFrame<0 || ph.Air?.Kind!=AirKind.Wings || start.Air?.WingTime<=0 ||
                    launch.ExtraHold>=0)continue;
                int wingStart=launch.ExtraFrame;
                var jumps=ph.ExtraJump;
                for(int i=0;i<launch.ExtraCount && jumps!=null;i++,jumps=jumps.Next)
                    wingStart+=jumps.HoldFrames(ph)+2;
                // Keep jump held across the end of the last extra jump.
                wingStart-=2;
                foreach(int hold in new[]{12,32,64,(int)Math.Min(120,start.Air.WingTime)}.Distinct())
                    if(hold>0 && hold<=start.Air.WingTime)yield return launch with{AirStart=wingStart,AirHold=hold};
            }
        }

        internal NavigationQuote Advance(NativeMotionWorld world,LandingRegion destination,int direction,NavigationFailureMemory failures)
        {
            var timer=System.Diagnostics.Stopwatch.StartNew();
            do
            {
                if(!launches.MoveNext()){Finished=true;launches.Dispose();break;}
                var a=launches.Current;
                var jump=PhysicsSimulator.Predict(Start,direction,a.Hold,Physics,world,extraJumpFrame:a.ExtraFrame,
                    extraCount:a.ExtraCount,extraHoldLimit:a.ExtraHold,airStartFrame:a.AirStart,airHoldFrames:a.AirHold);
                trials++;
                float progress=destination.Distance(Start)-destination.Distance(jump.Landing);
                string why=!jump.Safe?jump.Reason:progress<48?"insufficient progress":
                    !PhysicsSimulator.Settled(jump.Landing,world)?"unsettled landing":
                    failures.Reject(Start,jump,(long)Main.GameUpdateCount,world,Physics)?"previously failed":null;
                if(why!=null){rejected[why]=rejected.GetValueOrDefault(why)+1;continue;}
                string family=a.ExtraFrame>=0?(a.AirStart>=0?"extra_wing":"extra"):a.AirStart>=0?"wing":"jump";
                furthest[family]=Math.Max(furthest.GetValueOrDefault(family),direction*(jump.Landing.Px-Start.Px));
                float rate=progress/jump.Frames.Count;
                if(rate<=bestRate)continue;
                bestRate=rate;best=jump with{ExecuteFrames=jump.Frames.Count};
                bestTerrain=world.TerrainSignature(Start,best);
            }while(timer.Elapsed.TotalMilliseconds<3);
            if(!Finished || best==null)return null;
            var goal=new LandingRegion(best.Landing.Px-3,best.Landing.Px+3,best.Landing.Py+PhysicsSimulator.PlayerH);
            long signature=bestTerrain;
            return new(Start,Physics,goal,new Route(new(){best},0,0,ReachedGoal:true,Kind:"bank_air_crossing"),
                ()=>world.TerrainSignature(Start,best)==signature);
        }
    }

    // Estimate a level deck to the same landing. If any segment cannot be
    // supported or supplied, it is not a comparable walk across this pool.
    private float? DeckCost(PhysicsState start,PhysicsState end,int side,PhysicsParameters physics)
    {
        int row=FallSafety.Row(start.Py+42);
        if(Math.Abs(end.Py-start.Py)>LandingRegion.FeetTolerance)return null;
        var view=new LocalTerrainView(edits.Environment);
        int first=FallSafety.Row(start.Px+10),last=FallSafety.Row(end.Px+10);
        int count=0;
        for(int col=first+side;side*(last-col)>=0;col+=side)
        {
            if(view.DryCell(col,row-1)!='.' || view.DryCell(col,row-2)!='.' || view.DryCell(col,row-3)!='.')return null;
            if(view.Cell(col,row) is '#' or '=')continue;
            if(!LocalEditGeometry.WaterDeckCell(view,col,row))return null;
            count++;
        }
        var material=edits.Materials.FirstOrDefault(m=>m.Shape=='=' && m.Count>=count);
        if(count>0 && material==null)return null;
        return Math.Abs(end.Px-start.Px)/physics.MaxRun+count*((material?.Updates??0)+12);
    }

    private bool CheckBankAir()
    {
        var state=ServerAiNavigation.Observe(player);
        int side=Math.Sign(Math.Clamp(state.State.Px,destination.MinPlayerLeftX,destination.MaxPlayerLeftX)-state.State.Px);
        if(!state.Settled || side==0 || player.wet || !GameNavigation.Ordinary(player))return false;
        var world=new NativeMotionWorld(state.State);
        if(!world.WaterAhead(state.State,side))return false;
        var ph=NativeNavigation.Parameters(player);
        if(ph.ExtraJump==null && ph.Air?.Kind!=AirKind.Wings)return false;
        long terrain=world.TerrainSignature(state.State);
        if(bankSearch==null || bankSearch.Start!=state.State || bankSearch.Physics!=ph || bankSearch.Terrain!=terrain)
            bankSearch=new(state.State,ph,terrain,side);
        if(bankSearch.Finished)return false;
        bankCrossing=bankSearch.Advance(world,destination,side,knowledge.Failures);
        float? bankDeckCost=null;
        if(bankCrossing is {} crossing)
        {
            var edge=crossing.Route.Actions[0];
            bankDeckCost=DeckCost(state.State,edge.Landing,side,ph);
            if(bankDeckCost+8<edge.ExecuteFrames)bankCrossing=null;
        }
        if(bankSearch.Finished)
        {
            if(bankDecisions.Count==8)bankDecisions.Dequeue();
            bankDecisions.Enqueue(new{air=bankSearch.Evidence,deckCost=bankDeckCost,selected=bankCrossing!=null});
        }
        PlanningPending=!bankSearch.Finished;
        PlanningEvidence=new{status="bank_air_search",decision=bankSearch.Evidence};
        return PlanningPending || bankCrossing!=null;
    }

    public bool PreferEdit(int remainingTiles)
    {
        if(wallPlatform==null && CheckBankAir())return false;
        if(wallPlatform!=null || PlanWallPlatform()!=null)return true;
        bridgeStep=ContinueBridge();
        if(bridgeStep!=null){planningDetail="continue verified platform deck";return true;}
        // Let the movement child handle an ordinary doorway before pricing terrain edits.
        if(ServerDoor.Ahead(player,Math.Clamp(player.position.X,destination.MinPlayerLeftX,destination.MaxPlayerLeftX),out _,out _))
        {
            ReleasePlanning();
            return false;
        }
        Evaluate(remainingTiles,false);
        if(!ready||selected!=null)return ready&&selected!=null;
        if(!preferDryBank)return false;
        bridgeStep=BridgeStart(placeOnly:true);
        return bridgeStep!=null;
    }

    public void ReleasePlanning()
    {evaluation?.Dispose();evaluation=null;movement?.Dispose();movement=null;ready=false;preferDryBank=false;selected=null;PlanningPending=false;}
    internal long InventoryStamp()
    {
        unchecked
        {
            long hash=ActorMining.CapabilityStamp(player);
            foreach(var item in player.inventory.Take(50))
            {
                foreach(int value in new[]{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item),item.type,item.prefix,item.stack,
                    item.pick,item.axe,item.tileBoost,item.useTime,item.useAnimation,item.createTile,item.placeStyle})hash=hash*31+value;
            }
            return hash;
        }
    }
    private double planningMs,maxSliceMs;
    public object PlanningTime=>new{planningMs,maxSliceMs};
    private void Evaluate(int remaining,bool force)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        try{EvaluateSlice(remaining,force);}
        finally{double ms=timer.Elapsed.TotalMilliseconds;planningMs+=ms;maxSliceMs=Math.Max(ms,maxSliceMs);}
    }
    private void EvaluateSlice(int remaining,bool force)
    {
        if(!GameNavigation.Ordinary(player))
        {
            ReleasePlanning();PlanningFailure=new("failed","unsupported_navigation_profile",null,true);
            planningDetail="local evaluation requires the supported ground profile";return;
        }
        var observed=ServerAiNavigation.Observe(player);
        if(!observed.Settled){PlanningPending=true;planningDetail="waiting for real settled planning state";return;}
        var pose=observed.State;var ph=NativeNavigation.Parameters(player);var world=new NativeMotionWorld(pose,breath:player.breath);
        long terrain=world.TerrainSignature(pose),inventory=InventoryStamp();
        if(evaluation==null||plannedStart!=pose||plannedPhysics!=ph||plannedTerrain!=terrain||plannedInventory!=inventory||forced!=force)
        {
            ReleasePlanning();PlanningFailure=null;forced=force;plannedStart=pose;plannedPhysics=ph;plannedTerrain=terrain;plannedInventory=inventory;
            edits.BeginLocalPlanning(pose);
            knowledge.Bind(player,identity.World,identity.Generation,identity.Slot);knowledge.ObservePhysics(ph);
            evaluation=new(pose,destination,ph,edits,remaining);
        }
        if(ready)return;
        PlanningPending=true;evaluation.Advance();
        if(!evaluation.Finished)
        {PlanningEvidence=new{status="evaluation_budget",work=evaluation.Work};planningDetail="local candidate evaluation deferred";return;}
        var plans=evaluation.Plans;
        Route route=null;SurfaceGraphSnapshot graph=null;float? moving=null;bool complete=false;
        bool dryBank=!pose.Wet&&world.WaterAhead(pose,Math.Sign(x-player.Center.X));
        if(!force&&(dryBank||plans.Any(p=>p.WaterPlatform))&&PoolCrossing.TryPlan(pose,destination,ph,world) is {} crossing
            &&!knowledge.Failures.Reject(pose,crossing.Actions[0],(long)Main.GameUpdateCount,world,ph))
        {
            ready=true;PlanningPending=false;selected=null;planningDetail="verified pool crossing before platform placement";
            PlanningEvidence=new{status="complete",choice=PoolCrossing.Kind,frames=crossing.Actions[0].ExecuteFrames};return;
        }
        if(!force&&plans.Length>0)
        {
            if(destination.Distance(pose)<=64)route=SafeGroundWalk.TryPlan(pose,(destination.MinPlayerLeftX+destination.MaxPlayerLeftX)/2,
                destination.MinPlayerLeftX>=pose.Px?1:-1,ph,world,destination);
            if(route==null)
            {
                knowledge.Search.Scope(terrain,ph,(long)Main.GameUpdateCount);
                movement??=new(knowledge.Surfaces,pose,destination,ph,world,world,knowledge.Search,
                    (at,edge)=>knowledge.Failures.Reject(at,edge,(long)Main.GameUpdateCount,world,ph),allowTravelPrefix:true);
                route=movement.Advance(budgetMs:3);graph=movement.Snapshot;
                if(route.Actions.Count==0&&!movement.Finished)
                {PlanningEvidence=new{status="movement_budget",candidates=plans.Length,graph};planningDetail="movement comparison deferred";return;}
                if(route.Actions.Count==0&&route.TimeBudgetReached)
                {
                    // A total local-search allowance is inconclusive. Retain
                    // normal native navigation rather than declaring no route
                    // and performing edits on the strength of a budget exit.
                    ready=true;PlanningPending=false;selected=null;
                    PlanningEvidence=new{status="movement_evaluation_incomplete",choice="movement",graph};
                    planningDetail="movement comparison incomplete; defer to native movement";return;
                }
                if(route.SearchExit is "surface_node_budget" or "unknown_terrain" or "unsupported_model")
                {
                    string reason=route.SearchExit=="surface_node_budget"?"navigation_surface_node_budget":route.SearchExit=="unknown_terrain"?"native_navigation_terrain_unknown":"unsupported_navigation_profile";
                    PlanningFailure=new(route.SearchExit=="unknown_terrain"?"unknown":route.SearchExit=="surface_node_budget"?"timeout":"failed",reason,null,true);
                    PlanningPending=false;return;
                }
            }
            if(route.Actions.Count>0)
            {
                complete=route.ReachedGoal;
                float actual=route.Actions.Sum(a=>a.ExecuteFrames);
                moving=complete?actual:Math.Max(actual,graph?.EstimatedRouteUpdates??(actual+destination.Distance(route.Actions[^1].Landing)/ph.MaxRun));
                // A prefix ending at the blocker is not a priced bypass. Keep
                // that uncertainty explicit rather than treating straight-line
                // remaining distance as a proved cheap route through the wall.
                if(!complete&&plans.Any(p=>p.Cost.Remaining*ph.MaxRun<destination.Distance(route.Actions[^1].Landing)))moving=null;
            }
        }
        preferDryBank=!force&&dryBank&&!complete;
        bool localLiquidBoundary=evaluation.LiquidBoundary;
        liquidBoundary|=localLiquidBoundary;
        selected=complete&&localLiquidBoundary?null:LocalEditEvaluation.Choose(plans,moving,complete);
        ready=true;PlanningPending=false;
        planningDetail=selected!=null?"bounded local edit plan selected; execute one action then reobserve"
            :liquidBoundary&&!complete?"no verified movement or admissible edit across the local liquid boundary"
            :"movement preferred or no admissible local edits";
        var decision=new{choice=selected==null?"movement":"local_edits",movementCost=moving,movementVerifiedComplete=complete,
            candidateCount=plans.Length,selected,liquidBoundary=localLiquidBoundary,virtualNativeVerified=false,gameUpdate=Main.GameUpdateCount};
        if(decisions.Count>=8)decisions.Dequeue();decisions.Enqueue(decision);
        PlanningEvidence=new{status="complete",decision.choice,decision.movementCost,decision.movementVerifiedComplete,
            candidateCount=plans.Length,selected,liquidBoundary,virtualNativeVerified=false,graph,history=decisions.ToArray(),
            candidates=plans.OrderBy(p=>p.Cost.Total).Take(6).Select(p=>new{p.Cost,p.OpenExit,changes=p.Changes,steps=p.Actions.Length}).ToArray()};
    }

    internal static JsonElement RegionArguments(LandingRegion region)=>JsonSerializer.SerializeToElement(new
    {
        minPlayerLeftX=region.MinPlayerLeftX,maxPlayerLeftX=region.MaxPlayerLeftX,surfaceY=region.SurfaceY
    });

    public string RevalidateOpening()=>wallPlacementIssued?ValidateWallPlacement():edits.RevalidateOpening();
    public bool OpeningSettled()=>edits.OpeningSettled();
    public string OpeningLandingError()
    {
        if(wallPlacementIssued)
        {
            var step=wallPlatform;wallPlatform=null;wallPlacementIssued=false;
            return Main.tile[step.X,step.Y].HasUnactuatedTile && Main.tile[step.X,step.Y].TileType==TileID.Platforms
                && Math.Abs(player.Bottom.Y-step.Y*16)<=LandingRegion.FeetTolerance?null:"wall platform landing changed";
        }
        var error=edits.OpeningLandingError();
        if(pendingTip.HasValue&&error==null)bridgeTip=pendingTip;
        pendingTip=null;
        return error;
    }
}
