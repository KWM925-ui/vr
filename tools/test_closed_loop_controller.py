"""Deterministic mechanical tests and a real subprocess/UDP integration test."""

import base64
import copy
import importlib.util
import itertools
import json
import math
import os
from pathlib import Path
import queue
import socket
import struct
import subprocess
import sys
import threading
import time
import unittest
import zlib

sys.dont_write_bytecode = True
SCRIPT = Path(__file__).resolve().parents[1] / "DragonBoatVR" / "Assets" / "StreamingAssets" / "closed_loop_controller.py"
SPEC = importlib.util.spec_from_file_location("closed_loop_controller", SCRIPT)
controller = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(controller)


def decode_encoder_packet(encoded):
    """Independent DBEP contract reader; never call the worker's serializer."""
    if not isinstance(encoded, str):
        raise ValueError("DBEP base64 must be a string")
    packet = base64.b64decode(encoded, validate=True)
    if len(packet) != 52:
        raise ValueError("DBEP requires exactly 52 bytes")
    magic, version, sequence, device_time, *values = struct.unpack("<IIIQ6ifI", packet)
    if magic != 0x44424550 or version != 1:
        raise ValueError("wrong DBEP magic or version")
    counts, grip_force, crc = values[:6], values[6], values[7]
    if crc != zlib.crc32(packet[:48]):
        raise ValueError("bad DBEP CRC")
    if not math.isfinite(grip_force) or grip_force < 0:
        raise ValueError("invalid DBEP grip force")
    return sequence, device_time, counts, grip_force


def assert_encoder_feedback(test, payload, outer_sequence):
    sequence, device_time, counts, grip_force = decode_encoder_packet(payload["encoderPacketBase64"])
    test.assertEqual(sequence, outer_sequence)
    test.assertEqual(sequence, payload["encoderSequence"])
    test.assertEqual(device_time, int(payload["controllerTimeSeconds"] * 1_000_000))
    test.assertEqual(counts, payload["rawEncoderCounts"])
    test.assertEqual(payload["countsPerMeter"], 10000)
    test.assertEqual(payload["countsPerDegree"], 100)
    # Convert the independently computed vector norm to the on-wire float32.
    expected_force = math.sqrt(sum(value * value for value in payload["actualGripEffortN"]))
    expected_force = struct.unpack("<f", struct.pack("<f", expected_force))[0]
    test.assertEqual(grip_force, expected_force)
    return device_time


def command(sequence=1, tension=0.0, paired_geometry=False):
    base, top = [], []
    base_angles = [15, 105, 135, 225, 255, 345] if paired_geometry else [i * 60 for i in range(6)]
    top_angles = [45, 75, 165, 195, 285, 315] if paired_geometry else [i * 60 + 30 for i in range(6)]
    for a, b in zip(base_angles, top_angles):
        a, b = math.radians(a), math.radians(b)
        base += [0.8 * math.cos(a), 0.0, 0.8 * math.sin(a)]
        top += [0.55 * math.cos(b), 0.0, 0.55 * math.sin(b)]
    result = {
        "kind": "step", "commandSequence": sequence, "simulationTime": sequence * 0.01,
        "sourceFeedbackSequence": 0, "sourceSampleTick": 0,
        "active": True, "cableValid": True, "platformValid": True,
        "desiredGripPosition": [0.0, 0.6, 0.0], "desiredGripRotation": [0.0, 0.0, 0.0, 1.0],
        "desiredGripVelocity": [0.0, 0.0, 0.0], "desiredGripAngularVelocity": [0.0, 0.0, 0.0], "shaftLength": 1.0,
        # The fixture attaches at the grip, isolating translational hand impedance.
        "tensionTargets": [tension], "maxTensions": [300.0],
        "anchors": [1.2, 0.6, 0.0], "attachments": [0.0, 1.0, 0.0],
        "requestForce": [0.0, 0.0, 0.0], "requestTorque": [0.0, 0.0, 0.0],
        "platformTargetLengthsMm": [0.0] * 6, "platformBase": base, "platformTop": top,
        "platformNeutralHeight": 0.6, "platformMinMm": [300.0] * 6, "platformMaxMm": [1100.0] * 6,
        "platformTargetPosition": [0.0] * 3, "platformTargetEuler": [0.0] * 3,
        "motorTauSeconds": 0.035, "motorSlewNPerSecond": 2000.0,
        "platformTauSeconds": 0.06, "platformSpeedMmPerSecond": 500.0,
        "gripStiffnessNPerM": 1600.0, "gripDampingNsPerM": 100.0, "gripMassKg": 2.0,
        "angularStiffness": 30.0, "angularDamping": 3.0, "angularInertia": 0.15,
    }
    lengths, _ = controller.platform_geometry(result, [0.0] * 3, [0.0, 0.0, 0.0, 1.0])
    result["platformTargetLengthsMm"] = [x * 1000 for x in lengths]
    return result


