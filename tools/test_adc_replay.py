"""Boundary tests for PC normalized ADC preparation; socket operations are mocked."""

import contextlib
import csv
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import adc_replay as adc
import run_adc_runtime_check as runtime
import unity_execution


class AdcRuntimeRunnerTests(unittest.TestCase):
    def test_empty_or_duplicate_runtime_check_set_cannot_report_pass(self):
        required = sorted(runtime.REQUIRED_CHECKS)
        for names in ([], required[:-1] + [required[0]]):
            with self.subTest(names=names), tempfile.TemporaryDirectory(prefix="adc-evidence-", dir=Path(__file__).resolve().parents[1] / "tmp") as temporary:
                root = Path(temporary); unity = root / "Unity.exe"; unity.write_bytes(b"never launch")
                def converted(arguments, artifacts, label, report, timeout=30):
                    if label == "convert": (artifacts / "normalized.jsonl").write_bytes(b"{}\n" * runtime.PACKET_COUNT)
                def launch(command, **kwargs):
                    artifacts = Path(command[command.index("-adcRuntimeArtifacts") + 1])
                    (artifacts / "unity.log").write_text("[ADC_RUNTIME] RESULT=PASS\n", encoding="utf-8")
                    (artifacts / "screen.png").write_bytes(b"fake screenshot")
                    (artifacts / "adc_runtime_result.json").write_text(json.dumps({
                        "result": "PASS", "errors": [],
                        "checks": [{"name": name, "passed": True} for name in names],
                        "screenshots": [{"file": "screen.png", "completed": True}]}), encoding="utf-8")
                    process = mock.Mock(); process.poll.return_value = 0; process.returncode = 0; process.wait.return_value = 0
                    return process
                with mock.patch.object(runtime, "WORKSPACE", root), \
                        mock.patch.object(runtime, "resolve_unity", return_value=unity), \
                        mock.patch.object(runtime, "fingerprints", return_value={}), \
                        mock.patch.object(runtime, "cli_call", side_effect=converted), \
                        mock.patch.object(runtime, "unity_project_guard", side_effect=lambda project: contextlib.nullcontext()), \
                        mock.patch.object(runtime.subprocess, "Popen", side_effect=launch), \
                        mock.patch.object(runtime, "wait_ready", return_value={"port": 12345, "sourceId": runtime.SOURCE_ID}), \
                        mock.patch.object(runtime.time, "sleep"), contextlib.redirect_stdout(io.StringIO()):
                    code = runtime.main([])
                report_path, = (root / "tools/adc_runtime_checks").glob("*/verification.json")
                report = json.loads(report_path.read_text(encoding="utf-8"))
                self.assertEqual(code, 1)
                self.assertEqual(report["result"], "FAIL")
                self.assertIn("runtime check set is empty, missing, unexpected or duplicated", report["reasons"])

    def test_busy_project_preserves_failure_without_launching_unity(self):
        with tempfile.TemporaryDirectory(prefix="adc-runner-", dir=Path(__file__).resolve().parents[1] / "tmp") as temporary:
            root = Path(temporary)
            unity = root / "Unity.exe"
            unity.write_bytes(b"must not launch")
            def converted(arguments, artifacts, label, report, timeout=30):
                if label == "convert":
                    (artifacts / "normalized.jsonl").write_bytes(b"{}\n" * runtime.PACKET_COUNT)
            with mock.patch.object(runtime, "WORKSPACE", root), \
                    mock.patch.object(runtime, "resolve_unity", return_value=unity), \
                    mock.patch.object(runtime, "fingerprints", return_value={}), \
                    mock.patch.object(runtime, "cli_call", side_effect=converted), \
                    mock.patch.object(runtime, "unity_project_guard", side_effect=unity_execution.UnityProjectBusyError("another test runner")), \
                    mock.patch.object(runtime.subprocess, "Popen") as launch, contextlib.redirect_stdout(io.StringIO()):
                code = runtime.main([])
            launch.assert_not_called()
            report_path, = (root / "tools/adc_runtime_checks").glob("*/verification.json")
            report = json.loads(report_path.read_text(encoding="utf-8"))
            self.assertEqual(code, 1)
            self.assertEqual(report["result"], "FAIL")
            self.assertTrue(any("another test runner" in reason for reason in report["reasons"]))

    def test_timeout_cleanup_finishes_while_project_lock_is_held(self):
        with tempfile.TemporaryDirectory(prefix="adc-runner-", dir=Path(__file__).resolve().parents[1] / "tmp") as temporary:
            root = Path(temporary)
            unity = root / "Unity.exe"; unity.write_bytes(b"must not launch")
            held = {"value": False, "terminated": False}
            @contextlib.contextmanager
            def guard(project):
                held["value"] = True
                try: yield
                finally: held["value"] = False
            def converted(arguments, artifacts, label, report, timeout=30):
                if label == "convert": (artifacts / "normalized.jsonl").write_bytes(b"{}\n" * runtime.PACKET_COUNT)
            process = mock.Mock()
            process.poll.return_value = None; process.returncode = 9
            def terminate():
                self.assertTrue(held["value"])
                held["terminated"] = True
            process.terminate.side_effect = terminate
            def launch(*args, **kwargs):
                self.assertTrue(held["value"])
                return process
            with mock.patch.object(runtime, "WORKSPACE", root), \
                    mock.patch.object(runtime, "resolve_unity", return_value=unity), \
                    mock.patch.object(runtime, "fingerprints", return_value={}), \
                    mock.patch.object(runtime, "cli_call", side_effect=converted), \
                    mock.patch.object(runtime, "unity_project_guard", side_effect=guard), \
                    mock.patch.object(runtime.subprocess, "Popen", side_effect=launch), \
                    mock.patch.object(runtime, "wait_ready", side_effect=TimeoutError("fixture ready timeout")), \
                    contextlib.redirect_stdout(io.StringIO()):
                code = runtime.main([])
            self.assertEqual(code, 1)
            self.assertTrue(held["terminated"])
            self.assertFalse(held["value"])

    def test_fixture_includes_independent_invalid_missing_and_saturated_channels(self):
        with tempfile.TemporaryDirectory(prefix="adc-fixture-", dir=Path(__file__).resolve().parents[1] / "tmp") as temporary:
            root = Path(temporary)
            output = root / "normalized.jsonl"
            adc.convert_csv(runtime.make_fixture(root), output)
            packets, counts = adc.load_jsonl(output)
            self.assertEqual(len(packets), 80)
            self.assertEqual([p["sequence"] for p in packets[:3]], [adc.UINT32_MAX - 1, adc.UINT32_MAX, 0])
            middle = {c["channelId"]: c for c in packets[30]["channels"]}
            self.assertTrue(middle["CH1"]["valid"])
            self.assertFalse(middle["CH2"]["valid"])
            self.assertNotIn("CH3", middle)
            self.assertFalse(middle["CH4"]["hasRaw"])
            final = {c["channelId"]: c for c in packets[70]["channels"]}
            self.assertTrue(final["CH3"]["saturated"])
            self.assertTrue(final["CH4"]["hasRaw"])


