using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CompanionBridge.Motion;

// Branch-local edits. Native collision always reads the real world.
internal sealed class LocalTerrainView
{
    private readonly Func<int,int,TerrainCell> read;
    private readonly Dictionary<(int X,int Y),(char Shape,int? TileType)> edits;
    internal LocalTerrainView(Func<int,int,char> read):this((x,y)=>new TerrainCell(read(x,y),default)){}
    internal LocalTerrainView(Func<int,int,TerrainCell> read){this.read=read;edits=new();}
    private LocalTerrainView(LocalTerrainView other){read=other.read;edits=new(other.edits);}
    internal LocalTerrainView Fork()=>new(this);
    internal TerrainCell Environment(int x,int y)
    {
        var cell=read(x,y);
        return edits.TryGetValue((x,y),out var edit)
            ?new(edit.Shape,cell.Liquid,edit.TileType):cell;
    }
    internal char Cell(int x,int y)=>Environment(x,y).Shape;
    internal char DryCell(int x,int y)=>Environment(x,y).DryShape;
    internal bool LiquidAtSupport(int x,int y)=>Environment(x,y).Liquid.Amount>0||Environment(x,y+1).Liquid.Amount>0;
    internal bool LiquidNear(int x,int y)
    {
        for(int tx=x-1;tx<=x+1;tx++)
            for(int ty=y-1;ty<=y+1;ty++)
                if(Environment(tx,ty).Liquid.Amount>0)return true;
        return false;
    }
    internal bool Set(int x,int y,char shape,int? type=null)
    {
        if(Cell(x,y) is '?' or '!'||shape is not ('.' or '#' or '='))return false;
        edits[(x,y)]=(shape,type);return true;
    }
}

internal static class LocalEditGeometry
{
    internal static (int X,int Y)[] Across(PhysicsState pose,float destination)
    {
        float left=Math.Min(pose.Px,destination-2),right=Math.Max(pose.Px,destination+22);
        int top=(int)Math.Floor(pose.Py/16),bottom=(int)Math.Round((pose.Py+42)/16);
        return (from y in Enumerable.Range(top,bottom-top)
                from x in Enumerable.Range((int)Math.Floor(left/16),(int)Math.Ceiling(right/16)-(int)Math.Floor(left/16))
                select (x,y)).ToArray();
    }
    internal static bool LandingBand(PhysicsState pose,Func<int,int,char> read)
    {for(int dx=-2;dx<=2;dx++)if(!MotionSupport.SafeFlatLanding(pose with{Px=pose.Px+dx},read))return false;return true;}

    internal static LandingRegion FindFlatLandingBand(PhysicsState pose,Func<int,int,char> read)
    {
        bool Safe(int offset)=>MotionSupport.SafeFlatLanding(pose with{Px=pose.Px+offset},read);
        // Return the sampled pose itself when adjoining poses cannot be proved safe.
        foreach(int offset in new[]{0,-1,1,-2,2})
            if(Safe(offset))
            {
                int left=offset,right=offset;
                while(left>-2&&Safe(left-1))left--;
                while(right<2&&Safe(right+1))right++;
                return new(pose.Px+left,pose.Px+right,pose.Py+PhysicsSimulator.PlayerH);
            }
        return null;
    }
    internal static char LandingCell(LocalTerrainView view,int x,int y,int supportRow)
    {
        var cell=view.Environment(x,y);
        return y==supportRow&&cell.Liquid.Kind==LiquidKind.Water?cell.Shape:cell.DryShape;
    }

