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
            var world = camera + new Vector2(853.25f,456.75f);
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
        Console.WriteLine("PASS: installed tModLoader IL: SpriteViewMatrix.ZoomMatrix -> PlayerInput.SetZoom_World -> Main.MouseWorld; production ItemCheck injection, zoom 1/1.25/1.5/2, camera offsets, integer rounding; no game launched");
    }
}
