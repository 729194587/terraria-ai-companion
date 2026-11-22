using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace VirtualPlayerEngineProbe;

// Exact pinned Player placement graph for ordinary tiles and TileObject placement. Original
// native rules remain intact. Only coordinate reads gain a checked Player scope.
public static class PlacementPatches
{
    public static readonly IReadOnlyDictionary<string,int> Reads=new Dictionary<string,int>{
        ["PlaceThing_Tiles"]=12,["PlaceThing_TryReplacingTiles"]=28,
        ["PlaceThing_ValidTileForReplacement"]=12,["PlaceThing_CheckSpecificValidtyCaseForBlockSwap"]=3,
        ["PlaceThing_Tiles_PlaceIt"]=16,["PlaceThing_Tiles_PlaceIt_UnslopeForSolids"]=4,
        ["PlaceThing_Tiles_PlaceIt_KillGrassForSolids"]=4,["PlaceThing_Tiles_PlaceIt_AutoPaintAndActuate"]=6,
        ["PlaceThing_Tiles_PlaceIt_SpinSmartPlatform"]=6,["PlaceThing_Tiles_PlaceIt_TriggerLogicLamp"]=4,
        ["PlaceThing_Tiles_PlaceIt_SpinBedsAndBaths"]=2,["PlaceThing_Tiles_PlaceIt_SpinChairs"]=6,
        ["PlaceThing_Tiles_PlaceIt_SpinTraps"]=4,["PlaceThing_Tiles_PlaceIt_AdjustPlants"]=4,
        ["PlaceThing_Tiles_PlaceIt_StaffOfRegrowthCheck"]=20,
        ["PlaceThing_Tiles_BlockPlacementForAssortedThings"]=210,
        ["PlaceThing_Tiles_CheckLavaBlocking"]=6
    };
    private static bool Target(Instruction i)=>i.Operand is FieldReference f
        &&f.DeclaringType.FullName=="Terraria.Player"&&f.Name is "tileTargetX" or "tileTargetY";
    private static void Require(bool ok,string detail){if(!ok)throw new InvalidOperationException("Unsupported native placement IL; installation refused: "+detail);}
    public static void Validate(MethodDefinition m)
    {
        const string prefix="Terraria.Player::";
        string name=m.Name.StartsWith(prefix,StringComparison.Ordinal)?m.Name.Substring(prefix.Length):m.Name;
        bool instance=m.HasThis&&m.DeclaringType.FullName=="Terraria.Player"&&m.Name==name;
        bool lifted=!m.HasThis&&m.Name==prefix+name&&m.Parameters.Count>0&&m.Parameters[0].ParameterType.FullName=="Terraria.Player";
        Require(Reads.TryGetValue(name,out int count)&&(instance||lifted)
            &&m.HasBody&&m.Body.ExceptionHandlers.Count==(name=="PlaceThing_Tiles_PlaceIt_StaffOfRegrowthCheck"?2:0)&&m.Body.Instructions.Count(Target)==count
            &&m.Body.Instructions.Where(Target).All(i=>i.OpCode==OpCodes.Ldsfld),m.FullName);
        bool Call(int offset,string name)=>m.Body.Instructions.Any(i=>i.Offset==offset&&i.Operand is MethodReference r&&r.Name==name);
        if(name=="PlaceThing_Tiles")Require(Call(0x02ca,"CanPlace")&&Call(0x030a,"CanPlace")&&Call(0x0315,"PlaceThing_Tiles_BlockPlacementIfOverPlayers")&&Call(0x034b,"PlaceThing_Tiles_PlaceIt")
            &&Call(0x033a,"PlaceThing_Tiles_BlockPlacementForAssortedThings")
            &&Call(0x012d,"get_TileReplacementEnabled"),m.FullName+" dispatch anchors");
        if(name=="PlaceThing_Tiles_PlaceIt")Require(Call(0x006e,"Place")&&Call(0x00e8,"PlaceTile")&&Call(0x0114,"ApplyItemTime")&&Call(0x0231,"PlaceInWorld"),m.FullName+" placement anchors");
    }
    public static void Patch(MethodDefinition m,MethodReference getX,MethodReference getY,MethodReference replacement=null)
    {
        Validate(m);var il=m.Body.GetILProcessor();
        if(replacement!=null)
        {
            var gate=m.Body.Instructions.Single(i=>i.Offset==0x012d);
            gate.OpCode=OpCodes.Call;gate.Operand=replacement;
        }
        foreach(var i in m.Body.Instructions.Where(Target).ToArray())
        {
            string name=((FieldReference)i.Operand).Name;
            // Keep the original instruction as every branch/EH entry. No branch
            // can bypass the Player argument or leave an invalid evaluation stack.
            i.OpCode=OpCodes.Ldarg_0;i.Operand=null;
            il.InsertAfter(i,Instruction.Create(OpCodes.Call,name=="tileTargetX"?getX:getY));
        }
    }

    internal const string RopeMethod="PlaceThing_Tiles_CheckRopeUsability";
    internal static void ValidateRope(MethodDefinition m)
    {
        bool instance=m.HasThis&&m.DeclaringType.FullName=="Terraria.Player"&&m.Name==RopeMethod&&m.Parameters.Count==1;
        bool lifted=!m.HasThis&&m.Name=="Terraria.Player::"+RopeMethod&&m.Parameters.Count==2
            &&m.Parameters[0].ParameterType.FullName=="Terraria.Player";
        Require((instance||lifted)&&m.Parameters[^1].ParameterType.MetadataType==MetadataType.Boolean
            &&m.ReturnType.MetadataType==MetadataType.Boolean&&m.HasBody,m.FullName);
        var fields=m.Body.Instructions.Where(Target).ToArray();
        Require(
            m.Body.ExceptionHandlers.Count==0&&fields.Length==8
            &&fields.Count(i=>i.OpCode==OpCodes.Ldsfld&&((FieldReference)i.Operand).Name=="tileTargetX")==3
            &&fields.Count(i=>i.OpCode==OpCodes.Ldsfld&&((FieldReference)i.Operand).Name=="tileTargetY")==4
            &&fields.Count(i=>i.OpCode==OpCodes.Stsfld&&((FieldReference)i.Operand).Name=="tileTargetY")==1,
            m.FullName+" rope coordinate contract");
    }

    internal static void PatchRope(MethodDefinition m,MethodReference getX,MethodReference getY,MethodReference setY)
    {
        ValidateRope(m);
        var il=m.Body.GetILProcessor();
        foreach(var i in m.Body.Instructions.Where(Target).ToArray())
        {
            var method=i.OpCode==OpCodes.Stsfld?setY:((FieldReference)i.Operand).Name=="tileTargetX"?getX:getY;
            // The native store already has the new Y on the stack.
            i.OpCode=OpCodes.Ldarg_0;i.Operand=null;
            il.InsertAfter(i,Instruction.Create(OpCodes.Call,method));
        }
    }
}
