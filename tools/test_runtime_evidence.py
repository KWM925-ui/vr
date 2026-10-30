"""Independent fixtures for background evidence completeness and parsing refusal."""
import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from runtime_evidence import verify_runtime_evidence


class RuntimeEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="runtime-evidence-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.frame_bytes = b'{"stage":"Udp","frame":{"value":1}}\r\n{"stage":"Coast","frame":{"value":2}}\r\n'
        self.return_bytes = b'{"timestampUs":1,"dragN":0.5}\n'
        (self.root / "normal_fixed_frames.jsonl").write_bytes(self.frame_bytes)
        (self.root / "boatstate_loopback.jsonl").write_bytes(self.return_bytes)
        self.report = {
            "evidenceWritersDrainedAndCountVerified": True, "observedFixedFrames": 2,
            "frameEvidenceQueuedLines": 2, "frameEvidenceWrittenLines": 2,
            "returnEvidenceQueuedLines": 1, "returnEvidenceWrittenLines": 1,
            "frameEvidenceSha256": hashlib.sha256(self.frame_bytes).hexdigest(),
            "returnEvidenceSha256": hashlib.sha256(self.return_bytes).hexdigest(),
        }

    def substitute(self, raw, frame=True):
        prefix = "frame" if frame else "return"
        name = "normal_fixed_frames.jsonl" if frame else "boatstate_loopback.jsonl"
        (self.root / name).write_bytes(raw)
        count = len(raw.splitlines())
        self.report[prefix + "EvidenceQueuedLines"] = count
        self.report[prefix + "EvidenceWrittenLines"] = count
        self.report[prefix + "EvidenceSha256"] = hashlib.sha256(raw).hexdigest()
        if frame:
            self.report["observedFixedFrames"] = count

    def test_physical_crlf_and_lf_counts_hash_and_inputs_unchanged(self):
        before = {path.name: path.read_bytes() for path in self.root.iterdir()}
        report = copy.deepcopy(self.report)
        result = verify_runtime_evidence(self.root, self.report)
        self.assertEqual(result["status"], "VERIFIED")
        self.assertEqual(result["frame_evidence"]["lines"], 2)
        self.assertEqual(result["frame_evidence"]["bytes"], len(self.frame_bytes))
        self.assertEqual(result["frame_evidence"]["sha256"], self.report["frameEvidenceSha256"])
        self.assertEqual(result["return_evidence"]["lines"], 1)
        self.assertIn("does not establish runtime acceptance", result["boundary"])
        self.assertEqual({path.name: path.read_bytes() for path in self.root.iterdir()}, before)
        self.assertEqual(self.report, report)

    def test_whole_line_tail_loss_is_not_success(self):
        (self.root / "normal_fixed_frames.jsonl").write_bytes(self.frame_bytes.splitlines(keepends=True)[0])
        with self.assertRaisesRegex(ValueError, "actual/queued/written"):
            verify_runtime_evidence(self.root, self.report)

    def test_valid_json_without_final_newline_is_torn(self):
        self.substitute(b'{"frame":{}}')
        with self.assertRaisesRegex(ValueError, "Unterminated/torn"):
            verify_runtime_evidence(self.root, self.report)

    def test_partial_json_is_refused_even_with_matching_hash_count(self):
        self.substitute(b'{"frame":{"value":\n')
        with self.assertRaises(ValueError):
            verify_runtime_evidence(self.root, self.report)

    def test_missing_legacy_fields_and_incomplete_writer_are_refused(self):
        for key in self.report:
            changed = self.report.copy()
            del changed[key]
            with self.subTest(key=key), self.assertRaises(ValueError):
                verify_runtime_evidence(self.root, changed)
        for value in (False, None, 1, "true"):
            changed = dict(self.report, evidenceWritersDrainedAndCountVerified=value)
            with self.subTest(value=value), self.assertRaises(ValueError):
                verify_runtime_evidence(self.root, changed)

    def test_counter_mismatch_and_noninteger_counters_are_refused(self):
        for key in ("observedFixedFrames", "frameEvidenceQueuedLines", "frameEvidenceWrittenLines",
                    "returnEvidenceQueuedLines", "returnEvidenceWrittenLines"):
            for value in (3, True, 0, -1, 1.0, "2"):
                changed = dict(self.report, **{key: value})
                with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                    verify_runtime_evidence(self.root, changed)

    def test_hash_mismatch_and_invalid_hash_are_refused(self):
        for key in ("frameEvidenceSha256", "returnEvidenceSha256"):
            for value in ("0" * 64, "not-sha256", None):
                changed = dict(self.report, **{key: value})
                with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                    verify_runtime_evidence(self.root, changed)

    def test_invalid_utf8_bom_duplicate_and_nonfinite_are_refused(self):
        for raw in (
                b'{"frame":{"text":"\xff"}}\n',
                b'\xef\xbb\xbf{"frame":{}}\n',
                b'{"frame":{},"frame":{}}\n',
                b'{"frame":{"value":1,"value":2}}\n',
                b'{"frame":{"value":1,"va\\u006cue":2}}\n',
                b'{"frame":{"value":NaN}}\n',
                b'{"frame":{"value":Infinity}}\n',
                b'{"frame":{"value":-Infinity}}\n',
                b'{"frame":{"value":1e999}}\n'):
            with self.subTest(raw=raw):
                self.substitute(raw)
                before = (self.root / "normal_fixed_frames.jsonl").read_bytes()
                with self.assertRaises(ValueError):
                    verify_runtime_evidence(self.root, self.report)
                self.assertEqual((self.root / "normal_fixed_frames.jsonl").read_bytes(), before)

    def test_empty_nondict_and_missing_frame_shape_are_refused(self):
        for raw in (b"", b"\n", b"  \n", b"{}\n", b"[]\n", b"null\n", b'{"stage":"Udp"}\n',
                    b'{"frame":null}\n', b'{"frame":[]}\n'):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                self.substitute(raw)
                verify_runtime_evidence(self.root, self.report)

    def test_return_file_has_same_strict_parser_without_protocol_mirror(self):
        self.substitute(b'{"otherDictionaryShape":true}\n', frame=False)
        self.assertEqual(verify_runtime_evidence(self.root, self.report)["return_evidence"]["lines"], 1)
        for raw in (b'{}\n', b'{"value":1,"value":2}\n', b'{"value":1e999}\n', b'{"value":"\xff"}\n'):
            with self.subTest(raw=raw):
                self.substitute(raw, frame=False)
                with self.assertRaises(ValueError):
                    verify_runtime_evidence(self.root, self.report)

    def test_missing_file_is_value_error(self):
        (self.root / "boatstate_loopback.jsonl").unlink()
        with self.assertRaisesRegex(ValueError, "boatstate_loopback"):
            verify_runtime_evidence(self.root, self.report)


if __name__ == "__main__":
    unittest.main()
