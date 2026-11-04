using System;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Only invoked by the double-flag + marker guarded copied-world action fixture.
// This probes real native observation/cleanup, not a replay of the original map.
internal static class ServerAiLandingFixture
{
    internal static void Run(Player p)
    {
        var position=p.position;var velocity=p.velocity;
        var identity=new AiReadOnlyProtocol.Identity(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,new{},p.selectedItem,Main.GameUpdateCount,true);
        var nav=new ServerAiNavigation();
        var results=new System.Collections.Generic.List<object>();
        foreach(string terminal in new[]{"failed","cancelled","timeout"})
        {
            p.position=position;p.velocity=Vector2.Zero;p.releaseJump=true;p.jump=0;
            nav.Start(identity,position.X+160);
            // Diagnostic fault state: do not invoke or fake a successful landing.
            p.position.Y-=8;p.velocity=new(2.8799992f,0.33129883f);p.controlRight=true;p.controlJump=true;
            nav.Current.ReasonDetail="recorded edge endpoint not yet grounded";
            nav.Stop(terminal,"diagnostic_"+terminal);
            var r=nav.Current;
            bool released=!p.controlRight&&!p.controlLeft&&!p.controlJump&&!p.controlUseItem;
            if(!released||p.velocity.X!=0||Math.Abs(p.velocity.Y-0.33129883f)>0.00001f
                ||r.FinalObservation.Settled||r.FinalObservation.State.Grounded
                ||r.Status!=(terminal=="failed"?"unknown":terminal)
                ||terminal=="failed"&&r.Reason!="native_navigation_landing_unconfirmed")
                throw new InvalidOperationException("Native airborne terminal classification/cleanup mismatch.");
            results.Add(new{requested=terminal,r.Status,r.Reason,r.ReasonDetail,inputsReleased=released,
                velocityBeforeRelease=r.VelocityBeforeRelease,observation=r.FinalObservation});
        }
        p.position=position;p.velocity=Vector2.Zero;
        nav.Start(identity,position.X);nav.Stop("completed","native_navigation_arrival_verified");
        if(!nav.Current.FinalObservation.Settled)throw new InvalidOperationException("Native flat fixture not safely settled.");
        p.velocity=velocity;
        File.WriteAllText(Path.Combine(Main.SavePath,"landing-cleanup-evidence.json"),JsonSerializer.Serialize(new{
            result="passed",results,flatSettled=true,graphicalHostAndPlay="not executed",
            mode="isolated native cleanup/observation; diagnostic airborne state injection"}));
    }
}
