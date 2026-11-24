using System;
using System.Linq;
using System.Collections.Generic;

namespace CompanionBridge.Motion;

// A vertical shaft beside a full-solid shelf. The anchor is separate from the
// shelf so the player can rise above its edge without pulling into the shelf.
internal sealed record GrappleClimbPlan(LandingRegion Landing,int AnchorX,int AnchorY,int Direction,
    int Left,int Top,int Right,int Bottom,long Terrain)
{
    internal Prediction Approach { get; init; }
    internal const float Range=280; // Conservative subset of the basic hook's range.
    // The verified basic hook carries upward momentum through native detach.
    // Release below the rim so the player can steer before reaching the anchor.
    internal const int ReleaseBelowShelf=64;
    internal float AimX=>AnchorX*16+8;
    internal float AimY=>AnchorY*16+8;
    internal bool Unchanged(ISurfaceTerrain terrain)=>Stamp(terrain,Left,Top,Right,Bottom)==Terrain;

    internal static GrappleClimbPlan Find(PhysicsState start,LandingRegion landing,
        PhysicsParameters physics,IMotionWorld world,ISurfaceTerrain terrain)
        =>Options(start,landing,physics,world,terrain).FirstOrDefault(p=>p!=null);

    // For a distant destination, climb onto an observed shelf first. Navigation
    // resumes from the verified landing and may walk on or climb another shelf.
    internal static IEnumerable<GrappleClimbPlan> Toward(PhysicsState start,LandingRegion goal,
        PhysicsParameters physics,IMotionWorld world,ISurfaceTerrain terrain)
    {
        foreach(var option in Options(start,goal,physics,world,terrain))yield return option;
        int feet=(int)Math.Round((start.Py+42)/16);
        var targetX=(goal.MinPlayerLeftX+goal.MaxPlayerLeftX)/2;
        int dir=targetX>=start.Px?1:-1;
        if(feet*16-goal.SurfaceY<144 || feet*16-goal.SurfaceY<=192 && Math.Abs(targetX-start.Px)<=112)
            yield break;
        int column=(int)Math.Floor((start.Px+10)/16);
        foreach(int step in new[]{dir,-dir})
        for(int rise=12;rise>=9;rise--)
        {
            yield return null;
            int row=feet-rise;
            for(int offset=4;offset<=7;offset++)
            {
                int x=column+step*offset;
                if(terrain.ReadSurfaceCell(x,row).Shape!='#'||terrain.ReadSurfaceCell(x,row-1).Shape!='.')continue;
                var shelf=LandingRegion.AtDestination(x*16+8,row*16);
                var rest=start with{Px=x*16-2,Py=row*16-42};
                if(goal.Distance(rest)>=goal.Distance(start)-16)continue;
                foreach(var option in Options(start,shelf,physics,world,terrain))yield return option;
                break;
            }
        }
    }

    // Null yields mark resumable work. Every non-null option has a collision-
    // checked approach, pull preview and supported landing.
    internal static IEnumerable<GrappleClimbPlan> Options(PhysicsState start,LandingRegion landing,
        PhysicsParameters physics,IMotionWorld world,ISurfaceTerrain terrain)
    {
        if(physics.GroundOnly||physics.Air?.Kind==AirKind.RocketBoots||(!physics.IsBasic&&!physics.Matches(PhysicsParameters.Swiftness))
            ||start.Py+42-landing.SurfaceY is <144 or >192||!PhysicsSimulator.Settled(start,world))yield break;
        foreach(var plan in FromLaunch(start,landing,physics,world,terrain))yield return plan;
        int toward=Math.Sign((landing.MinPlayerLeftX+landing.MaxPlayerLeftX)/2-start.Px);
        foreach(int stride in new[]{12,24,36})
        foreach(int dir in new[]{toward,-toward})
        {
            if(dir==0)continue;
            yield return null;
            var walk=PhysicsSimulator.Predict(start,dir,0,physics,world,walkFrames:stride);
            if(!walk.Safe||!PhysicsSimulator.Settled(walk.Landing,world)
                ||walk.Frames.Exists(f=>!f.State.Grounded||Math.Abs(f.State.Py-start.Py)>.1f))continue;
            foreach(var plan in FromLaunch(walk.Landing,landing,physics,world,terrain))
                yield return plan==null?null:plan with{Approach=walk with{ExecuteFrames=walk.Frames.Count}};
        }
    }

    private static IEnumerable<GrappleClimbPlan> FromLaunch(PhysicsState start,LandingRegion landing,
        PhysicsParameters physics,IMotionWorld world,ISurfaceTerrain terrain)
    {
        float feet=start.Py+PhysicsSimulator.PlayerH;
        float rise=feet-landing.SurfaceY;
        float goal=(landing.MinPlayerLeftX+landing.MaxPlayerLeftX)/2;
        int direction=goal>start.Px?1:-1;
        float distance=Math.Abs(goal-start.Px);
        if(rise<144||rise>192||distance<32||distance>112||landing.SurfaceY%16!=0)yield break;
        var rest=new PhysicsState(goal,landing.SurfaceY-PhysicsSimulator.PlayerH,0,0,true);
        if(!landing.Arrived(rest,world)||!world.SafeFlatLanding(rest))yield break;

        int centerX=(int)Math.Floor((start.Px+10)/16);
        int shelf=(int)(landing.SurfaceY/16);
        foreach(int offset in new[]{0,direction,-direction,2*direction,-2*direction})
        for(int anchorY=shelf-9;anchorY<=shelf-5;anchorY++)
        {
            int anchorX=centerX+offset;
            var anchor=terrain.ReadSurfaceCell(anchorX,anchorY);
            float dx=anchorX*16+8-start.Px-10,dy=anchorY*16+8-start.Py-21;
            if(dx*dx+dy*dy>Range*Range||anchor.Shape!='#'||anchor.Liquid.Amount>0)continue;
            yield return null;
            int shaftLeft=(int)Math.Floor((start.Px-3)/16),shaftRight=(int)Math.Floor((start.Px+22)/16);
            int left=Math.Min(anchorX-1,Math.Min(shaftLeft,(int)Math.Floor((goal-16)/16)));
            int right=Math.Max(anchorX+1,Math.Max(shaftRight,(int)Math.Floor((goal+36)/16)));
            int bottom=(int)Math.Ceiling(feet/16)-1;
            if(!ClearShot(start,anchorX,anchorY,terrain,null))continue;
            bool clear=true;
            int supportLeft=(int)Math.Floor(goal/16),supportRight=(int)Math.Ceiling((goal+20)/16)-1;
            for(int x=supportLeft;x<=supportRight;x++)
                if(terrain.ReadSurfaceCell(x,shelf).Shape!='#')clear=false;
            if(!clear)continue;
            var candidate=new GrappleClimbPlan(landing,anchorX,anchorY,direction,left,anchorY,right,bottom,
                Stamp(terrain,left,anchorY,right,bottom));
            if(candidate.Preview(start,physics,world,null)!=null)yield return candidate;
        }
    }

    // Conservatively sweep a hook-sized lane to the nominated tile. Another
    // solid, platform or unknown tile in front changes the first hit.
    internal static bool ClearShot(PhysicsState start,int ax,int ay,ISurfaceTerrain terrain,Func<int,int,bool> known)
    {
        float x=start.Px+10,y=start.Py+21,dx=ax*16+8-x,dy=ay*16+8-y;
        int steps=Math.Max(1,(int)Math.Ceiling(Math.Max(Math.Abs(dx),Math.Abs(dy))/4));
        for(int i=0;i<=steps;i++)
        {
            float px=x+dx*i/steps,py=y+dy*i/steps;
            for(int tx=(int)Math.Floor((px-5)/16);tx<=(int)Math.Floor((px+5)/16);tx++)
            for(int ty=(int)Math.Floor((py-5)/16);ty<=(int)Math.Floor((py+5)/16);ty++)
            {
                if(known!=null&&!known(tx,ty))return false;
                var cell=terrain.ReadSurfaceCell(tx,ty);
                if(cell.Liquid.Amount>0||cell.Shape!=(tx==ax&&ty==ay?'#':'.'))return false;
            }
        }
        return true;
    }

    // Basic hook pulls toward the attached tile centre at 11 px/update. The
    // executor measures the actual attachment and replans the detach tail.
    internal Prediction Preview(PhysicsState start,PhysicsParameters physics,IMotionWorld world,
        Func<PhysicsState,bool> allowed)
    {
        var s=Approach?.Landing??start;
        if(Approach!=null&&allowed!=null&&Approach.Frames.Exists(f=>!allowed(f.State)))return null;
        try
        {
            for(int tick=0;tick<32;tick++)
            {
                float dx=AimX-(s.Px+10),dy=AimY-(s.Py+21);
                float length=MathF.Sqrt(dx*dx+dy*dy);
                if(length<11)return null;
                var next=world.Move(s,11*dx/length,11*dy/length,1);
                if(!world.SafeSweep(s,next,out _)||allowed!=null&&!allowed(next)||next.Py>=s.Py)return null;
                s=next with {Air=s.Air};
                float feet=s.Py+42;
                if(feet>Landing.SurfaceY+ReleaseBelowShelf)continue;
                var release=world.Move(s,s.Vx,s.Vy+.01f,1) with {Air=s.Air};
                if(world.SafeSweep(s,release,out _)&&(allowed==null||allowed(release))
                    &&Tail(release,physics,(world as ISurfaceTerrain)?.MotionAt(release)??world) is {} tail
                    &&(allowed==null||!tail.Frames.Exists(f=>!allowed(f.State))))return tail;
                if(feet<Landing.SurfaceY+40)return null;
            }
        }
        catch(UnsafeMotionException){}
        return null;
    }

    internal Prediction Tail(PhysicsState actual,PhysicsParameters physics,IMotionWorld world)
    {
        // Use the actual detach velocity, including a collision clip if one
        // happened. Simulate horizontal input followed by a full braking tail.
        if(actual.Grounded||actual.Vy>=0)return null;
        float goal=(Landing.MinPlayerLeftX+Landing.MaxPlayerLeftX)/2;
        float x=actual.Px,v=actual.Vx*Direction;
        var strides=new (int Frames,float Error)[48];
        // Order horizontal input durations by the unobstructed braking point.
        // Only the native collision prediction below can approve a duration.
        for(int i=0;i<strides.Length;i++)
        {
            if(v<0)v+=physics.RunSlowdown;
            if(v<physics.MaxRun)v+=physics.AccRun;
            x+=Direction*v;
            int brake=(int)Math.Max(0,Math.Ceiling(Math.Abs(v)/physics.RunSlowdown)-1);
            float stopping=brake*v-Math.Sign(v)*physics.RunSlowdown*brake*(brake+1)/2;
            strides[i]=(i+1,Math.Abs(x+Direction*stopping-goal));
        }
        foreach(int stride in strides.OrderBy(s=>s.Error).Take(8).Select(s=>s.Frames))
        {
            var edge=PhysicsSimulator.Predict(actual,Direction,0,physics,world,walkFrames:stride);
            if(!edge.Safe)continue;
            if(Landing.Arrived(edge.Landing,world))return edge with {ExecuteFrames=edge.Frames.Count};
            // A precise exploration anchor may fall between two airborne
            // steering durations. Land on the shelf and verify the final steps.
            if(Math.Abs(edge.Landing.Py+42-Landing.SurfaceY)>.5f||Landing.Distance(edge.Landing)>12)continue;
            var align=SafeGroundWalk.TryPlan(edge.Landing,goal,goal>edge.Landing.Px?1:-1,physics,world,Landing);
            if(align?.ReachedGoal==true)
            {
                var adjustment=align.Actions[0];
                var frames=edge.Frames.Concat(adjustment.Frames).ToList();
                return edge with{Frames=frames,ExecuteFrames=frames.Count,Landing=adjustment.Landing};
            }
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
