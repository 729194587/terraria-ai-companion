using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CompanionBridge.Motion;
using Terraria;
using Terraria.ModLoader;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Explicit copied-world fixture, at the same native boundary as navigation.
// Temporary diagnostic field injection is restored before movement runs.
internal static class MovementDiscoveryFixture
{
    private static bool completed;
    internal static void AtRunSpeeds(Player p)
    {
        if(completed||!Program.LaunchParameters.ContainsKey("-companionmobilitytest")
            ||!Program.LaunchParameters.ContainsKey("-companionactionstest")||!ReferenceEquals(p,ProbeSession.Actor))return;
        int checks=0;
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException("movement discovery: "+why);checks++;}
        void Verify()
        {
            var s=NativeMovementReader.Capture(p);var ordered=ExtraJumpLoader.OrderedJumps;
            Check(s.ExtraJumps.Length==ordered.Count,"registry count");
            for(int i=0;i<ordered.Count;i++)
            {
                ref var state=ref p.GetJumpState(ordered[i]);var j=s.ExtraJumps[i];
                Check(j.FullName==ordered[i].FullName&&j.Order==i&&j.Enabled==state.Enabled&&j.Available==state.Available&&j.Active==state.Active,"ordered native jump state");
            }
            Check(s.BlockExtraJumps==p.blockExtraJumps,"jump blocking");
            Check(s.Wings.FunctionalSlot==p.wingsLogic&&s.Wings.Time==p.wingTime&&s.Wings.TimeMax==p.wingTimeMax,"functional wings and resources");
            if(s.Wings.BaseStats!=null)
            {
                var w=Terraria.ID.ArmorIDs.Wing.Sets.Stats[p.wingsLogic];var b=s.Wings.BaseStats;
                Check(b.FlyTime==w.FlyTime&&b.AccRunSpeedOverride==w.AccRunSpeedOverride&&b.AccRunAccelerationMult==w.AccRunAccelerationMult
                    &&b.HasDownHoverStats==w.HasDownHoverStats&&b.DownHoverSpeedOverride==w.DownHoverSpeedOverride&&b.DownHoverAccelerationMult==w.DownHoverAccelerationMult,"base wing registry values");
            }
            Check(s.Rockets==new RocketSnapshot(p.rocketBoots,p.rocketTime,p.rocketTimeMax),"rocket fields");
            Check(s.Mount.Active==p.mount.Active&&s.Mount.Type==p.mount.Type,"mount fields");
            Check(s.Environment==new MovementEnvironment(p.pulley,p.pulleyDir,p.wet,p.honeyWet,p.lavaWet,p.shimmerWet),"rope and liquids");
            Check(s.GrappleCount==p.grapCount&&s.Grapples.Length==Math.Clamp(p.grapCount,0,p.grappling.Length),"grapple list");
            foreach(var h in s.Grapples)
            {
                Check(h.ProjectileIndex==p.grappling[h.Order],"grapple index");
                if(h.Valid){var projectile=Main.projectile[h.ProjectileIndex];Check(h.Type==projectile.type&&h.Identity==projectile.identity&&h.Owner==projectile.owner&&h.Active==projectile.active,"projectile identity");}
            }
        }
        Verify();
        int wings=p.wings,wingsLogic=p.wingsLogic,wingMax=p.wingTimeMax,boots=p.rocketBoots,rocket=p.rocketTime,rocketMax=p.rocketTimeMax,count=p.grapCount,index=p.grappling[0];
        float time=p.wingTime;bool block=p.blockExtraJumps,pulley=p.pulley,wet=p.wet,honey=p.honeyWet,lava=p.lavaWet,shimmer=p.shimmerWet;byte dir=p.pulleyDir;
        try
        {
            p.wings=2;p.wingsLogic=1;p.wingTime=50;p.wingTimeMax=100;p.rocketBoots=1;p.rocketTime=10;p.rocketTimeMax=20;
            p.pulley=true;p.pulleyDir=2;p.wet=true;p.honeyWet=true;p.lavaWet=true;p.shimmerWet=true;
            p.grapCount=1;p.grappling[0]=-1;p.blockExtraJumps=true;Verify();
            var a=NativeMovementReader.Capture(p);p.wingTime--;p.rocketTime--;var b=NativeMovementReader.Capture(p);
            Check(a.SamePlanningAssumptions(b),"native resource drain does not invalidate");
            p.grappling[0]=Main.projectile.Length-1;Verify();
            foreach(var jump in ExtraJumpLoader.OrderedJumps)
            {
                ref var state=ref p.GetJumpState(jump);var saved=state;
                try{state.Enable();state.Available=true;Verify();state.Disable();Verify();}
                finally{state=saved;}
            }
        }
        finally
        {
            p.wings=wings;p.wingsLogic=wingsLogic;p.wingTime=time;p.wingTimeMax=wingMax;p.rocketBoots=boots;p.rocketTime=rocket;p.rocketTimeMax=rocketMax;
            p.grapCount=count;p.grappling[0]=index;p.blockExtraJumps=block;p.pulley=pulley;p.pulleyDir=dir;p.wet=wet;p.honeyWet=honey;p.lavaWet=lava;p.shimmerWet=shimmer;
        }
        Verify();completed=true;
        File.WriteAllText(Path.Combine(Main.SavePath,"movement-discovery.json"),JsonSerializer.Serialize(new{checks,boundary="PostUpdateRunSpeeds",tick=Main.GameUpdateCount,
            slot=p.whoAmI,jumps=ExtraJumpLoader.OrderedJumps.Select(j=>j.FullName).ToArray(),fieldInjectionRestored=true,additionalPlayerUpdates=0}));
    }
}