def advance(model, payload, duration, start=0.0):
    now = start
    for i in range(round(duration / controller.DT)):
        if i % 10 == 0:
            payload = copy.deepcopy(payload)
            payload["commandSequence"] += 1
            model.set_command(controller.validate_step(payload), now)
        now += controller.DT
        model.step(controller.DT, now)
    return now


def heartbeat(sequence):
    payload = command(sequence)
    payload.update(cableValid=False, platformValid=False)
    return payload


def from_observation(payload, feedback):
    payload["sourceFeedbackSequence"] = feedback["encoderSequence"]
    payload["sourceSampleTick"] = feedback["plantSampleTick"]
    return payload


class CommandSourceTests(unittest.TestCase):
    def test_only_fresh_issued_identity_can_authorize_either_route(self):
        window = controller.CommandSourceWindow()
        feedback = dict(plantSampleTick=2, valid=True, watchdog=False)
        window.record(7, feedback, 0.0)
        valid = command()
        valid.update(sourceFeedbackSequence=7, sourceSampleTick=2)
        self.assertEqual(window.authorize(valid, .1), 0.)
        for route in ('cableValid', 'platformValid'):
            payload = heartbeat(2)
            payload.update(sourceFeedbackSequence=7, sourceSampleTick=2)
            payload[route] = True
            for changed, now in (({}, .100001), ({'sourceFeedbackSequence': 8}, .05),
                                 ({'sourceSampleTick': 3}, .05)):
                with self.subTest(route=route, changed=changed, now=now):
                    with self.assertRaisesRegex(ValueError, 'fresh issued observation'):
                        window.authorize(dict(payload, **changed), now)
        window.record(7, dict(feedback, watchdog=True), .01)
        with self.assertRaises(ValueError): window.authorize(valid, .05)

    def test_zero_hold_can_bootstrap_and_recover_but_not_hide_active_targets(self):
        window = controller.CommandSourceWindow()
        self.assertIsNone(window.authorize(heartbeat(1), 0.))
        window.record(3, dict(plantSampleTick=6, valid=False, watchdog=True), 0.)
        payload = heartbeat(2)
        payload.update(sourceFeedbackSequence=3, sourceSampleTick=6)
        self.assertEqual(window.authorize(payload, .5), 0.)
        for name in ('tensionTargets', 'requestForce', 'requestTorque', 'desiredGripVelocity'):
            changed = copy.deepcopy(payload)
            changed[name][0] = 1.
            with self.subTest(name=name), self.assertRaises(ValueError):
                window.authorize(changed, .5)
        window.record(4, dict(plantSampleTick=8, valid=True, watchdog=False), .51)
        payload = command(3)
        payload.update(sourceFeedbackSequence=4, sourceSampleTick=8)
        self.assertEqual(window.authorize(payload, .52), .51)

    def test_execution_expires_from_source_even_after_a_recent_command_receipt(self):
        payload = command(tension=80.)
        payload.update(sourceFeedbackSequence=7, sourceSampleTick=2)
        window = controller.CommandSourceWindow()
        window.record(7, dict(plantSampleTick=2, valid=True, watchdog=False), 0.)
        model = controller.ControllerModel()
        model.set_command(controller.validate_step(payload), .09, window.authorize(payload, .09))
        model.step(.005, .095)
        tension = model.tensions[0]
        lengths = model.platform_lengths[:]
        model.step(.005, .100001)
        self.assertFalse(model.watchdog(.100001))
        self.assertFalse(model.execution_fresh(.100001))
        self.assertLess(model.tensions[0], tension)
        self.assertEqual(model.platform_lengths, lengths)
        feedback = model.feedback(.100001, .01, 8)
        self.assertTrue(feedback['valid'])  # sensor heartbeat can recover the loop
        self.assertFalse(feedback['cableExecutionEnabled'])
        self.assertFalse(feedback['platformExecutionEnabled'])


