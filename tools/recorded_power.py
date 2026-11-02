"""Extract power and travel from a completed software closed-loop recording.

Example: python.exe tools/recorded_power.py tools/closed_loop_checks/<run> --output tmp/power
Writes two numerical CSVs. Never runs Unity, commands a device or changes a verdict.
Water-relative dissipation, world-frame requested work and sampled cable power
in the controller's rig frame are different quantities. None is metabolic energy
or electrical motor power. Sparse feedback cannot recover total delivered work.
"""

import argparse
import csv
import hashlib
import json
import math
from pathlib import Path

from run_closed_loop_check import envelope, parse


ROOT = Path(__file__).resolve().parents[1]


def vector(value):
    result = [float(value[k]) for k in ("x", "y", "z")] if isinstance(value, dict) else list(value)
    if len(result) != 3 or not all(math.isfinite(x) for x in result):
        raise ValueError("finite three-vector required")
    return result


def dot(a, b):
    return math.fsum(x * y for x, y in zip(a, b))


def cross(a, b):
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]


def requested_power(paddle, request):
    """Rigid-body power F.v_grip + torque_at_grip.omega, in world coordinates."""
    force, torque = vector(request["forceWorldN"]), vector(request["torqueAtGripWorldNm"])
    blade, grip = vector(paddle["bladePosition"]), vector(request["gripWorldPosition"])
    velocity, omega = vector(paddle["bladeVelocity"]), vector(paddle["bladeAngularVelocity"])
    offset = [g - b for g, b in zip(grip, blade)]
    correction = cross(omega, offset)
    grip_velocity = [v + c for v, c in zip(velocity, correction)]
    return dot(force, grip_velocity) + dot(torque, omega)


def remember_command(telemetry, segment, step, commands, previous):
    """Track every recorded outgoing command, including those without a reply.

    A missing command, nonzero request, invalid request or simulation-time gap
    breaks the observed zero-request duration. Feedback-only sampling would
    miss short nonzero commands between two acknowledged zero requests.
    """
    if not telemetry.get("commandSent"):
        return previous
    encoded = telemetry["commandPacketBase64"]
    session = telemetry["sessionId"]
    sequence, command = envelope(encoded, session, "step", telemetry["commandSequence"])
    key = (session, sequence)
    if key in commands:
        if commands[key][0] != encoded:
            raise ValueError("recorded command identity reused with different bytes")
        return previous
    time = command["simulationTime"]
    if not math.isfinite(time) or time < 0:
        raise ValueError("invalid outgoing command simulation time")
    zero = (command["active"] and command["cableValid"]
            and math.hypot(*vector(command["requestForce"])) < 1e-6
            and math.hypot(*vector(command["requestTorque"])) < 1e-6)
    age = 0. if zero else None
    if zero and previous is not None:
        old_segment, old_session, old_sequence, old_time, old_age = previous
        if (old_age is not None and (old_segment, old_session) == (segment, session)
                and (sequence - old_sequence) & 0xFFFFFFFF == 1
                and math.isclose(time - old_time, step, rel_tol=1e-4, abs_tol=1e-6)):
            age = old_age + time - old_time
    commands[key] = (encoded, age)
    return segment, session, sequence, time, age


