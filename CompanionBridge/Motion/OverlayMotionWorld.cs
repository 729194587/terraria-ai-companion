using System;
using System.Collections.Generic;

namespace CompanionBridge.Motion;

// Dry full-block contact only. Step assist and diagonal corner contacts are
// deliberately outside this model. Reject those edges before they can be priced.
internal sealed class OverlayMotionWorld(LocalTerrainView view, Func<TerrainCell,bool> supported) : IMotionWorld
{
    internal readonly Dictionary<(int X,int Y),TerrainCell> Dependencies=new();

    private char Read(int x,int y)
    {
        var cell=view.Environment(x,y);
        Dependencies[(x,y)]=cell;
        if(cell.Shape is not ('.' or '#')||cell.Liquid.Amount!=0||!supported(cell))
            throw new UnsafeMotionException($"unsupported overlay terrain ({x},{y})");
        return cell.Shape;
    }

    public bool SafeSweep(PhysicsState from,PhysicsState to,out string reason)
    {
        try
        {
            for(int x=Tile(Math.Min(from.Px,to.Px)-2);x<End(Math.Max(from.Px,to.Px)+22);x++)
            for(int y=Tile(Math.Min(from.Py,to.Py)-2);y<End(Math.Max(from.Py,to.Py)+44);y++)
                Read(x,y);
            reason=MotionSupport.ClearanceRejection(to,Read);
            return reason==null;
        }
        catch(UnsafeMotionException error){reason=error.Message;return false;}
    }

    public bool SafeLanding(PhysicsState pose)=>SafeSweep(pose,pose,out _)
        &&MotionSupport.SafeFlatLanding(pose,Read);

    private static int Tile(float px)=>(int)Math.Floor(px/16);
    private static int End(float px)=>(int)Math.Ceiling(px/16);

    public PhysicsState Move(PhysicsState start,float vx,float vy,float gravity)
    {
        if(!float.IsFinite(vx)||!float.IsFinite(vy)||Math.Abs(vx)>=16||Math.Abs(vy)>=16)
            throw new UnsafeMotionException("unsupported overlay velocity");
        float px=start.Px+vx;
        int left=Tile(px),right=End(px+PhysicsSimulator.PlayerW);
        // Any side collision might invoke StepUp. Only certify clear horizontal travel.
        for(int x=left;x<right;x++)
        for(int y=Tile(start.Py);y<End(start.Py+PhysicsSimulator.PlayerH);y++)
            if(Read(x,y)=='#')throw new UnsafeMotionException("blocked overlay movement");
        float feet=start.Py+PhysicsSimulator.PlayerH;
        // StepDown snaps a stationary player to a full floor one tile below
        // on the first gravity update after its last support has been mined.
        if(vx==0&&start.Vy==0&&vy==gravity&&feet==Tile(feet)*16)
        {
            bool held=false,lower=false;
            for(int x=left;x<right;x++)
            {
                held|=Read(x,Tile(feet))=='#';
                lower|=Read(x,Tile(feet)+1)=='#';
            }
            if(!held&&lower)return new(px,start.Py+16,0,0,true);
        }
        float dy=vy;
        bool contact=false;
        for(int x=left;x<right;x++)
        {
            if(vy>0)
            {
                int row=Tile(feet+vy);
                if(row*16>=feet&&Read(x,row)=='#')
                {
                    // A newly entered column at a simultaneous corner may
                    // resolve horizontally instead in Collision.TileCollision.
                    if(x<Tile(start.Px)||x>=End(start.Px+PhysicsSimulator.PlayerW))continue;
                    dy=row*16-feet;
                    contact=dy<vy;
                }
            }
            else if(vy<0 && Tile(start.Py+vy)<End(start.Py) && Read(x,Tile(start.Py+vy))=='#')
                throw new UnsafeMotionException("unsupported overlay ceiling contact");
        }
        if(start.Grounded&&vx!=0&&vy>0&&!contact)
            throw new UnsafeMotionException("unsupported native step down");
        return new(px,start.Py+dy,vx,dy,contact);
    }
}