def current_platform_command():
    """Read the same explicit/fallback geometry selection as the Unity host."""
    profile = json.loads((SCRIPT.parent / "platform_rig.json").read_text(encoding="utf-8-sig"))
    payload = command()
    payload["platformNeutralHeight"] = profile["neutralHeight"]
    payload["platformMinMm"] = profile["minActuatorLengthsMm"][:]
    payload["platformMaxMm"] = profile["maxActuatorLengthsMm"][:]
    if profile["useExplicitJointGeometry"]:
        for source, destination in (("explicitBaseJointsLocal", "platformBase"),
                                    ("explicitTopJointsLocal", "platformTop")):
            joints = profile[source]
            if len(joints) != 6:
                raise ValueError("current platform requires six explicit joints: " + source)
            payload[destination] = [point[axis] for point in joints for axis in "xyz"]
    else:
        for i in range(6):
            a, b = math.radians(i * 60), math.radians(i * 60 + 30)
            payload["platformBase"][i * 3:i * 3 + 3] = [profile["baseRadius"] * math.cos(a), 0,
                                                       profile["baseRadius"] * math.sin(a)]
            payload["platformTop"][i * 3:i * 3 + 3] = [profile["topRadius"] * math.cos(b), 0,
                                                      profile["topRadius"] * math.sin(b)]
    lengths, _ = controller.platform_geometry(payload, [0.0] * 3, [0.0, 0.0, 0.0, 1.0])
    payload["platformTargetLengthsMm"] = [length * 1000 for length in lengths]
    return payload, profile


class ProtocolTests(unittest.TestCase):
    def test_dbep_inner_corruption_is_detected_even_with_valid_outer_crc(self):
        model = controller.ControllerModel()
        now = advance(model, command(tension=80), 0.2)
        feedback = model.feedback(now, now, 37)
        assert_encoder_feedback(self, feedback, 37)
        packet = base64.b64decode(feedback["encoderPacketBase64"], validate=True)
        damaged = {"truncated": packet[:-1], "extra_byte": packet + b"\x00"}
        for name, offset in (("counts", 20), ("crc", 48)):
            altered = bytearray(packet)
            altered[offset] ^= 1
            damaged[name] = bytes(altered)
        for name, offset, format_string, value in (("magic", 0, "<I", 0), ("version", 4, "<I", 2),
                                                  ("negative_force", 44, "<f", -1.0),
                                                  ("nan_force", 44, "<f", math.nan),
                                                  ("infinite_force", 44, "<f", math.inf)):
            altered = bytearray(packet)
            struct.pack_into(format_string, altered, offset, value)
            struct.pack_into("<I", altered, 48, zlib.crc32(altered[:48]))
            damaged[name] = bytes(altered)
        encoded_cases = {name: base64.b64encode(raw).decode("ascii") for name, raw in damaged.items()}
        encoded_cases["bad_base64"] = feedback["encoderPacketBase64"] + "!"
        for name, encoded in encoded_cases.items():
            with self.subTest(damage=name):
                altered = dict(feedback, encoderPacketBase64=encoded)
                outer = controller.encode_packet(altered, 37, 123)
                sequence, received = controller.decode_packet(outer, 123)
                self.assertEqual(sequence, 37)  # Valid DBCL CRC does not establish DBEP validity.
                with self.assertRaises(ValueError):
                    decode_encoder_packet(received["encoderPacketBase64"])

    def test_dbep_fields_must_match_the_received_feedback(self):
        model = controller.ControllerModel()
        now = advance(model, command(tension=80), 0.2)
        feedback = model.feedback(now, now, 37)
        for field, value in (("encoderSequence", 38), ("controllerTimeSeconds", now + 0.01),
                             ("rawEncoderCounts", [0] * 6), ("actualGripEffortN", [0.0] * 3),
                             ("countsPerMeter", 1000), ("countsPerDegree", 10)):
            with self.subTest(field=field):
                # Each DBEP remains valid; inconsistent outer metadata must still fail.
                outer = controller.encode_packet(dict(feedback, **{field: value}), 37, 123)
                sequence, received = controller.decode_packet(outer, 123)
                with self.assertRaises(AssertionError):
                    assert_encoder_feedback(self, received, sequence)
        with self.assertRaises(AssertionError):
            assert_encoder_feedback(self, feedback, 38)

    def test_crc_session_length_and_nonfinite_are_rejected(self):
        payload = command()
        packet = controller.encode_packet(payload, 4, 123)
        sequence, decoded = controller.decode_packet(packet, 123)
        self.assertEqual(sequence, 4)
        self.assertEqual(decoded, payload)
        bad = bytearray(packet)
        bad[30] ^= 1
        for data, session in ((bad, 123), (packet, 124), (packet[:-1], 123)):
            with self.subTest(session=session, size=len(data)):
                with self.assertRaises(ValueError):
                    controller.decode_packet(data, session)
        with self.assertRaises(ValueError):
            controller.encode_packet({"bad": math.nan}, 5, 123)
        for body in (b'{"kind":"step","bad":1e999}', b'{"kind":"step","kind":"reset"}'):
            header = controller.HEADER.pack(controller.MAGIC, 1, 5, 123, len(body))
            content = header + body
            with self.assertRaises(ValueError):
                controller.decode_packet(content + struct.pack("<I", zlib.crc32(content)), 123)

    def test_sequence_wrap_and_configuration_validation(self):
        self.assertTrue(controller.newer(0, 0xFFFFFFFF))
        self.assertFalse(controller.newer(2, 2))
        self.assertFalse(controller.newer(1, 2))
        self.assertFalse(controller.newer(0x80000000, 0))
        for field, value in (("maxTensions", []), ("motorTauSeconds", 0),
                             ("desiredGripRotation", [0, 0, 0, 0]), ("gripMassKg", 0.1),
                             ("platformMaxMm", [100.0] * 6), ("active", 1)):
            payload = command()
            payload[field] = value
            if field == "gripMassKg":
                payload["gripStiffnessNPerM"] = 50000
            with self.subTest(field=field):
                with self.assertRaises(ValueError):
                    controller.validate_step(payload)


