using System;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace CompanionBridge.Motion;

// Called on the existing simulation-thread boundary. Never asks whether a jump
// can start, reapplies effects or invokes movement/wing/grapple extension hooks.
internal static class NativeMovementReader
{
    internal static MovementSnapshot Capture(Player player)
    {
        var ordered=ExtraJumpLoader.OrderedJumps;
        var jumps=new ExtraJumpSnapshot[ordered.Count];
        for(int i=0;i<ordered.Count;i++)
        {
            var jump=ordered[i];ref var state=ref player.GetJumpState(jump);
            jumps[i]=new(jump.FullName,i,state.Enabled,state.Available,state.Active);
        }
        var stats=ArmorIDs.Wing.Sets.Stats;
        int slot=player.wingsLogic;
        WingBaseSnapshot wing=null;
        if(slot>0&&stats!=null&&slot<stats.Length)
        {
            var s=stats[slot];
            wing=new(s.FlyTime,s.AccRunSpeedOverride,s.AccRunAccelerationMult,s.HasDownHoverStats,s.DownHoverSpeedOverride,s.DownHoverAccelerationMult);
        }
        int count=player.grapCount,length=player.grappling?.Length??0;
        var hooks=new GrappleSnapshot[Math.Clamp(count,0,length)];
        for(int i=0;i<hooks.Length;i++)
        {
            int index=player.grappling[i];
            bool valid=Main.projectile!=null&&index>=0&&index<Main.projectile.Length&&Main.projectile[index]!=null;
            if(!valid){hooks[i]=new(i,index,false,false,-1,-1,-1,false);continue;}
            var p=Main.projectile[index];
            hooks[i]=new(i,index,true,p.active,p.type,p.identity,p.owner,p.type>=0&&p.type<Main.projHook.Length&&Main.projHook[p.type]);
        }
        var mount=player.mount;
        int type=mount?.Type??-1;
        string mountName=type>=0&&type<MountLoader.MountCount?MountLoader.GetMount(type)?.FullName:null;
        return new(jumps,player.blockExtraJumps,new(slot,player.wingTime,player.wingTimeMax,stats?.Length??0,wing),
            new(player.rocketBoots,player.rocketTime,player.rocketTimeMax),new(mount?.Active??false,type,mountName,MountLoader.MountCount),
            count,count>=0&&count<=length,hooks,new(player.pulley,player.pulleyDir,player.wet,player.honeyWet,player.lavaWet,player.shimmerWet));
    }
}
