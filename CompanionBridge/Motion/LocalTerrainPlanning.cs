using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CompanionBridge.Motion;

// Structural counterfactuals only. Neither this view nor its plans implement
// IMotionWorld: native Collision always reads the real world after execution.
internal sealed class LocalTerrainView
{
    private readonly Func<int,int,char> read;
    private readonly Dictionary<(int X,int Y),char> edits;
    internal LocalTerrainView(Func<int,int,char> read){this.read=read;edits=new();}
    private LocalTerrainView(LocalTerrainView other){read=other.read;edits=new(other.edits);}
    internal LocalTerrainView Fork()=>new(this);
    internal char Cell(int x,int y)=>edits.TryGetValue((x,y),out var c)?c:read(x,y);
    internal bool Set(int x,int y,char shape)
    {
        if(Cell(x,y) is '?' or '!'||shape is not ('.' or '#' or '='))return false;
        edits[(x,y)]=shape;return true;
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
    internal static bool DropOneRow(PhysicsState pose,Func<int,int,char> read)
    {
        float feet=pose.Py+42;int row=(int)Math.Round(feet/16);
        if(Math.Abs(feet-row*16)>LandingRegion.FeetTolerance)return false;
        for(int x=(int)Math.Floor(pose.Px/16);x<Math.Ceiling((pose.Px+20)/16);x++)if(read(x,row)!='.')return false;
        return MotionSupport.SafeFlatLanding(pose with{Py=pose.Py+16},read);
    }
}

internal enum LocalEditKind { Move, Mine, Place }
internal sealed record LocalMaterial(int Slot,int Type,char Shape,int Count,float Updates);
internal sealed record LocalEditQuote(int Slot,int Type,float Updates);
internal interface ILocalEditSource
{
    char Cell(int x,int y);
    LocalEditQuote Mine(PhysicsState pose,int x,int y,LocalTerrainView view);
    bool Place(PhysicsState pose,int x,int y,LocalMaterial material,LocalTerrainView view);
    IReadOnlyList<LocalMaterial> Materials {get;}
}
internal sealed record LocalEditAction(LocalEditKind Kind,PhysicsState Start,PhysicsState Expected,
    int X=0,int Y=0,int Slot=-1,int Type=-1,char Shape='.');
internal sealed record LocalPlanCost(float Movement,float Mining,float Placement,float Materials,float Alteration,float Remaining)
{
    internal float Total=>Movement+Mining+Placement+Materials+Alteration+Remaining;
}
internal sealed record LocalEditPlan(LocalEditAction[] Actions,LocalPlanCost Cost,bool OpenExit)
{
    internal bool NativeVerified=>false;
    internal int Changes=>Actions.Count(a=>a.Kind!=LocalEditKind.Move);
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
        var usable=plans.Where(p=>p.Changes>0).ToArray();
        var complete=usable.Where(p=>p.OpenExit).OrderBy(p=>p.Cost.Total).FirstOrDefault();
        // An unfinished corridor must not displace a verified complete detour.
        var best=complete??(movementComplete?null:usable.OrderByDescending(p=>p.Actions.Count(a=>a.Kind==LocalEditKind.Move))
            .ThenBy(p=>p.Cost.Total).FirstOrDefault());
        return best!=null&&(!movementCost.HasValue||best.Cost.Total+8<movementCost.Value)?best:null;
    }

