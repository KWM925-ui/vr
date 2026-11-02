"""Apply the CAD paddle silhouette to the motion saved in a software recording.

python.exe tools/compare_paddle_surface.py tools/closed_loop_checks/<run> --output tmp/surface

The existing rectangle must first reproduce the saved raw force and immersion.
Both shapes then use identical recorded motion, water and drag coefficients.
This is a fixed-motion geometry comparison, not a new trajectory simulation,
physical calibration, replacement acceptance verdict or hardware command.
Exports a reusable single-face mesh, its coordinate definition and numerical CSV.
"""

import argparse
import csv
import json
import math
from pathlib import Path
import re

import numpy as np
import yaml

from cad_paddle_geometry import CAD, ROOT, native_blade_bounds, read, sha
from cad_sources import native_source
from cable_allocation import optimum, recorded_cable_matrix
from paddle_surface import WaterSurface, integrate_drag, rectangle_mesh, rotation_matrix, silhouette_mesh, three
from run_closed_loop_check import envelope, parse


SCOPE = "CAD_THIN_PLATE_FIXED_RECORDED_MOTION_NOT_HARDWARE"
PROJECT = ROOT / "DragonBoatVR"
# Numerical reconstruction limits; these do not change a project acceptance gate.
MAX_FORCE_RECONSTRUCTION_N = .005
MAX_TORQUE_RECONSTRUCTION_NM = .005
MAX_WET_FRACTION_RECONSTRUCTION = .0001


def load_scene_component(blocks, script):
    metadata = script.with_suffix(script.suffix + ".meta")
    match = re.search(r"(?m)^guid: ([0-9a-f]{32})$", metadata.read_text(encoding="utf-8-sig"))
    if not match:
        raise ValueError("script has no unambiguous Unity GUID")
    selected = [block for block in blocks if "guid: " + match[1] in block]
    if len(selected) != 1:
        raise ValueError("scene must contain one " + script.stem)
    return yaml.safe_load(selected[0].split("\n", 1)[1])["MonoBehaviour"]


def context(artifact):
    result, verification = read(artifact / "result.json"), read(artifact / "verification.json")
    if result["result"] != "PASS" or verification["result"] != "PASS":
        raise ValueError("requires a completed software transport recording")
    command = verification.get("command", [])
    if "-environmentSweep" in command:
        raise ValueError("this comparison currently requires the constant initial environment")
    scene = PROJECT / "Assets/Scenes/Demo.unity"
    inputs = [scene, PROJECT / "Assets/Scripts/WaveField.cs", PROJECT / "Assets/Scripts/PaddleHydrodynamics.cs",
              PROJECT / "Assets/Scripts/SimulationSession.cs", PROJECT / "Assets/Scripts/RiverEnvironmentController.cs",
              PROJECT / "Assets/StreamingAssets/paddle_geometry.json", PROJECT / "Assets/StreamingAssets/river_environment.json"]
    for path in inputs:
        key = path.relative_to(PROJECT).as_posix()
        if sha(path) != verification["source_sha256"].get(key):
            raise ValueError("recorded model source differs from current input: " + key)
    observation_path = artifact / "observations.jsonl"
    if sha(observation_path) != result["observationsSha256"]:
        raise ValueError("original recording bytes changed")
    blocks = re.split(r"(?m)^--- ", scene.read_text(encoding="utf-8-sig"))
    wave = load_scene_component(blocks, inputs[1])
    hydro = load_scene_component(blocks, inputs[2])
    transforms = [yaml.safe_load(b.split("\n", 1)[1])["Transform"] for b in blocks
                  if b.startswith("!u!4 ") and re.search(r"m_GameObject: \{fileID: " + str(wave["m_GameObject"]["fileID"]) + r"\}", b)]
    if len(transforms) != 1 or transforms[0]["m_Father"]["fileID"] != 0:
        raise ValueError("water base height needs a resolved root transform")
    prototype, environment = read(inputs[5]), read(inputs[6])
    heading = math.radians(environment["defaultCurrentHeadingDegrees"])
    # SimulationSession.Bind installs these initial settings before the run.
    water = WaterSurface(wave["waves"], intensity=environment["calmIntensity"],
                         intensity_rate=0., time_scale=wave["timeScale"],
                         horizontal=bool(wave["horizontalDisplacement"]), iterations=wave["inverseIterations"],
                         base_height=transforms[0]["m_LocalPosition"]["y"],
                         current=[math.sin(heading) * environment["defaultCurrentSpeedMps"], 0.,
                                  math.cos(heading) * environment["defaultCurrentSpeedMps"]])
    match = re.search(r"const int GridX = (\d+), GridY = (\d+);", inputs[2].read_text(encoding="utf-8-sig"))
    if not match:
        raise ValueError("recorded rectangle discretization is unavailable")
    baseline = rectangle_mesh(prototype["bladeAreaM2"] / prototype["bladeLengthM"], prototype["bladeLengthM"],
                              int(match[1]), int(match[2]))
    coefficients = dict(density=hydro["waterDensity"], cd_flat=hydro["cdFlat"], cd_edge=hydro["cdEdge"])
    rig_path = PROJECT / ("Assets/StreamingAssets/tendon_rig_cad.json" if verification["cad_tendon_rig_requested"]
                          else "Assets/StreamingAssets/tendon_rig.json")
    if sha(rig_path) != verification["source_sha256"].get(rig_path.relative_to(PROJECT).as_posix()):
        raise ValueError("recorded blade-tip registration changed")
    tip = three(read(rig_path)["bladeTipLocal"])
    if np.linalg.norm(tip - [0., -prototype["bladeLengthM"] / 2, 0.]) > 1e-7:
        raise ValueError("recorded tendon and rectangular blade tips disagree")
    inputs.extend([rig_path, observation_path, artifact / "result.json", artifact / "verification.json",
                   inputs[1].with_suffix(".cs.meta"), inputs[2].with_suffix(".cs.meta")])
    return water, coefficients, baseline, prototype, tip, inputs, result


