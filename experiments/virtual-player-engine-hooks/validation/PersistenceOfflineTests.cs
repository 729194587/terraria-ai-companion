using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using VirtualPlayerEngineProbe;

public static class PersistenceOfflineTests
{
    private static int checks;
    private static void Check(bool value, string message) { if(!value) throw new Exception(message); checks++; Console.WriteLine("PASS G3.1: " + message); }
    private static void Reject(Action action, string message)
    {
        bool rejected=false; try { action(); } catch (Exception e) when(e is IOException || e is ArgumentException || e is UnauthorizedAccessException) { rejected=true; }
        Check(rejected,message);
    }
    public class FileState { public string Path => "private"; public bool IsCloudSave => false; }
    public class Closure
    {
        public FileState playerFile;
        public void Save() { Effects.Writes++; if(Effects.Fail) throw new IOException("native fixture write failure"); }
    }
    public class Achievements { public void Save() => Effects.AchievementSaves++; }
    public class UiState { }
    public static class Effects
    {
        public static FileState Owned;
        public static bool Fail, ServerSideCharacter, gameMenu;
        public static int Writes, Maps, AchievementSaves, Errors, Quits, menuMode;
        public static bool Owns(FileState file) => ReferenceEquals(Owned,file);
        public static Achievements get_Achievements() => new();
        public static void InternalSaveMap(bool cloud) => Maps++;
        public static void ProtectedInvoke(Action action) => action();
        public static void ShowFileSavingFailError(Exception e,string path) => Errors++;
        public static void SaveAndQuit(Action callback) => Quits++;
        public static string GetTextValue(string key) => key;
        public static void ShowFancyErrorMessage(string text,int menu,UiState state) => Errors++;
        public static void Reset() { Owned=null; Fail=ServerSideCharacter=gameMenu=false; Writes=Maps=AchievementSaves=Errors=Quits=0; }
    }
    public static void Run(string gamePath,string output)
    {
        var files=new CharacterFiles(Path.Combine(output,"character-files-"+Guid.NewGuid().ToString("N")));
        foreach(var id in new[]{"..", "../Players/human", "C:\\human", "", new string('A',32),new string('z',32)})
            Reject(()=>files.Begin(id),"unsafe ID rejected: "+id);
        string safe=CharacterFiles.NewId();
        var first=files.Begin(safe); File.WriteAllText(first,"native-plr-1"); File.WriteAllText(Path.ChangeExtension(first,".tplr"),"native-tplr-1");
        files.Commit(safe,first,"state1"); Check(files.Open(safe).Path==first,"committed native pair opens");
        var incomplete=files.Begin(safe); File.WriteAllText(incomplete,"partial");
        Reject(()=>files.Commit(safe,incomplete,"state2"),"incomplete pair cannot commit");
        Check(files.Open(safe).Path==first,"failed generation retains old pointer");
        var second=files.Begin(safe); File.WriteAllText(second,"native-plr-2"); File.WriteAllText(Path.ChangeExtension(second,".tplr"),"native-tplr-2");
        files.Commit(safe,second,"state2"); Check(files.Open(safe).Path==second && File.ReadAllText(first)=="native-plr-1","new pair commits without overwriting previous pair");
        File.AppendAllText(Path.ChangeExtension(second,".tplr"),"corrupt");
        Reject(()=>files.Open(safe),"tampered ModPlayer file rejected before native load");
        var blocked=CharacterFiles.NewId(); File.WriteAllText(files.DirectoryFor(blocked),"blocked directory");
        Reject(()=>files.Begin(blocked),"unwritable destination fails before native save");

        using var native=AssemblyDefinition.ReadAssembly(Path.Combine(gamePath,"tModLoader.dll"));
        var player=native.MainModule.Types.Single(t=>t.FullName=="Terraria.Player");
        var save=player.Methods.Single(m=>m.Name=="SavePlayer");
        Check(save.Parameters.Count==2 && save.Parameters[1].Name=="skipMapSave"
            && !save.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldarg_1),"installed skipMapSave is unused; scoped global-effects guard is necessary");
        var load=player.Methods.Single(m=>m.Name=="LoadPlayer");
        Check(load.ReturnType.FullName=="Terraria.IO.PlayerFileData","installed LoadPlayer returns PlayerFileData");
        var context=native.MainModule.Types.Single(t=>t.FullName=="Terraria.Main").NestedTypes.Single(t=>t.Name=="CurrentPlayerOverride");
        Check(!context.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.OpCode==OpCodes.Stelem_Ref),
            "native CurrentPlayerOverride never assigns Main.player array");
        var baseline=Compile(save,"SaveBaseline");
        var predicate=save.Module.ImportReference(typeof(Effects).GetMethod(nameof(Effects.Owns)));
        PersistencePatches.SavePlayer(save,predicate);
        var patched=Compile(save,"SavePatched");
        foreach(bool fail in new[]{false,true}) foreach(bool menu in new[]{false,true})
        {
            var human=new FileState(); Effects.Reset(); Effects.Fail=fail; Effects.gameMenu=menu;
            Invoke(baseline,human); var before=(Effects.Writes,Effects.Maps,Effects.AchievementSaves,Effects.Errors,Effects.Quits);
            Effects.Reset(); Effects.Fail=fail; Effects.gameMenu=menu; Invoke(patched,human);
            Check(before==(Effects.Writes,Effects.Maps,Effects.AchievementSaves,Effects.Errors,Effects.Quits),$"human save path unchanged: failure={fail}, menu={menu}");
        }
        foreach(bool fail in new[]{false,true})
        {
            Effects.Reset(); Effects.Owned=new FileState(); Effects.Fail=fail; Invoke(patched,Effects.Owned);
            Check(Effects.Writes==1 && Effects.Maps==0 && Effects.AchievementSaves==0 && Effects.Errors==0 && Effects.Quits==0,
                "scoped AI save suppresses map/global error/exit effects, failure="+fail);
        }
        foreach(var mutation in new[]{"signature","map","catch","exit"})
        {
            using var altered=AssemblyDefinition.ReadAssembly(Path.Combine(gamePath,"tModLoader.dll"));
            var method=altered.MainModule.Types.Single(t=>t.FullName=="Terraria.Player").Methods.Single(m=>m.Name=="SavePlayer");
            if(mutation=="signature") method.Parameters[1].ParameterType=altered.MainModule.TypeSystem.Int32;
            else if(mutation=="catch") method.Body.ExceptionHandlers[0].HandlerStart=method.Body.Instructions[0];
            else method.Body.Instructions.Single(i=>i.Offset==(mutation=="map"?0x22:0x5b)).OpCode=OpCodes.Nop;
            var count=method.Body.Instructions.Count;
            bool rejected=false; try{PersistencePatches.SavePlayer(method,predicate);}catch(InvalidOperationException){rejected=true;}
            Check(rejected && method.Body.Instructions.Count==count,"unsupported IL rejected without mutation: "+mutation);
        }
        var runtime=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gamePath,"tModLoader.dll"));
        using(var dmd=new DynamicMethodDefinition(runtime.GetType("Terraria.Player").GetMethod("SavePlayer")))
        using(var il=new ILContext(dmd.Definition))
        {
            il.Invoke(c=>PersistencePatches.SavePlayer(c.Method,c.Module.ImportReference(typeof(Effects).GetMethod(nameof(Effects.Owns)))));
            Check(true,"real DMD/MonoMod accepts scoped SavePlayer patch");
        }
        Console.WriteLine($"G3.1 offline checks passed: {checks}; no live client/persistence claim.");
    }
    private static void Invoke(Action<FileState,bool> action,FileState file) { try{action(file,true);}catch(IOException){} }

    // Execute the complete audited native outer body, replacing only file/global effects.
    private static Action<FileState,bool> Compile(MethodDefinition source,string name)
    {
        using var asm=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name,new Version(1,0)),name,ModuleKind.Dll);
        var module=asm.MainModule;
        var map=new Dictionary<string,Type> {
            ["Terraria.IO.PlayerFileData"]=typeof(FileState), ["Terraria.IO.FileData"]=typeof(FileState),
            ["Terraria.Player/<>c__DisplayClass1967_0"]=typeof(Closure),
            ["Terraria.Main"]=typeof(Effects), ["Terraria.Player"]=typeof(Effects),
            ["Terraria.Utilities.FileUtilities"]=typeof(Effects), ["Terraria.UI.FancyErrorPrinter"]=typeof(Effects),
            ["Terraria.WorldGen"]=typeof(Effects), ["Terraria.Localization.Language"]=typeof(Effects),
            ["Terraria.Utils"]=typeof(Effects), ["Terraria.UI.UIState"]=typeof(UiState),
            ["Terraria.Achievements.AchievementManager"]=typeof(Achievements)
        };
        TypeReference Type(TypeReference t)=>map.TryGetValue(t.FullName,out var mapped)?module.ImportReference(mapped):module.ImportReference(t);
        var runner=new TypeDefinition("Fixture",name,Mono.Cecil.TypeAttributes.Public,module.TypeSystem.Object); module.Types.Add(runner);
        var dest=new MethodDefinition("Save",Mono.Cecil.MethodAttributes.Public|Mono.Cecil.MethodAttributes.Static,module.TypeSystem.Void);
        runner.Methods.Add(dest); foreach(var p in source.Parameters) dest.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,Type(p.ParameterType)));
        dest.Body.InitLocals=true; dest.Body.MaxStackSize=source.Body.MaxStackSize;
        foreach(var v in source.Body.Variables) dest.Body.Variables.Add(new VariableDefinition(Type(v.VariableType)));
        var code=source.Body.Instructions.ToDictionary(i=>i,_=>Instruction.Create(OpCodes.Nop));
        object Import(object operand)=>operand switch {
            Instruction i=>code[i], VariableDefinition v=>dest.Body.Variables[v.Index], ParameterDefinition p=>dest.Parameters[p.Index],
            FieldReference f when map.TryGetValue(f.DeclaringType.FullName,out var t)=>module.ImportReference(t.GetField(f.Name)),
            MethodReference m when m.Name=="<SavePlayer>b__0"=>module.ImportReference(typeof(Closure).GetMethod(nameof(Closure.Save))),
            MethodReference m when m.Name==".ctor" && map.TryGetValue(m.DeclaringType.FullName,out var t)=>module.ImportReference(t.GetConstructors().Single()),
            MethodReference m when map.TryGetValue(m.DeclaringType.FullName,out var t)=>module.ImportReference(
                t.GetMethods().Single(x=>x.Name==m.Name && x.GetParameters().Length==m.Parameters.Count)),
            MethodReference m=>module.ImportReference(m), TypeReference t=>Type(t), FieldReference f=>module.ImportReference(f), _=>operand
        };
        foreach(var i in source.Body.Instructions){code[i].OpCode=i.OpCode;code[i].Operand=Import(i.Operand);dest.Body.Instructions.Add(code[i]);}
        foreach(var e in source.Body.ExceptionHandlers) dest.Body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType){
            TryStart=code[e.TryStart],TryEnd=code[e.TryEnd],HandlerStart=code[e.HandlerStart],HandlerEnd=code[e.HandlerEnd],CatchType=Type(e.CatchType)});
        using var bytes=new MemoryStream();asm.Write(bytes);bytes.Position=0;
        return (Action<FileState,bool>)AssemblyLoadContext.Default.LoadFromStream(bytes).GetType("Fixture."+name).GetMethod("Save").CreateDelegate(typeof(Action<FileState,bool>));
    }
}