    // Verify only the new deck column. Every placed platform becomes the next
    // native attachment point, even when the far bank is outside this window.
    internal static bool WaterDeckCell(LocalTerrainView view,int x,int row)
    {
        for(int y=row-3;y<row;y++)if(view.DryCell(x,y)!='.')return false;
        var deck=view.Environment(x,row);
        var below=view.Environment(x,row+1);
        return deck.Shape=='.'&&below.Shape is ('.' or '#' or '=' or 'h' or '1' or '2' or '3' or '4')
            &&deck.Liquid.Kind is (LiquidKind.None or LiquidKind.Water)
            &&below.Liquid.Kind is (LiquidKind.None or LiquidKind.Water)
            &&(deck.Liquid.Kind==LiquidKind.Water||below.Liquid.Kind==LiquidKind.Water);
    }
    internal static PhysicsState? FallLanding(PhysicsState pose,Func<int,int,char> read,
        PhysicsParameters physics,float maxDrop)
    {
        if(!physics.IsSupportedGroundPhysics||!pose.Grounded||Math.Abs(pose.Vx)>.01f||Math.Abs(pose.Vy)>.01f
            ||MotionSupport.ClearanceRejection(pose,read)!=null)return null;
        float startFeet=pose.Py+PhysicsSimulator.PlayerH,feet=startFeet,vy=0;
        float right=pose.Px+PhysicsSimulator.PlayerW;
        // Counterfactual neutral fall only. The live world is never edited to probe collision.
        for(int frame=0;frame<PhysicsSimulator.MaxFrames;frame++)
        {
            vy=Math.Min(vy+physics.Gravity,physics.MaxFall);
            float nextFeet=feet+vy,contact=float.PositiveInfinity;
            for(int x=(int)Math.Floor(pose.Px/16);x<Math.Ceiling(right/16);x++)
                for(int y=(int)Math.Floor(feet/16);y<=(int)Math.Floor(nextFeet/16);y++)
                {
                    char shape=read(x,y);
                    if(shape is '?' or '!')return null;
                    float surface=y*16+TerrainClassifier.SurfaceOffset(shape,pose.Px-x*16,right-x*16);
                    if(surface>=feet&&surface<=nextFeet)contact=Math.Min(contact,surface);
                }
            float reached=Math.Min(nextFeet,contact);
            if(reached-startFeet>maxDrop)return null;
            var next=pose with{Py=reached-PhysicsSimulator.PlayerH};
            if(float.IsFinite(contact))
                return contact-startFeet>LandingRegion.FeetTolerance&&physics.Fall.Safe(pose.Py,next.Py)
                    &&MotionSupport.SafeLanding(next,read)?next:null;
            if(MotionSupport.ClearanceRejection(next,read)!=null)return null;
            feet=reached;
        }
        return null;
    }

    internal static PhysicsState? LowerWalkLanding(PhysicsState start,PhysicsParameters physics,IMotionWorld world,
        float targetX,float targetFeet,float maxDrop)
    {
        PhysicsState? best=null;
        float Score(PhysicsState p)=>Math.Abs(p.Px+PhysicsSimulator.PlayerW/2f-targetX)
            +Math.Abs(p.Py+PhysicsSimulator.PlayerH-targetFeet);
        float score=Score(start);
        foreach(int direction in new[]{-1,1})
            foreach(int stride in new[]{12,20,30})
            {
                var prediction=PhysicsSimulator.Predict(start,direction,0,physics,world,walkFrames:stride);
                var landing=prediction.Landing;
                float candidate=Score(landing);
                if(!prediction.Safe||landing.Py<=start.Py+LandingRegion.FeetTolerance
                    ||landing.Py-start.Py>maxDrop||candidate>=score)continue;
                best=landing;score=candidate;
            }
        return best;
    }
}

internal enum LocalEditKind { Move, Mine, Place }
internal sealed record LocalMaterial(int Slot,int Type,char Shape,int Count,float Updates);
internal sealed record LocalEditQuote(int Slot,int Type,float Updates);
internal interface ILocalEditSource
{
    char Cell(int x,int y);
    TerrainCell Environment(int x,int y)=>new(Cell(x,y),default);
    LocalEditQuote Mine(PhysicsState pose,int x,int y,LocalTerrainView view);
    float? PriceMiningRun(PhysicsState pose,IReadOnlyList<LocalEditAction> cuts,LocalTerrainView view)=>null;
    bool Place(PhysicsState pose,int x,int y,LocalMaterial material,LocalTerrainView view);
    IReadOnlyList<LocalMaterial> Materials {get;}
}
internal sealed record LocalEditAction(LocalEditKind Kind,PhysicsState Start,PhysicsState Expected,
    int X=0,int Y=0,int Slot=-1,int Type=-1,char Shape='.',int WaterDirection=0);
internal sealed record LocalPlanCost(float Movement,float Mining,float Placement,float Materials,float Alteration,float Remaining)
{
    internal float Total=>Movement+Mining+Placement+Materials+Alteration+Remaining;
}
internal sealed record LocalEditPlan(LocalEditAction[] Actions,LocalPlanCost Cost,bool OpenExit)
{
    internal bool NativeVerified=>false;
    internal int Changes=>Actions.Count(a=>a.Kind!=LocalEditKind.Move);
    internal bool WaterPlatform=>Actions.Any(a=>a.WaterDirection!=0);
}

