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
    private PhysicsParameters planningPhysics;
    private PhysicsState planningPose;
    private readonly NavigationSearchMemory quoteMemory=new(cacheSafe:true);
    private sealed record Passage(PhysicsState Start,PhysicsState End,int Updates,Route Movement,GrappleClimbPlan Hook,Prediction HookTail=null);
    private sealed record QuotedTrip(LandingRegion Goal,Passage Outbound,Passage Return,long Terrain);
    private readonly Dictionary<MapPoint,QuotedTrip> quotes=new();
    private bool searchLimited;
    private double maxPlanningSliceMs;
    private int planningSlices,outboundQueries,outboundReached,returnsReached;
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
        mapRevision=map?.Revision??0,planningSlices,maxPlanningSliceMs,outboundQueries,outboundReached,returnsReached,candidates=trip?.Choices.Select(c=>new{x=c.Stance.X,y=c.Stance.Y,gain=c.Gain,
            outboundUpdates=c.Outbound,returnUpdates=c.Return}).ToArray()};
    internal override object Progress=>Evidence();
    internal override Result Prepare(AiReadOnlyProtocol.Identity frame)
    {
        if(map==null||!WorldCharacterPersistence.Ready||map.World!=Main.ActiveWorldFileData.UniqueId||
            map.Character!=WorldCharacterPersistence.ProfileId)return Failed("exploration_map_unavailable");
        life=actor.statLife;
        if(!GameNavigation.Ordinary(actor)||!ServerAiNavigation.Observe(actor).Settled||
            Math.Abs(actor.Bottom.Y-At.Y*16)>.5f)return Failed("exploration_requires_settled_flat_anchor");
        if(NativeNavigationWorld.CheckHazards(frame,life,travelling:true) is {} danger)return Failed(danger);
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
    private LandingRegion Region(MapPoint p)=>p==trip.Anchor
        ?new(anchorLeft-1,anchorLeft+1,p.Y*16):new(p.X*16,p.X*16+15,p.Y*16);

    private long TerrainStamp(Passage outbound,Passage back)
    {
        var hash=new HashCode();
        foreach(var passage in new[]{outbound,back})
        {
            if(passage==null)continue;
            var pose=passage.Start;
            if(passage.Hook is {} hook)
            {
                var world=new NativeMotionWorld(pose);
                hash.Add(ActorGrapple.Selected(actor));
                hash.Add(hook.Unchanged(world));
                hash.Add(world.TerrainSignature(pose,passage.HookTail));
                if(hook.Approach!=null)hash.Add(world.TerrainSignature(pose,hook.Approach));
                continue;
            }
            foreach(var edge in passage.Movement.Actions)
            {
                hash.Add(new NativeMotionWorld(pose).TerrainSignature(pose,edge));
                pose=edge.Landing;
            }
        }
        return hash.ToHashCode();
    }

    private IEnumerable<int> FindPassage(PhysicsState start,LandingRegion goal,Action<Passage> accept)
    {
        bool Known(PhysicsState pose)=>KnownPose(map,trip.Window,pose);
        var world=new NativeMotionWorld(start);
        Passage hookPassage=null;
        if(ActorGrapple.Selected(actor)!=null && !ActorGrapple.HasHook(actor) && !actor.noItems && !actor.CCed)
        {
            bool KnownCell(int x,int y)=>trip.Window.Contains(new(x,y)) && map.Get(x,y)!=0;
            foreach(var hook in GrappleClimbPlan.Options(start,goal,planningPhysics,world,world))
            {
                yield return 1;
                if(hook==null||!Known(start)||map.Get(hook.AnchorX,hook.AnchorY)!=2)continue;
                var launch=hook.Approach?.Landing??start;
                if(!GrappleClimbPlan.ClearShot(launch,hook.AnchorX,hook.AnchorY,world,KnownCell))continue;
                if(hook.Preview(start,planningPhysics,world,Known) is not {} tail)continue;
                hookPassage=new(start,tail.Landing,145+(hook.Approach?.ExecuteFrames??0),null,hook,tail);
                if(planningPhysics.Air==null && planningPhysics.Impulse==null){accept(hookPassage);yield break;}
                break;
            }
        }
        var query=new LandingRouteQuote();
        foreach(int work in query.Find(start,goal,planningPhysics,p=>new NativeMotionWorld(p),
            (from,edge)=>!Known(from)||edge.Frames.Exists(f=>!Known(f.State)),start==planningPose?quoteMemory:null))
            yield return work;
        searchLimited|=query.BudgetLimited;
        if(query.Route is {} route)
        {
            int updates=route.Actions.Sum(e=>e.ExecuteFrames);
            if(hookPassage==null || updates<=hookPassage.Updates)
            {accept(new(start,route.Actions[^1].Landing,updates,route,null));yield break;}
        }
        if(hookPassage!=null)accept(hookPassage);
    }

    private IEnumerable<int> Quote(MapPoint? observation,Action<ExplorationLeg,int> accept)
    {
        if(!observation.HasValue && Home)yield break;
        var target=observation??trip.Anchor;
        var goal=Region(target);
        outboundQueries++;
        Passage outward=null,back=null;
        foreach(int work in FindPassage(planningPose,goal,p=>outward=p))yield return work;
        if(outward==null)yield break;
        outboundReached++;
        if(observation.HasValue)
        {
            foreach(int work in FindPassage(outward.End,Region(trip.Anchor),p=>back=p))yield return work;
            if(back==null)yield break;
            returnsReached++;
        }
        // Searches may span live terrain changes. Replay both proofs against
        // current collision and retain their footprints through child admission.
        long stamp=TerrainStamp(outward,back);
        foreach(var passage in new[]{outward,back})
        {
            if(passage==null)continue;
            if(passage.Hook is {} hook)
            {
                if(ActorGrapple.Selected(actor)==null||!hook.Unchanged(new NativeMotionWorld(passage.Start)))yield break;
                continue;
            }
            var pose=passage.Start;
            foreach(var edge in passage.Movement.Actions)
            {
                if(!Replay(pose,edge,planningPhysics))yield break;
                pose=edge.Landing;
                yield return 1;
            }
        }
        if(stamp!=TerrainStamp(outward,back))yield break;
        quotes[target]=new(goal,outward,back,stamp);
        accept(new(planningStart,target,outward.Updates,null),back?.Updates??0);
    }

    private bool Replay(PhysicsState state,Prediction edge,PhysicsParameters physics)
    {
        var world=new NativeMotionWorld(state);
        int direction=edge.Frames[0].Input.Direction;
        int stride=edge.Jump?24:edge.Frames.Count(f=>f.Input.Direction!=0);
        var proof=PhysicsSimulator.Predict(state,direction,edge.InitialHold,physics,world,walkFrames:stride,
            rollingJump:true,extraJumpFrame:edge.ExtraJumpFrame,airStartFrame:edge.AirStartFrame,
            airHoldFrames:edge.AirHoldFrames,walkingGoal:new(edge.Frames[^1].State.Px,edge.Frames[^1].State.Px,
                edge.Frames[^1].State.Py+PhysicsSimulator.PlayerH));
        return proof.Safe && proof.Frames.SequenceEqual(edge.Frames)
            && proof.Frames.TrueForAll(f=>KnownPose(map,trip.Window,f.State));
    }

    private long Signature(ExplorationLeg edge)
    {
        var hash=new HashCode();
        hash.Add(map.ChunkRevision(edge.From.X,edge.From.Y));
        hash.Add(map.ChunkRevision(edge.To.X,edge.To.Y));
        hash.Add(NativeNavigation.Parameters(actor));
        hash.Add(NativeMotionWorld.Observe(actor));
        hash.Add(ActorGrapple.Selected(actor));
        if(AllowDig)hash.Add(ActorMining.CapabilityStamp(actor));
        return hash.ToHashCode();
    }
    internal override Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        if(!ReferenceEquals(map,AiMapRuntime.Map))return Stop(frame,"unknown","exploration_map_changed");
        if(NativeNavigationWorld.CheckHazards(frame,life,travelling:true) is {} danger)return Stop(frame,"cancelled",danger);
        foreach(var shot in Main.projectile)
        {
            if(!shot.active||!shot.hostile||shot.damage<=0)continue;
            var threat=SceneThreat.Project(new(actor.position.X,actor.position.Y,actor.width,actor.height),new(actor.velocity.X,actor.velocity.Y),
                new(shot.position.X,shot.position.Y,shot.width,shot.height),new(shot.velocity.X,shot.velocity.Y),shot.extraUpdates);
            if(threat.ContactAfterTicks!=null && NativeNavigation.ExceedsTravelBudget(actor,life,shot.damage))return Stop(frame,"cancelled","exploration_projectile_approaching");
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
            if(!action.Started && (Signature(leg)!=quotedSignature || leg.Cut is {} tile && Inspect(tile,true).MiningUpdates==0 ||
                quotes.TryGetValue(leg.Cut==null?leg.To:trip.Anchor,out var quoteBeforeStart) && TerrainStamp(quoteBeforeStart.Outbound,quoteBeforeStart.Return)!=quoteBeforeStart.Terrain))
            {
                base.Stop(frame,"cancelled","exploration_quote_changed");
                action=null;return Running();
            }
            bool starting=!action.Started;
            var result=RunChild(action,frame,600);
            if(starting && action.Started)trip.Attempt();
            if(result==null)return Running();
            acquired|=result.InputsAcquired;action=null;
            if(!result.InputsReleased||result.Status is "unknown" or "cancelled")return result;
            if(result.Status!="completed")
            {
                if(result.Status!="timeout" && result.ReasonDetail!="exploration_quote_changed")trip.Reject(leg,(long)frame.GameUpdate,quotedSignature);
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
        var physics=NativeNavigation.Parameters(actor);
        int remaining=(int)Math.Max(0,(long)deadline-(long)frame.GameUpdate);
        if(!trip.Returning && trip.Departed && remaining<=reservedReturn)
        {
            planning?.Dispose();planning=null;trip.ReturnNow();
        }
        if(planning==null || At!=planningStart || planningRevision!=map.Revision || physics!=planningPhysics || observation.State!=planningPose)
        {
            planning?.Dispose();
            planningStart=At;planningRevision=map.Revision;planningPhysics=physics;planningPose=observation.State;
            quotes.Clear();quoteMemory.Clear();searchLimited=false;
            var costs=new ExplorationCostmap(map,trip.Window,new(1,3,24+(int)Math.Ceiling(16/physics.MaxRun)),p=>Inspect(p,true))
                {Navigation=Quote,AllowExcavation=AllowDig};
            planning=trip.Plan(costs,At,(long)frame.GameUpdate,remaining,Signature).GetEnumerator();
        }
        long started=Stopwatch.GetTimestamp();
        planningSlices++;
        bool pending;
        for(int work=0;(pending=planning.MoveNext());work++)
            if(work>=31 || Stopwatch.GetElapsedTime(started).TotalMilliseconds>=2)break;
        maxPlanningSliceMs=Math.Max(maxPlanningSliceMs,Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if(pending)return Running();
        planning.Dispose();planning=null;
        leg=trip.Next;
        if(!trip.Returning && trip.Choices.Count>0)
        {
            var choice=trip.Choices[0];
            reservedReturn=choice.Return+45;
            if(remaining<choice.Outbound+reservedReturn){trip.ReturnNow();return Running();}
        }
        bool finalAlignment=trip.Returning&&At==trip.Anchor;
        if(trip.Returning&&Home && !trip.Departed && searchLimited)return End("unknown","exploration_route_unconfirmed");
        if(trip.Returning&&Home)
            return trip.NewTiles>0&&trip.Departed?new("completed","frontier_observed_and_returned",Evidence(true),true,true,acquired)
                :new("failed","frontier_no_new_tiles",Evidence(true),true,false,acquired);
        if(finalAlignment && !Home && !quotes.ContainsKey(At))
            return End("unknown","exploration_anchor_alignment_unconfirmed");
        if(leg==null && searchLimited)return End("unknown","exploration_route_unconfirmed");
        if(leg==null)return End("failed",trip.Returning?"frontier_return_unavailable":"frontier_unavailable");
        if(remaining<leg.Updates)return End("timeout","exploration_budget_exhausted");
        string op;JsonElement parameters;
        expectedLanding=null;
        if(leg.Cut is {} cut)
        {
            if(!Home && (!quotes.TryGetValue(trip.Anchor,out var returnProof)
                || TerrainStamp(returnProof.Outbound,null)!=returnProof.Terrain))return Running();
            if(trip.Returning || Inspect(cut,true).MiningUpdates==0)return Reject(frame);
            op="mine_tile";parameters=JsonSerializer.SerializeToElement(new{x=cut.X,y=cut.Y});
        }
        else
        {
            op="navigate_to_landing_region";
            if(!quotes.TryGetValue(leg.To,out var quote)||TerrainStamp(quote.Outbound,quote.Return)!=quote.Terrain)
                return Running();
            executor.ExplorationWindow=trip.Window;
            executor.ExplorationQuote=new(planningPose,planningPhysics,quote.Goal,quote.Outbound.Movement,
                ()=>TerrainStamp(quote.Outbound,quote.Return)==quote.Terrain,quote.Outbound.Hook);
            expectedLanding=quote.Goal;
            parameters=NativeNavigationWorld.RegionArguments(quote.Goal);
        }
        quotedSignature=Signature(leg);
        action=new(executor,op,parameters,taskId+"/"+(++sequence));
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
