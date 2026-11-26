using System;
using System.Collections.Generic;
using System.Linq;
using CompanionBridge.Motion;

namespace CompanionBridge.ServerIpc;

// The external destination remains a landing region. A work goal instead
// supplies the outstanding cells and prices the first safe strike at a pose.
internal sealed class TerrainRouteGoal
{
    internal readonly LandingRegion Region;
    internal readonly ExcavationCell[] Cells;
    private readonly AiReadOnlyProtocol.Identity identity;
    internal TerrainRouteGoal(LandingRegion region){Region=region;}
    internal TerrainRouteGoal(AiReadOnlyProtocol.Identity identity,ExcavationCell[] cells)
    {this.identity=identity;Cells=cells;}

    internal (float X,float Y) Aim(PhysicsState pose)
    {
        if(Region!=null)return ((Region.MinPlayerLeftX+Region.MaxPlayerLeftX)/2+10,Region.SurfaceY);
        var near=Cells.MinBy(c=>Math.Abs(c.X*16-pose.Px)+Math.Abs(c.Y*16-pose.Py));
        return (near.X*16+8,near.Y*16);
    }
    internal float Distance(PhysicsState pose)=>Region?.Distance(pose)??Cells.Min(c=>
        Math.Abs(c.X*16+8-pose.Px-10)+Math.Abs(c.Y*16+8-pose.Py-21));
    internal bool InWindow(PhysicsState pose)=>Region!=null
        ?Region.Distance(pose)<=18*16
        :Cells.Any(c=>Math.Abs(c.X*16-pose.Px)<=18*16&&Math.Abs(c.Y*16-pose.Py)<=14*16);
    internal IEnumerable<(ExcavationCell Cell,float Updates)> Quotes(PhysicsState pose,LocalTerrainView view,bool edited)
    {
        if(Region!=null)
        {
            if(Region.Contains(pose))yield return (null,0);
            yield break;
        }
        foreach(var cell in Cells)
        {
            if(edited&&!NativeNavigationEdits.SafeRemovalNeighbours(cell.X,cell.Y,view))continue;
            var quote=NativeExcavation.Choose(identity,pose,new[]{cell},view.Fork(),false);
            if(quote!=null)yield return (cell,quote.RunUpdates);
        }
    }
}