// Small finite templates, not a second movement search/runtime. All branches
// own an overlay and material ledger. Every yield retains the exact cursor.
internal sealed class LocalEditEvaluation : IDisposable
{
    internal const int MaxSteps=4,MaxChanges=18;
    private readonly IEnumerator<int> steps;
    private readonly List<LocalEditPlan> plans=new();
    internal LocalEditPlan[] Plans=>plans.ToArray();
    internal bool Finished {get;private set;}
    internal int Work {get;private set;}
    internal string Exit=>Finished?"complete":"evaluation_budget";
    internal bool LiquidBoundary {get;private set;}
    internal LocalEditEvaluation(PhysicsState start,LandingRegion goal,PhysicsParameters physics,ILocalEditSource source,int maxChanges=MaxChanges)
        =>steps=Run(start,goal,physics,source,Math.Min(MaxChanges,maxChanges)).GetEnumerator();
    internal void Advance(int maxWork=64,double budgetMs=3)
    {
        if(Finished)return;
        var timer=Stopwatch.StartNew();Work=0;
        while(Work<maxWork&&timer.Elapsed.TotalMilliseconds<budgetMs)
        {if(!steps.MoveNext()){Finished=true;break;}Work++;}
    }
    public void Dispose()=>steps.Dispose();
    internal static LocalEditPlan Choose(IEnumerable<LocalEditPlan> plans,float? movementCost,bool movementComplete)
    {
        var usable=plans.Where(p=>p.Changes>0&&(!movementComplete||!p.WaterPlatform)).ToArray();
        var complete=usable.Where(p=>p.OpenExit).OrderBy(p=>p.Cost.Total).FirstOrDefault();
        // An unfinished corridor must not displace a verified complete detour.
        var best=complete??(movementComplete?null:usable.OrderByDescending(p=>p.Actions.Count(a=>a.Kind==LocalEditKind.Move))
            .ThenBy(p=>p.Cost.Total).FirstOrDefault());
        return best!=null&&PreferClearance(best.Cost.Total,movementCost)?best:null;
    }

    internal static bool PreferClearance(float edit,float? movement)=>movement==null||edit+8<movement.Value;