class AdcReplayTests(unittest.TestCase):
    def setUp(self):
        temporary_root = Path(__file__).resolve().parents[1] / "tmp" / "adc_replay_tests"
        temporary_root.mkdir(parents=True, exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(prefix="adc_replay_test_", dir=temporary_root)
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def packet(self, sequence=1, sample=1, channel_id="CH1"):
        return {"format": adc.FORMAT, "sourceId": "csv-source", "sourceKind": 2,
                "sequenceValid": True, "sequence": sequence, "deviceTimeValid": False,
                "channels": [{"channelId": channel_id, "sampleSequenceValid": True,
                              "sampleSequence": sample, "sampleTimeValid": False,
                              "rawUnits": "adc-count", "hasRaw": True, "raw": 12.5,
                              "valid": True, "saturated": False}]}

    def row(self, **values):
        result = {"packet_sequence": "1", "source_id": "csv-source", "channel_id": "CH1",
                  "sample_sequence": "1", "raw_units": "adc-count", "raw": "12.5",
                  "valid": "true", "saturated": "false"}
        result.update(values)
        return result

    def write_csv(self, rows, optional=()):
        path = self.root / "input.csv"
        fields = ["packet_sequence", "source_id", "channel_id", "sample_sequence",
                  "raw_units", "raw", "valid", "saturated", *optional]
        with path.open("w", encoding="utf-8", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=fields)
            writer.writeheader()
            writer.writerows(rows)
        return path

    def write_jsonl(self, packets):
        path = self.root / "input.jsonl"
        path.write_bytes(b"\n".join(adc.packet_bytes(packet) for packet in packets) + b"\n")
        return path

    def test_fixture_conversion_preserves_wrap_partial_and_missing_raw(self):
        fixture = Path(__file__).parent / "fixtures" / "adc_csv" / "software_example.csv"
        output = self.root / "converted.jsonl"
        counts = adc.convert_csv(fixture, output)
        self.assertEqual((counts.lines, counts.packets, counts.channels), (7, 2, 7))
        packets, validated = adc.load_jsonl(output)
        self.assertEqual([p["sequence"] for p in packets], [adc.UINT32_MAX, 0])
        self.assertEqual([c["channelId"] for c in packets[1]["channels"]], ["CH1", "CH2", "CH4"])
        self.assertEqual(validated.channels, 7)
        self.assertEqual(packets[0]["sourceKind"], 2)
        self.assertEqual(packets[0]["channels"][0]["raw"], 123)
        self.assertEqual(packets[0]["channels"][0]["rawUnits"], "example-count")
        self.assertFalse(packets[1]["channels"][-1]["hasRaw"])
        self.assertNotIn("raw", packets[1]["channels"][-1])
        self.assertNotIn("forceN", output.read_text(encoding="utf-8"))

    def test_csv_duplicate_channel_nonconsecutive_packet_and_source_changes(self):
        cases = [
            [self.row(), self.row()],
            [self.row(), self.row(packet_sequence="2", sample_sequence="2"),
             self.row(packet_sequence="1", sample_sequence="3")],
            [self.row(), self.row(channel_id="CH2", source_id="other")],
            [self.row(), self.row(packet_sequence="2", sample_sequence="2", source_id="other")],
            [self.row(channel_id="CH5")],
        ]
        for rows in cases:
            with self.subTest(rows=rows), self.assertRaises(adc.ReplayError):
                adc.load_csv(self.write_csv(rows))

    def test_csv_strict_bool_and_missing_raw(self):
        for token in ("True", "FALSE", "yes", "", "2"):
            with self.subTest(token=token), self.assertRaises(adc.ReplayError):
                adc.load_csv(self.write_csv([self.row(valid=token)]))
        for token in ("true", "false", "1", "0"):
            with self.subTest(token=token):
                packets, _ = adc.load_csv(self.write_csv([self.row(valid=token, saturated=token)]))
                self.assertIs(packets[0]["channels"][0]["valid"], token in {"true", "1"})
        with self.assertRaises(adc.ReplayError):
            adc.load_csv(self.write_csv([self.row(raw="")]))
        packets, _ = adc.load_csv(self.write_csv([self.row(raw="", has_raw="false")], optional=("has_raw",)))
        self.assertNotIn("raw", packets[0]["channels"][0])
        with self.assertRaises(adc.ReplayError):
            adc.load_csv(self.write_csv([self.row(raw="", has_raw="true")], optional=("has_raw",)))

    def test_csv_finite_numbers_and_unsigned_bounds(self):
        for raw in ("NaN", "inf", "-Infinity", "1e309", "0x12", "abc"):
            with self.subTest(raw=raw), self.assertRaises(adc.ReplayError):
                adc.load_csv(self.write_csv([self.row(raw=raw)]))
        for sequence in ("-1", "4294967296", "1.5", "1e1", "+1", ""):
            with self.subTest(sequence=sequence), self.assertRaises(adc.ReplayError):
                adc.load_csv(self.write_csv([self.row(packet_sequence=sequence)]))
        row = self.row(device_time_us=str(adc.UINT64_MAX), sample_time_us=str(adc.UINT64_MAX))
        packets, _ = adc.load_csv(self.write_csv([row], optional=("device_time_us", "sample_time_us")))
        self.assertEqual(packets[0]["deviceTimeUs"], adc.UINT64_MAX)
        with self.assertRaises(adc.ReplayError):
            adc.load_csv(self.write_csv([self.row(device_time_us=str(adc.UINT64_MAX + 1))], optional=("device_time_us",)))

    def test_device_time_consistent_across_packet_optional_sample_time(self):
        row1 = self.row(device_time_us="12", sample_time_us="12")
        row2 = self.row(channel_id="CH2", device_time_us="13", sample_time_us="14")
        with self.assertRaises(adc.ReplayError):
            adc.load_csv(self.write_csv([row1, row2], optional=("device_time_us", "sample_time_us")))
        row2["device_time_us"] = "12"
        packets, _ = adc.load_csv(self.write_csv([row1, row2], optional=("device_time_us", "sample_time_us")))
        self.assertEqual(packets[0]["channels"][1]["sampleTimeUs"], 14)
        plain, _ = adc.load_csv(self.write_csv([self.row()]))
        self.assertFalse(plain[0]["deviceTimeValid"])
        self.assertNotIn("deviceTimeUs", plain[0])
        self.assertFalse(plain[0]["channels"][0]["sampleTimeValid"])

    def test_csv_header_and_row_field_evidence(self):
        path = self.root / "bad.csv"
        for text in ("raw,valid\n1,true\n", "packet_sequence,packet_sequence\n1,1\n",
                     "packet_sequence,source_id,channel_id,sample_sequence,raw_units,raw,valid,saturated\n"
                     "1,csv-source,CH1,1,raw,1,true\n"):
            path.write_text(text, encoding="utf-8")
            with self.subTest(text=text), self.assertRaises(adc.ReplayError):
                adc.load_csv(path)

    def test_json_duplicate_keys_utf8_nonfinite_and_surrogates(self):
        encoded = adc.packet_bytes(self.packet())
        bad = [encoded.replace(b'"sequence":1', b'"sequence":1,"sequence":2'),
               encoded.replace(b'"raw":12.5', b'"raw":12.5,"raw":13'),
               encoded.replace(b'"raw":12.5', b'"raw":NaN'),
               encoded.replace(b'"raw":12.5', b'"raw":1e309'),
               b'\xff' + encoded, b'\xef\xbb\xbf' + encoded,
               encoded.replace(b'"sourceId":"csv-source"', b'"sourceId":"\\ud800"')]
        for data in bad:
            with self.subTest(data=data[:90]), self.assertRaises(adc.ReplayError):
                adc.parse_packet(data)

    def test_json_explicit_field_types_and_unknown_fields(self):
        for field in ("format", "sourceId", "sourceKind", "sequenceValid", "sequence", "deviceTimeValid", "channels"):
            packet = self.packet()
            del packet[field]
            with self.subTest(field=field), self.assertRaises(adc.ReplayError):
                adc.parse_packet(json.dumps(packet))
        for field in ("channelId", "rawUnits", "sampleSequenceValid", "sampleSequence", "sampleTimeValid",
                      "hasRaw", "raw", "valid", "saturated"):
            packet = self.packet()
            del packet["channels"][0][field]
            with self.subTest(field=field), self.assertRaises(adc.ReplayError):
                adc.parse_packet(json.dumps(packet))
        for field, value in (("sequence", True), ("sequenceValid", 1), ("sequence", 1.5), ("sourceKind", "2")):
            packet = self.packet()
            packet[field] = value
            with self.subTest(field=field), self.assertRaises(adc.ReplayError):
                adc.parse_packet(json.dumps(packet))
        packet = self.packet()
        packet["channels"][0]["forceN"] = 100
        with self.assertRaises(adc.ReplayError):
            adc.parse_packet(json.dumps(packet))

    def test_json_byte_depth_and_node_limits(self):
        packet = self.packet()
        packet["channels"][0]["rawText"] = ""
        base = adc.packet_bytes(packet)
        packet["channels"][0]["rawText"] = "x" * (adc.MAX_PACKET_BYTES - len(base))
        self.assertEqual(len(adc.packet_bytes(packet)), adc.MAX_PACKET_BYTES)
        self.assertEqual(adc.parse_packet(adc.packet_bytes(packet))["sequence"], 1)
        packet["channels"][0]["rawText"] += "x"
        with self.assertRaises(adc.ReplayError):
            adc.packet_bytes(packet)
        packet["channels"][0]["rawText"] = "汉" * 6000
        with self.assertRaises(adc.ReplayError):
            adc.packet_bytes(packet)
        with self.assertRaises(adc.ReplayError):
            adc.parse_packet("[" * 50 + "0" + "]" * 50)
        with self.assertRaises(adc.ReplayError):
            adc.parse_packet("[" + ",".join("0" for _ in range(4096)) + "]")

    def test_jsonl_sequence_wrap_and_per_channel_order(self):
        valid = [self.packet(adc.UINT32_MAX, adc.UINT32_MAX), self.packet(0, 0)]
        _, counts = adc.load_jsonl(self.write_jsonl(valid))
        self.assertEqual(counts.packets, 2)
        for packets in ([self.packet(), self.packet(1, 2)], [self.packet(), self.packet(0, 2)],
                        [self.packet(0, 0), self.packet(1 << 31, 1)],
                        [self.packet(1, 5), self.packet(2, 5)],
                        [self.packet(1, 5), self.packet(2, 7, "CH2"), self.packet(3, 4)]):
            with self.subTest(sequences=[p["sequence"] for p in packets]), self.assertRaises(adc.ReplayError):
                adc.load_jsonl(self.write_jsonl(packets))

    def test_invalid_input_creates_no_output_and_existing_output_is_preserved(self):
        path = self.write_csv([self.row(raw="")])
        output = self.root / "result.jsonl"
        with self.assertRaises(adc.ReplayError):
            adc.convert_csv(path, output)
        self.assertFalse(output.exists())
        output.write_bytes(b"existing-user-file")
        with self.assertRaises(adc.ReplayError):
            adc.convert_csv(self.write_csv([self.row()]), output)
        self.assertEqual(output.read_bytes(), b"existing-user-file")

    def test_convert_validate_cli_never_opens_socket(self):
        input_csv = self.write_csv([self.row()])
        output = self.root / "converted.jsonl"
        capture = io.StringIO()
        with mock.patch.object(adc.socket, "socket", side_effect=AssertionError("network must be explicit")), contextlib.redirect_stdout(capture):
            self.assertEqual(adc.main(["convert", "--csv", str(input_csv), "--out", str(output)]), 0)
            self.assertEqual(adc.main(["validate", "--input", str(output)]), 0)
        self.assertIn("lines=1 packets=1 channels=1 source_id=csv-source", capture.getvalue())

    def test_profile_cli_preserves_four_explicit_units_and_raw_only_limits(self):
        packets = []
        expected_units = ["adc-count", "millivolt", "bridge-ratio", "vendor-raw"]
        for index, units in enumerate(expected_units):
            packet = self.packet(index + 1, 1, "CH" + str(index + 1))
            packet["channels"][0]["rawUnits"] = units
            packets.append(packet)
        input_path = self.write_jsonl(packets)
        output = self.root / "raw_profile.json"
        with mock.patch.object(adc.socket, "socket", side_effect=AssertionError("profile must not use network")), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(adc.main(["profile", "--input", str(input_path), "--out", str(output), "--port", "9874"]), 0)
        profile = json.loads(output.read_text(encoding="utf-8"))
        self.assertEqual((profile["schemaVersion"], profile["version"], profile["sourceId"]), (1, "pc-csv-raw-only-v1", "csv-source"))
        self.assertEqual(profile["loopbackPort"], 9874)
        self.assertTrue(profile["enableLoopbackUdp"])
        self.assertEqual([c["channelId"] for c in profile["channels"]], ["CH1", "CH2", "CH3", "CH4"])
        self.assertEqual([c["rawUnits"] for c in profile["channels"]], expected_units)
        for channel in profile["channels"]:
            self.assertEqual(channel["mapping"], "Unassigned")
            self.assertEqual(channel["maximumAgeSeconds"], .1)
            self.assertFalse(channel["rawRangeConfigured"])
            self.assertEqual((channel["minimumRaw"], channel["maximumRaw"]), (0, 0))
            calibration = channel["calibration"]
            self.assertEqual(calibration["mode"], 0)
            self.assertFalse(calibration["measured"])
            self.assertEqual((calibration["sourceId"], calibration["version"], calibration["evidence"]), ("", "", ""))
            self.assertEqual(calibration["scaleNPerRaw"], 0)
            self.assertEqual(calibration["points"], [])
            self.assertFalse(calibration["forceRangeConfigured"])
            self.assertNotIn("forceN", channel)

    def test_profile_missing_channel_or_changed_units_or_source_creates_no_output(self):
        complete = [self.packet(index + 1, 1, "CH" + str(index + 1)) for index in range(4)]
        changed_units = self.packet(5, 2, "CH1")
        changed_units["channels"][0]["rawUnits"] = "changed-unit"
        changed_source = self.packet(5, 2, "CH1"); changed_source["sourceId"] = "other-source"
        for packets in (complete[:-1], complete + [changed_units], complete + [changed_source]):
            with self.subTest(packets=packets):
                output = self.root / "raw_profile.json"
                with self.assertRaises(adc.ReplayError): adc.create_raw_profile(self.write_jsonl(packets), output, 9874)
                self.assertFalse(output.exists())

    def test_profile_existing_output_bad_port_and_bad_later_line_preserve_files(self):
        packets = [self.packet(index + 1, 1, "CH" + str(index + 1)) for index in range(4)]
        input_path = self.write_jsonl(packets)
        output = self.root / "raw_profile.json"
        for port in (0, 65536, True, 1.2):
            with self.subTest(port=port), self.assertRaises(adc.ReplayError): adc.create_raw_profile(input_path, output, port)
            self.assertFalse(output.exists())
        input_path.write_bytes(input_path.read_bytes() + b"{bad}\n")
        with self.assertRaises(adc.ReplayError): adc.create_raw_profile(input_path, output, 9874)
        self.assertFalse(output.exists())
        output.write_bytes(b"existing user profile")
        with self.assertRaises(adc.ReplayError): adc.create_raw_profile(self.write_jsonl(packets), output, 9874)
        self.assertEqual(output.read_bytes(), b"existing user profile")

    def test_send_rejects_host_invalid_port_interval_and_bad_later_line_before_socket(self):
        path = self.write_jsonl([self.packet()])
        with mock.patch.object(adc.socket, "socket", side_effect=AssertionError("no socket before validation")):
            for host in ("localhost", "::1", "192.168.1.2", "127.0.0.2"):
                with self.subTest(host=host), self.assertRaises(adc.ReplayError):
                    adc.send_jsonl(path, 9874, host=host)
            for port in (0, 65536, True):
                with self.subTest(port=port), self.assertRaises(adc.ReplayError):
                    adc.send_jsonl(path, port)
            for interval in (0, -0.1, float("nan"), float("inf")):
                with self.subTest(interval=interval), self.assertRaises(adc.ReplayError):
                    adc.send_jsonl(path, 9874, interval)
            path.write_bytes(adc.packet_bytes(self.packet()) + b"\n{bad}\n")
            with self.assertRaises(adc.ReplayError):
                adc.send_jsonl(path, 9874)

    def test_send_explicit_packet_count_and_assumed_cadence_without_real_network(self):
        packets = [self.packet(1, 1), self.packet(2, 2)]
        path = self.write_jsonl(packets)
        connection = mock.MagicMock()
        connection.sendto.side_effect = lambda data, address: len(data)
        socket_context = mock.MagicMock()
        socket_context.__enter__.return_value = connection
        capture = io.StringIO()
        with mock.patch.object(adc.socket, "socket", return_value=socket_context) as create, \
                mock.patch.object(adc.time, "sleep") as sleep, contextlib.redirect_stdout(capture):
            counts = adc.send_jsonl(path, 9874)
        create.assert_called_once_with(adc.socket.AF_INET, adc.socket.SOCK_DGRAM)
        self.assertEqual(counts.packets, 2)
        self.assertEqual(connection.sendto.call_count, 2)
        for call, packet in zip(connection.sendto.call_args_list, packets):
            self.assertEqual(call.args[1], ("127.0.0.1", 9874))
            self.assertEqual(adc.parse_packet(call.args[0]), packet)
        sleep.assert_called_once_with(0.02)
        self.assertIn("PC_REPLAY_CADENCE_ASSUMED", capture.getvalue())


if __name__ == "__main__":
    unittest.main()
