"""Hardware handoff byte, unit, source-identity and malformed-input checks."""

import copy
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zlib

from paddle_hardware import (COMMAND, CRC, HEADER, MAGIC, TELEMETRY, VERSION, ContractError,
                             check_capture, decode, encode, example_stop, example_telemetry, newer,
                             validate_command, validate_telemetry, write_decoded_capture)


ROOT = Path(__file__).resolve().parents[1]


def raw_frame(payload, *, sequence=1, boot=123, kind=TELEMETRY):
    data = HEADER.pack(MAGIC, VERSION, kind, len(payload), sequence, boot) + payload
    return data + CRC.pack(zlib.crc32(data))


class PaddleHardwareTests(unittest.TestCase):
    def check_rows(self, frames, *, allow_simulated=True):
        with tempfile.TemporaryDirectory(dir=ROOT / "tmp", prefix="hardware_capture_") as folder:
            path = Path(folder) / "capture.bin"
            path.write_bytes(b"".join(frames))
            return check_capture(path, allow_simulated=allow_simulated)

    def test_layout_and_checksum_match_an_independent_bitwise_crc(self):
        data = encode(COMMAND, 0x12345678, 0x1020304050607080, example_stop())
        self.assertEqual(HEADER.size, 24)
        self.assertEqual(data[:8], b"DBPF\x01\x00\x02\x00")
        self.assertEqual(data[12:16], b"\x78\x56\x34\x12")
        self.assertEqual(data[16:24], b"\x80\x70\x60\x50\x40\x30\x20\x10")
        crc = 0xFFFFFFFF
        for value in data[:-4]:
            crc ^= value
            for _ in range(8):
                crc = (crc >> 1) ^ (0xEDB88320 if crc & 1 else 0)
        self.assertEqual(struct.unpack_from("<I", data, len(data) - 4)[0], crc ^ 0xFFFFFFFF)

    def test_round_trip_preserves_raw_data_without_inventing_force_or_pose(self):
        body = example_telemetry()
        frame = decode(encode(TELEMETRY, 7, 99, body))
        self.assertEqual(frame["sequence"], 7)
        self.assertEqual(frame["body"], body)
        self.assertIsNone(frame["body"]["forces"][0]["forceN"])
        self.assertIsNone(frame["body"]["imu"]["quaternionXyzw"])

    def test_bad_crc_lengths_magic_and_version_are_rejected(self):
        original = encode(TELEMETRY, 1, 123, example_telemetry())
        bad_crc = bytearray(original)
        bad_crc[-5] ^= 1
        for data in (original[:-1], original + b"0", b"NOPE" + original[4:], bytes(bad_crc), original[:4] + b"\x02\x00" + original[6:]):
            with self.subTest(length=len(data)), self.assertRaises(ContractError):
                decode(data)

    def test_duplicate_json_keys_and_nonfinite_literals_are_rejected(self):
        text = json.dumps(example_telemetry())
        for payload in (text.replace('{"deviceId":', '{"deviceId":"duplicate","deviceId":', 1),
                        text.replace('"deviceTimeUs": 10000', '"deviceTimeUs": NaN'),
                        text.replace('"deviceTimeUs": 10000', '"deviceTimeUs": 1e999')):
            with self.assertRaises(ContractError):
                decode(raw_frame(payload.encode()))

    def test_unknown_fields_require_a_version_change(self):
        body = example_telemetry()
        body["guess"] = 1
        with self.assertRaises(ContractError):
            validate_telemetry(body)

    def test_adc_channel_identity_and_fault_flags_cannot_be_hidden(self):
        for status in (1, 0x40, 0x20):
            body = example_telemetry()
            body["forces"][0]["status"] = status
            with self.subTest(status=status), self.assertRaises(ContractError):
                validate_telemetry(body)

    def test_uncalibrated_force_and_invalid_saturated_force_are_rejected(self):
        for change in ("uncalibrated", "saturated", "rail"):
            body = example_telemetry()
            sample = body["forces"][0]
            if change == "uncalibrated": sample["forceN"] = 10.
            elif change == "saturated": sample["saturated"] = True
            else: sample["rawCode"] = 0xFFFFFF
            with self.subTest(change=change), self.assertRaises(ContractError):
                validate_telemetry(body)

    def test_raw_acquisition_can_start_before_other_devices_are_available(self):
        body = example_telemetry()
        body["forces"][3].update(valid=False, sampleSequence=None, sampleTimeUs=None, rawCode=None, status=None)
        validate_telemetry(body)
        self.assertEqual(body["motorEnabled"], [False] * 3)

    def test_sample_time_must_be_acquisition_time_in_the_device_clock(self):
        for value in (10001, -1, True, 1.5):
            body = example_telemetry()
            body["forces"][0]["sampleTimeUs"] = value
            with self.subTest(value=value), self.assertRaises(ContractError):
                validate_telemetry(body)

    def test_int64_encoder_counts_use_lossless_decimal_strings(self):
        body = example_telemetry()
        sample = body["encoders"][0]
        sample.update(valid=True, rawCount=str((1 << 63) - 1), sampleSequence=1, sampleTimeUs=9000)
        validate_telemetry(body)
        for value in (1 << 63, str(1 << 63), "01", "-0", "1.0"):
            sample["rawCount"] = value
            with self.subTest(value=value), self.assertRaises(ContractError):
                validate_telemetry(body)

    def test_attitude_requires_a_unit_quaternion(self):
        body = example_telemetry()
        body["imu"].update(valid=True, sampleSequence=1, sampleTimeUs=9000, quaternionXyzw=[0., 0., 0., 1.])
        validate_telemetry(body)
        body["imu"]["quaternionXyzw"] = [0., 0., 0., 0.]
        with self.assertRaises(ContractError): validate_telemetry(body)

    def test_nonarmed_device_cannot_report_enabled_motor_targets(self):
        for key, value in (("motorEnabled", [True, False, False]), ("targetTensionN", [5., 0., 0.])):
            body = example_telemetry()
            body[key] = value
            with self.subTest(key=key), self.assertRaises(ContractError): validate_telemetry(body)

    def test_arm_and_tension_require_source_and_configuration_but_stop_does_not(self):
        body = example_stop()
        validate_command(body)
        body.update(action="arm", configurationId="a" * 64, sourceSequence=1, sourceDeviceTimeUs=10000, ttlUs=100000)
        validate_command(body)
        body["tensionsN"] = [5., 0., 0.]
        with self.assertRaises(ContractError): validate_command(body)
        body["action"] = "tension"
        validate_command(body)
        for key, value in (("configurationId", None), ("sourceSequence", None), ("ttlUs", 100001), ("tensionsN", [-1., 0., 0.])):
            bad = copy.deepcopy(body)
            bad[key] = value
            with self.subTest(key=key), self.assertRaises(ContractError): validate_command(bad)

    def test_simulated_data_needs_explicit_opt_in(self):
        frames = [encode(TELEMETRY, 1, 123, example_telemetry())]
        with self.assertRaises(ContractError): self.check_rows(frames, allow_simulated=False)
        result = self.check_rows(frames)
        self.assertFalse(result["hardware_acceptance"])

    def test_packet_duplicate_reordering_and_clock_reset_are_rejected(self):
        first = encode(TELEMETRY, 1, 123, example_telemetry())
        for second in (first, encode(TELEMETRY, 0, 123, example_telemetry(2)), encode(TELEMETRY, 2, 123, example_telemetry())):
            with self.assertRaises(ContractError): self.check_rows([first, second])
        self.assertEqual(self.check_rows([first, encode(TELEMETRY, 1, 124, example_telemetry())])["telemetry"], 2)

    def test_held_samples_retain_their_original_time_and_values(self):
        a, b = example_telemetry(), example_telemetry(2)
        b["forces"][0] = copy.deepcopy(a["forces"][0])
        first = encode(TELEMETRY, 1, 123, a)
        self.assertEqual(self.check_rows([first, encode(TELEMETRY, 2, 123, b)])["frames"], 2)
        b["forces"][0]["sampleTimeUs"] += 1
        with self.assertRaises(ContractError): self.check_rows([first, encode(TELEMETRY, 2, 123, b)])

    def test_partial_captures_and_empty_files_cannot_pass(self):
        frame = encode(TELEMETRY, 1, 123, example_telemetry())
        for data in (b"", frame[:12], frame[:-2]):
            with self.assertRaises(ContractError): self.check_rows([data])

    def test_timeout_can_revoke_a_sample_without_fabricating_a_new_acquisition(self):
        a = example_telemetry()
        a["forces"][0].update(forceN=3., calibrationId="a" * 64)
        b = copy.deepcopy(a)
        b["deviceTimeUs"] += 10000
        b["forces"][0].update(valid=False, forceN=None)
        first = encode(TELEMETRY, 1, 123, a)
        second = encode(TELEMETRY, 2, 123, b)
        self.assertEqual(self.check_rows([first, second])["frames"], 2)
        revived = copy.deepcopy(a)
        revived["deviceTimeUs"] += 20000
        with self.assertRaises(ContractError):
            self.check_rows([first, second, encode(TELEMETRY, 3, 123, revived)])
        b["forces"][0]["rawCode"] += 1
        with self.assertRaises(ContractError):
            self.check_rows([first, encode(TELEMETRY, 2, 123, b)])

    def test_acquired_identity_cannot_be_cleared_to_hide_a_sensor_reset(self):
        a, b = example_telemetry(), example_telemetry(2)
        b["forces"][0].update(valid=False, sampleSequence=None, sampleTimeUs=None, rawCode=None, status=None)
        with self.assertRaises(ContractError):
            self.check_rows([encode(TELEMETRY, 1, 123, a), encode(TELEMETRY, 2, 123, b)])

    def test_sequence_wrap_keeps_unsigned_half_range_rules(self):
        self.assertTrue(newer(0, 0xFFFFFFFF))
        self.assertFalse(newer(0xFFFFFFFF, 0))
        self.assertFalse(newer(0x80000000, 0))
        self.assertFalse(newer(8, 8))

    def test_decoded_file_is_utf8_jsonl_and_existing_capture_is_never_overwritten(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "tmp", prefix="hardware_decode_") as folder:
            capture, output = Path(folder) / "capture.bin", Path(folder) / "decoded.jsonl"
            data = encode(TELEMETRY, 1, 123, example_telemetry())
            capture.write_bytes(data)
            self.assertEqual(write_decoded_capture(capture, output), 1)
            self.assertEqual(json.loads(output.read_bytes().decode("utf-8"))["body"], example_telemetry())
            self.assertFalse(output.read_bytes().startswith(b"\xef\xbb\xbf"))
            with self.assertRaises(FileExistsError):
                write_decoded_capture(capture, capture)
            self.assertEqual(capture.read_bytes(), data)


if __name__ == "__main__":
    unittest.main()
