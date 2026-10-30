"""Run new closed-loop boundary checks in an exclusive Unity Editor."""
import argparse
import json
from pathlib import Path
import subprocess
import time
import uuid
from run_all_checks import PROJECT, WORKSPACE, assess_log, read_lines, resolve_unity
from unity_execution import unity_project_guard

CHECKS = {
    "interior": ("InteriorWaterReview", "[INTERIOR_WATER]"),
    "experience": ("ExperienceSelfTest", "[EXPERIENCE]"),
    "variants": ("BoatVariantsSelfTest", "[BOAT_VARIANTS]"),
    "rocks": ("NatureRockSelfTest", "[NATURE_ROCKS]"),
    "shoreview": ("ShoreCameraSelfTest", "[SHORE_CAMERA]"),
    "river": ("RiverCollisionSelfTest", "[RIVER_COLLISION]"),
    "input": ("PaddleInputBoundarySelfTest", "[PADDLEINPUT]"),
    "platform": ("PlatformRejectionSelfTest", "[PLATFORM_REJECTION]"),
    "feedback": ("SoftwareDeviceFeedbackSelfTest", "[DEVICEFEEDBACK]"),
    "solver": ("SolverOptimalitySelfTest", "[SOLVEROPTIMAL]"),
    "loop": ("ClosedLoopBoundarySelfTest", "[CLOSEDLOOP_BOUNDARY]"),
    "observation": ("CableObservationPoseSelfTest", "[CABLE_OBSERVATION]"),
    "render": ("PaddleExecutionPoseSelfTest", "[PADDLE_EXECUTION_POSE]"),
    "height": ("WaveHeightSamplerSelfTest", "[WAVEHEIGHT]"),
    "reflection": ("PlanarReflectionMathSelfTest", "[PLANAR_REFLECTION]"),
    "xr": ("XrReadinessSelfTest", "[XR_READINESS]"),
    "view": ("PlatformViewSelfTest", "[PLATFORMVIEW]"),
    "foam": ("InteractionFoamTrailSelfTest", "[FOAM_TRAIL]"),
    "hydro": ("HydroForceCoverageSelfTest", "[HYDROSWEEP]"),
    "placement": ("PaddlePlacementSelfTest", "[PADDLE_PLACEMENT]"),
    "hull": ("BoatMotionAxesSelfTest", "[BOAT_MOTION_AXES]"),
    "sound": ("RiverAudioMotionSelfTest", "[AUDIO_MOTION]"),
    "contact": ("BoatCollisionShapeSelfTest", "[BOAT_CONTACT]"),
    "surface": ("WaterSurfaceConsistencySelfTest", "[WATER_SURFACE_GPU]"),
}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("checks", nargs="*", choices=list(CHECKS))
    parser.add_argument("--boat", choices=["majesty", "ornament"], default="majesty")
    args = parser.parse_args()
    if args.boat != "majesty" and (not args.checks or any(name != "rocks" for name in args.checks)):
        parser.error("The per-boat selector currently applies to the complete rocks checks only.")
    out = WORKSPACE / "tools/targeted_checks" / (time.strftime("%Y%m%dT%H%M%S") + "_" + uuid.uuid4().hex[:8])
    out.mkdir(parents=True)
    print("ARTIFACTS=" + str(out), flush=True)
    reports = []
    for name in args.checks or CHECKS:
        method, marker = CHECKS[name]
        log = out / (name + ".log")
        graphics = [] if name in ("surface", "interior", "xr") else ["-nographics"]
        extra = ["-xrReadinessArtifacts", str(out / "xr_gpu")] if name == "xr" else []
        with unity_project_guard(PROJECT):
            result = subprocess.run([str(resolve_unity()), "-batchmode", *graphics, "-quit", "-job-worker-count", "2",
                "-kinematic-fixture", "-projectPath", str(PROJECT), "-executeMethod", "DragonBoatVR.EditorTools." + method + ".Run",
                "-testBoat", args.boat, *extra, "-logFile", str(log)], stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, timeout=150,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        report = assess_log(read_lines(log), marker, result.returncode)
        report["check"] = name
        reports.append(report)
        print(name, report, flush=True)
    (out / "results.json").write_text(json.dumps(reports, ensure_ascii=False, indent=2), encoding="utf-8")
    return int(any(item["result"] != "PASS" for item in reports))

if __name__ == "__main__":
    raise SystemExit(main())
