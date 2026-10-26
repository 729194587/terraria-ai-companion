using System;
using System.IO;
using System.Linq;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using VirtualPlayerEngineProbe;
using Terraria;
using EngineProgram = Terraria.Program;
using ModState = VirtualPlayerEngineProbe.VirtualPlayerEngineProbe;

public static class ControlOfflineTests
{
    private static int checks, calls;
    private static void Check(bool value,string label) { if(!value)throw new Exception("G4: "+label);checks++;Console.WriteLine("PASS G4: "+label); }
    private static void Reject(Action action,string label) { bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Check(rejected,label); }
    private static Player Reset()
    {
        ActorControl.Reset();Main.netMode=2;Main.dedServ=true;Main.gameMenu=Netplay.Disconnect=false;Main.GameUpdateCount=0;
        EngineProgram.IsMainThread=true;ModState.Ready=true;ModState.Fault=null;
        ProbeSession.Reservation=new();ProbeSession.Reservation.ArmBeforeListening(false,4);
        Main.player=Enumerable.Range(0,256).Select(i=>new Player{whoAmI=i,position=new Vector{X=100,Y=100}}).ToArray();
        Netplay.Clients=Enumerable.Range(0,256).Select(_=>new RemoteClient()).ToArray();
        ProbeSession.Actor=Main.player[3];ProbeSession.ActorGeneration=1;ProbeSession.WorldSession="world-a";
        ProbeSession.Quarantined=ProbeSession.ThrowLog=false;ProbeSession.Events.Clear();
        NetworkStateSync.Packets.Clear();NetworkStateSync.ThrowFull=NetworkStateSync.ThrowTick=false;calls=0;
        return ProbeSession.Actor;
    }
    private static void Start(Player p)=>ActorControl.Start(p,ProbeSession.ActorGeneration,ProbeSession.WorldSession);
    private static bool Released(Player p)=>!p.controlLeft&&!p.controlRight&&!p.controlUp&&!p.controlDown&&!p.controlJump&&!p.controlUseItem&&!p.controlUseTile;
    // Deliberately simple deterministic physics fixture, NOT native physics proof.
    private static void FixtureUpdate(Player p,int slot)
    {
        calls++;ActorControl.BeforeMovement(p);
        if(p.controlRight){p.velocity.X=Math.Min(3,p.velocity.X+0.08f);p.position.X+=p.velocity.X;}
    }
    public static void Run(string gamePath)
    {
        var p=Reset();Start(p);var context=ActorControl.Current;
        Check(ReferenceEquals(context.Actor,p)&&context.World=="world-a"&&context.Generation==1,"context binds actual reference, world and incarnation");
        Reject(()=>Start(p),"overlapping action rejected");
        while(context.Phase==ActionPhase.Running){Main.GameUpdateCount++;ActorControl.Update(FixtureUpdate,p,3);Check(Released(p),"finally releases inputs between native updates");}
        Check(context.Phase==ActionPhase.Completed&&context.DeltaX>=64&&context.DeltaX<67&&context.Updates<=90&&calls==context.Updates,
            "deterministic fixture reaches short right target with exactly one original call per update");
        Check(p.velocity.X==0&&Released(p)&&NetworkStateSync.Packets.Last()==new NetworkStateSync.Snapshot(p.position.X,0,false),"completion actively brakes and sends final released state");
        Check(NetworkStateSync.Packets.Any(s=>s.Right&&s.Vx>0),"G2 movement sync observes native result before finally releases input");
        ActorControl.Update((player,slot)=>calls++,p,3);Check(Released(p)&&context.Phase==ActionPhase.Completed,"finished action does not resume controls");
        ActorControl.Stop(p,1,"world-a");Check(context.Phase==ActionPhase.Completed,"stop after completion preserves the completed result");
        p=Reset();Start(p);for(int i=0;i<90;i++){Main.GameUpdateCount++;ActorControl.Update((player,slot)=>{calls++;ActorControl.BeforeMovement(player);},p,3);}
        Check(ActorControl.Current.Phase==ActionPhase.TimedOut&&ActorControl.Current.DeltaX==0&&calls==90&&Released(p),"blocked movement times out instead of claiming completion");
        p=Reset();Start(p);p.velocity.Y=2;ActorControl.Update(FixtureUpdate,p,3);ActorControl.Stop(p,1,"world-a");
        Check(ActorControl.Current.Phase==ActionPhase.Cancelled&&Released(p)&&p.velocity.X==0&&p.velocity.Y==2,"explicit stop preserves vertical physics and releases owned inputs");
        p=Reset();Start(p);ActorControl.Update(FixtureUpdate,p,3);ActorControl.Retire(p);
        Check(ActorControl.Current==null&&Released(p)&&p.velocity.X==0,"normal despawn retires context and inputs before leave synchronization");
        foreach(var fault in new[]{"world","generation","actor","slot-object","connection","slot-identity"})
        {
            p=Reset();Start(p);var old=p;var other=new Player{whoAmI=3,controlRight=true,velocity=new Vector{X=7}};
            if(fault=="world")ProbeSession.WorldSession="world-b";
            if(fault=="generation")ProbeSession.ActorGeneration++;
            if(fault=="actor")ProbeSession.Actor=other;
            if(fault=="slot-object")Main.player[3]=other;
            if(fault=="connection")Netplay.Clients[3].PendingTermination=true;
            if(fault=="slot-identity")p.whoAmI=2;
            ActorControl.CheckBinding();
            Check(ActorControl.Current.Phase==ActionPhase.Stale&&Released(old)&&other.controlRight&&other.velocity.X==7,
                "stale binding cannot overwrite/release unknown object: "+fault);
        }
        p=Reset();var queued=p;ProbeSession.ActorGeneration++;
        Reject(()=>ActorControl.Start(queued,1,"world-a"),"queued old incarnation discarded even with same Player reference");
        p=Reset();ProbeSession.WorldSession="world-b";
        Reject(()=>ActorControl.Start(p,1,"world-a"),"queued old world command rejected");
        p=Reset();queued=p;ProbeSession.Actor=Main.player[3]=new Player{whoAmI=3};
        Reject(()=>ActorControl.Start(queued,1,"world-a"),"queued old reference rejected when slot is reused");
        p=Reset();var human=Main.player[0];human.controlRight=true;Start(p);
        bool humanError=false;
        try{ActorControl.Update((player,slot)=>{calls++;throw new IOException("human native error");},human,0);}catch(IOException){humanError=true;}
        ActorControl.BeforeMovement(human);
        Check(humanError&&calls==1&&human.controlRight&&ActorControl.Current.Phase==ActionPhase.Running,"human native path and exceptions remain unchanged");
        foreach(var fault in new[]{"native","missing-hook","tick-sync","full-sync","nan","disabled","death","duplicate"})
        {
            p=Reset();Start(p);
            if(fault=="tick-sync")NetworkStateSync.ThrowTick=true;
            if(fault=="full-sync")NetworkStateSync.ThrowFull=true;
            if(fault=="disabled")p.frozen=true;
            On_Player.orig_Update native=(player,slot)=>{
                calls++;if(fault=="missing-hook")return;ActorControl.BeforeMovement(player);
                player.velocity.X=2;
                if(fault=="native")throw new IOException("native AI error");
                if(fault=="nan")player.position.X=float.NaN;
                else if(fault=="full-sync")player.position.X+=64;
                if(fault=="death")player.dead=true;
            };
            ActorControl.Update(native,p,3);
            if(fault=="duplicate")ActorControl.Update(native,p,3);
            Check(ActorControl.Current.Phase==(fault=="death"?ActionPhase.Cancelled:ActionPhase.Failed)&&Released(p)&&p.velocity.X==0,"AI error isolation and input release: "+fault);
        }
        p=Reset();Start(p);ActorControl.Update((player,slot)=>{
            ActorControl.BeforeMovement(player);Main.player[3]=new Player{whoAmI=3,controlRight=true,velocity=new Vector{X=7}};
        },p,3);
        Check(ProbeSession.Quarantined&&ActorControl.Current.Phase==ActionPhase.Stale&&Main.player[3].controlRight&&Main.player[3].velocity.X==7,
            "mid-update reference replacement quarantines without finally writing the unknown player");
        p=Reset();Start(p);Exception wrongThread=null;
        var thread=new Thread(()=>{try{ActorControl.Update(FixtureUpdate,p,3);}catch(Exception e){wrongThread=e;}});thread.Start();thread.Join();
        Check(wrongThread is InvalidOperationException&&calls==0&&Released(p),"foreign thread rejected before native call or input writes");
        EngineProgram.IsMainThread=false;Reject(()=>ActorControl.Stop(p,1,"world-a"),"engine main-thread identity required for explicit stop");
        p=Reset();Start(p);ProbeSession.ThrowLog=true;Main.GameUpdateCount++;
        ActorControl.Update((player,slot)=>{ActorControl.BeforeMovement(player);player.position.X+=64;},p,3);
        Check(ActorControl.Current.Phase==ActionPhase.Completed&&Released(p),"logging failure cannot leave inputs pressed");
        p=Reset();Start(p);ActorControl.BeforeMovement(p);
        Check(Released(p),"movement hook outside wrapped native update cannot inject input");
        AuditNative(gamePath);
        Console.WriteLine($"G4 targeted checks passed: {checks}; fixtures and installed IL only; live server/client movement NOT VERIFIED.");
    }

