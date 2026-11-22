using System;
using System.Text.Json;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using Terraria;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Straight, dry vanilla rope only. Terraria owns attachment, movement and exit.
internal sealed class ServerAiRope : ActorControl.IMovementDriver
{
    internal sealed class Run(AiReadOnlyProtocol.Identity identity,int x,double target,bool land,int maxUpdates)
    {
        internal readonly AiReadOnlyProtocol.Identity Identity=identity;
        internal readonly int RopeX=x, MaxUpdates=maxUpdates;
        internal readonly double TargetFeetY=target;
        internal readonly bool Land=land;
        internal int LastRope, Updates, Stable, Idle, Health;
        internal float StartFeetY, FeetY;
        internal bool Attached, AttachedObserved, Grounded, SafeSupport, Settled, InputReleased;
        internal string Status="running",Reason="rope_movement_accepted",Detail;
        internal ulong? Tick;
    }
    internal Run Current;
    private bool executing, injected;
    public bool Running=>Current?.Status=="running";
    public bool Matches(Player p)=>ReferenceEquals(Current?.Identity.Actor,p);
    private static int Head(double top)=>(int)Math.Floor((top-8)/16);
    private static bool Aligned(Player p,int x)=>(int)Math.Floor(p.Center.X/16)==x;
    private static bool Rope(int x,int y)=>TerrainReader.Loaded(x,y)&&ActorRope.IsRope(Main.tile[x,y]);
    private static bool Supported(Player p)=>p.width==20&&p.height==42&&p.gravDir==1&&!p.mount.Active
        &&!p.CCed&&!p.frozen&&!p.stoned&&!p.webbed&&!p.tongued&&!p.wet&&!p.lavaWet&&!p.honeyWet&&!p.shimmerWet
        &&!p.shimmering&&!p.sleeping.isSleeping&&!p.sitting.isSitting&&p.grappling[0]==-1;

    internal string Start(AiReadOnlyProtocol.Identity identity,JsonElement args,out Run run)
    {
        ServerAiNativeActions.Require(identity);
        run=null;
        var p=(Player)identity.Actor;
        int x=args.GetProperty("ropeX").GetInt32();
        double target=args.GetProperty("targetFeetY").GetDouble();
        bool land=args.GetProperty("finish").GetString()=="land";
        int head=Head(p.position.Y),end=Head(target-p.height);
        if(!Supported(p)||Math.Abs(p.velocity.X)>.1f||Math.Abs(p.velocity.Y)>.1f)return "unsupported_rope_start_state";
        if(!Aligned(p,x)||!Rope(x,head)||!Rope(x,head-1)&&!Rope(x,head+1))return "rope_not_in_attach_range";
        if(Math.Abs(target-p.Bottom.Y)>384)return "rope_target_out_of_range";
        var candidate=new Run(identity,x,target,land,args.GetProperty("maxUpdates").GetInt32())
        {StartFeetY=p.Bottom.Y,Health=p.statLife,LastRope=end};
        if(land)
        {
            if(target<=p.Bottom.Y+.5f)return "rope_landing_requires_descent";
            int missing=head;
            while(missing<=end&&Rope(x,missing))missing++;
            double drop=target-(missing*16+8+p.height);
            if(missing>end||drop<0||drop>32||!TerrainReader.Loaded(x,missing)||Main.tile[x,missing].HasTile)
                return "rope_landing_exit_unavailable";
            candidate.LastRope=missing-1;
            if(!LandingSupported(candidate))return "rope_landing_unsafe";
        }
        if(!Continuous(candidate,head))return "rope_interrupted";
        if(!Clear(p,candidate,(float)(target-p.height)))return "rope_corridor_blocked";
        Current=run=candidate;
        Observe(p);
        return null;
    }

    private static bool Continuous(Run r,int head)
    {
        for(int y=Math.Min(head,r.LastRope);y<=Math.Max(head,r.LastRope);y++)
            if(!Rope(r.RopeX,y))return false;
        return true;
    }

