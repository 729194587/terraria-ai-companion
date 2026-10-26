using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;

public static class ProbeUxOfflineTests
{
    private static int checks;
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;Console.WriteLine("PASS probe UX: "+text);}
    public static void Run(AssemblyDefinition probe)
    {
        var target=probe.MainModule.Types.Single(t=>t.Name=="ProbeTargeting");
        bool Calls(MethodDefinition m,string name)=>m.HasBody&&m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name==name);
        var capture=target.Methods.Single(m=>m.Name=="Capture");var submit=target.Methods.Single(m=>m.Name=="Submit");
        Check(Calls(capture,"get_MouseWorld")&&Calls(capture,"get_LocalPlayer")&&Calls(capture,"InWorld"),"capture uses native local mouse/world coordinates and world bounds");
        Check(new[]{"drawingPlayerChat","playerInventory","mapFullscreen","mouseInterface"}.All(n=>capture.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name==n)),"chat/inventory/map/UI freeze the last world target");
        Check(Calls(submit,"SendChatMessageFromClient")&&submit.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldstr&&((string)i.Operand).Contains("/vph mine ")),"mouse mining submits the existing native /vph mine command");
        Check(!target.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference r&&r.Name is "KillTile" or "PickTile" or "GetPacket"),"client target helper cannot execute world actions or bypass the command gate");
        Check(!target.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.OpCode.FlowControl!=FlowControl.Call&&i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.DeclaringType.FullName=="Terraria.Player"),"client target helper never writes Player inputs/animation");
        var player=probe.MainModule.Types.Single(t=>t.Name=="ProbePlayer");
        Check(Calls(player.Methods.Single(m=>m.Name=="PreItemCheck"),"Capture")&&Calls(player.Methods.Single(m=>m.Name=="PostItemCheck"),"Observe"),"capture and read-only client animation observation use native item hooks");
        var session=probe.MainModule.Types.Single(t=>t.Name=="ProbeSession");
        var status=session.Methods.Single(m=>m.Name=="StatusText");
        Check(new[]{"ActorItemUse","ActorControl","ActorMining"}.All(n=>status.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.Name==n&&r.Name=="Summary")),"readable status includes item, walking and mining state");
        var command=probe.MainModule.Types.Single(t=>t.Name=="ProbeCommand");
        Check(command.NestedTypes.SelectMany(t=>t.Methods).Any(m=>Calls(m,"StatusText")&&Calls(m,"Reply")),"queued status command explicitly replies with readable state");

        // Execute the packaged projection methods with real installed FNA types;
        // no Terraria Main initializer, world, graphics device or window is used.
        using var bytes=new MemoryStream();probe.Write(bytes);bytes.Position=0;
        var runtime=AssemblyLoadContext.Default.LoadFromStream(bytes);
        var ui=runtime.GetType(target.FullName);
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Static;
        var rectangle=ui.GetMethod("TargetRectangle",flags);var worldTile=ui.GetMethod("WorldTile",flags);
        var parameters=rectangle.GetParameters();var pointType=parameters[0].ParameterType;var vectorType=parameters[1].ParameterType;var matrixType=parameters[2].ParameterType;
        object Vector(float x,float y)=>Activator.CreateInstance(vectorType,new object[]{x,y});
        object Point(int x,int y)=>Activator.CreateInstance(pointType,new object[]{x,y});
        string XY(object p)=>p.GetType().GetField("X").GetValue(p)+","+p.GetType().GetField("Y").GetValue(p);
        string Box(object matrix,float scale,float sx=1500,float sy=760)
        {
            var b=rectangle.Invoke(null,new[]{Point(100,50),Vector(sx,sy),matrix,(object)scale});
            return XY(b)+","+b.GetType().GetField("Width").GetValue(b)+","+b.GetType().GetField("Height").GetValue(b);
        }
        var identity=matrixType.GetProperty("Identity").GetValue(null);
        var createScale=matrixType.GetMethod("CreateScale",new[]{typeof(float),typeof(float),typeof(float)});
        Check(Box(identity,1)=="100,40,16,16","actual overlay projection aligns a tile with the camera");
        Check(Box(identity,1,1516,776)=="84,24,16,16","camera movement moves only the displayed box");
        Check(Box(createScale.Invoke(null,new object[]{1.5f,1.5f,1f}),1.25f)=="120,48,19,19","game zoom and UI scale are applied in distinct spaces");
        Check(Box(createScale.Invoke(null,new object[]{1f,-1f,1f}),1)=="100,-56,16,16","flipped world transform retains positive target bounds");
        Check(XY(worldTile.Invoke(null,new[]{Vector(33680,3318)}))=="2105,207","world pixel position maps to the actual tile grid");
        Check(XY(worldTile.Invoke(null,new[]{Vector(-0.1f,-16.1f)}))=="-1,-2","outside-world negative mouse coordinates do not truncate into tile zero");
        Console.WriteLine($"Probe UX checks passed: {checks}; graphical overlay and live client display NOT EXECUTED.");
    }
}
