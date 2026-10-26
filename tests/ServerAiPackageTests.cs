using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class ServerAiPackageTests
{
    public static void Run(string package)
    {
        int checks=0;
        void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;Console.WriteLine("PASS AI package: "+text);}
        using var file=File.OpenRead(package);using var reader=new BinaryReader(file);
        Check(System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4))=="TMOD","real TMOD package");
        reader.ReadString();reader.ReadBytes(276);reader.ReadInt32();
        Check(reader.ReadString()=="CompanionBridge"&&reader.ReadString()=="0.1.42","formal package identity/version");
        int count=reader.ReadInt32(),offset=0,dllOffset=-1,size=0,packed=0;
        for(int i=0;i<count;i++){string name=reader.ReadString();int length=reader.ReadInt32(),compressed=reader.ReadInt32();if(name=="CompanionBridge.dll"){dllOffset=offset;size=length;packed=compressed;}offset+=compressed;}
        Check(dllOffset>=0,"formal DLL packaged");file.Position+=dllOffset;
        using var payload=new MemoryStream(reader.ReadBytes(packed));using var bytes=new MemoryStream();
        if(packed==size)payload.CopyTo(bytes);else {using var inflate=new System.IO.Compression.DeflateStream(payload,System.IO.Compression.CompressionMode.Decompress);inflate.CopyTo(bytes);}
        bytes.Position=0;using var assembly=AssemblyDefinition.ReadAssembly(bytes);
        IEnumerable<TypeDefinition> Walk(TypeDefinition t)=>new[]{t}.Concat(t.NestedTypes.SelectMany(Walk));
        var types=assembly.MainModule.Types.SelectMany(Walk).ToArray();
        TypeDefinition Type(string name)=>types.Single(t=>t.Name==name);
        bool Calls(MethodDefinition m,string name)=>m.HasBody&&m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name==name);
        Check(types.Count(t=>t.BaseType?.FullName=="Terraria.ModLoader.Mod")==1&&Type("CompanionBridge").BaseType.FullName=="Terraria.ModLoader.Mod","one formal Mod entry; runtime is not a second mod");
        var save=Type("ProbePlayer").Methods.Single(m=>m.Name=="SaveData");
        Check(save.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr&&(string)i.Operand=="VirtualPlayerEngineProbe")
            && save.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="Actor")
            && save.Body.Instructions.Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch),"probe persistence marker is guarded for formal human saves");
        Check(Type("VirtualPlayerEngineProbe").BaseType.FullName=="System.Object","promoted runtime hosted by formal Mod");
        var wire=types.Where(t=>t.FullName.StartsWith("CompanionBridge.ServerIpc.AiReadOnlyServer")).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!wire.Any(i=>i.Operand is MemberReference r&&r.DeclaringType?.Namespace.StartsWith("Terraria")==true),"all TCP worker/state-machine IL contains zero Terraria access");
        var adapter=Type("ServerAiSystem");var capture=adapter.Methods.Single(m=>m.Name=="Capture");var pump=adapter.Methods.Single(m=>m.Name=="Pump");
        Check(Calls(capture,"Require")&&Calls(capture,"AssertOwner"),"native snapshot reuses audited binding guard on owner thread");
        Check(!adapter.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference r&&r.Name=="get_LocalPlayer"),"server adapter has no LocalPlayer fallback");
        Check(Calls(pump,"get_IsMainThread")&&Calls(pump,"get_Gate")&&Calls(pump,"Tick"),"simulation pump enforces thread then Gate before dispatch");
        var select=adapter.Methods.Single(m=>m.Name=="Select");
        Check(Calls(select,"AssertOwner")&&select.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.Name=="ActorItemUse"&&r.Name=="Select"),"server atomic selection delegates to G6 after owner assertion");
        Check(!select.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference r&&r.DeclaringType.FullName=="Terraria.Player"),"adapter does not reimplement native selection or mutate Player fields");
        Check(!Calls(adapter.Methods.Single(m=>m.Name=="OnWorldLoad"),"Start")&&!Calls(adapter.Methods.Single(m=>m.Name=="OnWorldLoad"),"Spawn"),"worker world-load callback never starts listener or actor");
        var started=adapter.Methods.Single(m=>m.Name=="Started");
        int CallOffset(string name)=>started.Body.Instructions.First(i=>i.Operand is MethodReference r&&r.Name==name).Offset;
        Check(CallOffset("Invoke")<CallOffset("Begin")&&CallOffset("Begin")<CallOffset("Pump"),"native server startup finishes before listener and actor publication");
        Check(Calls(adapter.Methods.Single(m=>m.Name=="OnWorldUnload"),"Stop")&&Calls(adapter.Methods.Single(m=>m.Name=="UnloadRuntime"),"Unload"),"world/mod unload stop IPC and delegate lifecycle teardown");
        foreach(string name in new[]{"ProbeCommand","ClientProbeCommand","ProbeTargetCommand","ProbeMouseMineCommand","ProbeTargetingSystem"})
            Check(Type(name).Methods.Any(m=>m.Name=="IsLoadingEnabled"&&m.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr&&(string)i.Operand=="VirtualPlayerEngineProbe")),"experimental content disabled for formal owner: "+name);
        var mod=Type("CompanionBridge");Check(Calls(mod.Methods.Single(m=>m.Name=="Load"),"LoadRuntime")&&Calls(mod.Methods.Single(m=>m.Name=="Load"),"RegisterKeybind"),"formal Mod retains independent server and human-client entry paths");
        Console.WriteLine($"AI formal package: {checks} IL checks passed; no graphical game launched.");
    }
}
