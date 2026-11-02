"""Use the native drum groove to convert cable demand into shaft demand.

python.exe tools/cable_drum.py tools/closed_loop_checks/<run> --output tmp/drum

No device is commanded. The conversion is an ideal single-layer, round, taut,
no-slip cable model. Native surfaces determine the groove, not the installed
cable diameter, effective radius, gearing, friction or motor capability.
Saved lengths give interval-average speeds, never unobserved speed peaks.
"""

import argparse
import csv
import json
import math
from pathlib import Path

import numpy as np

from build_cad_tendon_profile import transform
from cad_paddle_geometry import CAD, ROOT, read, sha
from cad_sources import native_source
from run_closed_loop_check import envelope, parse


SCOPE = "CAD_SINGLE_LAYER_DRUM_WITH_SOFTWARE_LOAD_NOT_HARDWARE_CALIBRATION"


def groove_from_surfaces(part):
    """Find a complete cylindrical groove bounded by two larger coaxial rims.

    This native part has its axis along X. Off-axis holes and partial cylinders
    cannot supply a winding radius. A matching native 3-D circle and both side
    planes are required in addition to the three cylindrical faces.
    """
    candidates = []
    for body in part["bodies"]:
        surfaces = body["surfaces"]
        coaxial = [f for f in surfaces if "cylinder" in f
                   and np.allclose(np.abs(f["cylinder"][3:6]), [1., 0., 0.], atol=1e-9, rtol=0)
                   and np.linalg.norm(f["cylinder"][1:3]) < 1e-9]
        for bottom in coaxial:
            x0, x1 = bottom["box"][0], bottom["box"][3]
            radius = bottom["cylinder"][6]
            if radius <= 0 or x1 <= x0:
                continue
            # Full circumference is independently checked using native area.
            if not math.isclose(bottom["area_m2"], 2 * math.pi * radius * (x1 - x0), rel_tol=1e-7):
                continue
            left = [f for f in coaxial if f["cylinder"][6] > radius + 1e-9
                    and abs(f["box"][3] - x0) < 1e-9]
            right = [f for f in coaxial if f["cylinder"][6] > radius + 1e-9
                     and abs(f["box"][0] - x1) < 1e-9]
            if len(left) != 1 or len(right) != 1:
                continue
            rim_radius = left[0]["cylinder"][6]
            if not math.isclose(rim_radius, right[0]["cylinder"][6], abs_tol=1e-9, rel_tol=0):
                continue
            side_faces = []
            for x in (x0, x1):
                planes = [f for f in surfaces if "plane" in f
                          and np.allclose(np.abs(f["plane"][:3]), [1., 0., 0.], atol=1e-9, rtol=0)
                          and abs(f["plane"][3] - x) < 1e-9
                          and abs(f["box"][0] - x) < 1e-9 and abs(f["box"][3] - x) < 1e-9
                          and abs(f["box"][4] - rim_radius) < 1e-9
                          and abs(f["box"][1] + rim_radius) < 1e-9]
                if len(planes) != 1:
                    break
                side_faces.append(planes[0]["face"])
            if len(side_faces) != 2:
                continue
            circles = []
            for feature in part["features"]:
                if feature["type"] != "3DProfileFeature":
                    continue
                for segment in feature.get("sketch", {}).get("segments", []):
                    circle = segment.get("curve", {}).get("circle")
                    if (circle and np.allclose(circle[:3], [(x0 + x1) / 2, 0., 0.], atol=1e-9, rtol=0)
                            and np.allclose(np.abs(circle[3:6]), [1., 0., 0.], atol=1e-9, rtol=0)
                            and math.isclose(circle[6], radius, abs_tol=1e-9, rel_tol=0)):
                        circles.append(feature["name"])
            if not circles:
                continue
            candidates.append(dict(bottom_radius_m=radius, rim_radius_m=rim_radius,
                                   groove_width_m=x1 - x0, groove_depth_m=rim_radius - radius,
                                   groove_axial_bounds_m=[x0, x1], axis_part=[1., 0., 0.],
                                   centre_part_m=[(x0 + x1) / 2, 0., 0.], body_index=body["index"],
                                   bottom_face=bottom["face"], rim_faces=[left[0]["face"], right[0]["face"]],
                                   side_faces=side_faces, matching_native_sketches=circles))
    if len(candidates) != 1:
        raise ValueError("native surfaces and sketch do not identify one complete groove")
    return candidates[0]


