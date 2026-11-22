using Terraria;

namespace CompanionBridge.ServerIpc;

internal sealed record ExcavationCell(int X,int Y,int Type,string Shape,long Revision)
{
    internal static ExcavationCell Capture(int x,int y)
    {
        SceneMineTargets.Watch(x,y);
        return new(x,y,Main.tile[x,y].TileType,SceneMineTargets.NavigationShape(x,y),SceneMineTargets.Revision(x,y));
    }

    internal bool Matches()=>Main.tile[X,Y].HasTile&&SceneMineTargets.Revision(X,Y)==Revision
        &&SceneMineTargets.NavigationShape(X,Y)==Shape;
}
