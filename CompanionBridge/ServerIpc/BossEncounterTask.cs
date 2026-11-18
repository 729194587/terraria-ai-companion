using CompanionBridge.Motion;
using Terraria;
using Microsoft.Xna.Framework;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

// One body lease spans all member changes and flight assignments.
internal sealed class BossEncounterTask(string encounterId, NativeCombatWorld world,ServerAiNavigation navigation) : CompanionTask
{
    internal const ulong Budget=36000;
    private const ulong NoHitWindow=1800;
    private ulong started;
    private int? life;
    private string phase="bind_root";
    private bool acquired;
    private LandingRegion destination;
    private ServerAiNavigation.Run retreat;
    private ulong retreatDeadline;
    private bool escaped,attackReleased;
    private object retreatProof;
    internal string RequestRetreat(Identity frame,int x,int y)
    {
        if(!acquired || destination!=null)return "boss_retreat_unavailable";
        var player=(Terraria.Player)frame.Actor;
        if(!Terraria.WorldGen.InWorld(x/16,y/16,3) || Microsoft.Xna.Framework.Vector2.Distance(player.Bottom,new(x,y))>1600)
            return "invalid_destination";
        var pose=NativeMotionWorld.Observe(player) with{Px=x-player.width/2,Py=y-player.height,Vx=0,Vy=0,Grounded=true};
        if(!new NativeMotionWorld(pose).SafeLanding(pose))return "boss_retreat_unsafe_destination";
        destination=LandingRegion.AtDestination(x,y);
        retreatDeadline=frame.GameUpdate+900;
        attackReleased=world.Release();
        phase="retreat_requested";
        return null;
    }
    private Result Retreat(Identity frame)
    {
        phase="retreat";
        if(retreat==null)
        {
            if(!attackReleased)return Stop(frame,"unknown","boss_attack_release_unconfirmed");
            retreat=navigation.Start(frame,destination,encounterId);
        }
        if(frame.GameUpdate>=retreatDeadline)return Stop(frame,"timeout","boss_retreat_deadline");
        if(retreat.Status=="running")return Running();
        if(retreat.Status!="completed")return Stop(frame,retreat.Status,"boss_retreat_"+retreat.Reason);
        var p=(Terraria.Player)frame.Actor;
        foreach(var shot in Main.ActiveProjectiles)
            if(shot.hostile && shot.damage>0 && Vector2.DistanceSquared(shot.Center,p.Center)<160*160)return Stop(frame,"failed","boss_retreat_still_engaged");
        foreach(var n in Terraria.Main.ActiveNPCs)
            if(!n.friendly && n.life>0 && (n.damage>0 || world.Members.Contains(n)) && Microsoft.Xna.Framework.Vector2.DistanceSquared(n.Center,p.Center)<320*320)
                return Stop(frame,"failed","boss_retreat_still_engaged");
        var observed=ServerAiNavigation.Observe(p);
        if(!observed.Settled || !destination.Contains(observed.State))
            return Stop(frame,"unknown","boss_retreat_landing_unconfirmed");
        retreatProof=new{feetX=destination.MaxPlayerLeftX,feetY=destination.SurfaceY,
            left=p.position.X,right=p.position.X+p.width,bottom=p.Bottom.Y,settled=observed.Settled,
            npcClearRadius=320,projectileClearRadius=160,update=frame.GameUpdate,attackReleased};
        escaped=true;
        return Stop(frame,"cancelled","boss_retreat_verified");
    }
    internal override string Name=>CombatProtocol.Boss;
    internal override string Phase=>phase;
    internal override object Progress=>new {encounterId,source="boss",target=world.Target,weapon=world.Weapon,
        predictionUpdates=BossEvasion.Horizon,threatsTruncated=world.Evasion.Truncated,worstPoseErrorPx=world.Evasion.WorstPoseError,
        retreatProof,predictionFallbackSamples=world.Evasion.FallbackSamples,peakMemberCount=world.Members?.PeakCount??0,customTrajectorySamples=world.Evasion.CustomTrajectorySamples,retreatVerified=escaped,evasion=world.Evasion.Reason,evasionDecisions=world.Evasion.Decisions,noSafeRoute=world.Evasion.NoRoute,
        life,lastHit=world.LastHit,itemChecks=world.ItemChecks,combinedUpdates=world.CombinedUpdates,
        startedUpdate=started,rootDeathUpdate=world.Members?.Victory,seed=world.Members?.Seed,memberCount=world.Members?.LiveCount??0,
        headCount=world.Members?.Heads??0,verifiedHits=world.VerifiedHits,verifiedDamage=world.VerifiedDamage,
        impacts=world.RecentImpacts,deathProof=world.Members?.DeathProof??System.Array.Empty<object>()};
    internal override Result Prepare(Identity frame)=>world.Prepare() is {} reason?Failed(reason):null;
    internal override void Start(Identity frame)
    {
        started=frame.GameUpdate;
        world.Acquire();
        acquired=true;
    }
    internal override Result Tick(Identity frame)
    {
        if(destination!=null)return Retreat(frame);
        var error=world.UpdateMembers(out bool waiting);
        if(error!=null)return Stop(frame,"unknown",error);
        if(world.Members.Victory.HasValue){life=0;return Stop(frame,"completed","boss_root_defeated");}
        var observed=world.Observe();
        life=observed.SameSpawn?observed.Life:null;
        if(frame.GameUpdate-(world.LastHit?.GameUpdate??started)>=NoHitWindow)
            return Stop(frame,"timeout","boss_no_hit_deadline");
        if(waiting){phase=world.Evade()?world.Evasion.Reason??"evade":"waiting_for_attack_window";return Running();}
        var failure=world.Step(out phase);
        return failure==null?Running():Stop(frame,failure=="combat_native_unconfirmed"?"unknown":"failed",failure);
    }
    internal override Result Stop(Identity frame,string status,string reason)
    {
        if(retreat?.Status=="running")navigation.Stop("cancelled",reason);
        bool released=world.Release();
        return new(status,reason,Progress,released,released && status=="completed" && world.Members?.Victory!=null,acquired);
    }
    internal override void Abandon()=>world.Abandon();
}
