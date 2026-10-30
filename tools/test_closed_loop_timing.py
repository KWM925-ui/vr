"""State-sample clocks, independently checked against mechanics and UDP bytes."""

import base64
import os
import queue
import socket
import struct
import subprocess
import sys
import threading
import time
import unittest
import zlib

from test_closed_loop_controller import SCRIPT, command, controller, heartbeat, from_observation


class ConstantVelocityPlant:
    def __init__(self, velocity=3.0):
        self.velocity = velocity
        self.position = 0.0
        self.steps = []

    def step(self, dt, now):
        self.position += self.velocity * dt
        self.steps.append((dt, now))

    def set_command(self, velocity, now):
        self.velocity = velocity


class SampleClockTests(unittest.TestCase):
    def test_jittered_feedback_clock_recovers_constant_physical_velocity(self):
        clock = controller.PlantSampleClock(100.0)
        plant = ConstantVelocityPlant()
        samples = []
        for elapsed in (0.0113, 0.0227, 0.0381, 0.0498, 0.0612, 0.0744):
            clock.advance(plant, 100.0 + elapsed)
            samples.append((clock.sample_time, elapsed, plant.position))
        erroneous_wall_speeds = []
        for before, after in zip(samples, samples[1:]):
            distance = after[2] - before[2]
            self.assertAlmostEqual(distance / (after[0] - before[0]), plant.velocity, places=12)
            erroneous_wall_speeds.append(distance / (after[1] - before[1]))
        self.assertGreater(max(abs(value - plant.velocity) for value in erroneous_wall_speeds), 0.5)
        self.assertEqual(clock.dropped_ticks, 0)
        self.assertTrue(all(step[0] == 0.005 for step in plant.steps))

    def test_repeated_wall_observation_does_not_advance_the_state_timestamp(self):
        clock = controller.PlantSampleClock(0.0)
        plant = ConstantVelocityPlant()
        clock.advance(plant, 0.011)
        before = (plant.position, clock.sample_tick, clock.sample_time)
        self.assertEqual(clock.advance(plant, 0.0149), 0)
        self.assertEqual((plant.position, clock.sample_tick, clock.sample_time), before)
        clock.advance(plant, 0.016)
        self.assertAlmostEqual(plant.position - before[0], plant.velocity * 0.005)
        self.assertAlmostEqual(clock.sample_time - before[2], 0.005)

    def test_catch_up_preserves_the_work_bound_and_reports_unintegrated_time(self):
        clock = controller.PlantSampleClock(0.0)
        plant = ConstantVelocityPlant()
        self.assertEqual(clock.advance(plant, 1.037), 20)
        self.assertEqual(clock.sample_tick, 20)
        self.assertAlmostEqual(clock.sample_time, 0.1)
        self.assertAlmostEqual(plant.position, plant.velocity * clock.sample_time)
        self.assertEqual(clock.dropped_ticks, 187)
        self.assertGreater(clock.next_tick, 1.037)
        self.assertLessEqual(clock.next_tick - 1.037, controller.DT)
        before_position, before_time = plant.position, clock.sample_time
        clock.advance(plant, 1.041)
        self.assertEqual(clock.sample_tick, 21)
        self.assertEqual(clock.dropped_ticks, 187)
        self.assertAlmostEqual((plant.position - before_position) / (clock.sample_time - before_time), 3.0)
        # Catch-up steps use local observation time for expiry, not old tick times.
        self.assertTrue(all(now == 1.037 for _, now in plant.steps[:20]))

    def test_a_new_command_cannot_backdate_force_into_overdue_steps(self):
        clock = controller.PlantSampleClock(0.0)
        plant = ConstantVelocityPlant(1.0)
        clock.advance(plant, 0.011)
        clock.accept_command(plant, 100.0, 0.024)
        self.assertAlmostEqual(plant.position, 0.020)
        self.assertAlmostEqual(clock.sample_time, 0.020)
        clock.advance(plant, 0.026)
        self.assertAlmostEqual(plant.position, 0.020 + 100.0 * 0.005)

    def test_jitter_does_not_accumulate_sample_clock_drift(self):
        clock = controller.PlantSampleClock(20.0)
        plant = ConstantVelocityPlant()
        for index in range(1, 1001):
            elapsed = index * 0.012 + (index % 7) * 0.00037
            clock.advance(plant, clock.started + elapsed)
            self.assertEqual(clock.sample_time, clock.sample_tick * controller.DT)
            self.assertAlmostEqual(plant.position, plant.velocity * clock.sample_time, places=10)
            self.assertGreater(clock.next_tick, clock.started + elapsed)
            self.assertLessEqual(clock.next_tick - clock.started - elapsed, controller.DT + 1e-12)
        self.assertEqual(clock.dropped_ticks, 0)


