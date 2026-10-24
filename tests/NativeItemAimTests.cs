using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using CompanionBridge;

// Execute the installed game's actual input/view IL in a tiny isolated assembly.
// Main's graphics/audio/network static initializer is deliberately not copied.
// Only storage is substituted; coordinate-transform method bodies are unmodified.
public static class NativeItemAimTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static void Run(string gamePath)
    {
        using var game = AssemblyDefinition.ReadAssembly(Path.Combine(gamePath, "tModLoader.dll"));
        using var fixture = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("NativeInputContract", new Version(1,0)), "NativeInputContract", ModuleKind.Dll);
        var mod = fixture.MainModule;
        var types = new Dictionary<string, TypeDefinition>();
        var fields = new Dictionary<string, FieldDefinition>();
        var methods = new Dictionary<string, MethodDefinition>();
        string[] names = { "Terraria.Main", "Terraria.Player", "Terraria.GameInput.PlayerInput", "Terraria.Graphics.SpriteViewMatrix" };
        foreach (var name in names)
        {
            var src = game.MainModule.Types.Single(t => t.FullName == name);
            var dst = new TypeDefinition(src.Namespace, src.Name, src.Attributes, mod.TypeSystem.Object);
            mod.Types.Add(dst); types.Add(name, dst);
        }
        TypeReference Import(TypeReference t) => t is ArrayType array ? new ArrayType(Import(array.ElementType))
            : types.TryGetValue(t.FullName, out var mapped) ? mapped : mod.ImportReference(t);
        bool FieldIncluded(TypeDefinition t, FieldDefinition f) => t.Name == "SpriteViewMatrix"
            || t.Name == "Main" && new[] { "mouseX", "mouseY", "lastMouseX", "lastMouseY", "screenWidth", "screenHeight", "screenPosition", "myPlayer", "player", "GameViewMatrix" }.Contains(f.Name)
            || t.Name == "Player" && f.Name == "gravDir"
            || t.Name == "PlayerInput" && f.Name.StartsWith("_original");
        bool MethodIncluded(TypeDefinition t, MethodDefinition m) => t.Name == "SpriteViewMatrix"
            || t.Name == "Main" && new[] { "get_MouseScreen", "get_MouseWorld" }.Contains(m.Name)
            || t.Name == "PlayerInput" && new[] { "SetZoom_World", "SetZoom_MouseInWorld", "SetZoom_Scaled" }.Contains(m.Name);
        foreach (var name in names)
        {
            var src = game.MainModule.Types.Single(t => t.FullName == name);
            var dst = types[name];
            foreach (var f in src.Fields.Where(f => FieldIncluded(src,f)))
            {
                var nf = new FieldDefinition(f.Name, f.Attributes, Import(f.FieldType));
                dst.Fields.Add(nf); fields.Add(f.FullName,nf);
            }
            foreach (var m in src.Methods.Where(m => MethodIncluded(src,m)))
            {
                var nm = new MethodDefinition(m.Name,m.Attributes,Import(m.ReturnType));
                foreach(var p in m.Parameters) nm.Parameters.Add(new ParameterDefinition(p.Name,p.Attributes,Import(p.ParameterType)));
                dst.Methods.Add(nm); methods.Add(m.FullName,nm);
            }
        }
        foreach (var name in names)
        foreach (var src in game.MainModule.Types.Single(t => t.FullName == name).Methods.Where(m => MethodIncluded(m.DeclaringType,m)))
        {
            Check(src.HasBody && src.Body.ExceptionHandlers.Count == 0, "Unexpected native input IL shape");
            var dst = methods[src.FullName]; dst.Body.InitLocals = src.Body.InitLocals; dst.Body.MaxStackSize = src.Body.MaxStackSize;
            foreach(var v in src.Body.Variables) dst.Body.Variables.Add(new VariableDefinition(Import(v.VariableType)));
            var ins = src.Body.Instructions.ToDictionary(i => i, i => Instruction.Create(OpCodes.Nop));
            foreach(var i in src.Body.Instructions) { ins[i].OpCode = i.OpCode; dst.Body.Instructions.Add(ins[i]); }
            foreach(var i in src.Body.Instructions)
                ins[i].Operand = i.Operand switch {
                    Instruction branch => ins[branch],
                    Instruction[] branches => branches.Select(b => ins[b]).ToArray(),
                    VariableDefinition v => dst.Body.Variables[v.Index],
                    ParameterDefinition p => dst.Parameters[p.Index],
                    FieldReference f => fields.TryGetValue(f.FullName,out var nf) ? nf : mod.ImportReference(f),
                    MethodReference m => methods.TryGetValue(m.FullName,out var nm) ? nm : mod.ImportReference(m),
                    TypeReference t => Import(t),
                    _ => i.Operand };
        }
        // Ensure the tested coordinate space is the one used by the real hooks.
        var player = game.MainModule.Types.Single(t => t.FullName == "Terraria.Player");
        var update = player.Methods.Single(m => m.Name == "Update" && m.Parameters.Count == 1);
        var zoomCall = update.Body.Instructions.First(i => i.Operand is MethodReference m && m.Name == "SetZoom_World");
        var itemCall = update.Body.Instructions.First(i => i.Operand is MethodReference m && m.Name == "ItemCheckWrapped");
        Check(zoomCall.Offset < itemCall.Offset, "SetZoom_World must precede ItemCheck");
        Check(!update.Body.Instructions.Any(i => i.Offset > zoomCall.Offset && i.Offset < itemCall.Offset
            && i.Operand is MethodReference m && m.Name == "SetZoom_Unscaled"), "Unexpected unscaled switch before ItemCheck");
        var itemCheck = player.Methods.Single(m => m.Name == "ItemCheck" && m.Parameters.Count == 0);
        Check(itemCheck.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "PreItemCheck"), "Actual ItemCheck must invoke the production hook");
        var mining = player.Methods.Single(m => m.Name == "ItemCheck_UseMiningTools");
        var pickDispatch = mining.Body.Instructions.Single(i => i.Operand is MethodReference m
            && m.Name == "ItemCheck_UseMiningTools_ActuallyUseMiningTool");
        Check(pickDispatch.Previous.Operand is FieldReference fy && fy.Name=="tileTargetY"
            && pickDispatch.Previous.Previous.Operand is FieldReference fx && fx.Name=="tileTargetX",
            "Native mining must forward injected tile coordinates directly to the actual tool operation");
        foreach(var method in player.Methods.Where(m => m.Name is "ItemCheck_Inner" or "ItemCheck_OwnerOnlyCode"
            || m.Name.StartsWith("ItemCheck_UseMiningTools")))
        {
            Check(!method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name.StartsWith("SetZoom")
                || i.Operand is FieldReference f && f.Name=="GameViewMatrix"),"Unexpected mining zoom transform");
            Check(!method.Body.Instructions.Any(i => i.OpCode==OpCodes.Stsfld && i.Operand is FieldReference f
                && f.Name is "tileTargetX" or "tileTargetY"),"Mining path must not overwrite injected tile targets");
        }
        var placing = player.Methods.Single(m => m.Name == "PlaceThing_Tiles_PlaceIt");
        var placeCall = placing.Body.Instructions.Single(i => i.Operand is MethodReference m
            && m.DeclaringType.FullName=="Terraria.WorldGen" && m.Name=="PlaceTile");
        // WorldGen.PlaceTile's first two arguments must be the injected tile target,
        // followed by native item type/style/force/player arguments (not pixels).
        var argsBeforePlace = placing.Body.Instructions.TakeWhile(i => i!=placeCall).TakeLast(8).ToArray();
        Check(argsBeforePlace[0].OpCode==OpCodes.Ldsfld && argsBeforePlace[0].Operand is FieldReference px && px.Name=="tileTargetX"
            && argsBeforePlace[1].OpCode==OpCodes.Ldsfld && argsBeforePlace[1].Operand is FieldReference py && py.Name=="tileTargetY",
            "Ordinary native PlaceTile must receive the exact injected tile indices");
        foreach(var method in player.Methods.Where(m => m.Name is "PlaceThing" or "PlaceThing_Tiles" or "PlaceThing_Tiles_PlaceIt"))
        {
            Check(!method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name.StartsWith("SetZoom")
                || i.Operand is FieldReference f && f.Name=="GameViewMatrix"),"Unexpected native placement zoom transform");
            Check(!method.Body.Instructions.Any(i => i.OpCode==OpCodes.Stsfld && i.Operand is FieldReference f
                && f.Name is "tileTargetX" or "tileTargetY"),"Placement must not overwrite the injected target in its main path");
        }
        // Structural contracts from the installed game. These check the real native
        // paths, but do not execute world mutation, permission hooks or game timing.
        bool Calls(MethodDefinition method,string name) => method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name==name);
        bool Uses(MethodDefinition method,string name) => method.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name==name);
        Check(Calls(mining,"IsTargetTileInItemRange") && Uses(mining,"noBuilding") && Uses(mining,"toolTime"),
            "Native mining tools must retain range, building permission and tool cooldown gates");
        var wallHit=player.Methods.Single(m=>m.Name=="ItemCheck_UseMiningTools_TryHittingWall");
        Check(Calls(wallHit,"CanPlayerSmashWall") && Calls(wallHit,"PickWall") && Uses(wallHit,"hammer")
            && Uses(wallHit,"controlUseItem") && Uses(wallHit,"itemAnimation"),"Walls must use native hammer checks and PickWall, never PickTile");
        Check(!Calls(wallHit,"PickTile"),"Wall branch cannot mine a solid tile");
        var pounding=player.Methods.Single(m=>m.Name=="ItemCheck_UseMiningTools_TryPoundingTile");
        Check(Calls(pounding,"PoundTile") && Calls(pounding,"SlopeTile") && Uses(pounding,"poundRelease"),"Native hammer shape cycle and release gate");
        var wallPlacement=player.Methods.Single(m=>m.Name=="PlaceThing_Walls");
        Check(Calls(wallPlacement,"PlaceWall"),"Native wall placement entry calls WorldGen.PlaceWall");
        Check(Uses(wallPlacement,"tileTargetX") && Uses(wallPlacement,"tileTargetY"),"Native PlaceWall uses injected tile coordinates");
        var wiring=game.MainModule.Types.Single(t=>t.FullName=="Terraria.Wiring").Methods.Single(m=>m.Name=="HitSwitch");
        Check(Calls(wiring,"HitSwitch") && Calls(wiring,"TripWire") && Calls(wiring,"get_frameY") && Calls(wiring,"get_frameX"),
            "Native switch/lever path retains ModLoader veto, frame change and wire dispatch");
        Check(player.Methods.Single(m=>m.Name=="OpenChest").IsPublic,"Supported native chest UI helper must be public");
        var interactCheck=player.Methods.Single(m=>m.Name=="TileInteractionsCheck");
        var interactUse=player.Methods.Single(m=>m.Name=="TileInteractionsUse");
        Check(interactCheck.IsPublic && Calls(interactCheck,"TileInteractionsUse"),"Public interaction wrapper must dispatch to native use");
        Check(Uses(interactUse,"releaseUseTile") && Uses(interactUse,"tileInteractAttempted")
            && Calls(interactUse,"OpenChest") && Calls(interactUse,"HitSwitch"),"Native interaction gates and distinct chest/wiring paths");
        // Pin the actual installed vanilla wire contract used by NativeAuthorityWorld.
        // Case bodies remain structural inspection, not a live server execution.
        var getData=game.MainModule.Types.Single(t=>t.FullName=="Terraria.MessageBuffer").Methods.Single(m=>m.Name=="GetData");
        var cases=(Instruction[])getData.Body.Instructions.First(i=>i.OpCode==OpCodes.Switch && i.Operand is Instruction[] a && a.Length>100).Operand;
        Instruction[] Case(int id)
        {
            var start=cases[id-1]; int end=cases.Where(i=>i.Offset>start.Offset).Min(i=>i.Offset);
            return getData.Body.Instructions.Where(i=>i.Offset>=start.Offset && i.Offset<end).ToArray();
        }
        void Reads(int id,params string[] expected)
        {
            var actual=Case(id).Where(i=>i.Operand is MethodReference m && m.DeclaringType.FullName=="System.IO.BinaryReader")
                .Select(i=>((MethodReference)i.Operand).Name).Take(expected.Length).ToArray();
            Check(actual.SequenceEqual(expected),"Installed native message "+id+" binary layout changed");
        }
        bool CaseCalls(int id,string name)=>Case(id).Any(i=>i.Operand is MethodReference m && m.Name==name);
        Reads(17,"ReadByte","ReadInt16","ReadInt16","ReadInt16","ReadByte");
        Reads(19,"ReadByte","ReadInt16","ReadInt16","ReadByte");
        Reads(31,"ReadInt16","ReadInt16"); Reads(33,"ReadInt16","ReadInt16","ReadInt16","ReadByte");
        Reads(59,"ReadInt16","ReadInt16");
        foreach(string name in new[]{"KillWall","PlaceWall","PoundTile","SlopeTile"}) Check(CaseCalls(17,name),"Missing server tile mutation handler "+name);
        Check(CaseCalls(19,"OpenDoor") && CaseCalls(19,"CloseDoor"),"Server door handler changed");
        Check(CaseCalls(31,"FindChest") && CaseCalls(31,"UsingChest"),"Server chest ownership path changed");
        Check(CaseCalls(59,"SetCurrentUser") && CaseCalls(59,"HitSwitch"),"Server wiring actor/dispatch changed");
        using var stream = new MemoryStream(); fixture.Write(stream); stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        var main = assembly.GetType("Terraria.Main"); var input = assembly.GetType("Terraria.GameInput.PlayerInput");
        var viewType = assembly.GetType("Terraria.Graphics.SpriteViewMatrix"); var playerType = assembly.GetType("Terraria.Player");
        const BindingFlags flags = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
        void Set(Type type,string field,object value) => type.GetField(field,flags).SetValue(null,value);
        object Get(string field) => main.GetField(field,flags).GetValue(null);
        var localPlayer = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(playerType);
        playerType.GetField("gravDir").SetValue(localPlayer,1f);
        var players = Array.CreateInstance(playerType,1); players.SetValue(localPlayer,0); Set(main,"player",players);
        var view = Activator.CreateInstance(viewType, new object[] { null });
        viewType.GetMethod("SetViewportOverride").Invoke(view,new object[] { new Viewport(0,0,1920,1080) });
        Set(main,"GameViewMatrix",view); Set(input,"_originalScreenWidth",1920); Set(input,"_originalScreenHeight",1080);
        foreach(float zoom in new[] { 1f,1.25f,1.5f,2f })
        foreach(var camera in new[] { new Vector2(0,0),new Vector2(34000.25f,3600.5f) })
        {
            viewType.GetMethod("set_Zoom").Invoke(view,new object[] { new Vector2(zoom) });
            Set(main,"screenPosition",camera);
            foreach(var world in new[] { camera + new Vector2(853.25f,456.75f),new Vector2(2194*16+8,235*16+8) })
            {
            // Rendered screen -> the game's actual inverse input transform.
            var matrix = (Matrix)viewType.GetMethod("get_ZoomMatrix").Invoke(view,null);
            var rendered = Vector2.Transform(world-camera,matrix);
            Set(input,"_originalMouseX",(int)rendered.X); Set(input,"_originalMouseY",(int)rendered.Y);
            input.GetMethod("SetZoom_World").Invoke(null,null);
            var nativeWorld = (Vector2)main.GetMethod("get_MouseWorld").Invoke(null,null);
            Check(Vector2.Distance(nativeWorld,world) < 2.1f, "Actual rendered/inverse/native roundtrip failed");
            Check(ItemAim.TryGetItemCheckMouse(world,camera,out int x,out int y),"Production conversion accepted");
            Check(Math.Abs(x-(int)Get("mouseX")) <= 1 && Math.Abs(y-(int)Get("mouseY")) <= 1, "Production injection disagrees with native inverse");
            // This is the actual ItemCheck mouse space, not physical pixels.
            Set(main,"mouseX",x); Set(main,"mouseY",y);
            nativeWorld = (Vector2)main.GetMethod("get_MouseWorld").Invoke(null,null);
            Check(Vector2.Distance(nativeWorld,world) < 1.42f,"Injected coordinates do not reconstruct target world pixels");
        }
        }
        Console.WriteLine("PASS: installed tModLoader IL: executed native input/zoom transforms; structural contracts for mining/walls/hammer/interactions and server packet layouts 17/19/31/33/59; world effects and network timing not executed; no game launched");
    }
}
