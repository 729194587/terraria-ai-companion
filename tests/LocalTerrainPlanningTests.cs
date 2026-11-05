using System;
using System.Collections.Generic;
using System.Linq;
using CompanionBridge.Motion;

public static class LocalTerrainPlanningTests
{
    private static int checks;
    private static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
    private sealed class Source:ILocalEditSource
    {
        internal readonly Dictionary<(int,int),char> Tiles=new();
        internal int Stock=5,MineCalls,PlaceCalls;
        internal char MaterialShape='=';
        internal bool Tool=true,Unplaceable;
        public char Cell(int x,int y)=>Tiles.TryGetValue((x,y),out char c)?c:y>=100?'#':'.';
        public IReadOnlyList<LocalMaterial> Materials=>Stock>0?new[]{new LocalMaterial(2,19,MaterialShape,Stock,16)}:Array.Empty<LocalMaterial>();
        public LocalEditQuote Mine(PhysicsState s,int x,int y,LocalTerrainView view)
        {MineCalls++;return Tool&&view.Cell(x,y)=='#'?new(1,1,46):null;}
        public bool Place(PhysicsState s,int x,int y,LocalMaterial m,LocalTerrainView view)
        {PlaceCalls++;return !Unplaceable&&view.Cell(x,y)=='.'&&(view.Cell(x-1,y) is '#' or '='||view.Cell(x+1,y) is '#' or '='||view.Cell(x,y+1)=='#');}
    }
    private static readonly PhysicsState Start=new(1600,1558,0,0,true);
    private static LocalEditPlan[] Evaluate(Source source,LandingRegion goal=null)
    {
        using var search=new LocalEditEvaluation(Start,goal??LandingRegion.AtDestination(1680,1600),PhysicsParameters.Basic,source);
        int slices=0;
        int before=source.MineCalls+source.PlaceCalls;
        search.Advance(0,0);Check(!search.Finished&&source.MineCalls+source.PlaceCalls==before,"zero budget remains pending and performs no actions");
        while(!search.Finished&&slices++<2000){search.Advance(1,100);Check(search.Work<=1,"evaluation resumes with bounded work");}
        Check(search.Finished&&search.Exit=="complete","finite candidate evaluation completes");return search.Plans;
    }
    public static void Run()
    {
        var source=new Source();var view=new LocalTerrainView(source.Cell);var left=view.Fork();var right=view.Fork();
        Check(left.Set(100,100,'.')&&left.Cell(100,100)=='.'&&right.Cell(100,100)=='#'&&source.Cell(100,100)=='#',"candidate edits are ordered, isolated and never write source terrain");
        Check(left.Set(100,100,'=')&&left.Cell(100,100)=='=',"platform support remains distinct from full block support");
        source.Tiles[(101,100)]='?';source.Tiles[(102,100)]='!';
        Check(!left.Set(101,100,'.')&&!left.Set(102,100,'#'),"unknown/hazard cells cannot become optimistic virtual air/support");
        source=new();source.Stock=0;
        for(int x=102;x<=104;x++)for(int y=97;y<100;y++)source.Tiles[(x,y)]='#';
        var original=new Dictionary<(int,int),char>(source.Tiles);var plans=Evaluate(source);
        Check(plans.Any(p=>p.Actions.Count(a=>a.Kind==LocalEditKind.Mine)>=3),"continuous excavation participates in candidate plans");
        Check(plans.All(p=>!p.NativeVerified&&p.Changes<=18),"structural plans are bounded and never claim native collision verification");
        Check(source.Tiles.OrderBy(p=>p.Key).SequenceEqual(original.OrderBy(p=>p.Key)),"planning leaves real tile source unchanged");
        Check(LocalEditEvaluation.Choose(plans,20,true)==null,"short verified detour beats unnecessary excavation");
        Check(LocalEditEvaluation.Choose(plans,1000,true)!=null,"large movement cost can select safe bounded excavation");
        var best=plans.OrderBy(p=>p.Cost.Total).First();
        Check(LocalEditEvaluation.Choose(new[]{best},best.Cost.Total+4,true)==null,"small gains retain existing terrain");
        Check(best.Cost.Mining==46*best.Actions.Count(a=>a.Kind==LocalEditKind.Mine),"tool/target mining quotes contribute game-update costs");
        source=new();for(int x=102;x<=103;x++)for(int y=100;y<=104;y++)source.Tiles[(x,y)]='.';
        for(int y=97;y<100;y++)source.Tiles[(102,y)]='#';
        plans=Evaluate(source);
        var mixed=plans.FirstOrDefault(p=>p.OpenExit&&p.Actions.Any(a=>a.Kind==LocalEditKind.Place)&&p.Actions.Any(a=>a.Kind==LocalEditKind.Mine));
        Check(mixed!=null,"digging then placing support is evaluated as one ordered candidate");
        Check(Array.FindIndex(mixed.Actions,a=>a.Kind==LocalEditKind.Mine)<Array.FindIndex(mixed.Actions,a=>a.Kind==LocalEditKind.Place),"future placement sees earlier cleared cells");
        Check(mixed.Cost.Materials>0&&mixed.Cost.Placement>0&&mixed.Cost.Alteration>0,"material and alteration costs affect combined plan price");
        source.Stock=0;Check(Evaluate(source).Length==0,"missing bridge materials reject dependent excavation before any cut");
        source.Stock=5;source.Unplaceable=true;Check(Evaluate(source).Length==0,"native placement admission failure rejects dependent plan");
        source.Unplaceable=false;source.MaterialShape='!';Check(Evaluate(source).Length==0,"dangerous material cannot be treated as safe virtual support");
        source.MaterialShape='#';Check(Evaluate(source).Any(p=>p.Actions.Any(a=>a.Kind==LocalEditKind.Place&&a.Shape=='#')),"solid support has its own shape in continuous plans");
        source.MaterialShape='=';
        source.Unplaceable=false;source.Tool=false;Check(Evaluate(source).Length==0,"unavailable tool is a hard constraint, not a cost penalty");
        source=new();for(int x=101;x<=105;x++)for(int y=100;y<=104;y++)source.Tiles[(x,y)]='.';
        plans=Evaluate(source,LandingRegion.AtDestination(1672,1600));
        Check(plans.Any(p=>p.Actions.Count(a=>a.Kind==LocalEditKind.Place)>1),"consecutive supports see earlier virtual placements");
        source.Stock=1;plans=Evaluate(source,LandingRegion.AtDestination(1672,1600));
        Check(!plans.Any(p=>p.OpenExit)&&plans.Length==0,"one material cannot fund a multi-support crossing");
        foreach(char unsafeCell in new[]{'?','!'})
        {
            source=new();source.Tiles[(102,98)]=unsafeCell;
            Check(Evaluate(source).Length==0,"unknown or dangerous blocker never becomes a safe edit candidate");
        }
        source=new();plans=Evaluate(source,LandingRegion.AtDestination(1610,1616));
        Check(plans.Any(p=>p.Actions.Any(a=>a.Kind==LocalEditKind.Mine&&a.Expected.Py>a.Start.Py)),"safe one-row native excavation structure remains available");
        Console.WriteLine($"Local terrain planning: {checks} checks passed; structural counterfactuals only");
    }
}
