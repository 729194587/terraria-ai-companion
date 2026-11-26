"""Export fe6190e with the current controlled fixture and approach timing probes."""
import argparse
import io
from pathlib import Path
import subprocess
import zipfile


BASE = "fe6190efd8250b2dbe8e8f16e11f36c41e4fcce2"


def prepare(destination):
    destination.mkdir(parents=True, exist_ok=False)
    repo = Path(__file__).resolve().parents[1]
    archive = subprocess.check_output(["git", "archive", "--format=zip", BASE, "CompanionBridge"], cwd=repo)
    with zipfile.ZipFile(io.BytesIO(archive)) as source:
        source.extractall(destination)
    mod = destination / "CompanionBridge"
    runtime = mod / "ServerPlayerRuntime"
    fixture = (repo / "CompanionBridge/ServerPlayerRuntime/TerrainApproachSelfTest.cs").read_text(encoding="utf8")
    fixture = fixture.replace("int ticks=0;", "int ticks=0; BaselineTiming.Reset();")
    fixture = fixture.replace("new{scene,repeat,ticks,", "new{scene,repeat,ticks,BaselineTiming.planningMs,BaselineTiming.maxSliceMs,")
    (runtime / "TerrainApproachSelfTest.cs").write_text(fixture)
    # This file adds only the opt-in call to the controlled fixture.
    mining = runtime / "MiningSelfTest.cs"
    hook = (repo / "CompanionBridge/ServerPlayerRuntime/MiningSelfTest.cs").read_text(encoding="utf8")
    start = hook.index('            if(Environment.GetCommandLineArgs().Contains("-terrainapproachselftest"))')
    end = hook.index("            var wrapped=", start)
    mining.write_text(mining.read_text(encoding="utf8").replace("            var wrapped=", hook[start:end] + "            var wrapped="))
    (mod / "ServerIpc/BaselineTiming.cs").write_text("""
using System;
using System.Diagnostics;
namespace CompanionBridge.ServerIpc;
internal static class BaselineTiming
{
    internal static double planningMs,maxSliceMs;
    internal static void Reset(){planningMs=maxSliceMs=0;}
    internal static T Measure<T>(Func<T> work)
    {
        long start=Stopwatch.GetTimestamp();
        try{return work();}
        finally{
            double ms=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            planningMs+=ms;maxSliceMs=Math.Max(maxSliceMs,ms);
        }
    }
}
""")
    p = mod / "ServerIpc/ExcavationTask.cs"
    p.write_text(p.read_text(encoding="utf8").replace("&&search.MoveNext()", "&&BaselineTiming.Measure(search.MoveNext)"))
    p = mod / "ServerIpc/NativeMineTargetWorld.cs"
    p.write_text(p.read_text(encoding="utf8").replace("if(scan.MoveNext())", "if(BaselineTiming.Measure(scan.MoveNext))")
                 .replace("var step=edits.FindOpening(x*16+8,y*16,remainingTiles,mining:true);",
                          "var step=BaselineTiming.Measure(()=>edits.FindOpening(x*16+8,y*16,remainingTiles,mining:true));"))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    prepare(parser.parse_args().destination)
