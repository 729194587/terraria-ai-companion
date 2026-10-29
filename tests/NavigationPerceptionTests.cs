using System;
using System.Linq;
using System.Text.Json;
using CompanionBridge;
using CompanionBridge.Motion;

public static class NavigationPerceptionTests
{
    private static int checks;
    private static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
    // Stationary collision double for evidence transport, not Terraria physics.
    private sealed class BlockedWorld:IMotionWorld
    {
        internal bool Unknown,NoWitness;
        public NavigationBlockingTile[] LastHorizontalContact{get;private set;}
        public PhysicsState Move(PhysicsState s,float vx,float vy,float gravity)
        {
            LastHorizontalContact=vx>0&&!NoWitness?new[]{new NavigationBlockingTile(2,0,"Terraria/Stone")}:null;
            return s with{Vx=0,Vy=0};
        }
        public bool SafeSweep(PhysicsState s,PhysicsState e,out string reason){reason=Unknown?"unknown":null;return !Unknown;}
        public bool SafeLanding(PhysicsState s)=>true;
    }
    private sealed class JumpableWorld:IMotionWorld
    {
        private readonly MotionOfflineTests.World inner=new(){WallX=32,WallHeight=32};
        public NavigationBlockingTile[] LastHorizontalContact{get;private set;}
        public PhysicsState Move(PhysicsState s,float vx,float vy,float gravity)
        {
            var next=inner.Move(s,vx,vy,gravity);
            LastHorizontalContact=NavigationContact.Horizontal(s.Px,s.Py,vx,next.Px-s.Px,(x,y)=>('#',"Terraria/Stone"));
            return next;
        }
        public bool SafeSweep(PhysicsState s,PhysicsState e,out string reason)=>inner.SafeSweep(s,e,out reason);
        public bool SafeLanding(PhysicsState s)=>inner.SafeLanding(s);
    }
    public static void Run()
    {
        Check(BodyTileBounds.FromPixels(16,32,16,32)==new BodyTileBounds(1,2,1,3),"exact edges exclude neighbour");
        Check(BodyTileBounds.FromPixels(16,32,20,42)==new BodyTileBounds(1,2,2,4),"ordinary body spans several tiles");
        Check(BodyTileBounds.FromPixels(15.5,31.5,1,1)==new BodyTileBounds(0,1,1,2),"subpixel body crosses boundaries");
        Check(BodyTileBounds.FromPixels(-16,-32,16,32)==new BodyTileBounds(-1,-2,-1,-1),"negative exact boundary uses floor/ceiling");
        Check(BodyTileBounds.FromPixels(-0.5,-0.25,0.25,0.125)==new BodyTileBounds(-1,-1,-1,-1),"negative fractions never truncate to zero");
        Check(BodyTileBounds.FromPixels(0,0,48,64)==new BodyTileBounds(0,0,2,3),"larger body is not fixed 20x42");
        Check(BodyTileBounds.FromPixels(0,0,16.00001,16.00001)==new BodyTileBounds(0,0,1,1),"no epsilon swallows small overlap");
        foreach(double invalid in new[]{0d,-1,double.NaN,double.PositiveInfinity})
        {bool rejected=false;try{BodyTileBounds.FromPixels(0,0,invalid,42);}catch(ArgumentException){rejected=true;}Check(rejected,"invalid body rejected");}
        int reads=0;
        var right=NavigationContact.Horizontal(12,0,1,0,(x,y)=>{reads++;return ('#',"Terraria/Stone");});
        Check(right.Length==3&&right.All(t=>t.X==2)&&right.Select(t=>t.Y).SequenceEqual(new[]{0,1,2}),"right wall exact coordinates");
        Check(reads==3&&right.All(t=>t.ContentKey=="Terraria/Stone"),"bounded face-only reads and real keys");
        var left=NavigationContact.Horizontal(32,16,-1,0,(x,y)=>('#',"Terraria/Dirt"));
        Check(left.All(t=>t.X==1)&&left.Select(t=>t.Y).SequenceEqual(new[]{1,2,3}),"left wall tile before boundary");
        var aligned=NavigationContact.Horizontal(12,6,1,0,(x,y)=>('#',"Terraria/Stone"));
        Check(aligned.Length==3&&aligned.Last().Y==2,"feet boundary excludes supporting row");
        foreach(char shape in new[]{'.','=','a','b','?','!','h','1','2','3','4','A'})
            Check(NavigationContact.Horizontal(12,0,1,0,(x,y)=>(shape,"Terraria/Unrelated"))==null,"no proven full solid blocker: "+shape);
        Check(NavigationContact.Horizontal(12,0,1,0,(x,y)=>('#',null))==null,"unknown content cannot fabricate key");
        foreach(var pair in new[]{(1f,1f),(0f,0f),(1f,-0.1f),(1f,2f)})
            Check(NavigationContact.Horizontal(12,0,pair.Item1,pair.Item2,(x,y)=>throw new Exception("must not read"))==null,"no confirmed clip means no scan");
        Check(NavigationContact.Horizontal(12.1f,0,1,0,(x,y)=>throw new Exception("must not read"))==null,"nonaligned face cannot identify blocker");
        var mixed=NavigationContact.Horizontal(12,0,1,0,(x,y)=>y==1?('#',"Terraria/Stone"):('?',null));
        Check(mixed.Length==1&&mixed[0].Y==1,"unknown neighbours not confirmed");
        var start=new PhysicsState(12,0,0,0,true);
        var world=new BlockedWorld();
        var prediction=PhysicsSimulator.Predict(start,1,0,PhysicsParameters.Basic,world);
        Check(!prediction.Safe&&prediction.BlockingTiles?.Single().ContentKey=="Terraria/Stone","walking rejection retains clipped contact");
        var task=new NavigationTask();task.Before(1,1,160,start,PhysicsParameters.Basic,world);
        Check(task.Snapshot.Status=="failed"&&task.Snapshot.BlockingTiles?.Single().X==2,"no-route failure retains root forward evidence");
        string wire=JsonSerializer.Serialize(task.Snapshot,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        Check(wire.Contains("\"blockingTiles\":[{\"x\":2,\"y\":0,\"contentKey\":\"Terraria/Stone\""),"wire coordinates/content key");
        task.Fail("different failure");Check(task.Snapshot.BlockingTiles==null,"unrelated failure clears witness");
        task.Cancel();Check(task.Snapshot.BlockingTiles==null,"cancel clears diagnostic state");
        task.Before(2,1,160,start,PhysicsParameters.Basic,new BlockedWorld{Unknown=true});
        Check(task.Snapshot.Status=="failed"&&task.Snapshot.BlockingTiles==null,"unknown sweep cannot borrow contact");
        task.Before(3,1,160,start,PhysicsParameters.Basic,new BlockedWorld{NoWitness=true});
        Check(task.Snapshot.Status=="failed"&&task.Snapshot.BlockingTiles==null,"unreliable cause omits evidence");
        var timed=StateSpacePlanner.Plan(start,160,PhysicsParameters.Basic,world,budgetMs:0);
        Check(timed.TimeBudgetReached&&timed.BlockingTiles==null,"search deadline is not a wall");
        var jumpable=new JumpableWorld();var floorStart=new PhysicsState(12,1558,0,0,true);
        Check(PhysicsSimulator.Predict(floorStart,1,0,PhysicsParameters.Basic,jumpable).BlockingTiles!=null,"root walk hits jumpable wall");
        var alternative=StateSpacePlanner.Plan(floorStart,100,PhysicsParameters.Basic,jumpable,budgetMs:5000);
        Check(alternative.Actions.Count>0&&alternative.BlockingTiles==null,"valid alternative route never inherits rejected root evidence");
        Console.WriteLine($"Navigation perception: {checks} checks (collision doubles, not native execution).");
    }
}