    private static bool LandingSupported(Run r)
    {
        // Down is also a native platform-drop input. Only admit solid floors.
        if(r.TargetFeetY%16!=0)return false;
        for(int x=r.RopeX-1;x<=r.RopeX+1;x++)
            if(TerrainReader.ReadDryCell(x,(int)(r.TargetFeetY/16))!='#')return false;
        float center=r.RopeX*16+8;
        // Native pulleyDir can place the body at the centre or six pixels aside.
        for(int dx=-6;dx<=6;dx+=6)
        {
            var state=new PhysicsState(center-10+dx,(float)r.TargetFeetY-42,0,0,true);
            if(!new NativeMotionWorld(state).SafeLanding(state))return false;
        }
        return true;
    }

    private static bool Clear(Player p,Run r,float endTop)
    {
        float center=r.RopeX*16+8;
        float left=Math.Min(p.position.X,center-16),right=Math.Max(p.Right.X,center+16);
        float top=Math.Min(p.position.Y,endTop),bottom=Math.Max(p.position.Y,endTop)+p.height;
        for(int x=(int)Math.Floor(left/16);x<Math.Ceiling(right/16);x++)
            for(int y=(int)Math.Floor(top/16);y<Math.Ceiling(bottom/16);y++)
                if(TerrainReader.ReadEnvironment(x,y).DryShape!='.')return false;
        return true;
    }

    // PostUpdateMiscEffects is upstream of both FindPulley and the pulley
    // movement branches. The ordinary run-speeds boundary comes after both.
    internal void BeforePulley(Player p)
    {
        if(!executing||!Running||!Matches(p)||!NativeSurvival.Owns(p))return;
        ServerAiNativeActions.Require(Current.Identity);
        var r=Current;
        if(injected)throw new InvalidOperationException("Duplicate rope input boundary.");
        int head=Head(p.position.Y);
        if(!Supported(p)||p.statLife<r.Health){Stop("failed","rope_actor_state_changed");return;}
        if(!Aligned(p,r.RopeX)){Stop("failed","rope_alignment_lost");return;}
        bool exited=r.Land&&r.AttachedObserved&&head>r.LastRope;
        if(!exited&&!Continuous(r,head)){Stop("failed","rope_interrupted");return;}
        if(r.Land&&(!LandingSupported(r)||!TerrainReader.Loaded(r.RopeX,r.LastRope+1)
            ||Main.tile[r.RopeX,r.LastRope+1].HasTile)){Stop("failed","rope_landing_changed");return;}
        double error=r.TargetFeetY-p.Bottom.Y;
        float vy=p.velocity.Y;
        // Release early enough for native 0.7 damping, then correct at low
        // speed if necessary. Only observed neutral frames can prove arrival.
        int direction=r.Land?(exited?0:1)
            :!p.pulley?(error>0?1:-1)
            :Math.Abs(error)>Math.Max(4,Math.Abs(vy)*3)?Math.Sign(error):0;
        float next=p.position.Y+(direction==0?vy:direction*12);
        if(r.Land)next=Math.Min(next,(float)r.TargetFeetY-p.height);
        if(!Clear(p,r,next)){Stop("failed","rope_corridor_blocked");return;}
        Release(p);
        p.controlUp=direction<0;p.controlDown=direction>0;
        r.Updates++;injected=true;
    }
    public void BeforeMovement(Player p){} // No late injection at the navigation boundary.

    private static void Release(Player p)
    {
        p.controlLeft=p.controlRight=p.controlUp=p.controlDown=p.controlJump=p.controlUseItem=p.controlUseTile=p.channel=false;
    }

    private void Observe(Player p)
    {
        var r=Current;
        r.FeetY=p.Bottom.Y;r.Attached=p.pulley&&Aligned(p,r.RopeX)&&Rope(r.RopeX,Head(p.position.Y));
        r.AttachedObserved|=r.Attached;
        var landing=ServerAiNavigation.Observe(p);
        r.Grounded=landing.State.Grounded;r.SafeSupport=landing.SafeSupport;
        r.Settled=!p.wet&&Math.Abs(p.velocity.X)<=.1f&&Math.Abs(p.velocity.Y)<=.1f
            &&(r.Attached||!p.pulley&&landing.Settled);
    }

