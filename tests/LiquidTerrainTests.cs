using System;
using CompanionBridge;
using CompanionBridge.Motion;

public static class LiquidTerrainTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
        long previous=0;
        foreach(byte amount in new byte[]{1,128,255})
        {
            var water=new TileLiquid(0,amount);
            var platform=new TerrainCell('=',water);
            Check(platform.Shape=='='&&platform.Liquid.Kind==LiquidKind.Water
                &&platform.Liquid.Amount==amount,"liquid amount and structural platform coexist");
            Check(water.Stamp!=previous,"liquid amount participates in the cell stamp");
            previous=water.Stamp;
        }
        Check(new TileLiquid(3,0)==new TileLiquid(0,0),"empty cells ignore stale liquid type");
        Check(new TileLiquid(3,128).Stamp!=new TileLiquid(0,128).Stamp,"nonempty type participates in the stamp");
        Check(new TileLiquid(99,1).Kind==LiquidKind.Unknown,"unrecognized nonempty type is unknown");
        Check(new TileLiquid(99,1).Stamp!=new TileLiquid(98,1).Stamp,"unknown type changes invalidate the stamp");
        Check(new TerrainCell('?',new(0,255)).DryShape=='?',"legacy dry projection preserves unknown");
        Check(new TerrainCell('.',new(0,1)).DryShape=='!',"legacy dry projection rejects liquid");
        var half=new TileLiquid(2,128);
        Check(!half.OverlapsVertical(10,150,168)&&half.OverlapsVertical(10,150,168.01f),
            "half-filled surface uses a strict lower-body overlap");
        Check(!half.OverlapsVertical(10,176,186),"body entirely below this liquid cell has no overlap");
        var world=new WaterBoundary();
        var dry=PhysicsParameters.Basic with{CanWade=true};
        var start=new PhysicsState(0,0,3,0,true);
        var entering=PhysicsSimulator.Step(start,new(1,false),dry,world);
        var wading=PhysicsSimulator.Step(entering,new(1,false),dry,world);
        var exiting=PhysicsSimulator.Step(wading,new(1,false),dry,world);
        Check(!entering.Wet&&entering.Px==entering.Vx&&entering.Py==dry.Gravity,
            "entry displacement uses the native collision query at the pre-movement pose");
        Check(wading.Wet&&wading.Px==entering.Px+wading.Vx*.5f&&world.LastWaterGravity==dry.Gravity,
            "first water collision halves displacement, retaining velocity and that update's dry gravity");
        Check(!exiting.Wet&&exiting.Px==wading.Px+exiting.Vx&&world.LastDryGravity==PhysicsParameters.WaterGravity,
            "leaving water restores dry displacement before the next update restores dry gravity");
        var jump=PhysicsSimulator.Step(new(3,0,0,0,true,Wet:true),new(1,true),dry,world);
        Check(jump.JumpFramesLeft==PhysicsParameters.WaterJumpHeight&&jump.Vy< -5.8f&&jump.Py==jump.Vy*.5f,
            "water jump uses native wet launch parameters and displacement");
        var emerging=PhysicsSimulator.Step(new(5,0,0,-5,false,25,false,Wet:true),new(0,true),dry,world);
        Check(!emerging.Wet && emerging.JumpFramesLeft==PhysicsParameters.WaterJumpHeight/5,
            "emerging from ordinary water caps the remaining native jump hold");
        var pool=new FlatPool();
        var shore=new PhysicsState(0,0,0,0,true);
        var goal=LandingRegion.AtDestination(180,42);
        var across=PoolCrossing.TryPlan(shore,goal,dry,pool);
        Check(across?.Kind==PoolCrossing.Kind&&across.Actions[0].Landing.Px>64
            &&PhysicsSimulator.Settled(across.Actions[0].Landing,pool),"verify a complete traversal ending on a dry bank");
        pool.Breath=30;
        Check(PoolCrossing.TryPlan(shore,goal,dry,pool)==null,"no entry without enough breath to reach the opposite bank");
        pool.Breath=200;pool.BlockExit=true;
        Check(PoolCrossing.TryPlan(shore,goal,dry,pool)==null,"reject a submerged partial route at an impassable bank");
        Console.WriteLine($"Phase 4.6 liquid terrain: {checks} checks");
    }

    private sealed class FlatPool : IMotionWorld,IPoolMotionWorld
    {
        public int Breath {get;set;}=200;
        public bool BlockExit;
        public bool WaterAhead(PhysicsState s,int direction)=>true;
        public IMotionWorld PoolWorld(PhysicsState s)=>this;
        public bool WaterContact(PhysicsState s)=>s.Px>=16&&s.Px<=64;
        public PhysicsState Move(PhysicsState s,float vx,float vy,float gravity)
            =>new(s.Px+vx,0,vx,0,true);
        public PhysicsState MoveInWater(PhysicsState s,float vx,float vy,float gravity)
            =>Move(s,vx*.5f,vy,gravity) with{Vx=vx};
        public bool SafeSweep(PhysicsState a,PhysicsState b,out string reason)
        {reason=BlockExit&&b.Px>60?"blocked":null;return reason==null;}
        public bool SafeLanding(PhysicsState s)=>s.Grounded&&!WaterContact(s);
    }

    private sealed class WaterBoundary : IMotionWorld
    {
        public float LastDryGravity,LastWaterGravity;
        public bool WaterContact(PhysicsState s)=>s.Px>=2&&s.Px<4;
        public PhysicsState Move(PhysicsState s,float vx,float vy,float gravity)
        {
            LastDryGravity=gravity;
            return new(s.Px+vx,s.Py+vy,vx,vy,false);
        }
        public PhysicsState MoveInWater(PhysicsState s,float vx,float vy,float gravity)
        {
            LastWaterGravity=gravity;
            return new(s.Px+vx*.5f,s.Py+vy*.5f,vx,vy,false);
        }
        public bool SafeSweep(PhysicsState a,PhysicsState b,out string reason){reason=null;return true;}
        public bool SafeLanding(PhysicsState s)=>s.Grounded;
    }
}
