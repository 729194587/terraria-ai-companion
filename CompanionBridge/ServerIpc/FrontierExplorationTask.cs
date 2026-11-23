using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using Terraria;
using VirtualPlayerEngineProbe;
using Result=CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

// One cancellable native root owns outbound travel, observation and return.
internal sealed class FrontierExplorationTask : AbstractCompanionTask
{
    internal const ulong Budget=3600;
    private readonly ServerAiNativeActions.ChildExecutor executor;
    private readonly JsonElement args;
    private readonly AiReadOnlyProtocol.Identity identity;
    private readonly Player actor;
    private readonly AiMap map;
    private ExplorationTrip trip;
    private ExplorationLeg leg;
    private LandingRegion expectedLanding;
    private NativeActionTask action;
    private int life,sequence;
    private float anchorLeft;
    private ulong deadline,nextObservation;
    private long quotedSignature;
    private int reservedReturn;
    private IEnumerator<int> planning;
    private MapPoint planningStart;
    private long planningRevision;
    private TravelProfile planningProfile;
    private bool acquired;
    private bool AllowDig=>args.TryGetProperty("allowDig",out var v)&&v.GetBoolean();
    internal FrontierExplorationTask(string id,AiReadOnlyProtocol.Identity identity,JsonElement args,ServerAiNativeActions.ChildExecutor executor):base(id)
    {this.identity=identity;actor=(Player)identity.Actor;this.args=args.Clone();this.executor=executor;map=AiMapRuntime.Map;}
    internal override string Name=>"explore_frontier";
    internal override string Phase=>trip==null?"preflight":trip.Returning?"returning":"exploring";
    private MapPoint At=>new((int)Math.Floor(actor.position.X/16),(int)Math.Round(actor.Bottom.Y/16));
    private bool Home=>trip!=null && Math.Abs(actor.position.X-anchorLeft)<=2 &&
        Math.Abs(actor.Bottom.Y-trip.Anchor.Y*16)<=.5f;
    private object Evidence(bool returned=false)=>new{segments=trip?.Segments??0,newTiles=trip?.NewTiles??0,returned,
        anchorLeft,anchorFeetY=(trip?.Anchor.Y??0)*16,actualLeft=actor.position.X,actualFeetY=actor.Bottom.Y,
        mapRevision=map?.Revision??0,candidates=trip?.Choices.Select(c=>new{x=c.Stance.X,y=c.Stance.Y,gain=c.Gain,
            outboundUpdates=c.Outbound,returnUpdates=c.Return}).ToArray()};
    internal override object Progress=>Evidence();
    internal override Result Prepare(AiReadOnlyProtocol.Identity frame)
    {
        if(map==null||!WorldCharacterPersistence.Ready||map.World!=Main.ActiveWorldFileData.UniqueId||
            map.Character!=WorldCharacterPersistence.ProfileId)return Failed("exploration_map_unavailable");
        life=actor.statLife;
        if(!GameNavigation.Ordinary(actor)||!ServerAiNavigation.Observe(actor).Settled||
            Math.Abs(actor.Bottom.Y-At.Y*16)>.5f)return Failed("exploration_requires_settled_flat_anchor");
        if(NativeNavigationWorld.CheckHazards(frame,life) is {} danger)return Failed(danger);
        AiMapRuntime.Tick(true);
        anchorLeft=actor.position.X;
        trip=new(map,At,args.GetProperty("radiusTiles").GetInt32(),args.GetProperty("maxSegments").GetInt32());
        deadline=frame.GameUpdate+Budget-2;
        return null;
    }
    private TravelCell Inspect(MapPoint p,bool quote)
    {
        byte remembered=map.Get(p.X,p.Y);
        if(remembered==0)return new('?');
        if(Math.Abs(p.X-At.X)>20 || Math.Abs(p.Y-At.Y)>11)
            return new(remembered switch{1=>'.',2=>'#',3=>'=',_=>'?'});
        var tile=TerrainReader.ReadEnvironment(p.X,p.Y);
        if(tile.Liquid.Amount>0)
            return new(remembered==4&&tile.Shape=='.'&&tile.Liquid.Kind==LiquidKind.Water&&tile.Liquid.Amount<=128?'~':'!');
        char shape=tile.Shape;
        if(Main.tile[p.X,p.Y].HasUnactuatedTile && TerrainReader.UnsupportedMotionSurface(Main.tile[p.X,p.Y].TileType))return new('!');
        if(shape=='.'&&remembered!=1 || shape=='#'&&remembered!=2 || shape=='='&&remembered!=3)return new('?');
        if(!quote || !AllowDig || shape!='#' || Math.Abs(p.X-At.X)>5 || Math.Abs(p.Y-At.Y)>5)return new(shape);
        // Reject uncertain or wet removal neighborhoods, including falling blocks
        // and furniture supported by the cut. Keep the same rules as local excavation.
        for(int x=p.X-1;x<=p.X+1;x++)
        for(int y=p.Y-1;y<=p.Y+1;y++)
            if(!trip.Window.Contains(new(x,y)) || map.Get(x,y) is 0 or >=4)return new(shape);
        var view=new LocalTerrainView(TerrainReader.ReadEnvironment);
        if(view.LiquidNear(p.X,p.Y)||!NativeNavigationEdits.SafeExcavation(p.X,p.Y,view))return new(shape);
        var tool=ActorMining.ChooseTool(actor,identity.World,identity.Generation,p.X,p.Y,true,out int slot,out float updates);
        return new(shape,tool==ActorMining.Failure.None&&ActorMining.SupportedPick(actor.inventory[slot])?(int)Math.Ceiling(updates):0);
    }
    private TravelProfile Profile()
    {
        var ph=NativeNavigation.Parameters(actor);
        int rise=ph.Air!=null&&actor.wingTime>0 || ph.ExtraJump!=null&&NativeMotionWorld.Observe(actor).ExtraAvailable?3:1;
        return new(rise,3,24+(int)Math.Ceiling(16/ph.MaxRun));
    }
    private long Signature(ExplorationLeg edge)
    {
        var world=new NativeMotionWorld(NativeMotionWorld.Observe(actor));
        var hash=new HashCode();
        hash.Add(NativeNavigation.Parameters(actor));hash.Add(actor.wingTime);
        if(AllowDig)hash.Add(ActorMining.CapabilityStamp(actor));
        for(int y=Math.Min(edge.From.Y,edge.To.Y)-4;y<=Math.Max(edge.From.Y,edge.To.Y);y++)
        for(int x=Math.Min(edge.From.X,edge.To.X)-1;x<=Math.Max(edge.From.X,edge.To.X)+2;x++)
            if(map.Get(x,y)!=0)hash.Add(world.ReadSurfaceCell(x,y).Signature);
        return hash.ToHashCode();
    }
    internal override Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        if(!ReferenceEquals(map,AiMapRuntime.Map))return Stop(frame,"unknown","exploration_map_changed");
        if(NativeNavigationWorld.CheckHazards(frame,life) is {} danger)return Stop(frame,"cancelled",danger);
        foreach(var shot in Main.projectile)
        {
            if(!shot.active||!shot.hostile||shot.damage<=0)continue;
            var threat=SceneThreat.Project(new(actor.position.X,actor.position.Y,actor.width,actor.height),new(actor.velocity.X,actor.velocity.Y),
                new(shot.position.X,shot.position.Y,shot.width,shot.height),new(shot.velocity.X,shot.velocity.Y),shot.extraUpdates);
            if(threat.ContactAfterTicks!=null)return Stop(frame,"cancelled","exploration_projectile_approaching");
        }
        if(action!=null)
        {
            if(!trip.Returning && (long)deadline-(long)frame.GameUpdate<=reservedReturn &&
                ServerAiNavigation.Observe(actor).Settled)
            {
                var stop=base.Stop(frame,"cancelled","exploration_return_reserve");
                if(!stop.InputsReleased||stop.Status=="unknown")return stop;
                acquired|=stop.InputsAcquired;
                action=null;trip.ReturnNow();return Running();
            }
            if(!action.Started && Signature(leg)!=quotedSignature)
            {
                base.Stop(frame,"cancelled","exploration_quote_changed");
                action=null;return Running();
            }
            var result=RunChild(action,frame,600);
            if(result==null)return Running();
            acquired|=result.InputsAcquired;action=null;
            if(!result.InputsReleased||result.Status is "unknown" or "cancelled")return result;
            if(result.Status!="completed")
            {
                trip.Reject(leg,(long)frame.GameUpdate,quotedSignature);
                if(!ServerAiNavigation.Observe(actor).Settled)return result;
            }
            else if(expectedLanding!=null)
            {
                var actual=ServerAiNavigation.Observe(actor);
                if(!actual.Settled)return End("unknown","exploration_landing_unconfirmed");
                if(expectedLanding.Contains(actual.State))trip.ArrivedAt(At,(long)frame.GameUpdate);
                else if(leg.From==leg.To)return End("failed","exploration_anchor_missed");
                else trip.Reject(leg,(long)frame.GameUpdate,quotedSignature);
            }
            nextObservation=frame.GameUpdate+15;
            return Running();
        }
        if(frame.GameUpdate<nextObservation)return Running();
        var observation=ServerAiNavigation.Observe(actor);
        if(!observation.Settled || !GameNavigation.Ordinary(actor))return End("failed","exploration_stance_unconfirmed");
        var profile=Profile();
        int remaining=(int)Math.Max(0,(long)deadline-(long)frame.GameUpdate);
        if(planning==null || At!=planningStart || planningRevision!=map.Revision || profile!=planningProfile)
        {
            planning?.Dispose();
            planningStart=At;planningRevision=map.Revision;planningProfile=profile;
            var costs=new ExplorationCostmap(map,trip.Window,profile,p=>Inspect(p,true));
            planning=trip.Plan(costs,At,(long)frame.GameUpdate,remaining,Signature).GetEnumerator();
        }
        long started=Stopwatch.GetTimestamp();
        for(int work=0;planning.MoveNext();work++)
            if(work>=31 || Stopwatch.GetElapsedTime(started).TotalMilliseconds>=2)return Running();
        planning.Dispose();planning=null;
        leg=trip.Next;
        if(!trip.Returning && trip.Choices.Count>0)
        {
            var choice=trip.Choices[0];
            reservedReturn=choice.Return+45;
            if(remaining<choice.Outbound+reservedReturn){trip.ReturnNow();return Running();}
        }
        bool finalAlignment=trip.Returning&&At==trip.Anchor;
        if(trip.Returning&&Home)
            return trip.NewTiles>0&&trip.Departed?new("completed","frontier_observed_and_returned",Evidence(true),true,true,acquired)
                :new("failed","frontier_no_new_tiles",Evidence(true),true,false,acquired);
        if(finalAlignment)leg=new(At,At,24,null);
        if(leg==null)return End("failed",trip.Returning?"frontier_return_unavailable":"frontier_unavailable");
        if(remaining<leg.Updates)return End("timeout","exploration_budget_exhausted");
        if(!finalAlignment)
        {
            var live=new ExplorationCostmap(map,trip.Window,profile,p=>Inspect(p,true));
            if(live.Edge(leg.From,leg.To)!=leg)return Reject(frame);
        }
        float left=finalAlignment?anchorLeft:leg.To.X*16+3;
        var target=new LandingRegion(left-1,left+1,leg.To.Y*16);
        var pose=new PhysicsState(left,target.SurfaceY-42,0,0,true);
        var world=new NativeMotionWorld(observation.State);
        if(leg.Cut==null && (!world.SafeLanding(pose)||!KnownPose(map,trip.Window,pose)))
            return finalAlignment?End("failed","exploration_anchor_changed"):Reject(frame);
        if(leg.To.Y>leg.From.Y)
        {
            // Price directionally, then prove the reverse rise with spent air resources
            // before committing to the descent. Keep the native footprint inside memory.
            var back=new LandingRegion(actor.position.X-1,actor.position.X+1,actor.Bottom.Y);
            var route=StateSpacePlanner.Plan(pose,back,NativeNavigation.Parameters(actor),world,budgetMs:4);
            if(!route.ReachedGoal || route.Actions.Any(e=>e.Frames.Any(f=>!KnownPose(map,trip.Window,f.State))))return Reject(frame);
        }
        string op;JsonElement parameters;
        expectedLanding=null;
        if(leg.Cut is {} cut)
        {
            if(trip.Returning || Inspect(cut,true).MiningUpdates==0)return Reject(frame);
            op="mine_tile";parameters=JsonSerializer.SerializeToElement(new{x=cut.X,y=cut.Y});
        }
        else
        {
            op="navigate_to_landing_region";
            var window=trip.Window;
            // Include body width, braking distance and headroom for a local jump.
            executor.ExplorationWindow=new MapWindow(Math.Max(window.Left,Math.Min(leg.From.X,leg.To.X)-1),
                Math.Max(window.Top,Math.Min(leg.From.Y,leg.To.Y)-9),
                Math.Min(window.Right,Math.Max(leg.From.X,leg.To.X)+2),Math.Max(leg.From.Y,leg.To.Y));
            expectedLanding=target;
            parameters=NativeNavigationWorld.RegionArguments(target);
        }
        quotedSignature=Signature(leg);
        trip.Attempt();action=new(executor,op,parameters,taskId+"/"+(++sequence));
        return Running();
    }
    internal static bool KnownPose(AiMap map,MapWindow bounds,PhysicsState s)
    {
        if(map==null)return false;
        int firstSupport=(int)Math.Ceiling((s.Py+42)/16);
        for(int x=(int)Math.Floor(s.Px/16);x<(int)Math.Ceiling((s.Px+20)/16);x++)
        for(int y=(int)Math.Floor(s.Py/16);y<firstSupport+(s.Grounded?1:0);y++)
        {
            byte cell=map.Get(x,y);
            if(!bounds.Contains(new(x,y)) || cell is 0 or 5 or 6 or 7 || y<firstSupport&&cell==2)return false;
        }
        return true;
    }
    private Result Reject(AiReadOnlyProtocol.Identity frame)
    {
        trip.Reject(leg,(long)frame.GameUpdate,Signature(leg));return Running();
    }
    private Result End(string status,string reason)=>new(status,reason,Evidence(),true,false,acquired);
    internal override Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)
    {
        planning?.Dispose();planning=null;
        var result=base.Stop(frame,status,reason);
        return result with{Evidence=Evidence(),InputsAcquired=acquired||result.InputsAcquired};
    }
}