    private IEnumerable<int> Run(PhysicsState start,LandingRegion goal,PhysicsParameters ph,ILocalEditSource source,int maxChanges)
    {
        int row=(int)Math.Round((start.Py+42)/16);
        var initial=new LocalTerrainView(source.Environment);
        if(!ph.IsSupportedGroundPhysics||!PhysicsSimulatorState(start)
            ||!MotionSupport.SafeFlatLanding(start,(x,y)=>LocalEditGeometry.LandingCell(initial,x,y,row))
            ||Math.Abs((start.Py+42)/16-Math.Round((start.Py+42)/16))>.01f)yield break;
        int direction=(goal.MinPlayerLeftX+goal.MaxPlayerLeftX)/2>=start.Px?1:-1;
        foreach(var material in source.Materials.Concat(new[]{(LocalMaterial)null}))
        {
            var view=new LocalTerrainView(source.Environment);var pose=start;var actions=new List<LocalEditAction>();
            char Landing(int x,int y)=>LocalEditGeometry.LandingCell(view,x,y,row);
            var emitted=new List<LocalEditPlan>();bool unsafeTail=false;
            int used=0,changes=0;float movement=0,mining=0,placing=0;
            for(int step=0;step<MaxSteps;step++)
            {
                float destination=pose.Px+direction*16;
                bool rejected=false;
                int firstCut=actions.Count;
                var beforeCut=view.Fork();
                float separateMining=0;
                foreach(var (x,y) in LocalEditGeometry.Across(pose,destination))
                {
                    if(view.Environment(x,y).Liquid.Amount>0){LiquidBoundary=true;unsafeTail=rejected=true;break;}
                    if(view.Cell(x,y)=='.')continue;
                    if(view.Cell(x,y)!='#'||changes>=maxChanges){unsafeTail=view.Cell(x,y)!='#';rejected=true;break;}
                    var quote=source.Mine(pose,x,y,view);yield return 0;
                    if(quote==null||!view.Set(x,y,'.')||!MotionSupport.SafeLanding(pose,Landing))
                    {
                        LiquidBoundary|=view.LiquidNear(x,y);
                        unsafeTail=true;rejected=true;break;
                    }
                    actions.Add(new(LocalEditKind.Mine,pose,pose,x,y,quote.Slot,quote.Type));
                    separateMining+=quote.Updates;changes++;
                }
                if(rejected)break;
                int cutCount=actions.Count-firstCut;
                mining+=(cutCount>1?source.PriceMiningRun(pose,actions.GetRange(firstCut,cutCount),beforeCut):null)??separateMining;
                if(cutCount>1)yield return 0;
                var next=pose with{Px=destination};
                if(!LocalEditGeometry.LandingBand(next,Landing))
                {
                    int left=(int)Math.Floor(destination/16),right=(int)Math.Ceiling((destination+20)/16);
                    LiquidBoundary|=Enumerable.Range(left,right-left).Any(x=>view.LiquidAtSupport(x,row));
                    if(material==null||used>=material.Count||changes>=maxChanges)
                    {unsafeTail=material==null||used>=material.Count;break;}
                    bool placed=false;
                    for(int x=left;x<right;x++)
                    {
                        if(view.Cell(x,row)!='.')continue;
                        bool water=view.LiquidAtSupport(x,row);
                        if(water)
                        {
                            if(material.Shape!='='||!LocalEditGeometry.WaterDeckCell(view,x,row))continue;
                        }
                        var branch=view.Fork();
                        char BranchLanding(int tx,int ty)=>LocalEditGeometry.LandingCell(branch,tx,ty,row);
                        bool allowed=source.Place(pose,x,row,material,view);yield return 0;
                        if(!allowed||!branch.Set(x,row,material.Shape,material.Type)||!LocalEditGeometry.LandingBand(next,BranchLanding)
                            ||!MotionSupport.SafeLanding(pose,BranchLanding))continue;
                        if(water&&!Enumerable.Range(1,15).All(dx=>LocalEditGeometry.LandingBand(pose with{Px=pose.Px+direction*dx},BranchLanding)))continue;
                        view=branch;actions.Add(new(LocalEditKind.Place,pose,pose,x,row,material.Slot,material.Type,material.Shape,water?direction:0));
                        used++;changes++;placing+=material.Updates;placed=true;break;
                    }
                    if(!placed){unsafeTail=true;break;}
                }
                actions.Add(new(LocalEditKind.Move,pose,next));movement+=20+16/ph.MaxRun;pose=next;
                var further=pose with{Px=pose.Px+direction*16};
                bool open=goal.Contains(pose)||LocalEditGeometry.Across(pose,further.Px).All(c=>view.DryCell(c.X,c.Y)=='.')
                    &&LocalEditGeometry.LandingBand(further,Landing);
                if(changes>0)emitted.Add(new(actions.ToArray(),new(movement,mining,placing,used*8,changes*4,goal.Distance(pose)/ph.MaxRun),open));
                yield return 0;
                if(goal.Contains(pose))break;
            }
            plans.AddRange(unsafeTail?emitted.Where(p=>p.OpenExit||p.WaterPlatform&&p.Actions.All(a=>a.Kind!=LocalEditKind.Mine)):emitted);
        }
        // Each real cut is followed by actual observation. A cut may release
        // support only if its own counterfactual has a safe landing.
        if(start.Px<=goal.MaxPlayerLeftX+20&&start.Px+20>=goal.MinPlayerLeftX&&goal.SurfaceY>start.Py+42)
        {
            var view=new LocalTerrainView(source.Environment);var pose=start;var actions=new List<LocalEditAction>();float mining=0;int changes=0;
            for(int depth=0;depth<MaxSteps;depth++)
            {
                int depthRow=(int)Math.Round((pose.Py+42)/16);
                var floor=Enumerable.Range((int)Math.Floor(pose.Px/16),(int)Math.Ceiling((pose.Px+20)/16)-(int)Math.Floor(pose.Px/16))
                    .Select(x=>(X:x,Y:depthRow)).Where(c=>view.Cell(c.X,c.Y)!='.')
                    .OrderBy(c=>Math.Min(pose.Px+20,(c.X+1)*16)-Math.Max(pose.Px,c.X*16)).ToArray();
                var after=view.Fork();bool allowed=floor.Length>0;
                foreach(var c in floor)allowed&=after.Set(c.X,c.Y,'.');
                if(!allowed||!LocalEditGeometry.FallLanding(pose,after.DryCell,ph,FallSafety.LookaheadPixels).HasValue)
                {LiquidBoundary|=floor.Any(c=>view.LiquidNear(c.X,c.Y));break;}
                foreach(var (x,y) in floor)
                {
                    if(view.Cell(x,y)!='#'||changes>=maxChanges)yield break;
                    var quote=source.Mine(pose,x,y,view);yield return 0;
                    if(quote==null){LiquidBoundary|=view.LiquidNear(x,y);yield break;}
                    view.Set(x,y,'.');var next=LocalEditGeometry.FallLanding(pose,view.DryCell,ph,FallSafety.LookaheadPixels)??pose;
                    if(next==pose&&!MotionSupport.SafeLanding(pose,view.DryCell))yield break;
                    actions.Add(new(LocalEditKind.Mine,pose,next,x,y,quote.Slot,quote.Type));mining+=quote.Updates;changes++;pose=next;
                }
                plans.Add(new(actions.ToArray(),new(depth*20,mining,0,0,changes*4,goal.Distance(pose)/ph.MaxRun),true));
                yield return 0;
                if(goal.Contains(pose))break;
            }
        }
    }
    private static bool PhysicsSimulatorState(PhysicsState s)=>s.Grounded&&Math.Abs(s.Vx)<=.1f&&Math.Abs(s.Vy)<=.1f&&s.ReleaseJump&&s.JumpFramesLeft==0;
}
