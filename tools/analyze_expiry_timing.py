"""Offline expiry observations; never launch Unity or infer a scheduling root cause.

Usage: python tools/analyze_expiry_timing.py ARTIFACTS [--read-only]
Inputs are unchanged. By default only expiry_timing_analysis.json is written.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys


BOUNDARY = (
    "Diagnostic observations only. Temporal overlap is not evidence of root cause. "
    "A passing run does not establish a deadline fix or hard-real-time delivery. "
    "Missing events, dropped events and unavailable GC counters limit attribution."
)
PAIRS = {
    "frame_observer": ("FrameObserverBegin", "FrameObserverEnd"),
    "editor_update": ("EditorUpdateBegin", "EditorUpdateEnd"),
    "camera_render": ("CameraRenderBegin", "CameraRenderEnd"),
    "render_observer": ("CameraRenderEnd", "RenderObserverEnd"),
    "frame_evidence_write": ("FrameEvidenceWriteBegin", "FrameEvidenceWriteEnd"),
    "return_evidence_write": ("ReturnEvidenceWriteBegin", "ReturnEvidenceWriteEnd"),
    "screenshot_poll": ("ScreenshotPollBegin", "ScreenshotPollEnd"),
    "return_drain": ("ReturnDrainBegin", "ReturnDrainEnd"),
}


def _unavailable(reason):
    return {"status": "unavailable", "reason": reason}


def _integer(value):
    return value if isinstance(value, int) and not isinstance(value, bool) else None


def _positive(value):
    value = _integer(value)
    return value if value is not None and value > 0 else None


def _gc_delta(before, after):
    if not all(_integer(x) is not None and x >= 0 for x in (*before, *after)):
        return _unavailable("GC counters are missing or -1")
    delta = [y - x for x, y in zip(before, after)]
    if any(x < 0 for x in delta):
        return _unavailable("GC counter decreased; endpoints are not comparable")
    return {"status": "available", "gen0": delta[0], "gen1": delta[1], "gen2": delta[2]}


def _gc(row, prefix):
    return [row.get(prefix + str(i), -1) for i in range(3)]


def _loads(text):
    def refuse(value):
        raise ValueError("Nonfinite JSON number: " + value)
    return json.loads(text, parse_constant=refuse)


def _read(path, sources, optional=False):
    if optional and not path.exists():
        return None
    raw = path.read_bytes()
    sources[path.name] = {"sha256": hashlib.sha256(raw).hexdigest(), "bytes": len(raw)}
    return raw.decode("utf-8-sig", errors="strict")


def _window(trace, report, acquisition):
    candidates = []
    if trace:
        candidates.append((trace, "trace"))
        windows = trace.get("windows", [])
        if isinstance(windows, dict):
            windows = [dict(value, name=name) for name, value in windows.items() if isinstance(value, dict)]
        if isinstance(windows, list):
            for value in windows:
                if not isinstance(value, dict):
                    continue
                label = str(value.get("name", value.get("kind", ""))).lower()
                if (acquisition and ("adc" in label or "acquisition" in label)) or (
                        not acquisition and ("coast" in label or "paddle" in label)) or len(windows) == 1:
                    candidates.append((value, "trace.windows"))
    for candidate, source in candidates:
        start = _positive(candidate.get("windowStartTicks", candidate.get("startTicks")))
        end = _positive(candidate.get("windowEndTicks", candidate.get("endTicks")))
        if start and end and end > start:
            return {"status": "available", "start_ticks": start, "end_ticks": end, "source": source}
    prefix = "acquisitionTimingWindow" if acquisition else "timingWindow"
    start, end = (_positive(report.get(prefix + suffix)) for suffix in ("StartTicks", "EndTicks"))
    if start and end and end > start:
        return {"status": "available", "start_ticks": start, "end_ticks": end, "source": "runtime_result"}
    return _unavailable("No valid timing window was recorded")


def _overlap(start, end, gap_start, gap_end):
    return end > start and gap_end > gap_start and start < gap_end and end > gap_start


def _trace_analysis(trace, window, frequency, previous_post, next_fixed, failure_post):
    if trace is None:
        return _unavailable("Timing trace file is absent")
    if window["status"] != "available" or not frequency:
        return _unavailable("Timing window or stopwatch frequency is unavailable")
    kinds = trace.get("eventKinds", [])
    if not isinstance(kinds, list) or not isinstance(trace.get("events"), list):
        return _unavailable("eventKinds/events are missing")
    events, malformed, out_of_order, previous_ticks = [], 0, 0, None
    for value in trace["events"]:
        if not isinstance(value, dict) or not _positive(value.get("ticks")):
            malformed += 1
            continue
        kind = value.get("kind")
        if _integer(kind) is not None and 0 <= kind < len(kinds):
            name = kinds[kind]
        elif isinstance(kind, str):
            name = kind
        else:
            malformed += 1
            continue
        ticks = value["ticks"]
        if previous_ticks is not None and ticks < previous_ticks:
            out_of_order += 1
        previous_ticks = ticks
        if window["start_ticks"] <= ticks <= window["end_ticks"]:
            events.append(dict(value, name=name))
    events.sort(key=lambda event: event["ticks"])
    dropped = _integer(trace.get("droppedEvents"))
    coverage_complete = dropped == 0 and malformed == 0 and out_of_order == 0
    summaries, near = {}, []
    for label, (begin, end) in PAIRS.items():
        opened, paired, unmatched_ends = {}, [], 0
        for event in events:
            key = event.get("cameraInstanceId", 0) if "render" in label else 0
            if event["name"] == begin:
                opened.setdefault(key, []).append(event)
            elif event["name"] == end:
                if not opened.get(key):
                    unmatched_ends += 1
                    continue
                first = opened[key].pop()
                interval = {
                    "event": label, "start_ticks": first["ticks"], "end_ticks": event["ticks"],
                    "elapsed_ticks": event["ticks"] - first["ticks"],
                    "elapsed_ms": (event["ticks"] - first["ticks"]) * 1000 / frequency,
                    "camera_instance_id": key,
                    "gc_delta": _gc_delta(_gc(first, "gc"), _gc(event, "gc")),
                    "overlaps_previous_post_to_next_fixed": (
                        _overlap(first["ticks"], event["ticks"], previous_post, next_fixed)
                        if previous_post is not None and next_fixed is not None else "unavailable"),
                }
                paired.append(interval)
                if previous_post is not None and failure_post is not None and _overlap(
                        first["ticks"], event["ticks"], previous_post, failure_post):
                    near.append(interval)
        unmatched_begins = sum(len(stack) for stack in opened.values())
        if not paired:
            summaries[label] = dict(_unavailable("No complete event pair"),
                                    unmatched_begins=unmatched_begins, unmatched_ends=unmatched_ends)
        else:
            complete = coverage_complete and unmatched_begins == 0 and unmatched_ends == 0
            summaries[label] = {
                "status": "available" if complete else "partial", "observed_pairs": len(paired),
                "maximum_elapsed_ms": max(item["elapsed_ms"] for item in paired),
                "total_elapsed_ms": sum(item["elapsed_ms"] for item in paired),
                "unmatched_begins": unmatched_begins, "unmatched_ends": unmatched_ends,
            }
    return {
        "status": "unavailable" if not events else "available" if coverage_complete else "partial",
        "recorded_events_in_window": len(events), "dropped_events": dropped if dropped is not None else "unavailable",
        "malformed_events": malformed, "out_of_order_events": out_of_order,
        "pair_summaries": summaries,
        "paired_intervals_near_failure": sorted(near, key=lambda item: (item["start_ticks"], item["event"])),
        "overlap_interval_definition": "Immediately preceding frame post marker to first-invalid frame fixed-start marker; half-open intervals.",
        "boundary": BOUNDARY,
    }


def _frame_summary(row, acquisition):
    return {"line": row["line"], "stage": row.get("stage"), "unity_frame": row.get("unityFrameCount"),
            "fixed_start_ticks": row.get("fixedInputObservationTicks"), "post_ticks": row.get("fixedPostObservationTicks"),
            "observation_ticks": row.get("adc_snapshot_ticks") if acquisition else row.get("fixedPostObservationTicks"),
            "raw_status": row.get("adc_status") if acquisition else None}


def _expiry(rows, report, trace, frequency, acquisition):
    window = _window(trace, report, acquisition)
    stop_name = "acquisitionStopRequestTicks" if acquisition else "senderStopRequestTicks"
    stop = _positive(report.get(stop_name))
    start = stop or (window.get("start_ticks") if window["status"] == "available" else None)
    if start is None:
        return dict(_unavailable("Sender stop marker/window is absent"), window=window)
    observation_key = "adc_snapshot_ticks" if acquisition else "fixedPostObservationTicks"
    receive_key = "adc_receive_ticks" if acquisition else "acceptedReceiveTicks"
    valid_key = "adc_valid" if acquisition else "paddle_valid"
    eligible = []
    for index, row in enumerate(rows):
        ticks = _positive(row.get(observation_key))
        if row.get("stage") not in ("Udp", "Coast") or row.get("replayed") or not ticks or ticks < start:
            continue
        if window["status"] == "available" and ticks > window["end_ticks"]:
            continue
        eligible.append((index, row))
    received = max((_positive(row.get(receive_key)) or 0 for _, row in eligible), default=0)
    failure = next((index for index, row in eligible if row.get(receive_key) == received
                    and row.get(valid_key) is False
                    and (not acquisition or row.get("adc_status") == "STALE")), None)
    if failure is None:
        return dict(_unavailable("No matching first-invalid observation in the recorded window"), window=window)
    row = rows[failure]
    if not received or not frequency:
        return dict(_unavailable("Accepted receive ticks or stopwatch frequency is absent"), window=window)
    previous = rows[failure - 1] if failure else None
    previous_valid = next((item for item in reversed(rows[:failure]) if not item.get("replayed")
                           and item.get("stage") in ("Udp", "Coast")
                           and item.get(receive_key) == received and item.get(valid_key) is True), None)
    observation, fixed = row[observation_key], _positive(row.get("fixedInputObservationTicks"))
    def elapsed(end, begin):
        if _integer(end) is None or _integer(begin) is None or end < begin:
            return _unavailable("Missing or reversed interval endpoints")
        return {"status": "available", "elapsed_ticks": end - begin, "elapsed_ms": (end - begin) * 1000 / frequency}
    previous_post = _positive(previous.get("fixedPostObservationTicks")) if previous else None
    intervals = {
        "accepted_receive_to_first_invalid": elapsed(observation, received),
        "accepted_receive_to_previous_valid": elapsed(previous_valid.get(observation_key) if previous_valid else None, received),
        "accepted_receive_to_fixed_start": elapsed(fixed, received),
        "fixed_start_to_observation": elapsed(observation, fixed),
        "previous_fixed_start_to_fixed_start": elapsed(fixed, previous.get("fixedInputObservationTicks") if previous else None),
        "previous_post_to_fixed_start": elapsed(fixed, previous_post),
        "previous_valid_to_first_invalid": elapsed(observation, previous_valid.get(observation_key) if previous_valid else None),
        "frame_observer_begin_to_post": elapsed(row.get("fixedPostObservationTicks"), row.get("frameObserverBeginTicks")),
        "previous_frame_observer_begin_to_end": elapsed(row.get("previousFrameObserverEndTicks"), previous.get("frameObserverBeginTicks") if previous else None),
    }
    return {
        "status": "available", "channel": "CH1" if acquisition else "paddle[0]", "window": window,
        "stop_request_ticks": stop if stop else "unavailable", "last_accepted_receive_ticks": received,
        "first_invalid": _frame_summary(row, acquisition),
        "previous_valid_same_receive": _frame_summary(previous_valid, acquisition) if previous_valid else _unavailable("No preceding valid observation for the same receive ticks"),
        "previous_completed_frame": _frame_summary(previous, acquisition) if previous else _unavailable("No preceding frame"),
        "intervals": intervals,
        "gc_deltas": {
            "previous_post_to_fixed_start": _gc_delta(_gc(previous or {}, "observerPostGc"), _gc(row, "fixedStartGc")),
            "fixed_start_to_observer_post": _gc_delta(_gc(row, "fixedStartGc"), _gc(row, "observerPostGc")),
        },
        "trace": _trace_analysis(trace, window, frequency, previous_post, fixed, row.get("fixedPostObservationTicks")),
        "observation_definition": ("ADC capturedMonotonicTicks minus CH1 receivedMonotonicTicks; not elapsed after sender stop."
                                   if acquisition else "Completed-frame fixedPostObservationTicks minus acceptedReceiveTicks; includes harness observation work, not elapsed after sender stop."),
        "receive_selection": "Latest positive accepted receive ticks among non-replay Udp/Coast observations after the stop marker inside the available window; first invalid observation of that final receive identity.",
        "boundary": BOUNDARY,
    }


def analyze(artifacts):
    artifacts = Path(artifacts)
    sources = {}
    frame_text = _read(artifacts / "normal_fixed_frames.jsonl", sources)
    rows = []
    for line, text in enumerate(frame_text.splitlines(), 1):
        if not text.strip():
            raise ValueError(f"Blank normal_fixed_frames.jsonl line {line}")
        value = _loads(text)
        if not isinstance(value, dict) or not isinstance(value.get("frame"), dict):
            raise ValueError(f"Invalid frame evidence at line {line}")
        frame = value.pop("frame")
        snapshot = frame.get("acquisitionSnapshot") or {}
        channels = snapshot.get("channels") or []
        channel = next((item for item in channels if isinstance(item, dict) and item.get("channelId") == "CH1"), {})
        paddles = frame.get("paddles") or []
        paddle = paddles[0] if paddles and isinstance(paddles[0], dict) else {}
        rows.append(dict(value, line=line, replayed=bool(value.get("sessionReplaying") or snapshot.get("replayed")),
                         adc_snapshot_ticks=snapshot.get("capturedMonotonicTicks"),
                         adc_receive_ticks=channel.get("receivedMonotonicTicks"), adc_valid=channel.get("rawValid"),
                         adc_status=channel.get("rawStatus"), paddle_valid=paddle.get("valid")))
    optional = {}
    for name in ("runtime_result.json", "adc_timing.json", "coast_timing.json"):
        text = _read(artifacts / name, sources, optional=True)
        optional[name] = _loads(text) if text is not None else None
        if optional[name] is not None and not isinstance(optional[name], dict):
            raise ValueError(name + " must be a JSON object")
    report = optional["runtime_result.json"] or {}
    frequencies = {_positive(value.get("stopwatchFrequency")) for name, value in optional.items()
                   if name != "runtime_result.json" and value}
    frequencies.add(_positive(report.get("timingStopwatchFrequency")))
    frequencies.discard(None)
    frequency = frequencies.pop() if len(frequencies) == 1 else None
    return {
        "schema_version": 1, "result": "DIAGNOSTIC_ONLY", "artifacts": str(artifacts.resolve()),
        "sources": sources, "frame_count": len(rows), "stopwatch_frequency": frequency or "unavailable",
        "frequency_boundary": "Only one consistent positive integer frequency is used; conflicting/missing clocks are unavailable.",
        "adc": _expiry(rows, report, optional["adc_timing.json"], frequency, True),
        "paddle": _expiry(rows, report, optional["coast_timing.json"], frequency, False),
        "boundary": BOUNDARY,
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifacts", type=Path)
    parser.add_argument("--read-only", action="store_true", help="Print JSON without writing any file")
    parser.add_argument("--output", type=Path, help="Defaults to ARTIFACTS/expiry_timing_analysis.json")
    args = parser.parse_args(argv)
    if args.read_only and args.output:
        parser.error("--read-only cannot be combined with --output")
    try:
        result = analyze(args.artifacts)
        text = json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
        if args.read_only:
            print(text, end="")
        else:
            output = args.output or args.artifacts / "expiry_timing_analysis.json"
            inputs = {args.artifacts / name for name in result["sources"]}
            if output.resolve() in {path.resolve() for path in inputs}:
                raise ValueError("Output cannot overwrite an input artifact")
            output.write_text(text, encoding="utf-8", newline="\n")
            print(str(output.resolve()))
        return 0
    except (OSError, UnicodeError, ValueError, TypeError, KeyError) as error:
        print("Expiry analysis failed: " + str(error), file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