class MechanicalTests(unittest.TestCase):
    def test_motor_load_changes_measured_encoder_without_direct_water_force(self):
        unloaded, loaded = controller.ControllerModel(), controller.ControllerModel()
        idle, force = command(), command(tension=80)
        idle["requestForce"], idle["requestTorque"] = [999.0] * 3, [999.0] * 3
        unloaded.set_command(controller.validate_step(idle), 0.0)
        loaded.set_command(controller.validate_step(force), 0.0)
        loaded.step(controller.DT, controller.DT)
        self.assertGreater(loaded.tensions[0], 0)
        self.assertLess(loaded.tensions[0], 80)
        self.assertLessEqual(loaded.tensions[0], 2000 * controller.DT)
        now = advance(loaded, force, 1.0, controller.DT)
        advance(unloaded, idle, 1.0)
        feedback = loaded.feedback(now, now, 100)
        zero = unloaded.feedback(1.0, 1.0, 100)
        self.assertTrue(feedback["valid"], feedback["status"])
        self.assertEqual(zero["rawEncoderCounts"][0], 0)
        self.assertEqual(zero["forceLocal"], [0.0] * 3)
        self.assertGreater(feedback["rawEncoderCounts"][0], 400)
        self.assertAlmostEqual(feedback["gripPosition"][0], 80 / 1600, delta=0.001)
        self.assertAlmostEqual(feedback["forceLocal"][0], feedback["tensionN"][0], places=5)
        self.assertAlmostEqual(feedback["actualGripEffortN"][0], -80, delta=0.5)
        self.assertGreater(feedback["cableLengthsMm"][0], 1000)

    def test_watchdog_decays_force_and_holds_actual_platform(self):
        model, payload = controller.ControllerModel(), command(tension=80)
        now = advance(model, payload, 0.5)
        position, lengths = model.platform_position[:], model.platform_lengths[:]
        old_tension = model.tensions[0]
        for _ in range(100):
            now += controller.DT
            model.step(controller.DT, now)
        feedback = model.feedback(now, now, 201)
        self.assertFalse(feedback["valid"])
        self.assertTrue(feedback["watchdog"])
        self.assertLess(model.tensions[0], old_tension * 0.001)
        self.assertEqual(model.platform_position, position)
        self.assertEqual(model.platform_lengths, lengths)

    def test_six_independent_lengths_feed_forward_kinematics_and_inertia(self):
        model, payload = controller.ControllerModel(), command(paired_geometry=True)
        model.set_command(controller.validate_step(payload), 0.0)
        neutral = model.platform_lengths[:]
        self.assertTrue(all(300 < length < 1100 for length in neutral))
        target_position, target_euler = [0.02, 0.03, -0.01], [2.0, 1.0, -1.0]
        lengths, _ = controller.platform_geometry(payload, target_position, controller.q_from_unity_euler(target_euler))
        payload["platformTargetPosition"], payload["platformTargetEuler"] = target_position, target_euler
        payload["platformTargetLengthsMm"] = [x * 1000 for x in lengths]
        max_grip_displacement = 0
        now = 0.0
        for i in range(400):
            if i % 10 == 0:
                model.set_command(controller.validate_step(payload), now)
            before = model.platform_lengths[:]
            now += controller.DT
            model.step(controller.DT, now)
            self.assertTrue(all(abs(model.platform_lengths[k] - before[k]) <= 2.500001 for k in range(6)))
            max_grip_displacement = max(max_grip_displacement, controller.norm(controller.sub(model.position, payload["desiredGripPosition"])))
        self.assertGreater(max_grip_displacement, 0.00001, "platform acceleration did not disturb hand dynamics")
        self.assertLess(model.platform_residual, 0.005)
        self.assertLess(controller.norm(controller.sub(model.platform_position, target_position)), 0.0001)
        self.assertEqual(model.geometry_rank, 6)
        self.assertEqual(model.fault, "")
        feedback = model.feedback(now, now, 400)
        reconstructed, _ = controller.platform_geometry(payload, feedback["platformPosition"],
                                                         controller.q_from_unity_euler(feedback["platformEuler"]))
        self.assertLess(max(abs(reconstructed[i] * 1000 - feedback["platformLengthsMm"][i]) for i in range(6)), 0.005)
        self.assertTrue(feedback["capabilityLimited"], "software fixture must not claim measured capability")

    def test_singular_geometry_reports_rank_and_unity_rotation_roundtrips(self):
        payload = command()
        model = controller.ControllerModel()
        model.set_command(controller.validate_step(payload), 0)
        self.assertLess(model.geometry_rank, 6)
        for angles in ([10, -20, 30], [89, 20, -30], [90, -40, 0], [-90, 50, 0]):
            rotation = controller.q_from_unity_euler(angles)
            returned = controller.q_from_unity_euler(controller.q_to_unity_euler(rotation))
            self.assertGreater(abs(controller.dot(rotation, returned)), 1 - 1e-9)

    def test_workspace_constraint_velocity_and_long_run_are_bounded(self):
        payload, model = command(tension=300), controller.ControllerModel()
        payload["desiredGripPosition"] = [2.0, 0.6, 0.0]
        payload["anchors"] = [3.0, 0.6, 0.0]
        model.set_command(controller.validate_step(payload), 0.0)
        self.assertTrue(model.workspace_constrained)
        now = advance(model, payload, 8.0)
        feedback = model.feedback(now, now, 1600)
        self.assertLessEqual(feedback["rawEncoderCounts"][0], 14000)
        self.assertLessEqual(feedback["gripVelocity"][0], 0.000001)
        self.assertEqual(model.fault, "")
        self.assertTrue(all(math.isfinite(x) for x in model.position + model.velocity + model.rotation))
        with self.assertRaises(ValueError):
            model.step(0.02, now)
        changed = command()
        with self.assertRaises(ValueError):
            model.set_command(controller.validate_step(changed), now)

    def test_current_three_tendon_profile_and_platform_geometry(self):
        assets = SCRIPT.parent
        rig = json.loads((assets / "tendon_rig.json").read_text(encoding="utf-8-sig"))
        payload, platform = current_platform_command()
        model = controller.ControllerModel()
        self.assertTrue(platform["useExplicitJointGeometry"])
        self.assertFalse(platform["measured"])
        self.assertEqual(payload["platformBase"], [point[axis] for point in platform["explicitBaseJointsLocal"] for axis in "xyz"])
        self.assertEqual(payload["platformTop"], [point[axis] for point in platform["explicitTopJointsLocal"] for axis in "xyz"])
        tendons = [t for t in rig["tendons"] if t["enabled"]]
        self.assertEqual(len(tendons), 3)
        payload["tensionTargets"] = [15.0, 25.0, 35.0]
        payload["maxTensions"] = [t["maxTensionN"] for t in tendons]
        payload["anchors"] = [t["anchorLocal"][axis] for t in tendons for axis in "xyz"]
        payload["attachments"] = [rig["bladeTipLocal"][axis] + t["attachmentFromBladeTip"][axis]
                                   if rig["attachmentCoordinatesFromBladeTip"] else t["attachLocal"][axis]
                                   for t in tendons for axis in "xyz"]
        payload["desiredGripPosition"] = [0.35, 0.65, 0.0]
        now = 0.0
        maximum_fit_residual = 0.0
        for i in range(600):
            if i % 10 == 0:
                payload["commandSequence"] += 1
                payload["platformTargetPosition"] = [0.01 * math.sin(now), 0.03 * math.sin(now), 0]
                payload["platformTargetEuler"] = [3 * math.sin(now), 0.5 * math.sin(now), 2 * math.cos(now)]
                lengths, _ = controller.platform_geometry(payload, payload["platformTargetPosition"],
                                                          controller.q_from_unity_euler(payload["platformTargetEuler"]))
                payload["platformTargetLengthsMm"] = [x * 1000 for x in lengths]
                model.set_command(controller.validate_step(payload), now)
            now += controller.DT
            model.step(controller.DT, now)
            maximum_fit_residual = max(maximum_fit_residual, model.platform_residual)
            self.assertEqual(model.geometry_rank, 6)
        feedback = model.feedback(now, now, 601)
        self.assertEqual(model.fault, "")
        self.assertTrue(feedback["valid"], feedback["status"])
        self.assertEqual(len(feedback["tensionN"]), 3)
        self.assertEqual(len(feedback["cableLengthsMm"]), 3)
        self.assertLessEqual(maximum_fit_residual, 0.5)
        self.assertTrue(all(0 <= feedback["tensionN"][i] <= payload["maxTensions"][i] for i in range(3)))
        print(f"[PYCONTROLLER] currentExplicitRig rank={model.geometry_rank} maxFitResidualMm={maximum_fit_residual:.9g}")

    def test_motor_and_platform_response_time_change_later_encoder_counts(self):
        observations = {}
        for response in ("motorTauSeconds", "platformTauSeconds"):
            for tau in (0.015, 0.3):
                with self.subTest(response=response, tau=tau):
                    payload, _ = current_platform_command()
                    payload[response] = tau
                    if response == "motorTauSeconds":
                        payload["tensionTargets"] = [80.0]
                    else:
                        payload["platformTargetPosition"] = [0.0, 0.03, 0.0]
                        lengths, _ = controller.platform_geometry(payload, payload["platformTargetPosition"], [0, 0, 0, 1])
                        payload["platformTargetLengthsMm"] = [length * 1000 for length in lengths]
                    model = controller.ControllerModel()
                    now = advance(model, payload, 0.1)
                    feedback = model.feedback(now, now, 20)
                    self.assertTrue(feedback["valid"], feedback["status"])
                    self.assertEqual(model.fault, "")
                    self.assertEqual(model.command["desiredGripPosition"], [0.0, 0.6, 0.0])
                    self.assertEqual(model.command["desiredGripRotation"], [0.0, 0.0, 0.0, 1.0])
                    self.assertEqual(feedback["geometryRank"], 6)
                    self.assertLessEqual(feedback["platformFitResidualMm"], 0.5)
                    observations[response, tau] = feedback
            fast, slow = observations[response, 0.015], observations[response, 0.3]
            axis = 0 if response == "motorTauSeconds" else 1
            self.assertGreater(abs(fast["rawEncoderCounts"][axis] - slow["rawEncoderCounts"][axis]), 10,
                               "actuator response changed but later sensor counts did not")
            if response == "motorTauSeconds":
                self.assertGreater(fast["tensionN"][0], slow["tensionN"][0])
                self.assertGreater(fast["rawEncoderCounts"][0], slow["rawEncoderCounts"][0])
                # Unclipped discrete first-order response has a closed-form value.
                expected_slow_tension = 80 * (1 - (0.3 / (0.3 + controller.DT)) ** 20)
                self.assertAlmostEqual(slow["tensionN"][0], expected_slow_tension, places=9)
            else:
                self.assertGreater(fast["platformPosition"][1], slow["platformPosition"][1])
                self.assertEqual(fast["tensionN"], [0.0])
                self.assertEqual(slow["tensionN"], [0.0])
            print(f"[PYCONTROLLER] response={response} sameIntent=True "
                  f"fastCounts={fast['rawEncoderCounts']} slowCounts={slow['rawEncoderCounts']}")

    def test_current_full_rank_geometry_still_rejects_unreachable_sampled_poses(self):
        payload, profile = current_platform_command()
        model = controller.ControllerModel()
        model.set_command(controller.validate_step(payload), 0.0)
        self.assertEqual(model.geometry_rank, 6)
        neutral_position, neutral_lengths = model.platform_position[:], model.platform_lengths[:]
        accepted_command = copy.deepcopy(model.command)
        limits = [profile["maxTranslationMeters"], profile["maxHeaveMeters"], profile["maxTranslationMeters"],
                  profile["maxTiltDegrees"], profile["maxYawDegrees"], profile["maxTiltDegrees"]]
        accepted, rejected = 0, 0
        rejected_example = None
        for pose in itertools.product(*[(-limit, 0.0, limit) for limit in limits]):
            trial = copy.deepcopy(payload)
            trial["commandSequence"] = 2
            trial["platformTargetPosition"], trial["platformTargetEuler"] = list(pose[:3]), list(pose[3:])
            lengths, _ = controller.platform_geometry(trial, trial["platformTargetPosition"],
                                                      controller.q_from_unity_euler(trial["platformTargetEuler"]))
            trial["platformTargetLengthsMm"] = [length * 1000 for length in lengths]
            within_stroke = all(low <= length <= high for low, length, high in
                                zip(trial["platformMinMm"], trial["platformTargetLengthsMm"], trial["platformMaxMm"]))
            if within_stroke:
                controller.validate_step(trial)
                accepted += 1
            else:
                with self.assertRaisesRegex(ValueError, "valid platform target exceeds actuator limits"):
                    model.set_command(controller.validate_step(trial), 0.02)
                self.assertEqual(model.command, accepted_command)
                self.assertEqual(model.last_command_time, 0.0, "rejected target refreshed the watchdog")
                self.assertEqual(model.platform_position, neutral_position)
                self.assertEqual(model.platform_lengths, neutral_lengths)
                rejected += 1
                rejected_example = rejected_example or trial
        self.assertEqual(accepted + rejected, 729)
        self.assertGreater(accepted, 0)
        self.assertGreater(rejected, 0, "full Jacobian rank must not be mistaken for full configured-box reachability")
        self.assertIsNotNone(rejected_example)
        print(f"[PYCONTROLLER] currentExplicitRig rank=6 sampledPoses=729 withinStroke={accepted} "
              f"rejectedByOriginalStrokeLimits={rejected}; sample is not a full workspace certification")

        # A host-labelled rejected target remains diagnostic data: real cylinder state holds.
        rejected_example["platformValid"] = False
        now = advance(model, rejected_example, 0.2)
        held = model.feedback(now, now, 40)
        self.assertEqual(held["platformLengthsMm"], neutral_lengths)
        self.assertEqual(held["platformPosition"], neutral_position)
        self.assertIn("PLATFORM_REQUEST_INVALID_HOLD", held["status"])
        self.assertFalse(model.command["platformValid"])
        self.assertEqual(model.command["platformTargetLengthsMm"], rejected_example["platformTargetLengthsMm"])

        recovery = copy.deepcopy(payload)
        recovery["platformTargetPosition"] = [0, 0.02, 0]
        lengths, _ = controller.platform_geometry(recovery, recovery["platformTargetPosition"], [0, 0, 0, 1])
        recovery["platformTargetLengthsMm"] = [length * 1000 for length in lengths]
        now = advance(model, recovery, 0.3, now)
        recovered = model.feedback(now, now, 100)
        self.assertTrue(recovered["valid"], recovered["status"])
        self.assertGreater(recovered["platformPosition"][1], 0.01)
        self.assertLessEqual(recovered["platformFitResidualMm"], 0.5)


