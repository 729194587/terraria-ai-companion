using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using CompanionBridge.ServerIpc;

namespace VirtualPlayerEngineProbe;

internal static class OverlayMotionSelfTest
{
    private sealed class Replay(Player actor) : ActorControl.IMovementDriver
    {
        internal PhysicsInput Input;
        public bool Running=>true;
        public bool Matches(Player p)=>p==actor;
        public void Update(On_Player.orig_Update orig,Player p,int slot)=>orig(p,slot);
        public void BeforeMovement(Player p)
        {
            p.controlRight=Input.Direction>0;p.controlLeft=Input.Direction<0;
            p.controlJump=Input.Jump;p.controlDown=false;
        }
        public void Retire(Player p){}
    }

    internal static object Run(Player p,string world,long generation,int origin,int y,Action frame,Action<bool,string> check)
    {
        var startPosition=p.position;
        var driver=ActorControl.Driver;
        var replay=new Replay(p);
        int x=origin+10;
        var rows=new List<object>();
        void Set(int tx,int ty,int type)
        {
            var t=Main.tile[tx,ty];t.ClearEverything();
            if(type>=0){t.HasTile=true;t.TileType=(ushort)type;}
        }
        try
        {
            ActorControl.Driver=replay;
            foreach(string action in new[]{"walk","walk_decor","jump","step_down","fall"})
            {
                for(int tx=x-4;tx<=x+16;tx++)
                for(int ty=y-14;ty<=y+3;ty++)Set(tx,ty,ty>=y?TileID.Stone:-1);
                bool fall=action is "step_down" or "fall";
                var cuts=action=="fall"?new[]{(x,y),(x+1,y),(x,y+1),(x+1,y+1),(x,y+2),(x+1,y+2)}:
                    fall?new[]{(x,y),(x+1,y)}:new[]{(x+2,y-2)};
                foreach(var (tx,ty) in cuts)Set(tx,ty,TileID.Dirt);
                if(action=="walk_decor")
                {
                    Set(x,y-1,TileID.LargePiles);
                    Set(x+2,y-1,TileID.ImmatureHerbs);
                    Set(x+3,y-1,TileID.Pots);
                }
                p.position=new Vector2(x*16,y*16-42);p.velocity=Vector2.Zero;
                p.releaseJump=true;p.jump=0;replay.Input=default;frame();
                var start=NativeMotionWorld.Observe(p);
                var native=new NativeMotionWorld(start);
                long before=native.TerrainSignature(start);
                var view=new LocalTerrainView(TerrainReader.ReadEnvironment);
                var ph=NativeNavigation.Parameters(p);
                var overlay=new OverlayMotionWorld(view,TerrainReader.OrdinaryOverlayCell);
                Prediction Predict()=>PhysicsSimulator.Predict(start,fall?0:1,action=="jump"?5:0,ph,overlay,walkFrames:fall?0:24);
                if(!fall && action=="walk")check(!Predict().Safe,"Walk crossed the uncut wall.");
                foreach(char unsupported in new[]{'h','1','2','3','4','=','?','!'})
                {
                    var unsupportedWorld=new OverlayMotionWorld(new LocalTerrainView((tx,ty)=>
                        tx==x && ty==y?unsupported:view.Cell(tx,ty)),_=>true);
                    check(!unsupportedWorld.SafeLanding(start),"Unsupported overlay support: "+unsupported);
                }
                foreach(var (tx,ty) in cuts)view.Set(tx,ty,'.');
                var prediction=Predict();
                check(prediction.Safe && before==native.TerrainSignature(start),"Overlay failed read-only "+action+": "+prediction.Reason);
                int oreX=(int)(prediction.Landing.Px/16)+4,oreY=y-2;
                bool reach=ActorMining.QueryReach(p,world,generation,p.inventory[0],oreX,oreY,prediction.Landing.Px,prediction.Landing.Py);
                // Construct the post-edit collision fixture only after the read-only check.
                foreach(var (tx,ty) in cuts)Set(tx,ty,-1);
                float positionError=0,velocityError=0;
                int life=p.statLife;
                foreach(var expected in prediction.Frames)
                {
                    replay.Input=expected.Input;frame();
                    positionError=Math.Max(positionError,Math.Max(Math.Abs(p.position.X-expected.State.Px),Math.Abs(p.position.Y-expected.State.Py)));
                    velocityError=Math.Max(velocityError,Math.Max(Math.Abs(p.velocity.X-expected.State.Vx),Math.Abs(p.velocity.Y-expected.State.Vy)));
                }
                var actual=NativeMotionWorld.Observe(p);
                var goal=new LandingRegion(prediction.Landing.Px-2,prediction.Landing.Px+2,prediction.Landing.Py+42);
                check(positionError<=6 && velocityError<=1.5f && goal.Arrived(actual,native) && life<=p.statLife
                    &&reach&&ActorMining.QueryReach(p,world,generation,p.inventory[0],oreX,oreY,actual.Px,actual.Py),
                    $"Overlay/native {action}: error={positionError}/{velocityError}; expected={prediction.Landing}; actual={actual}");
                rows.Add(new{action,frames=prediction.Frames.Count,positionError,velocityError,dependencies=overlay.Dependencies.Count,reach});
            }
        }
        finally
        {
            // Restore the ore strip used by the subsequent batch regression.
            for(int dx=6;dx<=26;dx++)
            {
                Set(origin+dx,y,TileID.Stone);
                Set(origin+dx,y-2,dx<=22?TileID.Iron:-1);
            }
            p.position=startPosition;p.velocity=Vector2.Zero;p.releaseJump=true;p.jump=0;
            p.controlJump=p.controlLeft=p.controlRight=false;ActorControl.Driver=driver;
        }
        return rows;
    }
}
