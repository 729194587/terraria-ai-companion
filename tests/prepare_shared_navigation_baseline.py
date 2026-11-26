"""Export the accepted main with the identical native fixture and rolling-planner timer."""
import argparse
import io
import shutil
import subprocess
import zipfile
from pathlib import Path

BASELINE = "f26a632183156b06353be565f3db194f0bde45a4"
ROOT = Path(__file__).resolve().parents[1]


def export(destination):
    destination.mkdir(parents=True, exist_ok=False)
    data = subprocess.check_output(["git", "archive", "--format=zip", BASELINE, "CompanionBridge"], cwd=ROOT)
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        archive.extractall(destination)
    fixture = Path("CompanionBridge/ServerPlayerRuntime/TerrainApproachSelfTest.cs")
    shutil.copyfile(ROOT / fixture, destination / fixture)
    ipc = destination / "CompanionBridge/ServerIpc"
    task = ipc / "AutonomousNavigationTask.cs"
    source = task.read_text(encoding="utf8").replace("    object PlanningEvidence=>null;",
        "    object PlanningEvidence=>null;\n    object PlanningTime=>null;")
    task.write_text(source.replace("planning=world.PlanningEvidence,bankAir=",
        "planning=world.PlanningEvidence,rollingPlanning=world.PlanningTime,bankAir="), encoding="utf8")
    world = ipc / "NativeNavigationWorld.cs"
    measured = (ROOT / "CompanionBridge/ServerIpc/NativeNavigationWorld.cs").read_text(encoding="utf8")
    timer = measured[measured.index("    private double planningMs,maxSliceMs;"):measured.index("    private void EvaluateSlice(")]
    source = world.read_text(encoding="utf8").replace("    private void Evaluate(", timer + "    private void EvaluateSlice(")
    world.write_text(source, encoding="utf8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    export(parser.parse_args().destination)
