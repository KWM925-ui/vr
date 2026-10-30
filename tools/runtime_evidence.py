"""Verify completed runtime evidence independently of Unity's success marker.

This module only reads the two evidence files. Old reports without the explicit
background completion counters and physical hashes cannot pass this verifier.
"""
from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path
import re


def _count(report, key):
    value = report.get(key)
    if not isinstance(value, int) or isinstance(value, bool) or value <= 0:
        raise ValueError(f"Missing or invalid positive evidence counter: {key}")
    return value


def _hash(report, key):
    value = report.get(key)
    if not isinstance(value, str) or re.fullmatch(r"[0-9a-f]{64}", value) is None:
        raise ValueError(f"Missing or invalid physical evidence SHA256: {key}")
    return value


def _object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON object key: " + key)
        result[key] = value
    return result


def _finite_float(token):
    value = float(token)
    if not math.isfinite(value):
        raise ValueError("Nonfinite JSON number: " + token)
    return value


def _constant(token):
    raise ValueError("Nonfinite JSON constant: " + token)


def _file(path, queued, written, expected_hash, frame_file):
    if queued != written:
        raise ValueError(f"{path.name}: queued/written evidence counts differ ({queued}/{written})")
    digest = hashlib.sha256()
    count, size = 0, 0
    try:
        with path.open("rb") as stream:
            for line_number, raw in enumerate(stream, 1):
                digest.update(raw)
                size += len(raw)
                if line_number == 1 and raw.startswith(b"\xef\xbb\xbf"):
                    raise ValueError("UTF8 BOM is forbidden")
                if not raw.endswith(b"\n"):
                    raise ValueError("Unterminated/torn evidence line")
                text = raw.decode("utf-8", errors="strict")
                if not text.strip():
                    raise ValueError("Empty evidence line")
                value = json.loads(text, object_pairs_hook=_object,
                                   parse_float=_finite_float, parse_constant=_constant)
                if not isinstance(value, dict) or not value:
                    raise ValueError("Evidence line must be a nonempty JSON object")
                if frame_file and not isinstance(value.get("frame"), dict):
                    raise ValueError("FrameEvidence requires a frame object")
                count += 1
    except (OSError, UnicodeError, ValueError, OverflowError) as error:
        location = f" line {line_number}" if "line_number" in locals() else ""
        raise ValueError(f"{path.name}{location}: {error}") from error
    if count != queued:
        raise ValueError(f"{path.name}: actual/queued/written evidence counts differ ({count}/{queued}/{written})")
    actual_hash = digest.hexdigest()
    if actual_hash != expected_hash:
        raise ValueError(f"{path.name}: physical evidence SHA256 differs")
    return {"path": str(path.resolve()), "lines": count, "queued_lines": queued,
            "written_lines": written, "bytes": size, "sha256": actual_hash}


def verify_runtime_evidence(artifacts, report):
    """Return observed file metrics, or raise ValueError for incomplete evidence.

    Does not validate the full physics/BoatState protocol, alter input files, or
    substitute these file checks for runtime acceptance or deadline reliability.
    """
    if not isinstance(report, dict):
        raise ValueError("Runtime report must be a JSON object")
    if report.get("evidenceWritersDrainedAndCountVerified") is not True:
        raise ValueError("Missing or false evidenceWritersDrainedAndCountVerified; legacy reports are unavailable")
    observed = _count(report, "observedFixedFrames")
    frame_queued, frame_written = (_count(report, "frameEvidence" + suffix) for suffix in ("QueuedLines", "WrittenLines"))
    return_queued, return_written = (_count(report, "returnEvidence" + suffix) for suffix in ("QueuedLines", "WrittenLines"))
    frame_hash, return_hash = (_hash(report, prefix + "EvidenceSha256") for prefix in ("frame", "return"))
    if observed != frame_queued or observed != frame_written:
        raise ValueError("Frame evidence counters differ from observedFixedFrames")
    root = Path(artifacts)
    frame = _file(root / "normal_fixed_frames.jsonl", frame_queued, frame_written, frame_hash, True)
    returned = _file(root / "boatstate_loopback.jsonl", return_queued, return_written, return_hash, False)
    return {"status": "VERIFIED", "observed_fixed_frames": observed,
            "frame_evidence": frame, "return_evidence": returned,
            "boundary": "Strict completed-file verification only; does not establish runtime acceptance, a deadline fix, or hardware behavior."}