def single_layer_radius(groove, diameter_m):
    """Cable centreline when a round cable sits on the bottom in one layer."""
    maximum = min(groove["groove_width_m"], groove["groove_depth_m"])
    if not math.isfinite(diameter_m) or diameter_m <= 0 or diameter_m > maximum + 1e-12:
        raise ValueError("round cable diameter must fit the groove width and depth")
    return groove["bottom_radius_m"] + diameter_m / 2


def ideal_shaft_load(radius_m, tension_n, cable_rate_m_s):
    """Positive shaft rotation winds IN; positive free-span rate pays OUT.

    The cable load needs T*r holding torque. Its ideal transmitted power is
    -T*length_rate, including the generating quadrant. Inertia and losses are
    excluded. A speed averaged over an interval cannot be combined with a
    single endpoint tension and labelled instantaneous or integrated power.
    """
    if (not all(math.isfinite(x) for x in (radius_m, tension_n, cable_rate_m_s))
            or radius_m <= 0 or tension_n < 0):
        raise ValueError("positive finite radius, nonnegative tension and finite cable rate required")
    torque, omega = tension_n * radius_m, -cable_rate_m_s / radius_m
    return dict(holding_torque_nm=torque, winding_angular_rate_rad_s=omega,
                winding_rpm=omega * 30 / math.pi, ideal_transmitted_power_w=torque * omega)


def interval_winding(previous, current, radius_m):
    """Return observed interval means only within a single recording segment."""
    if not math.isfinite(radius_m) or radius_m <= 0:
        raise ValueError("positive finite radius required")
    if previous is None or previous["segment_key"] != current["segment_key"]:
        return None
    dt = current["controller_s"] - previous["controller_s"]
    ticks = current["tick"] - previous["tick"]
    if dt <= 0 or ticks <= 0:
        raise ValueError("distinct, increasing controller samples required")
    delta = np.asarray(current["lengths_m"]) - np.asarray(previous["lengths_m"])
    if delta.shape != (3,) or not np.isfinite(delta).all() or not math.isfinite(dt):
        raise ValueError("three finite length increments required")
    angles = -delta / radius_m
    return dict(interval_s=dt, ticks=ticks, winding_radians=angles,
                mean_winding_rpm=angles / dt * 30 / math.pi)


def native_drum():
    path = CAD / "key_parts/f7fe903025d28442.json"
    part = read(path)
    source = native_source(part["source"])
    if not source.is_relative_to(ROOT) or sha(source) != part["sha256"]:
        raise ValueError("native wheel source is outside the workspace or has changed")
    groove = groove_from_surfaces(part)
    assembly_path = CAD / "native_state_probe/after_rebuild.json"
    reconciliation_path = CAD / "reference_reconciliation/reconciliation.json"
    observations_path = CAD / "cad_observations.json"
    native_assembly = ROOT / "10月11日修改/2.1 铝型材框架.SLDASM"
    observations, reconciliation = read(observations_path), read(reconciliation_path)
    if (sha(assembly_path) != reconciliation["identity"]["native_state_probe/after_rebuild.json"]
            or sha(reconciliation_path) != observations["source_identity"]["reference_reconciliation_sha256"]
            or sha(native_assembly) != observations["source_identity"]["assembly_native_sha256"]):
        raise ValueError("native assembly differs from its reconciled source identity")
    instances = []
    for component in read(assembly_path)["components"]:
        if component["path"] != part["source"]:
            continue
        if component["suppression"] != 2:
            raise ValueError("wheel instance is not fully resolved")
        r, t = transform(component)
        instances.append(dict(component=component["name"],
                              centre_assembly_m=(r @ groove["centre_part_m"] + t).tolist(),
                              axis_assembly=(r @ groove["axis_part"]).tolist(), protocol_motor_id=None))
    if len(instances) != 3:
        raise ValueError("expected three resolved instances of the extracted wheel")
    sources = [source, path, assembly_path, reconciliation_path, observations_path,
               native_assembly, ROOT / "tools/build_cad_tendon_profile.py",
               ROOT / "tools/cad_sources.py"]
    groove["instances"] = instances
    return groove, sources


