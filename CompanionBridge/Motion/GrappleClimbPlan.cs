using System;

namespace CompanionBridge.Motion;

// A vertical shaft beside a full-solid shelf. The anchor is separate from the
// shelf so the player can rise above its edge without pulling into the shelf.
internal sealed record GrappleClimbPlan(LandingRegion Landing,int AnchorX,int AnchorY,int Direction,
    int Left,int Top,int Right,int Bottom,long Terrain)
{
    internal const float Range=280; // Conservative subset of the basic hook's range.
    // The verified basic hook carries upward momentum through native detach.
    // Release below the rim so the player can steer before reaching the anchor.
    internal const int ReleaseBelowShelf=64;
    internal float AimX=>AnchorX*16+8;
    internal float AimY=>AnchorY*16+8;
    internal bool Unchanged(ISurfaceTerrain terrain)=>Stamp(terrain,Left,Top,Right,Bottom)==Terrain;

    internal static GrappleClimbPlan Find(PhysicsState start,LandingRegion landing,
        PhysicsParameters physics,IMotionWorld world,ISurfaceTerrain terrain)
    {
        if(physics.Air!=null||physics.ExtraJump!=null
            ||(!physics.IsBasic&&!physics.Matches(PhysicsParameters.Swiftness))
            ||!PhysicsSimulator.Settled(start,world))return null;
        float feet=start.Py+PhysicsSimulator.PlayerH;
        float rise=feet-landing.SurfaceY;
        float goal=(landing.MinPlayerLeftX+landing.MaxPlayerLeftX)/2;
        int direction=goal>start.Px?1:-1;
        float distance=Math.Abs(goal-start.Px);
        if(rise<144||rise>192||distance<32||distance>112||landing.SurfaceY%16!=0)return null;
        var rest=new PhysicsState(goal,landing.SurfaceY-PhysicsSimulator.PlayerH,0,0,true);
        if(!landing.Arrived(rest,world)||!world.SafeFlatLanding(rest))return null;

        int anchorX=(int)Math.Floor((start.Px+10)/16);
        // Avoid meaningful sideways pull before the handoff at the ledge.
        if(Math.Abs(anchorX*16+8-start.Px-10)>2)return null;
        int shelf=(int)(landing.SurfaceY/16);
        for(int anchorY=shelf-9;anchorY<=shelf-7;anchorY++)
        {
            var anchor=terrain.ReadSurfaceCell(anchorX,anchorY);
            if(start.Py+21-(anchorY*16+8)>Range||anchor.Shape!='#'||anchor.Liquid.Amount>0)continue;
            int shaftLeft=(int)Math.Floor((start.Px-3)/16),shaftRight=(int)Math.Floor((start.Px+22)/16);
            int left=Math.Min(shaftLeft,(int)Math.Floor((goal-16)/16));
            int right=Math.Max(shaftRight,(int)Math.Floor((goal+36)/16));
            int bottom=(int)Math.Ceiling(feet/16)-1;
            bool clear=true;
            for(int x=left;x<=right;x++)
                for(int y=anchorY;y<=bottom;y++)
                {
                    var cell=terrain.ReadSurfaceCell(x,y);
                    if(y==anchorY&&x==anchorX)continue;
                    bool openRequired=y<shelf||x>=shaftLeft&&x<=shaftRight;
                    if(cell.Liquid.Amount>0||(openRequired?cell.Shape!='.':cell.Shape is not ('.' or '#')))clear=false;
                }
            if(!clear)continue;
            int supportLeft=(int)Math.Floor(goal/16),supportRight=(int)Math.Ceiling((goal+20)/16)-1;
            for(int x=supportLeft;x<=supportRight;x++)
                if(terrain.ReadSurfaceCell(x,shelf).Shape!='#')clear=false;
            if(clear)return new(landing,anchorX,anchorY,direction,left,anchorY,right,bottom,
                Stamp(terrain,left,anchorY,right,bottom));
        }
        return null;
    }

    internal Prediction Tail(PhysicsState actual,PhysicsParameters physics,IMotionWorld world)
    {
        // Use the actual detach velocity, including a collision clip if one
        // happened. Simulate horizontal input followed by a full braking tail.
        if(actual.Grounded||actual.Vy>=0)return null;
        for(int stride=8;stride<=48;stride+=2)
        {
            var edge=PhysicsSimulator.Predict(actual,Direction,0,physics,world,walkFrames:stride,maxDropPixels:32);
            if(edge.Safe&&Landing.Arrived(edge.Landing,world))return edge with {ExecuteFrames=edge.Frames.Count};
        }
        return null;
    }

    private static long Stamp(ISurfaceTerrain terrain,int left,int top,int right,int bottom)
    {
        unchecked
        {
            long stamp=17;
            for(int x=left;x<=right;x++)
                for(int y=top;y<=bottom;y++)stamp=stamp*31+terrain.ReadSurfaceCell(x,y).Signature;
            return stamp;
        }
    }
}