def paired_feedback_power(telemetry, feedback, commands):
    """Compare the acknowledged wrench and actual cable wrench at one twist.

    Both use the current grip velocity in the controller's rig frame. This
    comparison is not the source-time water dissipation or a passivity verdict.
    Mechanical-stop reactions are deliberately excluded from cable power.
    """
    if feedback["measured"] is not False:
        raise ValueError("this extractor is restricted to declared software-controller records")
    session, sequence = telemetry["sessionId"], feedback["commandSequence"]
    encoded = telemetry["acknowledgedCommandPacketBase64"]
    command = envelope(encoded, session, "step", sequence)[1]
    known = commands.get((session, sequence))
    if known is not None and known[0] != encoded:
        raise ValueError("acknowledged command differs from recorded outgoing bytes")
    if (feedback["commandSourceFeedbackSequence"] != command["sourceFeedbackSequence"]
            or feedback["commandSourceSampleTick"] != command["sourceSampleTick"]):
        raise ValueError("feedback acknowledges a different command source")
    source_encoded = telemetry.get("acknowledgedSourcePacketBase64")
    if source_encoded:
        source = envelope(source_encoded, session, "feedback", command["sourceFeedbackSequence"])[1]
        if (source["plantSampleTick"] != command["sourceSampleTick"]
                or source["plantSampleTick"] > feedback["plantSampleTick"]):
            raise ValueError("acknowledged command source tick mismatch")
    elif command["cableValid"]:
        raise ValueError("valid cable command has no original source observation")
    velocity, omega = vector(feedback["gripVelocity"]), vector(feedback["gripAngularVelocity"])
    translation = dot(vector(feedback["forceLocal"]), velocity)
    rotation = dot(vector(feedback["torqueLocal"]), omega)
    request_force, request_torque = vector(command["requestForce"]), vector(command["requestTorque"])
    requested = dot(request_force, velocity) + dot(request_torque, omega)
    if not all(math.isfinite(x) for x in (translation, rotation, requested)):
        raise ValueError("nonfinite recorded power")
    return dict(command_seen_in_recording=known is not None,
                acknowledged_command_simulation_s=command["simulationTime"],
                command_source_feedback_sequence=command["sourceFeedbackSequence"],
                command_source_sample_tick=command["sourceSampleTick"],
                acknowledged_cable_request_valid=command["cableValid"],
                acknowledged_zero_request_observed_s=known[1] if known else None,
                acknowledged_request_force_n=math.hypot(*request_force),
                acknowledged_request_torque_nm=math.hypot(*request_torque),
                cable_translation_power_rig_w=translation, cable_rotation_power_rig_w=rotation,
                cable_power_into_paddle_rig_w=translation + rotation,
                acknowledged_request_power_at_current_grip_rig_w=requested,
                cable_minus_acknowledged_request_power_w=translation + rotation - requested)


