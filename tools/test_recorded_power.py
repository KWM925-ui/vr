"""Check the reference-point dependence of force, torque and velocity together."""

import base64
import copy
import hashlib
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zlib

from recorded_power import ROOT, extract, paired_feedback_power, remember_command, requested_power


def packet(payload, session, sequence):
    body = json.dumps(payload, separators=(",", ":"), allow_nan=False).encode()
    data = struct.pack("<4sIIQI", b"DBCL", 1, sequence, int(session), len(body)) + body
    return base64.b64encode(data + struct.pack("<I", zlib.crc32(data))).decode()


def observation(sequence=1, time=.01, force=None, *, tick=101, stage="Normal"):
    force = [0., 0., 0.] if force is None else force
    command = dict(kind="step", commandSequence=sequence, simulationTime=time,
                   sourceFeedbackSequence=7, sourceSampleTick=100, active=True, cableValid=True,
                   requestForce=force, requestTorque=[0., 0., 0.])
    feedback = dict(kind="feedback", commandSequence=sequence, measured=False, valid=True,
                    commandSourceFeedbackSequence=7, commandSourceSampleTick=100,
                    plantSampleTick=tick, plantStepSeconds=.005, controllerTimeSeconds=tick * .005,
                    forceLocal=[-2., 0., 0.], torqueLocal=[0., 0., -3.],
                    gripVelocity=[1., 0., 0.], gripAngularVelocity=[0., 0., 2.],
                    commandAgeSeconds=.01, commandSourceAgeSeconds=.02, hardwareStopActive=False)
    encoded = packet(command, "123", sequence)
    telemetry = dict(sessionId="123", commandSent=True, commandSequence=sequence,
                     commandPacketBase64=encoded, acknowledgedCommandPacketBase64=encoded,
                     acknowledgedSourcePacketBase64=packet(dict(kind="feedback", plantSampleTick=100), "123", 7),
                     feedbackPacketBase64=packet(feedback, "123", 8), feedbackSequence=8,
                     feedbackFresh=True, encoderConsumed=True)
    paddle = dict(valid=True, sequence=sequence, timeSeconds=time, hasAngularVelocity=True,
                  bladePosition=[0., 0., 0.], bladeVelocity=[2., 0., 0.], bladeAngularVelocity=[0., 0., 0.])
    request = dict(valid=True, inputSequence=sequence, timeSeconds=time, dissipativePowerW=10.,
                   forceWorldN=[-5., 0., 0.], torqueAtGripWorldNm=[0., 0., 0.], gripWorldPosition=[0., 1., 0.])
    frame = dict(closedLoop=telemetry, timeSeconds=time, fixedStepSeconds=.01,
                 configurationSignature="fixture", boatPosition=[sequence, 0., 0.], boatVelocity=[2., 0., 0.],
                 paddles=[paddle], feedbackRequests=[request])
    return dict(recordType="frame", stage=stage, frame=frame), feedback


def write_recording(directory, rows):
    data = ("\n".join(json.dumps(row, allow_nan=False) for row in rows) + "\n").encode()
    (directory / "observations.jsonl").write_bytes(data)
    (directory / "result.json").write_text(json.dumps(dict(observationsSha256=hashlib.sha256(data).hexdigest())))