    private IEnumerable<int> Run(PhysicsState start,LandingRegion goal,PhysicsParameters ph,ILocalEditSource source,int maxChanges)
    {
        if(!ph.IsSupportedGroundPhysics||!PhysicsSimulatorState(start)||!MotionSupport.SafeFlatLanding(start,source.Cell)
            ||Math.Abs((start.Py+42)/16-Math.Round((start.Py+42)/16))>.01f)yield break;
        int direction=(goal.MinPlayerLeftX+goal.MaxPlayerLeftX)/2>=start.Px?1:-1;
        foreach(var material in source.Materials.Concat(new[]{(LocalMaterial)null}))
        {
            var view=new LocalTerrainView(source.Cell);var pose=start;var actions=new List<LocalEditAction>();
            var emitted=new List<LocalEditPlan>();bool unsafeTail=false;
            int used=0,changes=0;float movement=0,mining=0,placing=0;
            for(int step=0;step<MaxSteps;step++)
            {
                float destination=pose.Px+direction*16;
                bool rejected=false;
                foreach(var (x,y) in LocalEditGeometry.Across(pose,destination))
                {
                    if(view.Cell(x,y)=='.')continue;
                    if(view.Cell(x,y)!='#'||changes>=maxChanges){unsafeTail=view.Cell(x,y)!='#';rejected=true;break;}
                    var quote=source.Mine(pose,x,y,view);yield return 0;
                    if(quote==null||!view.Set(x,y,'.')||!MotionSupport.SafeLanding(pose,view.Cell))
                    {unsafeTail=true;rejected=true;break;}
                    actions.Add(new(LocalEditKind.Mine,pose,pose,x,y,quote.Slot,quote.Type));
                    mining+=quote.Updates;changes++;
                }
                if(rejected)break;
                var next=pose with{Px=destination};
                if(!LocalEditGeometry.LandingBand(next,view.Cell))
                {
                    if(material==null||used>=material.Count||changes>=maxChanges)
                    {unsafeTail=material==null||used>=material.Count;break;}
                    int row=(int)Math.Round((pose.Py+42)/16);bool placed=false;
                    for(int x=(int)Math.Floor(destination/16);x<Math.Ceiling((destination+20)/16);x++)
                    {
                        if(view.Cell(x,row)!='.')continue;
                        var branch=view.Fork();
                        bool allowed=source.Place(pose,x,row,material,view);yield return 0;
                        if(!allowed||!branch.Set(x,row,material.Shape)||!LocalEditGeometry.LandingBand(next,branch.Cell)
                            ||!MotionSupport.SafeLanding(pose,branch.Cell))continue;
                        view=branch;actions.Add(new(LocalEditKind.Place,pose,pose,x,row,material.Slot,material.Type,material.Shape));
                        used++;changes++;placing+=material.Updates;placed=true;break;
                    }
                    if(!placed){unsafeTail=true;break;}
                }
                actions.Add(new(LocalEditKind.Move,pose,next));movement+=20+16/ph.MaxRun;pose=next;
                var further=pose with{Px=pose.Px+direction*16};
                bool open=goal.Contains(pose)||LocalEditGeometry.Across(pose,further.Px).All(c=>view.Cell(c.X,c.Y)=='.')
                    &&LocalEditGeometry.LandingBand(further,view.Cell);
                if(changes>0)emitted.Add(new(actions.ToArray(),new(movement,mining,placing,used*8,changes*4,goal.Distance(pose)/ph.MaxRun),open));
                yield return 0;
                if(goal.Contains(pose))break;
            }
            plans.AddRange(unsafeTail?emitted.Where(p=>p.OpenExit):emitted);
        }
        // Reuse the original safe one-row descent structure; only the last cut
        // may release support. Each real cut is followed by actual observation.
        if(start.Px<=goal.MaxPlayerLeftX+20&&start.Px+20>=goal.MinPlayerLeftX&&goal.SurfaceY>start.Py+42)
        {
            var view=new LocalTerrainView(source.Cell);var pose=start;var actions=new List<LocalEditAction>();float mining=0;int changes=0;
            for(int depth=0;depth<MaxSteps;depth++)
            {
                int row=(int)Math.Round((pose.Py+42)/16);
                var floor=Enumerable.Range((int)Math.Floor(pose.Px/16),(int)Math.Ceiling((pose.Px+20)/16)-(int)Math.Floor(pose.Px/16))
                    .Select(x=>(X:x,Y:row)).Where(c=>view.Cell(c.X,c.Y)!='.')
                    .OrderBy(c=>Math.Min(pose.Px+20,(c.X+1)*16)-Math.Max(pose.Px,c.X*16)).ToArray();
                var after=view.Fork();bool allowed=floor.Length>0;
                foreach(var c in floor)allowed&=after.Set(c.X,c.Y,'.');
                if(!allowed||!LocalEditGeometry.DropOneRow(pose,after.Cell))break;
                foreach(var (x,y) in floor)
                {
                    if(view.Cell(x,y)!='#'||changes>=maxChanges)yield break;
                    var quote=source.Mine(pose,x,y,view);yield return 0;
                    if(quote==null)yield break;
                    view.Set(x,y,'.');var next=LocalEditGeometry.DropOneRow(pose,view.Cell)?pose with{Py=pose.Py+16}:pose;
                    if(next==pose&&!MotionSupport.SafeLanding(pose,view.Cell))yield break;
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