def cad_surfaces(tip, pitch):
    part_path = CAD / "key_parts/018f5bb8e110e62c.json"
    part = read(part_path)
    native = native_source(part["source"])
    mesh_path = (part_path.parent / part["mesh_file"]).resolve()
    if not native.is_relative_to(ROOT) or not mesh_path.is_relative_to(ROOT):
        raise ValueError("native geometry must stay in this workspace")
    if sha(native) != part["sha256"] or sha(mesh_path) != part["mesh_sha256"]:
        raise ValueError("CAD geometry differs from the saved extraction")
    native_mesh = np.fromfile(mesh_path, dtype="<f4").reshape(-1, 3, 3).astype(float)
    if len(native_mesh) != part["triangles"]:
        raise ValueError("native triangle count changed")
    bottom, top = native_blade_bounds(part)
    count = int(math.ceil((top - bottom) / pitch))
    count += count % 2
    # Same tip as the recorded paddle. Changing to a centroid origin without
    # shifting positions and linear velocities would change the physical motion.
    translation = tip - [0., bottom, 0.]
    fine = silhouette_mesh(native_mesh, bottom, top, count) + translation
    coarse = silhouette_mesh(native_mesh, bottom, top, count // 2) + translation
    definition = dict(scope=SCOPE, measured=False, projection_normal_part=[0., 0., 1.],
                      source_part_to_recorded_blade_translation_m=translation.tolist(),
                      reference_basis="same blade tip and axes as the recorded rig; design registration, not measured mounting",
                      blade_tip_part_y_m=bottom, blade_root_part_y_m=top,
                      fine_strip_pitch_m=(top - bottom) / count, coarse_strip_pitch_m=2 * (top - bottom) / count,
                      width_subdivisions_per_interval=4, fine_triangles=len(fine), coarse_triangles=len(coarse))
    return fine, coarse, definition, [part_path, native, mesh_path]


def frames(artifact):
    selected, signature = [], None
    with (artifact / "observations.jsonl").open(encoding="utf-8-sig") as stream:
        for line_number, line in enumerate(stream, 1):
            record = parse(line)
            if record.get("recordType") != "frame" or record["stage"] != "Normal":
                continue
            frame = record["frame"]
            if signature is not None and frame["configurationSignature"] != signature:
                raise ValueError("normal-stage parameters changed during the recording")
            signature = frame["configurationSignature"]
            if frame["sessionConfiguration"]["measured"] is not False:
                raise ValueError("only declared software sessions are supported")
            if len(frame["paddles"]) != 1 or len(frame["feedbackRequests"]) != 1:
                raise ValueError("requires the project's one-paddle session")
            paddle, request = frame["paddles"][0], frame["feedbackRequests"][0]
            if not paddle["valid"]:
                continue
            if not paddle["hasAngularVelocity"] or not request["valid"]:
                raise ValueError("valid motion and its complete wrench are required")
            if request["inputSequence"] != paddle["sequence"] or request["timeSeconds"] != paddle["timeSeconds"]:
                raise ValueError("paddle and force request are from different input samples")
            telemetry = frame["closedLoop"]
            projection = None
            if telemetry["commandSent"] and telemetry["encoderConsumed"]:
                command = envelope(telemetry["commandPacketBase64"], telemetry["sessionId"], "step", telemetry["commandSequence"])[1]
                source = envelope(telemetry["feedbackPacketBase64"], telemetry["sessionId"], "feedback", telemetry["feedbackSequence"])[1]
                if command["cableValid"]:
                    if (command["sourceFeedbackSequence"] != telemetry["feedbackSequence"]
                            or command["sourceSampleTick"] != source["plantSampleTick"]):
                        raise ValueError("outgoing force allocation used another source pose")
                    if not np.array_equal(command["minTensions"], [5., 5., 5.]) or not np.array_equal(command["maxTensions"], [300., 300., 300.]):
                        raise ValueError("recorded three-cable tension limits changed")
                    projection = dict(matrix=recorded_cable_matrix(source, command),
                                      world_from_rig=rotation_matrix(telemetry["observationBoatRotation"]),
                                      source_tick=source["plantSampleTick"])
            selected.append(dict(line=line_number, simulation_s=frame["timeSeconds"], step_s=frame["fixedStepSeconds"],
                                 phase=record["driverPhase01"], wave_s=frame["waveTimeSeconds"], paddle=paddle, request=request,
                                 raw_force=three(frame["rawWaterForceN"][0]), wet_fraction=record["wettedFraction"],
                                 projection=projection))
    if not selected:
        raise ValueError("no complete motion samples")
    return selected


def compare(samples, water, coefficients, baseline, fine, coarse, prototype):
    rows = []
    for index, sample in enumerate(samples):
        kwargs = dict(water=water, wave_time=sample["wave_s"], paddle=sample["paddle"],
                      grip=sample["request"]["gripWorldPosition"], **coefficients)
        rectangular = integrate_drag(baseline, **kwargs)
        force_error = float(np.linalg.norm(rectangular["force_world_n"] - sample["raw_force"]))
        wet_error = abs(rectangular["wet_area_m2"] / prototype["bladeAreaM2"] - sample["wet_fraction"])
        if force_error > MAX_FORCE_RECONSTRUCTION_N or wet_error > MAX_WET_FRACTION_RECONSTRUCTION:
            raise ValueError(f"baseline reconstruction differs at line {sample['line']}: force={force_error} N, wet fraction={wet_error}")
        recorded_norm = np.linalg.norm(sample["raw_force"])
        gain = np.linalg.norm(three(sample["request"]["forceWorldN"])) / recorded_norm if recorded_norm > 1e-6 else 0.
        torque_error = None
        if gain > 1e-6:
            # The current runtime applies one common scalar to the complete
            # raw wrench. No raw moment is invented when the force is zero.
            inferred = three(sample["request"]["torqueAtGripWorldNm"]) / gain
            torque_error = float(np.linalg.norm(rectangular["torque_at_grip_world_nm"] - inferred))
            if torque_error > MAX_TORQUE_RECONSTRUCTION_NM:
                raise ValueError(f"baseline raw moment reconstruction differs at line {sample['line']}: {torque_error} Nm")
        cad, lower = integrate_drag(fine, **kwargs), integrate_drag(coarse, **kwargs)
        row = dict(scope=SCOPE, line=sample["line"], simulation_s=sample["simulation_s"], step_s=sample["step_s"],
                   stroke_phase_01=sample["phase"], wave_s=sample["wave_s"],
                   baseline_force_reconstruction_error_n=force_error, baseline_wet_fraction_reconstruction_error=wet_error,
                   baseline_raw_moment_reconstruction_error_nm=torque_error,
                   cad_fine_coarse_force_difference_n=float(np.linalg.norm(cad["force_world_n"] - lower["force_world_n"])),
                   cad_fine_coarse_moment_difference_nm=float(np.linalg.norm(cad["torque_at_grip_world_nm"] - lower["torque_at_grip_world_nm"])),
                   cad_fine_coarse_area_difference_m2=abs(cad["wet_area_m2"] - lower["wet_area_m2"]))
        for name, value in (("rectangle", rectangular), ("cad", cad)):
            row[name + "_wet_area_m2"] = value["wet_area_m2"]
            row[name + "_raw_force_n"] = float(np.linalg.norm(value["force_world_n"]))
            row[name + "_raw_moment_at_grip_nm"] = float(np.linalg.norm(value["torque_at_grip_world_nm"]))
            row[name + "_relative_water_dissipation_w"] = value["relative_water_dissipation_w"]
            row[name + "_power_into_paddle_world_w"] = value["power_into_paddle_world_w"]
            for field in ("force_world_n", "torque_at_grip_world_nm", "wet_area_centroid_world_m"):
                for axis, component in zip(("x", "y", "z"), value[field] if value[field] is not None else (None,) * 3):
                    row[name + "_" + field + "_" + axis] = None if component is None else float(component)
        row["geometry_force_delta_n"] = float(np.linalg.norm(cad["force_world_n"] - rectangular["force_world_n"]))
        row["geometry_moment_delta_nm"] = float(np.linalg.norm(cad["torque_at_grip_world_nm"] - rectangular["torque_at_grip_world_nm"]))
        projection = sample["projection"]
        row["allocation_source_sample_tick"] = None
        row["minimum_cable_direction_y_rig"] = None
        for name, value in (("rectangle", rectangular), ("cad", cad)):
            row[name + "_raw_force_y_rig_n"] = None
            row[name + "_raw_force_only_lower_bound_error_n"] = None
            if projection is not None:
                force_rig = projection["world_from_rig"].T @ value["force_world_n"]
                error, _ = optimum(projection["matrix"][:3], force_rig, np.array([5., 5., 5.]), np.array([300., 300., 300.]))
                row[name + "_raw_force_y_rig_n"] = float(force_rig[1])
                row[name + "_raw_force_only_lower_bound_error_n"] = error
        if projection is not None:
            row["allocation_source_sample_tick"] = projection["source_tick"]
            row["minimum_cable_direction_y_rig"] = float(projection["matrix"][1].min())
        rows.append(row)
        if (index + 1) % 100 == 0:
            print(json.dumps(dict(processed=index + 1, total=len(samples))), flush=True)
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--pitch-mm", type=float, default=1.)
    args = parser.parse_args()
    artifact, output = args.artifact.resolve(), args.output.resolve()
    if not artifact.is_relative_to(ROOT) or not output.is_relative_to(ROOT) or output.exists():
        parser.error("use an existing recording and a new output directory inside this workspace")
    if not math.isfinite(args.pitch_mm) or not .1 <= args.pitch_mm <= 10:
        parser.error("strip pitch must be in 0.1..10 mm")
    water, coefficients, baseline, prototype, tip, inputs, original = context(artifact)
    fine, coarse, definition, cad_inputs = cad_surfaces(tip, args.pitch_mm / 1000)
    samples = frames(artifact)
    rows = compare(samples, water, coefficients, baseline, fine, coarse, prototype)
    inputs.extend(cad_inputs + [Path(__file__), ROOT / "tools/paddle_surface.py", ROOT / "tools/cad_paddle_geometry.py",
                               ROOT / "tools/cable_allocation.py", ROOT / "tools/cad_sources.py",
                               ROOT / "tools/run_closed_loop_check.py"])
    definition.update(source_sha256={str(p.relative_to(ROOT)): sha(p) for p in inputs},
                      coefficients=coefficients, coefficients_measured=False, normal_samples=len(rows),
                      observations_sha256=original["observationsSha256"],
                      force_only_projection="raw force at the matching input pose, 5..300 N per cable; excludes torque and feedback smoothing; diagnostic lower bound only",
                      reconstruction_limits=dict(force_n=MAX_FORCE_RECONSTRUCTION_N, moment_nm=MAX_TORQUE_RECONSTRUCTION_NM,
                                                 wet_fraction=MAX_WET_FRACTION_RECONSTRUCTION))
    output.mkdir(parents=True, exist_ok=False)
    mesh_path = output / "paddle_single_face.npy"
    np.save(mesh_path, fine, allow_pickle=False)
    csv_path = output / "fixed_motion_comparison.csv"
    with csv_path.open("x", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    definition.update(mesh_file=mesh_path.name, mesh_sha256=sha(mesh_path), comparison_file=csv_path.name, comparison_sha256=sha(csv_path))
    (output / "paddle_surface.json").write_text(json.dumps(definition, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    values = lambda key: [r[key] for r in rows if r[key] is not None]
    loaded = [r for r in rows if r["rectangle_raw_force_n"] > 5]
    ratios = [r["cad_raw_force_n"] / r["rectangle_raw_force_n"] - 1 for r in loaded]
    projected = [r for r in rows if r["cad_raw_force_n"] > 5 and r["cad_raw_force_only_lower_bound_error_n"] is not None]
    print(json.dumps(dict(scope=SCOPE, output=str(output), samples=len(rows),
                          max_baseline_force_error_n=max(values("baseline_force_reconstruction_error_n")),
                          max_baseline_moment_error_nm=max(values("baseline_raw_moment_reconstruction_error_nm"), default=None),
                          max_baseline_wet_fraction_error=max(values("baseline_wet_fraction_reconstruction_error")),
                          max_geometry_force_delta_n=max(values("geometry_force_delta_n")),
                          max_geometry_moment_delta_nm=max(values("geometry_moment_delta_nm")),
                          max_fine_coarse_force_difference_n=max(values("cad_fine_coarse_force_difference_n")),
                          cad_loaded_projected_samples=len(projected),
                          cad_raw_force_lower_bound_mean_n=float(np.mean([r["cad_raw_force_only_lower_bound_error_n"] for r in projected])) if projected else None,
                          cad_downward_raw_force_with_only_upward_cables=sum(r["cad_raw_force_y_rig_n"] < -1 and r["minimum_cable_direction_y_rig"] > 0 for r in projected),
                          loaded_samples=len(loaded), loaded_relative_force_change_min=min(ratios, default=None),
                          loaded_relative_force_change_max=max(ratios, default=None)), ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