class ProcessIntegrationTests(unittest.TestCase):
    def test_parent_exit_and_idle_timeout_clean_up_workers(self):
        self.assertTrue(controller.parent_alive(os.getpid()))
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        parent = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(0.7)"], creationflags=flags)
        worker = None
        try:
            worker = subprocess.Popen([sys.executable, "-B", str(SCRIPT), "--session", "771",
                                       "--parent-pid", str(parent.pid)], stdout=subprocess.PIPE,
                                      stderr=subprocess.PIPE, text=True, creationflags=flags)
            parent.wait(timeout=2)
            stdout, stderr = worker.communicate(timeout=2)
            self.assertEqual(worker.returncode, 0, stderr)
            self.assertTrue(stdout.startswith("READY "))
            self.assertFalse(controller.parent_alive(parent.pid))
            worker = subprocess.Popen([sys.executable, "-B", str(SCRIPT), "--session", "772",
                                       "--parent-pid", str(os.getpid()), "--max-idle-seconds", "0.2"],
                                      stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, creationflags=flags)
            stdout, stderr = worker.communicate(timeout=2)
            self.assertEqual(worker.returncode, 0, stderr)
            self.assertTrue(stdout.startswith("READY "))
        finally:
            if parent.poll() is None:
                parent.terminate()
                parent.wait(timeout=2)
            if worker is not None and worker.poll() is None:
                worker.terminate()
                worker.communicate(timeout=2)

    def test_independent_udp_process_rejects_bad_freshness_and_recovers(self):
        session = 0x5A0135A7
        environment = dict(os.environ, PYTHONDONTWRITEBYTECODE="1")
        process = subprocess.Popen([sys.executable, "-B", str(SCRIPT), "--session", str(session),
                                    "--parent-pid", str(os.getpid()), "--max-idle-seconds", "3"],
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=environment,
                                   creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        lines = queue.Queue()
        threading.Thread(target=lambda: lines.put(process.stdout.readline()), daemon=True).start()
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        intruder = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind(("127.0.0.1", 0))
        intruder.bind(("127.0.0.1", 0))
        try:
            ready = lines.get(timeout=3).strip().split()
            self.assertEqual(ready[0], "READY")
            port, pid = int(ready[1]), int(ready[2])
            self.assertEqual(pid, process.pid)
            self.assertNotEqual(pid, os.getpid())
            address = ("127.0.0.1", port)

            def send(payload, sequence, source=sock):
                source.sendto(controller.encode_packet(payload, sequence, session), address)

            last_response = None
            last_device_time = None
            verified_encoder_packets = 0

            def receive(predicate, timeout=1.5):
                nonlocal last_response, last_device_time, verified_encoder_packets
                deadline = time.monotonic() + timeout
                while time.monotonic() < deadline:
                    sock.settimeout(max(0.001, deadline - time.monotonic()))
                    packet, sender = sock.recvfrom(controller.MAX_PACKET)
                    self.assertEqual(sender, address)
                    sequence, payload = controller.decode_packet(packet, session)
                    if last_response is not None:
                        self.assertTrue(controller.newer(sequence, last_response))
                    last_response = sequence
                    if payload.get("kind") == "feedback":
                        device_time = assert_encoder_feedback(self, payload, sequence)
                        if last_device_time is not None:
                            self.assertGreater(device_time, last_device_time)
                        last_device_time = device_time
                        verified_encoder_packets += 1
                    if predicate(payload):
                        return payload
                self.fail("feedback predicate timed out")

            send(heartbeat(0), 0)
            boot = receive(lambda value: value.get("kind") == "feedback" and value["commandSequence"] == 0)
            self.assertFalse(boot['cableExecutionEnabled'])
            self.assertFalse(boot['platformExecutionEnabled'])
            send(from_observation(command(tension=80), boot), 1)
            first = receive(lambda value: value.get("kind") == "feedback" and value["commandSequence"] == 1)
            self.assertTrue(first["valid"])
            self.assertGreater(first["tensionN"][0], 0)
            bad = bytearray(controller.encode_packet(command(sequence=2, tension=0), 2, session))
            bad[-5] ^= 1
            sock.sendto(bad, address)
            send(command(sequence=1, tension=0), 1)  # Duplicate cannot reset targets or freshness.
            send(command(sequence=100, tension=0), 100, intruder)
            stale = receive(lambda value: value.get("watchdog") is True
                            and value['controllerWallTimeSeconds'] - first['controllerWallTimeSeconds'] > .101)
            self.assertEqual(stale["commandSequence"], 1)
            self.assertFalse(stale["valid"])
            # Even a correct CRC and increasing command sequence cannot revive
            # the old observation, authorize a forged tick, or mismatch the header.
            send(from_observation(command(sequence=2, tension=80), first), 2)
            rejected = receive(lambda value: value.get('kind') == 'feedback')
            self.assertEqual(rejected['commandSequence'], 1)
            forged = from_observation(command(sequence=2), rejected)
            forged['sourceSampleTick'] += 1
            send(forged, 2)
            mismatch = from_observation(command(sequence=3), rejected)
            send(mismatch, 2)
            rejected = receive(lambda value: value.get('kind') == 'feedback')
            self.assertEqual(rejected['commandSequence'], 1)
            send(heartbeat(2), 2)
            restored = receive(lambda value: value.get("commandSequence") == 2 and value.get("valid") is True)
            self.assertFalse(restored["watchdog"])

            nonfinite = command(sequence=3)
            nonfinite["motorTauSeconds"] = math.nan
            body = json.dumps(nonfinite).encode("utf-8")
            content = controller.HEADER.pack(controller.MAGIC, 1, 3, session, len(body)) + body
            sock.sendto(content + struct.pack("<I", zlib.crc32(content)), address)
            stale = receive(lambda value: value.get("watchdog") is True and value.get("commandSequence") == 2)
            self.assertFalse(stale["valid"])
            send(heartbeat(3), 3)
            receive(lambda value: value.get("commandSequence") == 3 and value.get("valid") is True)
            send({"kind": "reset"}, 4)
            receive(lambda value: value.get("kind") == "reset_ack" and value["commandSequence"] == 4)
            send(command(sequence=3, tension=300), 3)  # Reset retains replay protection.
            repositioned = heartbeat(5)
            repositioned["desiredGripPosition"][0] = 0.1
            send(repositioned, 5)
            fresh = receive(lambda value: value.get("kind") == "feedback" and value["commandSequence"] == 5)
            self.assertEqual(fresh["rawEncoderCounts"][0], 1000)
            send({"kind": "shutdown"}, 6)
            receive(lambda value: value.get("kind") == "shutdown_ack")
            process.wait(timeout=2)
            self.assertEqual(process.returncode, 0, process.stderr.read())
            self.assertGreaterEqual(verified_encoder_packets, 5)
            print(f"[PYCONTROLLER] independentUdpDbep packets={verified_encoder_packets} bytes=52 "
                  "magicVersionSequenceDeviceTimeCountsGripForceCrc=PASS")
        finally:
            sock.close()
            intruder.close()
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=2)
            process.stdout.close()
            process.stderr.close()


if __name__ == "__main__":
    unittest.main()