    public static void AuditPackage(AssemblyDefinition package)
    {
        var types=package.MainModule.Types;
        var control=types.Single(t=>t.Name=="ActorControl");
        var code=control.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!code.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="Terraria.Player"&&m.Name is "Update" or "Teleport" or "Spawn")
            &&!code.Any(i=>i.Operand is MethodReference m&&m.Name=="get_LocalPlayer")
            &&!code.Any(i=>i.Operand is FieldReference f&&f.DeclaringType.FullName=="Terraria.Main"&&f.Name=="myPlayer"),
            "compiled controller never drives LocalPlayer, spoofs myPlayer or manually calls native Update/teleport");
        Check(!code.Any(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.Name=="position")
            &&!code.Any(i=>i.OpCode==OpCodes.Stelem_Ref),"compiled controller cannot write Player position or published player slots");
        var hook=types.Single(t=>t.Name=="ProbePlayer").Methods.Single(m=>m.Name=="PostUpdateRunSpeeds");
        Check(hook.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="BeforeMovement"),"packaged ModPlayer injects at audited server-reachable movement hook");
        var update=control.Methods.Single(m=>m.Name=="Update");
        Check(update.Body.ExceptionHandlers.Any(e=>e.HandlerType==ExceptionHandlerType.Finally)
            &&update.Body.ExceptionHandlers.Any(e=>e.HandlerType==ExceptionHandlerType.Catch),"packaged AI wrapper has error isolation and unconditional input-release finally");
        var engine=types.Single(t=>t.Name=="VirtualPlayerEngineProbe");
        System.Collections.Generic.IEnumerable<TypeDefinition> Walk(TypeDefinition t)=>new[]{t}.Concat(t.NestedTypes.SelectMany(Walk));
        var refs=Walk(engine).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Select(i=>i.Operand).OfType<MethodReference>().ToArray();
        Check(refs.Any(m=>m.DeclaringType.FullName=="Terraria.On_Player"&&m.Name=="add_Update")
            &&refs.Any(m=>m.DeclaringType.FullName=="Terraria.On_Player"&&m.Name=="remove_Update"),"native wrapper subscription has lifecycle rollback pair");
        var spawn=types.Single(t=>t.Name=="ProbeSession").Methods.Single(m=>m.Name=="Spawn");
        int Offset(string name)=>spawn.Body.Instructions.First(i=>i.OpCode==OpCodes.Stsfld&&i.Operand is FieldReference f&&f.Name==name).Offset;
        Check(Offset("ActorGeneration")<Offset("Actor"),"every publication advances runtime incarnation before exposing new actor");
    }
    private static void AuditNative(string gamePath)
    {
        using var native=AssemblyDefinition.ReadAssembly(Path.Combine(gamePath,"tModLoader.dll"));
        var main=native.MainModule.Types.Single(t=>t.FullName=="Terraria.Main");
        var player=native.MainModule.Types.Single(t=>t.FullName=="Terraria.Player");
        var update=player.Methods.Single(m=>m.Name=="Update"&&m.Parameters.Count==1);
        var code=update.Body.Instructions;
        Instruction At(int offset)=>code.Single(i=>i.Offset==offset);
        bool Calls(Instruction i,string type,string name)=>i.Operand is MethodReference m&&m.DeclaringType.FullName==type&&m.Name==name;
        Check(Calls(At(0x1010),"Terraria.ModLoader.PlayerLoader","SetControls")&&At(0x09ae).OpCode==OpCodes.Bne_Un
            &&((Instruction)At(0x09ae).Operand).Offset==0x1913,"installed SetControls is skipped for non-local actors; cannot reuse client control path");
        Check(Calls(At(0x4649),"Terraria.ModLoader.PlayerLoader","PostUpdateRunSpeeds")
            &&Calls(At(0x464f),"Terraria.Player","HorizontalMovement"),"chosen native hook immediately precedes horizontal physics");
        Check(Calls(At(0x8232),"Terraria.ModLoader.PlayerLoader","PreUpdateMovement")
            &&Calls(At(0x82d6),"Terraria.Player","DryCollision"),"PreUpdateMovement occurs after horizontal acceleration, before native collision");
        var world=main.Methods.Single(m=>m.Name=="DoUpdateInWorld");
        Check(world.Body.Instructions.Any(i=>i.Offset==0x3f&&i.Operand is FieldReference f&&f.Name=="active")
            &&world.Body.Instructions.Count(i=>Calls(i,"Terraria.Player","Update"))==1
            &&world.Body.Instructions.Any(i=>i.Offset==0x4f&&Calls(i,"Terraria.Player","Update")),
            "native world loop updates active players through one Player.Update call site");
        var horizontal=player.Methods.Single(m=>m.Name=="HorizontalMovement");
        Check(horizontal.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="controlRight")
            &&horizontal.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="runAcceleration")
            &&!horizontal.Body.Instructions.Any(i=>i.Offset<0x1400&&i.Operand is FieldReference f&&f.Name=="myPlayer"),
            "installed ordinary horizontal acceleration precedes the separate local-only lawn-mower branch");
        var dry=player.Methods.Single(m=>m.Name=="DryCollision");
        Check(dry.Body.Instructions.Any(i=>Calls(i,"Terraria.Collision","TileCollision"))
            &&dry.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.Name=="position")
            &&!dry.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name is "myPlayer" or "netMode"),
            "installed dry collision applies native position changes without a local/client gate");
    }
}