    internal void Stop(string status,string reason)
    {
        if(!Running)return;
        var r=Current;
        if(!ServerAiNativeActions.Owned(r.Identity)){r.Status="unknown";r.Reason="rope_binding_changed";return;}
        var p=(Player)r.Identity.Actor;
        Release(p);r.InputReleased=true;
        try
        {
            Observe(p);
            r.Status=status=="failed"&&r.AttachedObserved&&!r.Attached&&!r.Settled?"unknown":status;
            r.Reason=reason;
            if(!p.dead)NetworkStateSync.Full(p);
        }
        catch(Exception e){r.Status="unknown";r.Reason="rope_observation_unconfirmed";r.Detail=e.Message;}
    }

    public void Retire(Player p){if(Matches(p))Stop("cancelled",p.dead?"actor_died":"actor_retired");}
    public void Update(On_Player.orig_Update orig,Player p,int slot)
    {
        var r=Current;
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        lock(ProbeSession.Reservation.Gate)
        {
            if(!ServerAiNativeActions.Owned(r.Identity)||slot!=r.Identity.Slot)
            {Stop("unknown","rope_binding_changed");orig(p,slot);return;}
            if(p.dead||p.ghost||Main.gameMenu||Netplay.Disconnect)
            {Stop("cancelled",p.dead||p.ghost?"actor_died":"world_unavailable");orig(p,slot);return;}
            try
            {
                if(executing||r.Tick==Main.GameUpdateCount)throw new InvalidOperationException("Duplicate rope update.");
                r.Tick=Main.GameUpdateCount;executing=true;injected=false;
                float before=p.Bottom.Y;
                orig(p,slot);
                if(!Running)return;
                if(!ServerAiNativeActions.Owned(r.Identity)){Stop("unknown","rope_binding_changed");return;}
                if(!injected){Stop("unknown","rope_input_boundary_unconfirmed");return;}
                Observe(p);
                if(!Supported(p)||p.statLife<r.Health){Stop("failed","rope_actor_state_changed");return;}
                bool arrived=r.AttachedObserved&&r.Settled&&(r.Land
                    ?!p.pulley&&r.Grounded&&r.SafeSupport&&Math.Abs(r.FeetY-r.TargetFeetY)<=.5f
                    :r.Attached&&Math.Abs(r.FeetY-r.TargetFeetY)<=4);
                bool neutral=!p.controlUp&&!p.controlDown;
                r.Stable=arrived&&neutral?r.Stable+1:0;
                if(r.Stable>=2)Stop("completed",r.Land?"native_rope_landing_verified":"native_rope_hold_verified");
                else if(r.Updates>=r.MaxUpdates)Stop("timeout","rope_update_budget");
                else if(!r.AttachedObserved&&r.Updates>=20)Stop("failed","rope_attach_unconfirmed");
                else if(r.AttachedObserved&&!r.Attached&&(!r.Land||Head(p.position.Y)<=r.LastRope))Stop("failed","rope_attachment_lost");
                else if(Math.Abs(r.FeetY-before)>.05f)r.Idle=0;
                else if(!arrived&&++r.Idle>=20)Stop("failed","rope_movement_blocked");
                if(Running)NetworkStateSync.Tick(p);
            }
            catch(Exception e){r.Detail=e.Message;Stop("unknown","rope_update_unconfirmed");}
            finally
            {
                if(ServerAiNativeActions.Owned(r.Identity))Release(p);
                executing=injected=false;
            }
        }
    }

    internal object Evidence()=>new{ropeX=Current.RopeX,targetFeetY=Current.TargetFeetY,finish=Current.Land?"land":"hold",
        maxUpdates=Current.MaxUpdates,updates=Current.Updates,startFeetY=Current.StartFeetY,feetY=Current.FeetY,
        attached=Current.Attached,attachmentObserved=Current.AttachedObserved,grounded=Current.Grounded,
        safeSupport=Current.SafeSupport,settled=Current.Settled,inputReleased=Current.InputReleased};
}
