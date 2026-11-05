// Native collision sequence adapted from TerraBlind PhysicsSimulator.cs (MIT).
using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using CompanionBridge.Common.Systems;

namespace CompanionBridge.Motion;

internal sealed class NativeMotionWorld : IMotionWorld
{
    private const int CollisionMargin = 32;
    private readonly int _left, _top;
    private readonly bool _captureContacts;
    public NavigationBlockingTile[] LastHorizontalContact {get;private set;}
    internal NativeMotionWorld(PhysicsState start,bool captureContacts=false)
    {
        _captureContacts=captureContacts;
        _left = (int)Math.Floor((start.Px + 10) / 16) - 22;
        _top = (int)Math.Floor((start.Py + 42) / 16) - 17;
    }

    private void Check(float left, float top, float right, float bottom, bool hazards)
    {
        for (int x = (int)Math.Floor(left / 16); x < (int)Math.Ceiling(right / 16); x++)
            for (int y = (int)Math.Floor(top / 16); y < (int)Math.Ceiling(bottom / 16); y++)
            {
                if (x < _left || x >= _left + 45 || y < _top || y >= _top + 29)
                    throw new UnsafeMotionException($"outside local prediction window at ({x},{y})");
                char cell = TerrainReader.ReadCell(x, y);
                if (cell == '?' || (hazards && cell == '!'))
                    throw new UnsafeMotionException($"unknown/hazard terrain at ({x},{y})");
            }
    }

    public bool SafeSweep(PhysicsState start, PhysicsState end, out string reason)
    {
        try
        {
            Check(Math.Min(start.Px, end.Px) - 2, Math.Min(start.Py, end.Py) - 2,
                Math.Max(start.Px, end.Px) + 22, Math.Max(start.Py, end.Py) + 44, true);
            reason = null;
            return true;
        }
        catch (UnsafeMotionException error) { reason = error.Message; return false; }
    }

    public PhysicsState Move(PhysicsState start, float vx, float vy, float gravity)
        => MoveCore(start,vx,vy,gravity,stepAssist:true);

    // A stationary downward probe asks whether native collision holds this
    // exact pose. Step assist could move the probe onto a different surface.
    private PhysicsState MoveCore(PhysicsState start,float vx,float vy,float gravity,bool stepAssist)
    {
        if(stepAssist)LastHorizontalContact=null;
        // Native helpers inspect neighbouring tiles. Reject unloaded/custom tiles
        // there before calling them, not merely after accepting a trajectory.
        if(!float.IsFinite(vx)||!float.IsFinite(vy)||Math.Abs(vx)>CollisionMargin||Math.Abs(vy)>CollisionMargin)
            throw new UnsafeMotionException("movement exceeds validated native collision neighbourhood");
        Check(start.Px - CollisionMargin, start.Py - CollisionMargin,
            start.Px + PhysicsSimulator.PlayerW + CollisionMargin,
            start.Py + PhysicsSimulator.PlayerH + CollisionMargin, false);
        var pos = new Vector2(start.Px, start.Py);
        var vel = new Vector2(vx, vy);
        float stepSpeed = 0, gfxOffY = 0;
        // Collision helpers mutate shared flags. Planning must not leak those
        // speculative contacts into the real player's update.
        var flags = (Collision.stair, Collision.stairFall, Collision.honey, Collision.shimmer,
            Collision.sloping, Collision.landMine, Collision.up, Collision.down);
        try
        {
            if(stepAssist)
            {
                var down = Collision.WalkDownSlope(pos, vel, 20, 42, gravity);
                pos = new(down.X, down.Y); vel = new(down.Z, down.W);
                if (vel.Y == gravity)
                    Collision.StepDown(ref pos, ref vel, 20, 42, ref stepSpeed, ref gfxOffY);
                if (vel.Y >= gravity)
                    Collision.StepUp(ref pos, ref vel, 20, 42, ref stepSpeed, ref gfxOffY);
            }
            var clipped = Collision.TileCollision(pos, vel, 20, 42, false, false, 1);
            if(stepAssist&&_captureContacts)LastHorizontalContact=NavigationContact.Horizontal(pos.X,pos.Y,vel.X,clipped.X,(x,y)=>
            {
                // This face lies inside the neighbourhood already checked above.
                // Unknown/out-of-window positions never yield content evidence.
                if(x<_left||x>=_left+45||y<_top||y>=_top+29)return ('?',null);
                char shape=TerrainReader.ReadCell(x,y);
                if(shape!='#')return (shape,null);
                int type=Main.tile[x,y].TileType;
                return (shape,TileLoader.GetTile(type)?.FullName??"Terraria/"+TileID.Search.GetName(type));
            });
            var slope = Collision.SlopeCollision(pos + clipped, clipped, 20, 42, gravity, false);
            bool grounded = vel.Y >= 0 && (slope.W == 0 || (vel.Y > 0 && Math.Abs(slope.W - vel.Y) > 0.01f));
            return new(slope.X, slope.Y, slope.Z, slope.W, grounded);
        }
        finally
        {
            (Collision.stair, Collision.stairFall, Collision.honey, Collision.shimmer,
                Collision.sloping, Collision.landMine, Collision.up, Collision.down) = flags;
        }
    }

    public bool SafeLanding(PhysicsState state)
        => LandingRejection(state) == null;

    internal static PhysicsState Observe(Player player)
    {
        var state=new PhysicsState(player.position.X,player.position.Y,player.velocity.X,player.velocity.Y,
            false,player.jump,player.releaseJump);
        if(player.width!=PhysicsSimulator.PlayerW||player.height!=PhysicsSimulator.PlayerH||player.gravDir!=1)
            return state;
        bool eligible=Math.Abs(state.Vy)<.01f||player.sloping&&state.Vy>=-.01f;
        return state with{Grounded=eligible&&new NativeMotionWorld(state).ContactRejection(state)==null};
    }

    private string ContactRejection(PhysicsState state)
    {
        const float probe=.125f;
        try
        {
            var contact=MoveCore(state,0,probe,probe,stepAssist:false);
            return contact.Grounded&&Math.Abs(contact.Py-state.Py)<.01f&&Math.Abs(contact.Vy)<.01f
                ?null:"no native foot contact";
        }
        catch(UnsafeMotionException error){return error.Message;}
    }

    public bool SafeFlatLanding(PhysicsState state) => SafeLanding(state)
        && MotionSupport.SafeFlatLanding(state, TerrainReader.ReadCell);

    public string LandingRejection(PhysicsState state)
    {
        if (!state.Grounded) return "not grounded";
        if (!SafeSweep(state, state, out string reason)) return reason;
        try { Check(state.Px, state.Py - 2, state.Px + 20, state.Py + 46, true); }
        catch (UnsafeMotionException error) { return error.Message; }
        return MotionSupport.ClearanceRejection(state,TerrainReader.ReadCell)??ContactRejection(state);
    }
}
