using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using CompanionBridge.Common.Systems;

namespace CompanionBridge.ServerIpc;

// World-local reservations use inclusive tile coordinates. Traversal never consults them.
public sealed class NavigationProtection : ModSystem
{
    internal readonly record struct Area(int Left,int Top,int Right,int Bottom);
    private static readonly HashSet<Area> areas=new();
    private static readonly Dictionary<int,List<Area>> columns=new();
    internal static IEnumerable<Area> Areas=>areas;
    internal static bool Reserve(Area area)
    {
        if(area.Left>area.Right||area.Top>area.Bottom||!WorldGen.InWorld(area.Left,area.Top,0)
            ||!WorldGen.InWorld(area.Right,area.Bottom,0)||!areas.Add(area))return false;
        for(int col=area.Left/64;col<=area.Right/64;col++)
        {
            if(!columns.TryGetValue(col,out var list))columns[col]=list=new();
            list.Add(area);
        }
        return true;
    }
    internal static bool Remove(Area area)
    {
        if(!areas.Remove(area))return false;
        for(int col=area.Left/64;col<=area.Right/64;col++)columns[col].Remove(area);
        return true;
    }
    public override void ClearWorld(){areas.Clear();columns.Clear();}
    public override void SaveWorldData(TagCompound tag)=>tag["navigationReservations"]=areas.Select(a=>
        new TagCompound{{"left",a.Left},{"right",a.Right},{"top",a.Top},{"bottom",a.Bottom}}).ToList();
    public override void LoadWorldData(TagCompound tag)
    {
        foreach(var a in tag.GetList<TagCompound>("navigationReservations"))
            Reserve(new(a.GetInt("left"),a.GetInt("top"),a.GetInt("right"),a.GetInt("bottom")));
    }

    internal static bool Reserved(int x,int y)
    {
        if(!WorldGen.InWorld(x,y,2)||AutonomyPolicy.ProtectedTile(x,y))return true;
        if(columns.TryGetValue(x/64,out var list))
            foreach(var a in list)
                if(x>=a.Left&&x<=a.Right&&y>=a.Top&&y<=a.Bottom)return true;
        // Housing-safe background walls identify the interior and its immediate shell,
        // even before anyone inspects the room. Leave both sides of a door usable.
        for(int dx=-1;dx<=1;dx++)
        for(int dy=-1;dy<=1;dy++)
        {
            if(Math.Abs(dx)+Math.Abs(dy)>1)continue;
            var t=Main.tile[x+dx,y+dy];
            if(t.WallType!=0&&Main.wallHouse[t.WallType]||
                t.HasTile&&t.TileType is TileID.ClosedDoor or TileID.OpenDoor)return true;
        }
        return false;
    }
    internal static bool NaturalObstacle(int type)=>type is TileID.Dirt or TileID.Stone or TileID.Grass
        or TileID.Mud or TileID.ClayBlock or TileID.SnowBlock or TileID.IceBlock or TileID.Sandstone
        or TileID.HardenedSand or TileID.JungleGrass or TileID.CorruptGrass or TileID.CrimsonGrass
        or TileID.HallowedGrass or TileID.Ebonstone or TileID.Crimstone or TileID.Pearlstone;
    internal static bool CanCutNow(int x,int y)=>!Reserved(x,y)&&Main.tile[x,y].HasUnactuatedTile
        &&NaturalObstacle(Main.tile[x,y].TileType)
        &&NativeNavigationEdits.SafeRemovalNeighbours(x,y,new(TerrainReader.ReadEnvironment));
}

public sealed class NavigationProtectionCommand : ModCommand
{
    public override CommandType Type=>CommandType.Console;
    public override string Command=>"navprotect";
    public override string Usage=>"navprotect list | add/remove left top right bottom";
    public override string Description=>"Reserve world tile rectangles against navigation edits.";
    public override void Action(CommandCaller caller,string input,string[] args)
    {
        if(args.Length==1&&args[0]=="list")
        {
            caller.Reply($"Navigation reservations ({NavigationProtection.Areas.Count()})");
            foreach(var a in NavigationProtection.Areas)caller.Reply($"{a.Left} {a.Top} {a.Right} {a.Bottom}");
            return;
        }
        if(args.Length!=5||args[0] is not ("add" or "remove")||!int.TryParse(args[1],out int l)
            ||!int.TryParse(args[2],out int t)||!int.TryParse(args[3],out int r)||!int.TryParse(args[4],out int b))
        {caller.Reply(Usage);return;}
        var area=new NavigationProtection.Area(l,t,r,b);
        bool changed=args[0]=="add"?NavigationProtection.Reserve(area):NavigationProtection.Remove(area);
        caller.Reply(changed?"Navigation reservation updated.":"No reservation changed.");
    }
}
