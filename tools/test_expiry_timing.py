"""Independent tick fixtures for offline expiry diagnostics and input preservation."""
import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import analyze_expiry_timing as timing


def frame(fixed, snapshot, post, valid, *, gc=3, line_stage="Udp", receive=1000):
    return {
        "stage": line_stage, "unityFrameCount": fixed, "fixedInputObservationTicks": fixed,
        "fixedPostObservationTicks": post, "acceptedReceiveTicks": receive,
        "frameObserverBeginTicks": post - 1, "sessionReplaying": False,
        **{f"fixedStartGc{i}": gc for i in range(3)},
        **{f"observerPostGc{i}": gc for i in range(3)},
        "frame": {"paddles": [{"valid": valid}], "acquisitionSnapshot": {
            "capturedMonotonicTicks": snapshot, "replayed": False, "channels": [{
                "channelId": "CH1", "receivedMonotonicTicks": receive, "rawValid": valid,
                "rawStatus": "OK" if valid else "STALE", "ageSeconds": -123.0}]}}
    }


class ExpiryTimingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="expiry-timing-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.write("normal_fixed_frames.jsonl", "\n".join(json.dumps(x) for x in (
            frame(1097, 1098, 1099, True), frame(1109, 1111, 1112, False, gc=4))) + "\n")
        self.write("runtime_result.json", {
            "timingStopwatchFrequency": 1000, "acquisitionStopRequestTicks": 1005,
            "senderStopRequestTicks": 1005, "acquisitionTimingWindowStartTicks": 1005,
            "acquisitionTimingWindowEndTicks": 1505, "timingWindowStartTicks": 1005,
            "timingWindowEndTicks": 1505, "result": "PASS"})
        self.kinds = [name for pair in timing.PAIRS.values() for name in pair]

    def write(self, name, value):
        (self.root / name).write_text(value if isinstance(value, str) else json.dumps(value), encoding="utf-8")

    def event(self, name, ticks, camera=0, gc=3):
        return {"kind": self.kinds.index(name), "ticks": ticks, "cameraInstanceId": camera,
                "gc0": gc, "gc1": gc, "gc2": gc}

    def trace(self, events, **overrides):
        return {"eventKinds": self.kinds, "events": events, "stopwatchFrequency": 1000,
                "windowStartTicks": 1005, "windowEndTicks": 1505, "droppedEvents": 0, **overrides}

    def test_exact_ticks_and_overlap_do_not_establish_causality(self):
        events = [self.event("FrameEvidenceWriteBegin", 1099),
                  self.event("CameraRenderBegin", 1100, 7), self.event("CameraRenderBegin", 1101, 8),
                  self.event("CameraRenderEnd", 1102, 8), self.event("CameraRenderEnd", 1103, 7),
                  self.event("FrameEvidenceWriteEnd", 1104, gc=4),
                  self.event("ScreenshotPollBegin", 1109), self.event("ScreenshotPollEnd", 1110)]
        self.write("adc_timing.json", self.trace(events))
        result = timing.analyze(self.root)
        adc = result["adc"]
        intervals = adc["intervals"]
        self.assertEqual(intervals["accepted_receive_to_first_invalid"]["elapsed_ticks"], 111)
        self.assertEqual(intervals["accepted_receive_to_previous_valid"]["elapsed_ms"], 98)
        self.assertEqual(intervals["accepted_receive_to_fixed_start"]["elapsed_ms"], 109)
        self.assertEqual(intervals["fixed_start_to_observation"]["elapsed_ms"], 2)
        self.assertEqual(intervals["previous_fixed_start_to_fixed_start"]["elapsed_ms"], 12)
        self.assertEqual(intervals["previous_valid_to_first_invalid"]["elapsed_ms"], 13)
        self.assertEqual(result["paddle"]["intervals"]["accepted_receive_to_first_invalid"]["elapsed_ms"], 112)
        self.assertEqual(adc["gc_deltas"]["previous_post_to_fixed_start"]["gen0"], 1)
        pairs = adc["trace"]["paired_intervals_near_failure"]
        write = next(x for x in pairs if x["event"] == "frame_evidence_write")
        self.assertEqual(write["elapsed_ms"], 5)
        self.assertEqual(write["gc_delta"]["gen0"], 1)
        self.assertTrue(write["overlaps_previous_post_to_next_fixed"])
        cameras = {x["camera_instance_id"]: x["elapsed_ms"] for x in pairs if x["event"] == "camera_render"}
        self.assertEqual(cameras, {7: 3, 8: 1})
        screenshot = next(x for x in pairs if x["event"] == "screenshot_poll")
        self.assertFalse(screenshot["overlaps_previous_post_to_next_fixed"])
        self.assertEqual(result["result"], "DIAGNOSTIC_ONLY")
        self.assertIn("overlap is not evidence of root cause", result["boundary"])
        self.assertIn("does not establish a deadline fix", result["boundary"])

    def test_legacy_missing_adc_trace_and_gc_are_unavailable(self):
        self.write("normal_fixed_frames.jsonl", "\n".join(json.dumps(x) for x in (
            frame(1097, 1098, 1099, True, gc=-1), frame(1109, 1111, 1112, False, gc=-1))) + "\n")
        report = json.loads((self.root / "runtime_result.json").read_text())
        del report["acquisitionTimingWindowStartTicks"], report["acquisitionTimingWindowEndTicks"]
        self.write("runtime_result.json", report)
        self.write("coast_timing.json", self.trace([]))
        adc = timing.analyze(self.root)["adc"]
        self.assertEqual(adc["status"], "available")
        self.assertEqual(adc["window"]["status"], "unavailable")
        self.assertEqual(adc["trace"]["status"], "unavailable")
        self.assertEqual(adc["gc_deltas"]["fixed_start_to_observer_post"]["status"], "unavailable")

    def test_dropped_and_unpaired_events_preserve_partial_boundary(self):
        self.write("adc_timing.json", self.trace([
            self.event("FrameEvidenceWriteBegin", 1099), self.event("FrameEvidenceWriteEnd", 1104),
            self.event("EditorUpdateBegin", 1105)], droppedEvents=2))
        trace = timing.analyze(self.root)["adc"]["trace"]
        self.assertEqual(trace["status"], "partial")
        self.assertEqual(trace["dropped_events"], 2)
        self.assertEqual(trace["pair_summaries"]["frame_evidence_write"]["status"], "partial")
        self.assertEqual(trace["pair_summaries"]["editor_update"]["status"], "unavailable")
        self.assertEqual(trace["pair_summaries"]["editor_update"]["unmatched_begins"], 1)

    def test_windows_array_and_missing_stop_marker(self):
        self.write("runtime_result.json", {"timingStopwatchFrequency": 1000})
        trace = self.trace([], windowStartTicks=0, windowEndTicks=0,
                           windows=[{"name": "adc", "startTicks": 1005, "endTicks": 1505}])
        self.write("adc_timing.json", trace)
        result = timing.analyze(self.root)
        self.assertEqual(result["adc"]["window"]["source"], "trace.windows")
        self.assertEqual(result["adc"]["stop_request_ticks"], "unavailable")
        self.assertEqual(result["adc"]["trace"]["status"], "unavailable")
        self.assertEqual(result["paddle"]["status"], "unavailable")

    def test_conflicting_frequencies_do_not_mix_clocks(self):
        self.write("adc_timing.json", self.trace([], stopwatchFrequency=2000))
        result = timing.analyze(self.root)
        self.assertEqual(result["stopwatch_frequency"], "unavailable")
        self.assertEqual(result["adc"]["status"], "unavailable")

    def test_duplicate_receive_identity_selects_only_matching_last_valid(self):
        self.write("normal_fixed_frames.jsonl", "\n".join(json.dumps(x) for x in (
            frame(1097, 1098, 1099, True, receive=999), frame(1109, 1111, 1112, False))) + "\n")
        adc = timing.analyze(self.root)["adc"]
        self.assertEqual(adc["previous_valid_same_receive"]["status"], "unavailable")
        self.assertEqual(adc["intervals"]["previous_valid_to_first_invalid"]["status"], "unavailable")

    def test_delayed_last_receive_does_not_select_intermediate_stale(self):
        self.write("normal_fixed_frames.jsonl", "\n".join(json.dumps(x) for x in (
            frame(1097, 1098, 1099, True), frame(1109, 1111, 1112, False),
            frame(1197, 1198, 1199, True, receive=1100),
            frame(1209, 1211, 1212, False, receive=1100),
            frame(1397, 1398, 1399, False, receive=1200, line_stage="Record"),
            frame(1499, 1506, 1507, False, receive=1300))) + "\n")
        adc = timing.analyze(self.root)["adc"]
        self.assertEqual(adc["last_accepted_receive_ticks"], 1100)
        self.assertEqual(adc["first_invalid"]["line"], 4)
        self.assertEqual(adc["previous_valid_same_receive"]["line"], 3)
        self.assertEqual(adc["intervals"]["accepted_receive_to_first_invalid"]["elapsed_ms"], 111)

    def test_cli_read_only_and_default_output_preserve_inputs(self):
        before = {p.name: p.read_bytes() for p in self.root.iterdir()}
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            self.assertEqual(timing.main([str(self.root), "--read-only"]), 0)
        self.assertEqual(json.loads(stdout.getvalue())["result"], "DIAGNOSTIC_ONLY")
        self.assertEqual({p.name: p.read_bytes() for p in self.root.iterdir()}, before)
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(timing.main([str(self.root)]), 0)
        self.assertTrue((self.root / "expiry_timing_analysis.json").is_file())
        self.assertEqual({name: (self.root / name).read_bytes() for name in before}, before)
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(timing.main([str(self.root), "--output", str(self.root / "normal_fixed_frames.jsonl")]), 2)
        self.assertEqual({name: (self.root / name).read_bytes() for name in before}, before)

    def test_nonfinite_input_is_rejected_without_writing(self):
        self.write("runtime_result.json", '{"timingStopwatchFrequency": NaN}')
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(timing.main([str(self.root)]), 2)
        self.assertFalse((self.root / "expiry_timing_analysis.json").exists())


if __name__ == "__main__":
    unittest.main()
