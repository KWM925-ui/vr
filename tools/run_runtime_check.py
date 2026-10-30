"""Verify the normal Unity Play/render/UDP/replay chain in a dedicated Editor.

Usage: python.exe tools/run_runtime_check.py [--unity F:/Unity/Editor/Unity.exe]
The Editor uses the real player loop and graphics; it closes after validation.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import time
import uuid

from run_all_checks import PROJECT, WORKSPACE, assess_log, positive_timeout, read_lines, resolve_unity, utc_now
from unity_execution import UnityExecutionError, unity_project_guard
from runtime_evidence import verify_runtime_evidence
from tracking_evidence import verify_runtime_tracking


REQUIRED_CHECKS = {
    "normal_runtime_evidence_background_writer_complete",
    "normal_fixed_post_chain",
    "synthetic_propulsion_and_independent_wave_clock",
    "software_udp_input_identity_and_force",
    "actual_boatstate_loopback_return",
    "100ms_ttl_zero_force_after_sender_stops",
    "expired_input_boat_coasts_and_waves_continue",
    "real_nonzero_origin_record_replay",
    "paired_device_record_replay",
    "replay_eof_expires_and_wave_continues",
    "normal_rendering_three_scene_screenshots",
    "normalized_adc_four_channel_loopback_and_isolation",
    "independent_adc_ttl_and_sender_stop",
    "adc_record_replay_profile_and_eof",
    "hud_physics_statistics_and_rendered_details",
    "tracking_adapter_normal_lateupdate_and_stale_gate",
    "hud_acquisition_navigation_and_render",
    "replay_controls_normal_player_loop",
    "replay_configuration_abort_and_explicit_recovery",
}


def source_hashes():
    # Imported assets and GUID mappings are executable configuration too. Hash
    # source inputs only; Library, Builds and generated evidence are excluded.
    files = {}
    for folder in ("Assets", "ProjectSettings", "Packages"):
        files.update((p.relative_to(PROJECT).as_posix(), p)
                     for p in (PROJECT / folder).rglob("*") if p.is_file())
    files.update(("../tools/" + p.name, p) for p in (WORKSPACE / "tools").glob("*.py") if p.is_file())
    hashes = {}
    for name, path in sorted(files.items()):
        digest = hashlib.sha256()
        with path.open("rb") as source:
            for chunk in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(chunk)
        hashes[name] = digest.hexdigest()
    return hashes


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity")
    parser.add_argument("--timeout", type=positive_timeout, default=240)
    parser.add_argument("--profile", action="store_true", help="capture diagnostic CPU counters near ADC expiry")
    args = parser.parse_args(argv)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    artifacts = WORKSPACE / "tools/runtime_checks" / (stamp + "_" + uuid.uuid4().hex[:8])
    artifacts.mkdir(parents=True, exist_ok=False)
    unity = resolve_unity(args.unity)
    report = {"started_at": utc_now(), "artifact_dir": str(artifacts), "exit_code": None,
              "timed_out": False, "result": "FAIL", "reasons": [], "source_sha256": source_hashes()}
    print("ARTIFACTS=" + str(artifacts), flush=True)
    log = artifacts / "runtime.log"
    process_log = artifacts / "process.log"
    launch_error = None
    started = time.monotonic()
    if unity is None or not unity.is_file():
        launch_error = "Unity executable not found; use --unity or UNITY_EDITOR_PATH"
    else:
        command = [str(unity), "-batchmode", "-job-worker-count", "2", "-kinematic-fixture", "-projectPath", str(PROJECT),
                   "-executeMethod", "DragonBoatVR.EditorTools.RuntimeSmokeSelfTest.Run",
                   "-runtimeArtifacts", str(artifacts), "-logFile", str(log)]
        report["command"] = command
        if args.profile: command.append("-profileRuntime")
        try:
            with unity_project_guard(PROJECT):
                with process_log.open("xb") as output:
                    result = subprocess.run(command, stdout=output, stderr=subprocess.STDOUT,
                                            timeout=args.timeout, check=False,
                                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                report["exit_code"] = result.returncode
        except subprocess.TimeoutExpired:
            report["timed_out"] = True
        except (OSError, UnityExecutionError) as exc:
            launch_error = str(exc)
    assessment = assess_log(read_lines(log), "[RUNTIME]", report["exit_code"], read_lines(process_log),
                            report["timed_out"], launch_error)
    report.update(assessment)
    evidence_path = artifacts / "runtime_result.json"
    try:
        evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
        checks = evidence.get("checks", [])
        names = [item.get("name") for item in checks]
        if set(names) != REQUIRED_CHECKS or len(names) != len(REQUIRED_CHECKS):
            report["reasons"].append("normal-runtime check set is missing, unexpected or duplicated")
        if evidence.get("result") != "PASS" or evidence.get("errors") or any(item.get("passed") is not True for item in checks):
            report["reasons"].append("runtime evidence reports failed checks or errors")
        report["checks"] = checks
        report["completed_evidence_files"] = verify_runtime_evidence(artifacts, evidence)
        report["independent_tracking_evidence"] = verify_runtime_tracking(evidence)
        for shot in evidence.get("screenshots", []):
            if not shot.get("completed") or not (artifacts / shot["file"]).is_file():
                report["reasons"].append("missing completed screenshot: " + str(shot.get("file")))
    except (OSError, ValueError, TypeError, KeyError) as exc:
        report["reasons"].append("runtime evidence unavailable or invalid: " + str(exc))
    after = source_hashes()
    report["source_unchanged"] = report["source_sha256"] == after
    if not report["source_unchanged"]:
        report["reasons"].append("project sources changed during verification")
    report["result"] = "FAIL" if report["reasons"] else "PASS"
    report["duration_seconds"] = round(time.monotonic() - started, 3)
    report["completed_at"] = utc_now()
    path = artifacts / "verification.json"
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for item in report.get("checks", []):
        print(("PASS " if item.get("passed") else "FAIL ") + item["name"] + ": " + item.get("evidence", ""))
    for reason in report["reasons"]:
        print("ERROR: " + reason)
    print("RESULT=" + report["result"] + "; VERIFICATION_JSON=" + str(path))
    return 0 if report["result"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
