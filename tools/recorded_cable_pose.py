"""Use saved software cable lengths and orientation to reconstruct both positions.

python.exe tools/recorded_cable_pose.py tools/closed_loop_checks/<run> --output tmp/cable_pose

Position XYZ encoder fields are not used as reconstruction inputs. The saved
grip position is used only as independent software-record ground truth to score
the two candidates. This does not identify a real motor count/length calibration
or choose the correct branch for a hardware device.
"""

import argparse
import csv
import json
from pathlib import Path

import numpy as np

from cable_pose import pose_error_jacobians, position_candidates, range_sensitivity, sphere_centres
from cad_paddle_geometry import ROOT, read, sha
from run_closed_loop_check import envelope, euler_q, parse, rotate


SCOPE = "RECORDED_SOFTWARE_RANGE_INVERSION_NOT_HARDWARE_CALIBRATION"


def analyse(artifact):
    original, verification = read(artifact / "result.json"), read(artifact / "verification.json")
    source = artifact / "observations.jsonl"
    identity = sha(source)
    if identity != original["observationsSha256"] or original["result"] != "PASS" or verification["result"] != "PASS":
        raise ValueError("requires an unchanged completed software transport recording")
    rows, seen = [], set()
    with source.open(encoding="utf-8-sig") as stream:
        for line_number, line in enumerate(stream, 1):
            observation = parse(line)
            if observation.get("recordType") != "frame" or observation["stage"] != "Normal":
                continue
            telemetry = observation["frame"]["closedLoop"]
            if not (telemetry["feedbackFresh"] and telemetry["encoderConsumed"] and telemetry.get("feedbackPacketBase64")):
                continue
            session = telemetry["sessionId"]
            feedback = envelope(telemetry["feedbackPacketBase64"], session, "feedback", telemetry["feedbackSequence"])[1]
            if feedback["measured"] is not False:
                raise ValueError("the input must declare a software controller")
            key = session, feedback["plantSampleTick"]
            if key in seen or not feedback["valid"]:
                continue
            seen.add(key)
            command = envelope(telemetry["acknowledgedCommandPacketBase64"], session, "step", feedback["commandSequence"])[1]
            if (command["sourceSampleTick"] != feedback["commandSourceSampleTick"]
                    or command["sourceFeedbackSequence"] != feedback["commandSourceFeedbackSequence"]):
                raise ValueError("feedback and acknowledged command geometry are not paired")
            # The only orientation input is the recorded 0.01 degree example
            # encoder, not an unrecorded exact internal controller quaternion.
            q = euler_q([x / feedback["countsPerDegree"] for x in feedback["rawEncoderCounts"][3:]])
            rotation = np.array([rotate(q, axis) for axis in np.eye(3)]).T
            anchors = np.array(command["anchors"]).reshape(-1, 3)
            attachments = np.array(command["attachments"]).reshape(-1, 3) - [0., command["shaftLength"], 0.]
            lengths = np.array(feedback["cableLengthsMm"]) / 1000
            if len(lengths) != 3:
                raise ValueError("the saved project run must contain three drive cables")
            truth = np.array(feedback["gripPosition"])
            centres = sphere_centres(anchors, attachments, rotation)
            sensitivity = range_sensitivity(truth, centres)
            errors = pose_error_jacobians(truth, anchors, attachments, rotation)
            row = dict(scope=SCOPE, line=line_number, session_id=session, plant_sample_tick=feedback["plantSampleTick"],
                       controller_s=feedback["controllerTimeSeconds"], feedback_sequence=telemetry["feedbackSequence"],
                       command_sequence=feedback["commandSequence"], minimum_recorded_tension_n=min(feedback["tensionN"]),
                       orientation_quantization_deg=1 / feedback["countsPerDegree"],
                       status="", candidate_count=0, best_candidate_error_to_recorded_grip_m=None,
                       other_candidate_error_to_recorded_grip_m=None, candidate_separation_m=None,
                       length_jacobian_rank=sensitivity["rank"],
                       local_length_error_amplification=sensitivity["amplification"],
                       local_range_condition_number=sensitivity["condition_number"],
                       local_attitude_position_gain_m_per_rad=errors["worst_attitude_position_gain_m_per_rad"],
                       maximum_range_residual_m=None)
            for i, axis in enumerate("xyz"):
                for j, rotation_axis in enumerate("xyz"):
                    matrix = errors["position_from_attitude_m_per_rad"]
                    row[f"position_{axis}_per_rotation_{rotation_axis}_m_per_rad"] = None if matrix is None else float(matrix[i, j])
            for i in range(2):
                for axis in "xyz":
                    row[f"candidate_{i}_grip_{axis}_m"] = None
            for axis, value in zip("xyz", truth):
                row[f"recorded_grip_{axis}_m"] = float(value)
            try:
                solution = position_candidates(anchors, attachments, rotation, lengths)
                row.update(status=solution["status"], candidate_count=len(solution["candidates"]),
                           maximum_range_residual_m=solution["maximum_range_residual_m"])
                candidates = solution["candidates"]
                # Preserve solver order. Ground truth may score a branch, never
                # select the branch supplied to an acquisition/actuation path.
                errors = sorted(float(np.linalg.norm(p - truth)) for p in candidates)
                if errors:
                    row["best_candidate_error_to_recorded_grip_m"] = errors[0]
                if len(errors) == 2:
                    row["other_candidate_error_to_recorded_grip_m"] = errors[1]
                    row["candidate_separation_m"] = float(np.linalg.norm(candidates[1] - candidates[0]))
                for i, point in enumerate(candidates):
                    for axis, value in zip("xyz", point):
                        row[f"candidate_{i}_grip_{axis}_m"] = float(value)
            except ValueError as error:
                # Quantized orientation can make otherwise exact simulated
                # lengths inconsistent. Preserve this failure as data.
                row["status"] = "INCONSISTENT_RANGES: " + str(error)
            rows.append(row)
    if not rows:
        raise ValueError("no complete normal-stage software feedback samples")
    return rows, identity


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    artifact, output = args.artifact.resolve(), args.output.resolve()
    if not artifact.is_relative_to(ROOT) or not output.is_relative_to(ROOT) or output.exists():
        parser.error("input and a new output directory must stay in the workspace")
    rows, identity = analyse(artifact)
    output.mkdir(parents=True, exist_ok=False)
    csv_path = output / "position_candidates.csv"
    with csv_path.open("x", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    inputs = [artifact / "observations.jsonl", artifact / "result.json", artifact / "verification.json",
              Path(__file__), ROOT / "tools/cable_pose.py", ROOT / "tools/run_closed_loop_check.py"]
    calibration = dict(scope=SCOPE, measured=False, input_position_encoder_channels_used=False,
                       inputs="three simulated straight-span lengths plus the quantized simulated orientation",
                       saved_grip_position_role="verification only; not an input to the sphere intersections",
                       sensitivity_reference="local derivative at saved grip; fixed guide frame; attitude errors are rotation vectors in radians, not Euler increments",
                       sensitivity_scope="same position branch, ideal taut straight lengths; no measured sensor noise, timing, stretch or routing error model",
                       physical_length_calibration=None, physical_imu_mounting=None, selected_hardware_branch=None,
                       observations_sha256=identity, source_sha256={str(p.relative_to(ROOT)): sha(p) for p in inputs},
                       result_file=csv_path.name, result_sha256=sha(csv_path))
    (output / "range_geometry.json").write_text(json.dumps(calibration, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    errors = [r["best_candidate_error_to_recorded_grip_m"] for r in rows if r["best_candidate_error_to_recorded_grip_m"] is not None]
    amplification = [r["local_length_error_amplification"] for r in rows if r["local_length_error_amplification"] is not None]
    separation = [r["candidate_separation_m"] for r in rows if r["candidate_separation_m"] is not None]
    attitude_gain = [r["local_attitude_position_gain_m_per_rad"] for r in rows if r["local_attitude_position_gain_m_per_rad"] is not None]
    print(json.dumps(dict(scope=SCOPE, samples=len(rows), two_solution_samples=sum(r["candidate_count"] == 2 for r in rows),
                          inconsistent_samples=sum(r["status"].startswith("INCONSISTENT") for r in rows),
                          best_candidate_error_mean_m=float(np.mean(errors)) if errors else None,
                          best_candidate_error_max_m=max(errors, default=None),
                          local_length_amplification_min=min(amplification, default=None),
                          local_length_amplification_max=max(amplification, default=None),
                          local_attitude_gain_min_m_per_rad=min(attitude_gain, default=None),
                          local_attitude_gain_max_m_per_rad=max(attitude_gain, default=None),
                          candidate_separation_min_m=min(separation, default=None),
                          candidate_separation_max_m=max(separation, default=None), output=str(output)), ensure_ascii=False))


if __name__ == "__main__":
    main()
