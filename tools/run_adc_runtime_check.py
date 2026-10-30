"""Exercise the real CSV CLI through a normal Unity Play acquisition session.

This generates a small, explicitly synthetic four-channel CSV, invokes the
public convert/validate/send commands as separate processes, and checks what
Unity actually received, displayed, recorded and replayed. No hardware is used.
The 80-packet synthetic fixture uses a 40 ms PC send interval, independently
of the unchanged 100 ms observation TTL; this does not specify a PCB sample rate.
Only one Unity Editor should use this project at a time.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid
from datetime import datetime, timezone

from run_all_checks import (PROJECT, WORKSPACE, assess_log, positive_timeout,
                            read_lines, resolve_unity, utc_now)
from run_runtime_check import source_hashes
from unity_execution import UnityExecutionError, unity_project_guard


SOURCE_ID = "software-csv-runtime"
PACKET_COUNT = 80
# Slow this synthetic CSV fixture enough for completed physics observations to
# retain packet coverage while the normal Game view renders. This is not a PCB rate.
SEND_INTERVAL = 0.04
CLI = WORKSPACE / "tools/adc_replay.py"
# Kept explicit so a partial/empty Unity report cannot be accepted.
REQUIRED_CHECKS = {
    "normal_play_fixed_chain", "csv_packet_identity_and_wrap", "independent_channel_states",
    "adc_does_not_drive_paddle_or_forces", "sender_stop_and_duplicate_do_not_refresh",
    "hud_observes_csv_and_normal_render", "complete_acquisition_record_replay", "replay_eof_invalidates_acquisition",
    "explicit_raw_profile_hud_load_and_timeline_guards",
    "raw_profile_normal_gui_keyboard_and_click",
    "raw_replay_isolates_different_live_profile_and_restores_listener",
    "adc_receive_diagnostics_rejections_and_freshness",
    "adc_receive_diagnostics_listener_and_replay_ownership",
}


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def fingerprints():
    result = source_hashes()
    for path in (CLI, Path(__file__), WORKSPACE / "tools/run_all_checks.py",
                 WORKSPACE / "tools/run_runtime_check.py", WORKSPACE / "tools/unity_execution.py"):
        result["../" + path.relative_to(WORKSPACE).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result


def make_fixture(artifacts):
    csv_path = artifacts / "software_fixture.csv"
    fields = ["packet_sequence", "source_id", "channel_id", "sample_sequence",
              "raw_units", "raw", "valid", "saturated", "device_time_us",
              "sample_time_us", "has_raw", "raw_text"]
    with csv_path.open("x", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        for index in range(PACKET_COUNT):
            sequence = (0xFFFFFFFE + index) & 0xFFFFFFFF
            device_time = 1000000 + index * int(SEND_INTERVAL * 1000000)
            for channel in range(1, 5):
                if 20 <= index < 60 and channel == 3:
                    continue
                missing = 20 <= index < 60 and channel == 4
                writer.writerow({
                    "packet_sequence": sequence, "source_id": SOURCE_ID,
                    "channel_id": f"CH{channel}", "sample_sequence": sequence,
                    "raw_units": "example-count", "raw": "" if missing else 1000 + index * 10 + channel,
                    "valid": "false" if missing or (20 <= index < 40 and channel == 2) else "true",
                    "saturated": "true" if index >= 60 and channel == 3 else "false",
                    "device_time_us": device_time, "sample_time_us": device_time + channel,
                    "has_raw": "false" if missing else "true", "raw_text": "software fixture only",
                })
    write_json(artifacts / "scenario.json", {"sourceId": SOURCE_ID, "rawUnits": "example-count",
                                            "packetCount": PACKET_COUNT, "softwareSendIntervalSeconds": SEND_INTERVAL})
    return csv_path


def cli_call(arguments, artifacts, label, report, timeout=30):
    command = [sys.executable, str(CLI), *map(str, arguments)]
    output = artifacts / (label + ".log")
    environment = os.environ.copy()
    environment["PYTHONIOENCODING"] = "utf-8"
    item = {"name": label, "command": command, "log": str(output), "started_at": utc_now(),
            "exit_code": None}
    report["cli_calls"].append(item)
    with output.open("xb") as stream:
        result = subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT,
                                timeout=timeout, check=False, env=environment,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    item.update(exit_code=result.returncode, completed_at=utc_now())
    if result.returncode != 0:
        raise RuntimeError(f"{label} exited {result.returncode}; see {output.name}")


def wait_ready(process, artifacts, deadline):
    path = artifacts / "ready.json"
    while time.monotonic() < deadline:
        if path.is_file():
            ready = json.loads(path.read_text(encoding="utf-8-sig"))
            port = ready.get("port")
            if type(port) is not int or not 1 <= port <= 65535 or ready.get("sourceId") != SOURCE_ID:
                raise ValueError("Unity readiness port/source does not match the fixture")
            interval = ready.get("softwareSendIntervalSeconds")
            if type(interval) not in (int, float) or abs(interval - SEND_INTERVAL) > 1e-9:
                raise ValueError("Unity readiness software fixture cadence does not match")
            return ready
        if process.poll() is not None:
            raise RuntimeError("Unity exited before opening the acquisition listener")
        time.sleep(0.1)
    raise TimeoutError("Unity acquisition listener did not become ready before the deadline")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity")
    parser.add_argument("--timeout", type=positive_timeout, default=240)
    args = parser.parse_args(argv)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    artifacts = WORKSPACE / "tools/adc_runtime_checks" / (stamp + "_" + uuid.uuid4().hex[:8])
    artifacts.mkdir(parents=True, exist_ok=False)
    print("ARTIFACTS=" + str(artifacts), flush=True)
    report = {"result": "FAIL", "scope": "DESKTOP_SOFTWARE_CSV_IMPORT_ONLY",
              "started_at": utc_now(), "artifact_dir": str(artifacts), "cli_calls": [],
              "reasons": [], "source_sha256": fingerprints(), "exit_code": None,
              "timed_out": False}
    process = None
    unity_log = artifacts / "unity.log"
    process_log = artifacts / "process.log"
    launch_error = None
    started = time.monotonic()
    deadline = started + args.timeout
    try:
        csv_path = make_fixture(artifacts)
        normalized = artifacts / "normalized.jsonl"
        cli_call(["convert", "--csv", csv_path, "--out", normalized], artifacts, "convert", report)
        cli_call(["validate", "--input", normalized], artifacts, "validate", report)
        cli_call(["profile", "--input", normalized, "--out", artifacts / "raw_profile.json", "--port", 9874],
                 artifacts, "profile", report)
        lines = normalized.read_bytes().splitlines()
        if len(lines) != PACKET_COUNT:
            raise ValueError("conversion packet count differs from the fixture")
        duplicate = artifacts / "duplicate_last.jsonl"
        duplicate.write_bytes(lines[-1] + b"\n")
        unity = resolve_unity(args.unity)
        if unity is None or not unity.is_file():
            raise FileNotFoundError("Unity Editor not found; specify --unity")
        command = [str(unity), "-batchmode", "-job-worker-count", "2", "-projectPath", str(PROJECT),
                   "-executeMethod", "DragonBoatVR.EditorTools.AcquisitionRuntimeSelfTest.Run",
                   "-adcRuntimeArtifacts", str(artifacts), "-logFile", str(unity_log)]
        report["command"] = command
        # Keep the project lock through startup, CLI sending, timeout cleanup and process exit.
        with unity_project_guard(PROJECT):
            try:
                with process_log.open("xb") as stream:
                    process = subprocess.Popen(command, stdout=stream, stderr=subprocess.STDOUT,
                                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
                    ready = wait_ready(process, artifacts, min(deadline, time.monotonic() + 120))
                    report["ready"] = ready
                    print("Unity listener ready; sending the converted CSV through the public CLI.", flush=True)
                    cli_call(["send", "--input", normalized, "--port", ready["port"], "--interval", SEND_INTERVAL],
                             artifacts, "send", report)
                    time.sleep(0.15)
                    cli_call(["send", "--input", duplicate, "--port", ready["port"], "--interval", SEND_INTERVAL],
                             artifacts, "duplicate_send", report)
                    done = artifacts / "sender_done.json"
                    temporary = artifacts / "sender_done.pending"
                    write_json(temporary, {"success": True, "sentPackets": PACKET_COUNT, "duplicateSent": 1})
                    temporary.replace(done)
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise TimeoutError("runtime verification deadline exceeded")
                    report["exit_code"] = process.wait(timeout=remaining)
            finally:
                if process is not None:
                    if process.poll() is None:
                        process.terminate()
                        try:
                            process.wait(timeout=10)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait(timeout=10)
                    report["exit_code"] = process.returncode
    except (OSError, ValueError, RuntimeError, UnityExecutionError, subprocess.SubprocessError, TimeoutError) as error:
        launch_error = str(error)
        report["timed_out"] = isinstance(error, (TimeoutError, subprocess.TimeoutExpired))
    assessment = assess_log(read_lines(unity_log), "[ADC_RUNTIME]", report["exit_code"],
                            read_lines(process_log), report["timed_out"], launch_error)
    report.update(assessment)
    try:
        evidence = json.loads((artifacts / "adc_runtime_result.json").read_text(encoding="utf-8-sig"))
        checks = evidence.get("checks", [])
        names = [item.get("name") for item in checks]
        if not REQUIRED_CHECKS or set(names) != REQUIRED_CHECKS or len(names) != len(REQUIRED_CHECKS):
            report["reasons"].append("runtime check set is empty, missing, unexpected or duplicated")
        if evidence.get("result") != "PASS" or evidence.get("errors") or any(item.get("passed") is not True for item in checks):
            report["reasons"].append("Unity evidence reports failed checks or errors")
        report["checks"] = checks
        shots = evidence.get("screenshots", [])
        diagnostic_shots = evidence.get("diagnosticScreenshots", [])
        if not shots:
            report["reasons"].append("no rendered HUD screenshot")
        if len(diagnostic_shots) != 4:
            report["reasons"].append("missing ADC receive diagnostic screenshots")
        for shot in shots + diagnostic_shots:
            file = shot.get("file", "")
            path = (artifacts / file).resolve()
            if not file or not path.is_relative_to(artifacts.resolve()) or not shot.get("completed") or not path.is_file() or path.stat().st_size == 0:
                report["reasons"].append("missing completed screenshot: " + str(file))
    except (OSError, ValueError, TypeError, KeyError) as error:
        report["reasons"].append("runtime evidence unavailable or invalid: " + str(error))
    report["source_unchanged"] = report["source_sha256"] == fingerprints()
    if not report["source_unchanged"]:
        report["reasons"].append("project sources or tools changed during verification")
    report["fixture_sha256"] = {p.name: hashlib.sha256(p.read_bytes()).hexdigest()
                                for p in artifacts.iterdir() if p.suffix in (".csv", ".jsonl") or p.name == "raw_profile.json"}
    report["result"] = "FAIL" if report["reasons"] else "PASS"
    report["duration_seconds"] = round(time.monotonic() - started, 3)
    report["completed_at"] = utc_now()
    path = artifacts / "verification.json"
    write_json(path, report)
    for check in report.get("checks", []):
        print(("PASS " if check.get("passed") else "FAIL ") + check["name"] + ": " + check.get("evidence", ""))
    for reason in report["reasons"]:
        print("ERROR: " + reason)
    print("RESULT=" + report["result"] + "; VERIFICATION_JSON=" + str(path))
    return 0 if report["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