def extract(artifact):
    source = artifact / "observations.jsonl"
    with source.open("rb") as stream:
        identity = hashlib.file_digest(stream, "sha256").hexdigest()
    result = parse((artifact / "result.json").read_text(encoding="utf-8-sig"))
    if identity != result["observationsSha256"]:
        raise ValueError("recorded observation bytes differ from their saved identity")
    physics, feedback = [], []
    previous = None
    segment = 0
    seen_ticks = set()
    commands, previous_command = {}, None
    with Path(__file__).open("rb") as stream:
        extractor_identity = hashlib.file_digest(stream, "sha256").hexdigest()
    with source.open(encoding="utf-8-sig") as stream:
        for line_no, line in enumerate(stream, 1):
            observation = parse(line)
            if observation.get("recordType") != "frame":
                continue
            frame, stage = observation["frame"], observation["stage"]
            telemetry = frame["closedLoop"]
            session, time, dt = telemetry["sessionId"], frame["timeSeconds"], frame["fixedStepSeconds"]
            if not math.isfinite(time) or not math.isfinite(dt) or dt <= 0:
                raise ValueError("invalid physics observation time")
            key = (stage, session, frame["configurationSignature"])
            if previous is None or previous[0] != key or time < previous[1]:
                segment += 1
                previous = None
            if previous is not None and time == previous[1]:
                raise ValueError("duplicate physics step; do not double-count its work")
            previous_command = remember_command(telemetry, segment, dt, commands, previous_command)
            continuous = previous is not None and math.isclose(time - previous[1], dt, rel_tol=1e-4, abs_tol=1e-6)
            position, velocity = vector(frame["boatPosition"]), vector(frame["boatVelocity"])
            distance = math.hypot(position[0] - previous[2][0], position[2] - previous[2][2]) if continuous else None
            paddles, requests = frame["paddles"], frame["feedbackRequests"]
            if len(paddles) != len(requests) or not paddles:
                raise ValueError("paddle/request count mismatch")
            valid = all(p["valid"] and r["valid"] for p, r in zip(paddles, requests))
            power = dissipation = None
            if valid:
                for p, r in zip(paddles, requests):
                    if r["inputSequence"] != p["sequence"] or r["timeSeconds"] != p["timeSeconds"]:
                        raise ValueError("request does not match the recorded paddle input")
                    if not p["hasAngularVelocity"]:
                        raise ValueError("angular velocity is required for rigid-body power")
                power = math.fsum(requested_power(p, r) for p, r in zip(paddles, requests))
                dissipation = math.fsum(r["dissipativePowerW"] for r in requests)
                if not math.isfinite(dissipation) or dissipation < 0:
                    raise ValueError("invalid requested relative-water dissipation")
            common = dict(scope="RECORDED_SOFTWARE_NOT_HARDWARE", observations_sha256=identity,
                          extractor_sha256=extractor_identity,
                          line=line_no, segment=segment, stage=stage, session_id=session)
            physics.append(dict(**common, simulation_s=time, step_s=dt, input_valid=valid,
                                horizontal_speed_m_s=math.hypot(velocity[0], velocity[2]),
                                contiguous_horizontal_travel_m=distance,
                                requested_power_into_paddle_world_w=power,
                                requested_relative_water_dissipation_w=dissipation,
                                requested_work_world_j=power * dt if valid else None,
                                requested_relative_water_dissipation_j=dissipation * dt if valid else None))
            previous = (key, time, position)
            if not (telemetry["feedbackFresh"] and telemetry["encoderConsumed"] and telemetry.get("feedbackPacketBase64")):
                continue
            fb = envelope(telemetry["feedbackPacketBase64"], session, "feedback", telemetry["feedbackSequence"])[1]
            if fb["measured"] is not False:
                raise ValueError("this extractor is restricted to declared software-controller records")
            tick = (session, fb["plantSampleTick"])
            if tick in seen_ticks:
                continue
            seen_ticks.add(tick)
            if not fb["valid"]:
                continue
            powers = paired_feedback_power(telemetry, fb, commands)
            feedback.append(dict(**common, controller_s=fb["controllerTimeSeconds"],
                                 plant_sample_tick=fb["plantSampleTick"], plant_step_s=fb["plantStepSeconds"],
                                 feedback_sequence=telemetry["feedbackSequence"], command_sequence=fb["commandSequence"],
                                 **powers,
                                 command_age_s=fb["commandAgeSeconds"], command_source_age_s=fb["commandSourceAgeSeconds"],
                                 hardware_stop_active=fb["hardwareStopActive"]))
    if not physics or not feedback:
        raise ValueError("no physics steps or declared software feedback samples")
    return physics, feedback


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    artifact, output = args.artifact.resolve(), args.output.resolve()
    if not artifact.is_relative_to(ROOT) or not output.is_relative_to(ROOT):
        parser.error("inputs and outputs must stay in this workspace")
    if output.exists():
        parser.error("choose a new output directory")
    physics, feedback = extract(artifact)
    output.mkdir(parents=True, exist_ok=False)
    for name, rows in (("physics_power.csv", physics), ("feedback_power.csv", feedback)):
        with (output / name).open("x", encoding="utf-8", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
            writer.writeheader()
            writer.writerows(rows)
    for segment in sorted({r["segment"] for r in physics}):
        rows = [r for r in physics if r["segment"] == segment]
        samples = [r for r in feedback if r["segment"] == segment]
        total = lambda key: math.fsum(r[key] for r in rows if r[key] is not None)
        print(json.dumps(dict(segment=segment, stage=rows[0]["stage"], frames=len(rows),
                              first_s=rows[0]["simulation_s"], last_s=rows[-1]["simulation_s"],
                              recorded_physics_s=total("step_s"),
                              valid_input_s=math.fsum(r["step_s"] for r in rows if r["input_valid"]),
                              contiguous_travel_m=total("contiguous_horizontal_travel_m"),
                              requested_work_world_j=total("requested_work_world_j"),
                              requested_relative_water_dissipation_j=total("requested_relative_water_dissipation_j"),
                              unique_feedback_samples=len(samples),
                              delivered_work_j=None, metabolic_energy_j=None)))


if __name__ == "__main__":
    main()
