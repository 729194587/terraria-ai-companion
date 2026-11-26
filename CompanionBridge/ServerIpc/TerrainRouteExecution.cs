using System;
using System.Linq;
using System.Text.Json;
using Terraria;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

// The owning task keeps the native child lease and its chronological receipts.
// Only confirmed actor-owned effects advance the route's terrain snapshot.
internal sealed class TerrainRouteExecution(AiReadOnlyProtocol.Identity identity,AiAtomicActions.IExecutor native,
    NativeNavigationEdits edits,TerrainRouteSearch.Plan plan,PhysicsParameters physics,int capability,string routeId)
{
    private readonly Player player=(Player)identity.Actor;
    private int index,recorded=-1;
    private NativeActionTask active;
    internal int Cuts,Places,Moves,MoveFailures;
    internal bool Acquired;
    internal string Phase {get;private set;}="settling";
    private LocalEditAction Step=>index<plan.Actions.Length?plan.Actions[index]:null;
    internal LandingRegion MoveRegion=>Step is {Kind:LocalEditKind.Move} move?TerrainApproachPlanner.Region(move.Expected):null;
    private static TerrainCell After(LocalEditAction step)=>step.Kind==LocalEditKind.Mine
        ?new('.',default):new(step.Shape,default,step.Type);

    private bool Matches()
    {
        if(plan.Goal?.Matches()==false||identity.Autonomy is {Valid:false})return false;
        var step=Step;
        foreach(var (point,expected) in plan.Dependencies)
        {
            var cell=edits.Environment(point.X,point.Y);
            if(cell==expected)continue;
            if(active?.Started==true&&step.Kind!=LocalEditKind.Move&&point==(step.X,step.Y)&&cell==After(step))continue;
            return false;
        }
        if(active?.Started==true&&step.Kind!=LocalEditKind.Move)return true;
        if(physics!=NativeNavigation.Parameters(player)||capability!=ActorMining.CapabilityStamp(player))return false;
        if(plan.Cost.Places>0)
        {
            foreach(var group in plan.Actions.Skip(index).Where(a=>a.Kind==LocalEditKind.Place).GroupBy(a=>a.Slot))
            {
                var item=player.inventory[group.Key];
                if(item.createTile!=group.First().Type||item.stack<group.Count())return false;
            }
        }
        return true;
    }
    internal void Record(Result result)
    {
        if(result==null)return;
        Acquired|=result.InputsAcquired;
        if(recorded==index||Step==null||result.Evidence==null)return;
        var step=Step;
        if(step.Kind==LocalEditKind.Move)return;
        var proof=JsonSerializer.SerializeToElement(result.Evidence);
        bool mine=step.Kind==LocalEditKind.Mine;
        if(proof.ValueKind!=JsonValueKind.Object||
            !proof.TryGetProperty(mine?"destroyedByActor":"placedByActor",out var owned)||!owned.GetBoolean()
            ||proof.GetProperty(mine?"X":"x").GetInt32()!=step.X||proof.GetProperty(mine?"Y":"y").GetInt32()!=step.Y
            ||proof.GetProperty(mine?"TileType":"typeId").GetInt32()!=step.Type)return;
        recorded=index;
        if(mine)Cuts++;else Places++;
    }
    private Result Changed(AbstractCompanionTask owner,AiReadOnlyProtocol.Identity frame)
    {
        var result=owner.StopChild(frame,"cancelled","navigation_route_changed");
        Record(result);
        if(result?.Status=="unknown"||result?.InputsReleased==false)return result;
        return new("failed","navigation_route_changed",null,true,InputsAcquired:Acquired);
    }
    internal Result Tick(AbstractCompanionTask owner,AiReadOnlyProtocol.Identity frame)
    {
        var step=Step;
        if(!Matches())return Changed(owner,frame);
        if(active!=null)
        {
            if(!active.Started&&step.Kind!=LocalEditKind.Move&&edits.RevalidateOpening()!=null)return Changed(owner,frame);
            var result=owner.RunChild(active,frame,900);
            if(result==null)return null;
            Record(result);active=null;
            if(!result.InputsReleased)return result with{Status="unknown",EffectVerified=false};
            if(result.Status!="completed"||!result.EffectVerified)
            {
                if(step.Kind==LocalEditKind.Move)MoveFailures++;
                return result.Status=="completed"?Changed(owner,frame):result;
            }
            if(step.Kind!=LocalEditKind.Move)
            {
                if(recorded!=index||edits.Environment(step.X,step.Y)!=After(step))return Changed(owner,frame);
                plan.Dependencies[(step.X,step.Y)]=After(step);
            }
            index++;Phase="settling";return null;
        }
        var observed=ServerAiNavigation.Observe(player);
        if(!observed.Settled)return null;
        if(index>0&&!TerrainApproachPlanner.Region(plan.Actions[index-1].Expected).Contains(observed.State))return Changed(owner,frame);
        if(step==null)return new("completed","navigation_route_verified",null,true,true,Acquired);
        if(!TerrainApproachPlanner.Region(step.Start).Contains(observed.State))return Changed(owner,frame);
        var armed=edits.Arm(step with{Start=observed.State});
        if(armed==null)return Changed(owner,frame);
        Phase=step.Kind switch{LocalEditKind.Mine=>"clearing",LocalEditKind.Place=>"placing",_=>"navigating"};
        if(step.Kind==LocalEditKind.Move)Moves++;
        active=new(native,armed.Operation,armed.Arguments,routeId+"/"+index);
        return null;
    }
}