class RecordedPowerTests(unittest.TestCase):
    def test_same_physical_wrench_has_same_power_at_a_different_reference_point(self):
        paddle = dict(bladePosition=[0., 0., 0.], bladeVelocity=[3., 4., 0.],
                      bladeAngularVelocity=[0., 0., 2.])
        at_blade = dict(forceWorldN=[0., 5., 0.], torqueAtGripWorldNm=[0., 0., 7.],
                        gripWorldPosition=[0., 0., 0.])
        # Grip shifted 1 m in +X: v_grip becomes (3,6,0), torque becomes 7-5.
        at_grip = dict(forceWorldN=[0., 5., 0.], torqueAtGripWorldNm=[0., 0., 2.],
                       gripWorldPosition=[1., 0., 0.])
        self.assertAlmostEqual(requested_power(paddle, at_blade), 34.)
        self.assertAlmostEqual(requested_power(paddle, at_grip), 34.)

    def test_pure_rotation_work_and_absorbing_translation_keep_their_signs(self):
        paddle = dict(bladePosition=[1., 2., 3.], bladeVelocity=[0., 0., 0.],
                      bladeAngularVelocity=[0., 0., 2.])
        request = dict(forceWorldN=[0., 0., 0.], torqueAtGripWorldNm=[0., 0., -3.],
                       gripWorldPosition=[2., 2., 3.])
        self.assertEqual(requested_power(paddle, request), -6.)
        paddle.update(bladeVelocity=[2., 0., 0.], bladeAngularVelocity=[0., 0., 0.])
        request.update(forceWorldN=[-5., 0., 0.], torqueAtGripWorldNm=[0., 0., 0.])
        self.assertEqual(requested_power(paddle, request), -10.)

    def test_paired_power_uses_current_motion_and_excludes_stop_reaction(self):
        row, feedback = observation(force=[-5., 0., 0.])
        telemetry = row["frame"]["closedLoop"]
        feedback.update(hardwareStopActive=True, hardwareStopReactionForce=[1000., 0., 0.])
        powers = paired_feedback_power(telemetry, feedback, {})
        self.assertEqual(powers["cable_translation_power_rig_w"], -2.)
        self.assertEqual(powers["cable_rotation_power_rig_w"], -6.)
        self.assertEqual(powers["cable_power_into_paddle_rig_w"], -8.)
        self.assertEqual(powers["acknowledged_request_power_at_current_grip_rig_w"], -5.)
        self.assertEqual(powers["cable_minus_acknowledged_request_power_w"], -3.)
        self.assertFalse(powers["command_seen_in_recording"])
        self.assertIsNone(powers["acknowledged_zero_request_observed_s"])

    def test_source_identity_and_declared_hardware_cannot_be_silently_mixed(self):
        row, feedback = observation()
        original = row["frame"]["closedLoop"]
        for tick, session in ((99, "123"), (100, "999"), (102, "123")):
            with self.subTest(tick=tick, session=session):
                telemetry = copy.deepcopy(original)
                telemetry["acknowledgedSourcePacketBase64"] = packet(dict(kind="feedback", plantSampleTick=tick), session, 7)
                with self.assertRaises(ValueError):
                    paired_feedback_power(telemetry, feedback, {})
        with self.assertRaisesRegex(ValueError, "software-controller"):
            paired_feedback_power(original, dict(feedback, measured=True), {})
        commands = {("123", 1): ("different original packet", 0.)}
        with self.assertRaisesRegex(ValueError, "outgoing bytes"):
            paired_feedback_power(original, feedback, commands)

    def test_zero_request_age_checks_commands_missing_from_feedback(self):
        commands, previous = {}, None
        # Command 3 is nonzero even if its feedback was never captured.
        series = [(1, .01, 1, 0., 0.), (2, .02, 1, 0., .01), (3, .03, 1, 1., None),
                  (4, .04, 1, 0., 0.), (5, .05, 1, 0., .01),
                  (7, .07, 1, 0., 0.),  # Missing sequence 6.
                  (8, .10, 1, 0., 0.),  # Time gap despite consecutive sequence.
                  (9, .11, 2, 0., 0.)]  # New stage/configuration segment.
        for sequence, time, segment, force, age in series:
            row, feedback = observation(sequence, time, [force, 0., 0.])
            telemetry = row["frame"]["closedLoop"]
            previous = remember_command(telemetry, segment, .01, commands, previous)
            actual = paired_feedback_power(telemetry, feedback, commands)["acknowledged_zero_request_observed_s"]
            if age is None:
                self.assertIsNone(actual)
            else:
                self.assertAlmostEqual(actual, age)
        row, _ = observation(9, .11, [1., 0., 0.])
        with self.assertRaisesRegex(ValueError, "identity reused"):
            remember_command(row["frame"]["closedLoop"], 2, .01, commands, previous)

    def test_recording_does_not_bridge_gaps_restarts_or_repeat_feedback(self):
        rows = [observation(1, .01)[0], observation(2, .02)[0],
                observation(3, .06)[0], observation(4, .01)[0]]
        with tempfile.TemporaryDirectory(dir=ROOT / "tmp", prefix="power_test_") as directory:
            path = Path(directory)
            write_recording(path, rows)
            physics, feedback = extract(path)
        self.assertEqual([p["segment"] for p in physics], [1, 1, 1, 2])
        self.assertEqual([p["contiguous_horizontal_travel_m"] for p in physics], [None, 1., None, None])
        self.assertAlmostEqual(sum(p["requested_work_world_j"] for p in physics), -.4)
        self.assertEqual(len(feedback), 1)  # One plant state, despite four host observations.

    def test_changed_recording_or_duplicate_physics_time_is_rejected(self):
        row, _ = observation()
        with tempfile.TemporaryDirectory(dir=ROOT / "tmp", prefix="power_test_") as directory:
            path = Path(directory)
            write_recording(path, [row])
            with (path / "observations.jsonl").open("a") as stream:
                stream.write("\n")
            with self.assertRaisesRegex(ValueError, "saved identity"):
                extract(path)
            write_recording(path, [row, row])
            with self.assertRaisesRegex(ValueError, "duplicate physics step"):
                extract(path)

    def test_power_rejects_a_request_from_another_paddle_sample(self):
        row, _ = observation()
        row["frame"]["feedbackRequests"][0]["inputSequence"] += 1
        with tempfile.TemporaryDirectory(dir=ROOT / "tmp", prefix="power_test_") as directory:
            path = Path(directory)
            write_recording(path, [row])
            with self.assertRaisesRegex(ValueError, "recorded paddle input"):
                extract(path)


if __name__ == "__main__":
    unittest.main()