def normal_samples(artifact):
    source = artifact / "observations.jsonl"
    result, verification = read(artifact / "result.json"), read(artifact / "verification.json")
    if (sha(source) != result["observationsSha256"] or result["result"] != "PASS"
            or verification["result"] != "PASS"):
        raise ValueError("requires an unchanged completed software transport recording")
    samples, seen = [], set()
    normal_segment, was_normal = 0, False
    with source.open(encoding="utf-8-sig") as stream:
        for line_number, line in enumerate(stream, 1):
            observation = parse(line)
            if observation.get("recordType") != "frame":
                continue
            frame = observation["frame"]
            if observation["stage"] != "Normal":
                was_normal = False
                continue
            if not was_normal:
                normal_segment += 1
            was_normal = True
            telemetry = frame["closedLoop"]
            if not (telemetry["feedbackFresh"] and telemetry["encoderConsumed"] and telemetry.get("feedbackPacketBase64")):
                continue
            session = telemetry["sessionId"]
            feedback = envelope(telemetry["feedbackPacketBase64"], session, "feedback", telemetry["feedbackSequence"])[1]
            if feedback["measured"] is not False:
                raise ValueError("only explicitly simulated controller samples are supported")
            tick = feedback["plantSampleTick"]
            if not feedback["valid"] or (session, tick) in seen:
                continue
            seen.add((session, tick))
            command = envelope(telemetry["acknowledgedCommandPacketBase64"], session, "step", feedback["commandSequence"])[1]
            if (command["sourceSampleTick"] != feedback["commandSourceSampleTick"]
                    or command["sourceFeedbackSequence"] != feedback["commandSourceFeedbackSequence"]):
                raise ValueError("feedback and command sources differ")
            vectors = [feedback[k] for k in ("cableLengthsMm", "tensionN", "effectiveTensionTargets")]
            vectors += [command[k] for k in ("minTensions", "maxTensions")]
            if any(len(v) != 3 or not all(math.isfinite(x) and x >= 0 for x in v) for v in vectors):
                raise ValueError("three finite nonnegative cable channels required")
            if any(lo > hi for lo, hi in zip(vectors[3], vectors[4])):
                raise ValueError("recorded tension limits are reversed")
            samples.append(dict(line=line_number, session=session, tick=tick,
                                segment_key=(normal_segment, session, frame["configurationSignature"]),
                                feedback_sequence=telemetry["feedbackSequence"],
                                command_sequence=feedback["commandSequence"],
                                controller_s=feedback["controllerTimeSeconds"],
                                lengths_m=np.asarray(vectors[0]) / 1000,
                                tensions_n=vectors[1], effective_targets_n=vectors[2],
                                minimum_targets_n=vectors[3], maximum_targets_n=vectors[4],
                                capability_limited=feedback["capabilityLimited"],
                                execution_enabled=feedback["cableExecutionEnabled"],
                                hardware_stop_active=feedback["hardwareStopActive"]))
    if not samples:
        raise ValueError("no usable normal-stage software cable feedback")
    return samples


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cable-diameter-mm", type=float,
                        help="explicit round-cable diameter; still assumes one seated layer and no slip")
    args = parser.parse_args()
    artifact, output = args.artifact.resolve(), args.output.resolve()
    if not artifact.is_relative_to(ROOT) or not output.is_relative_to(ROOT) or output.exists():
        parser.error("input and a new output directory must stay in the workspace")
    groove, sources = native_drum()
    maximum_diameter = min(groove["groove_width_m"], groove["groove_depth_m"])
    if args.cable_diameter_mm is None:
        radii = {"zero_diameter_limit": groove["bottom_radius_m"],
                 "largest_seated_round_cable": single_layer_radius(groove, maximum_diameter)}
    else:
        radii = {"user_diameter_single_layer": single_layer_radius(groove, args.cable_diameter_mm / 1000)}
    samples = normal_samples(artifact)
    rows = []
    for name, radius in radii.items():
        previous = None
        for sample in samples:
            interval = interval_winding(previous, sample, radius)
            for i, tension in enumerate(sample["tensions_n"]):
                rows.append(dict(scope=SCOPE, line=sample["line"], session_id=sample["session"],
                                 plant_sample_tick=sample["tick"], feedback_sequence=sample["feedback_sequence"],
                                 command_sequence=sample["command_sequence"], software_cable_index=i,
                                 execution_enabled=sample["execution_enabled"],
                                 software_capability_limited=sample["capability_limited"],
                                 hardware_stop_active=sample["hardware_stop_active"],
                                 controller_s=sample["controller_s"], radius_case=name, radius_m=radius,
                                 cable_length_m=float(sample["lengths_m"][i]), sampled_tension_n=tension,
                                 ideal_holding_torque_nm=ideal_shaft_load(radius, tension, 0.)["holding_torque_nm"],
                                 ideal_effective_target_torque_nm=sample["effective_targets_n"][i] * radius,
                                 configured_min_target_torque_nm=sample["minimum_targets_n"][i] * radius,
                                 configured_max_target_torque_nm=sample["maximum_targets_n"][i] * radius,
                                 interval_s=interval["interval_s"] if interval else None,
                                 interval_plant_ticks=interval["ticks"] if interval else None,
                                 interval_winding_rad=float(interval["winding_radians"][i]) if interval else None,
                                 interval_mean_winding_rpm=float(interval["mean_winding_rpm"][i]) if interval else None))
            previous = sample
    output.mkdir(parents=True, exist_ok=False)
    csv_path = output / "recorded_drum_demand.csv"
    with csv_path.open("x", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    sources += [artifact / name for name in ("observations.jsonl", "result.json", "verification.json")]
    sources += [Path(__file__), ROOT / "tools/cad_paddle_geometry.py", ROOT / "tools/run_closed_loop_check.py"]
    results = {}
    for name in radii:
        selected = [r for r in rows if r["radius_case"] == name]
        results[name] = dict(radius_m=radii[name],
                             sampled_ideal_holding_torque_max_nm=max(r["ideal_holding_torque_nm"] for r in selected),
                             configured_min_target_torque_nm=min(r["configured_min_target_torque_nm"] for r in selected),
                             configured_max_target_torque_nm=max(r["configured_max_target_torque_nm"] for r in selected),
                             observed_interval_mean_abs_rpm_max=max(abs(r["interval_mean_winding_rpm"]) for r in selected
                                                                    if r["interval_mean_winding_rpm"] is not None))
    data = dict(scope=SCOPE, measured=False, groove=groove, radius_cases_m=radii,
                radius_condition="round cable sits at groove bottom in one layer; no slip; zero-diameter endpoint is open",
                installed_cable_diameter_m=None if args.cable_diameter_mm is None else args.cable_diameter_mm / 1000,
                installed_effective_radius_calibrated=False, protocol_motor_mapping_verified=False,
                speed_scope="averages between retained samples, including gaps; not instantaneous peaks",
                torque_scope="cable load at wheel shaft; excludes inertia, friction, gearing and drive losses",
                load_scope="existing software allocation, including capability-limited states; not the full desired water wrench or a motor sizing verdict",
                power_or_energy_from_interval_endpoints=False, motor_capability_verified=False,
                per_radian_cable_travel_m=radii,
                circumference_m={k: 2 * math.pi * r for k, r in radii.items()},
                outer_radius_relative_to_bottom=groove["rim_radius_m"] / groove["bottom_radius_m"] - 1,
                input_samples=len(samples), results=results,
                source_sha256={str(p.relative_to(ROOT)): sha(p) for p in sources},
                result_file=csv_path.name, result_sha256=sha(csv_path))
    (output / "drum_geometry.json").write_text(json.dumps(data, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps(dict(scope=SCOPE, groove_radius_m=groove["bottom_radius_m"],
                          groove_width_m=groove["groove_width_m"], samples=len(samples),
                          results=results, output=str(output)), ensure_ascii=False))


if __name__ == "__main__":
    main()
