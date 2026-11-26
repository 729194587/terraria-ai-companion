"""Export the pinned mining revisions with game-thread timers for the two performance fixtures."""
import argparse
import io
import subprocess
import sys
import zipfile
from pathlib import Path

DIRECT = "45b3de2af49d72fc07a8375ddae000e019548ad6"
PROBE = """using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
namespace CompanionBridge.ServerIpc;
internal sealed class MiningPerfProbe
{
    private readonly Dictionary<string,(int Count,long Time)> stats=new();
    internal readonly List<object> Choices=new();
    private void Add(string name,long start)
    {
        stats.TryGetValue(name,out var old);
        stats[name]=(old.Count+1,old.Time+(Stopwatch.GetTimestamp()-start));
    }
    internal T Time<T>(string name,Func<T> work)
    {
        long start=Stopwatch.GetTimestamp();
        try{return work();}
        finally{Add(name,start);}
    }
    internal IEnumerable<int> Steps(string name,IEnumerable<int> work)
    {
        using var it=work.GetEnumerator();
        while(Time(name,it.MoveNext))yield return it.Current;
    }
    internal object View()=>new{choices=Choices.ToArray(),timers=stats.ToDictionary(k=>k.Key,
        k=>new{calls=k.Value.Count,ms=k.Value.Time*1000.0/Stopwatch.Frequency})};
}
"""


def edit(path, replacements):
    source = path.read_text(encoding="utf8")
    for before, after in replacements:
        if source.count(before) != 1:
            raise ValueError(f"{path}: expected one occurrence of {before!r}")
        source = source.replace(before, after)
    path.write_text(source, encoding="utf8")


def timer(label, expression, probe="probe"):
    return expression, f'{probe}.Time("{label}",()=>{expression})'


def export_direct(destination):
    destination.mkdir(parents=True, exist_ok=False)
    repo = Path(__file__).resolve().parents[1]
    archive = subprocess.check_output(["git", "archive", "--format=zip", DIRECT, "CompanionBridge"], cwd=repo)
    with zipfile.ZipFile(io.BytesIO(archive)) as source:
        source.extractall(destination)
    # Both comparison packages must initialize the natural actor identically.
    fixture = Path("CompanionBridge/ServerIpc/ServerAiNaturalFixture.cs")
    (destination / fixture).write_bytes((repo / fixture).read_bytes())


def profile_direct(destination):
    ipc = destination / "CompanionBridge/ServerIpc"
    edit(ipc / "ExcavationApproach.cs", [
        ("private NativeNavigationEdits corridor;", "private readonly MiningPerfProbe probe=new();\n    private NativeNavigationEdits corridor;"),
        ("new{decisions=decisions.ToArray()", "new{perf=probe.View(),decisions=decisions.ToArray()"),
        ("physics,pending,corridor,LocalEditEvaluation.MaxChanges-cutsSinceOre)", "physics,pending,corridor,LocalEditEvaluation.MaxChanges-cutsSinceOre,probe)"),
        ("slice in local.Run()", 'slice in probe.Steps("local",local.Run())'),
        ("slice in FindWorkPosition(pose,world,physics,pending)", 'slice in probe.Steps("work",FindWorkPosition(pose,world,physics,pending))'),
        timer("bypass", "PhysicsSimulator.Predict(pose,direction,hold,physics,world,walkFrames:stride)"),
    ])
    edit(ipc / "OreApproachSearch.cs", [
        ("int remainingCuts)", "int remainingCuts,MiningPerfProbe probe)"),
        timer("edge", "PhysicsSimulator.Predict(node.Pose,direction,hold,physics,World(node.View),walkFrames:stride)"),
        timer("flat", "PhysicsSimulator.Predict(node.Pose,direction,0,physics,World(flat),walkFrames:stride)"),
        timer("fall", "PhysicsSimulator.Predict(pose,0,0,physics,world,walkFrames:0)"),
        timer("goalQuote", "NativeExcavation.Choose(identity,node.Pose,new[]{cell},node.View.Fork(),false)"),
        timer("cutQuote", "edits.Mine(pose,x,y,node.View)"),
    ])
    edit(ipc / "ExcavationTask.cs", [
        timer("landing", "(local?world:new NativeMotionWorld(stance)).SafeLanding(stance)"),
        timer("batchQuote", "NativeExcavation.Choose(identity,stance,pending,new(TerrainReader.ReadEnvironment),ordered:false)"),
        timer("firstQuote", "NativeExcavation.Choose(identity,stance,quote.Cells.Take(1),new(TerrainReader.ReadEnvironment),false)"),
        timer("ground", "SafeGroundWalk.TryPlan(start,x,x>=start.Px?1:-1,physics,world,candidate.Region)"),
        timer("surface", "navigator.Advance(budgetMs:2)"),
        ("destination=candidate;", """object Row(WorkPose c)=>new{x=(c.Region.MinPlayerLeftX+c.Region.MaxPlayerLeftX)/2,c.Coverage,c.LowerBound,c.Mining};
            probe.Choices.Add(new{start=start.Px,selected=Row(candidate),candidates=candidates.OrderBy(c=>c.LowerBound+c.Mining).Select(Row).ToArray()});
            destination=candidate;"""),
    ])


def profile_history(destination):
    fixture = Path(__file__).with_name("prepare_terrain_mining_baseline.py")
    subprocess.run([sys.executable, "-X", "utf8", str(fixture), str(destination)], check=True)
    mod = destination / "CompanionBridge"
    ipc = mod / "ServerIpc"
    edit(ipc / "BaselineTiming.cs", [
        ("internal static void Reset(){planningMs=maxSliceMs=0;}",
         "internal static MiningPerfProbe breakdown;\n    internal static void Reset(){planningMs=maxSliceMs=0;breakdown=new();}"),
    ])
    edit(ipc / "NativeMineTargetWorld.cs", [
        ("BaselineTiming.Measure(scan.MoveNext)", 'BaselineTiming.Measure(()=>BaselineTiming.breakdown.Time("scan",scan.MoveNext))'),
        timer("opening", "edits.FindOpening(x*16+8,y*16,remainingTiles,mining:true)", "BaselineTiming.breakdown"),
        timer("surface", "navigator.Advance(budgetMs:2)", "BaselineTiming.breakdown"),
        timer("ground", "SafeGroundWalk.TryPlan(observed.State,px,px>=observed.State.Px?1:-1,physics,world,region)", "BaselineTiming.breakdown"),
    ])
    edit(mod / "ServerPlayerRuntime/TerrainApproachSelfTest.cs", [
        ("new{scene,repeat,ticks,BaselineTiming.planningMs", "new{scene,repeat,ticks,perf=BaselineTiming.breakdown.View(),BaselineTiming.planningMs"),
    ])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path, help="new directory for the instrumented source export")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--historical", action="store_true", help="instrument the fe6190e comparison fixture")
    mode.add_argument("--fixture-only", action="store_true", help="export 45b3de2 with fixed starter equipment and no timers")
    args = parser.parse_args()
    if args.historical:
        profile_history(args.destination)
    else:
        export_direct(args.destination)
        if not args.fixture_only:
            profile_direct(args.destination)
    if not args.fixture_only:
        (args.destination / "CompanionBridge/ServerIpc/MiningPerfProbe.cs").write_text(PROBE, encoding="utf8")