class FeedbackClockTests(unittest.TestCase):
    def make_model(self):
        model = controller.ControllerModel()
        model.set_command(controller.validate_step(command()), 0.0)
        return model

    def test_dbep_timestamp_uses_integrated_sample_time_with_original_crc_and_identity(self):
        model = self.make_model()
        clock = controller.PlantSampleClock(0.0)
        clock.advance(model, 0.0237)
        feedback = model.feedback(0.0237, clock.sample_time, 37, controller_wall_time=0.0237,
                                  sample_tick=clock.sample_tick, dropped_ticks=clock.dropped_ticks)
        self.assertAlmostEqual(feedback["controllerTimeSeconds"], 0.020)
        self.assertAlmostEqual(feedback["controllerWallTimeSeconds"], 0.0237)
        self.assertEqual(feedback["plantSampleTick"], 4)
        self.assertEqual(feedback["plantStepSeconds"], 0.005)
        self.assertEqual(feedback["plantDroppedTicks"], 0)
        packet = base64.b64decode(feedback["encoderPacketBase64"], validate=True)
        magic, version, sequence, timestamp, *values = struct.unpack("<IIIQ6ifI", packet)
        self.assertEqual(len(packet), 52)
        self.assertEqual((magic, version, sequence), (0x44424550, 1, 37))
        self.assertEqual(timestamp, 20_000)
        self.assertNotEqual(timestamp, 23_700)
        self.assertEqual(values[:6], feedback["rawEncoderCounts"])
        self.assertEqual(values[-1], zlib.crc32(packet[:48]))
        outer = controller.encode_packet(feedback, 37, 991)
        outer_sequence, decoded = controller.decode_packet(outer, 991)
        self.assertEqual(outer_sequence, sequence)
        self.assertEqual(decoded, feedback)

    def test_100ms_watchdog_uses_wall_time_even_when_sample_clock_is_behind(self):
        model = self.make_model()
        before = model.feedback(0.099999, 0.025, 1, controller_wall_time=0.099999, sample_tick=5)
        after = model.feedback(0.100001, 0.025, 2, controller_wall_time=0.100001, sample_tick=5)
        self.assertTrue(before["valid"])
        self.assertFalse(before["watchdog"])
        self.assertFalse(after["valid"])
        self.assertTrue(after["watchdog"])
        self.assertEqual(before["controllerTimeSeconds"], after["controllerTimeSeconds"])
        self.assertGreater(after["controllerWallTimeSeconds"], before["controllerWallTimeSeconds"])
        # A large catch-up cannot refresh an old command by using the sample clock.
        clock = controller.PlantSampleClock(0.0)
        clock.advance(model, 1.037)
        expired = model.feedback(1.037, clock.sample_time, 3, controller_wall_time=1.037,
                                 sample_tick=clock.sample_tick, dropped_ticks=clock.dropped_ticks)
        self.assertFalse(expired["valid"])
        self.assertTrue(expired["watchdog"])
        self.assertIn("PLANT_SCHEDULER_DROPPED_TICKS", expired["status"])
        self.assertEqual(expired["plantDroppedTicks"], 187)

    def test_pure_model_feedback_defaults_to_callers_wall_clock(self):
        model = self.make_model()
        feedback = model.feedback(0.07, 0.025, 9)
        self.assertEqual(feedback["controllerWallTimeSeconds"], 0.07)
        self.assertEqual(feedback["controllerTimeSeconds"], 0.025)
        self.assertEqual(feedback["plantSampleTick"], 5)
        self.assertTrue(feedback["valid"])

    def test_platform_motion_fields_are_independent_snapshot_arrays(self):
        model = self.make_model()
        names = {"platformVelocity": "platform_velocity", "platformAngularVelocity": "platform_angular_velocity",
                 "platformAcceleration": "platform_acceleration", "platformAngularAcceleration": "platform_angular_acceleration",
                 "platformInducedAcceleration": "platform_induced_acceleration",
                 "platformInducedAngularAcceleration": "platform_induced_angular_acceleration"}
        for index, attribute in enumerate(names.values()):
            setattr(model, attribute, [float(index), 0.25, -0.5])
        feedback = model.feedback(0.01, 0.01, 2)
        for name, attribute in names.items():
            self.assertEqual(feedback[name], getattr(model, attribute))
            self.assertEqual(len(feedback[name]), 3)
            getattr(model, attribute)[0] += 100
            self.assertNotEqual(feedback[name], getattr(model, attribute))

    def test_real_udp_publishes_distinct_sample_and_wall_clocks(self):
        session = 0x43544C4B
        process = subprocess.Popen([sys.executable, "-B", str(SCRIPT), "--session", str(session),
                                    "--parent-pid", str(os.getpid()), "--max-idle-seconds", "3"],
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                   creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        lines = queue.Queue()
        threading.Thread(target=lambda: lines.put(process.stdout.readline()), daemon=True).start()
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind(("127.0.0.1", 0))
        observations = []
        try:
            ready = lines.get(timeout=3).strip().split()
            self.assertEqual(ready[0], "READY")
            self.assertEqual(int(ready[2]), process.pid)
            address = ("127.0.0.1", int(ready[1]))
            deadline = time.monotonic() + 2
            sequence = 0
            while len(observations) < 12 and time.monotonic() < deadline:
                sequence += 1
                target = from_observation(command(sequence), observations[-1]) if observations else heartbeat(sequence)
                sock.sendto(controller.encode_packet(target, sequence, session), address)
                sock.settimeout(0.2)
                packet, sender = sock.recvfrom(controller.MAX_PACKET)
                self.assertEqual(sender, address)
                outer_sequence, payload = controller.decode_packet(packet, session)
                if payload.get("kind") != "feedback":
                    continue
                self.assertTrue(payload["valid"], payload["status"])
                self.assertFalse(payload["watchdog"])
                self.assertEqual(payload["plantDroppedTicks"], 0)
                self.assertEqual(payload["controllerTimeSeconds"], payload["plantSampleTick"] * payload["plantStepSeconds"])
                self.assertGreaterEqual(payload["controllerWallTimeSeconds"], payload["controllerTimeSeconds"])
                self.assertLess(payload["controllerWallTimeSeconds"] - payload["controllerTimeSeconds"], 0.005 + 1e-9)
                raw = base64.b64decode(payload["encoderPacketBase64"], validate=True)
                self.assertEqual(struct.unpack_from("<I", raw, 8)[0], outer_sequence)
                self.assertEqual(struct.unpack_from("<Q", raw, 12)[0], int(payload["controllerTimeSeconds"] * 1e6))
                self.assertEqual(struct.unpack_from("<I", raw, 48)[0], zlib.crc32(raw[:48]))
                if observations:
                    previous = observations[-1]
                    self.assertGreater(payload["plantSampleTick"], previous["plantSampleTick"])
                    self.assertGreater(payload["controllerWallTimeSeconds"], previous["controllerWallTimeSeconds"])
                observations.append(payload)
            self.assertEqual(len(observations), 12)
            self.assertTrue(any(abs(value["controllerWallTimeSeconds"] - value["controllerTimeSeconds"]) > 1e-6
                                for value in observations))
            sequence += 1
            sock.sendto(controller.encode_packet({"kind": "shutdown"}, sequence, session), address)
            process.wait(timeout=2)
            self.assertEqual(process.returncode, 0, process.stderr.read())
        finally:
            sock.close()
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=2)
            process.stdout.close()
            process.stderr.close()


if __name__ == "__main__":
    unittest.main()
