# -*- coding: utf-8 -*-
"""Run Unity self-checks sequentially and retain evidence for this invocation.

Examples:
    python.exe tools/run_all_checks.py
    python.exe tools/run_all_checks.py physics waves chain
    python.exe tools/run_all_checks.py --unity F:/Unity/Editor/Unity.exe

Project and artifact paths follow this file's workspace, independent of cwd.
UNITY_EDITOR_PATH (or UNITY_PATH) can supply the editor executable. A check
passes only with exit code 0, no compilation/exception errors, and exactly
one result marker in the current Unity log: its own RESULT=PASS.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import uuid

from unity_execution import UnityExecutionError, ensure_unity_project_available, unity_project_guard


WORKSPACE = Path(__file__).resolve().parents[1]
PROJECT = WORKSPACE / "DragonBoatVR"
ARTIFACT_ROOT = WORKSPACE / "tools" / "check_results"

# (selector, executeMethod, current-log result prefix)
CHECKS = [
    ("physics", "DragonBoatVR.EditorTools.PhysicsSelfTest.Run", "[SELFTEST]"),
    ("link", "DragonBoatVR.EditorTools.LinkSelfTest.Run", "[LINKTEST]"),
    ("e2e", "DragonBoatVR.EditorTools.EndToEndSelfTest.Run", "[E2E]"),
    ("twins", "DragonBoatVR.EditorTools.TwinsSelfTest.Run", "[TWIN]"),
    ("waves", "DragonBoatVR.EditorTools.WaveHydroSelfTest.Run", "[WAVETEST]"),
    ("current", "DragonBoatVR.EditorTools.RiverCurrentSelfTest.Run", "[CURRENTTEST]"),
    ("scenery", "DragonBoatVR.EditorTools.RiverScenerySelfTest.Run", "[SCENERY]"),
    ("audio", "DragonBoatVR.EditorTools.RiverAudioSelfTest.Run", "[AUDIOTEST]"),
    ("chain", "DragonBoatVR.EditorTools.SimulationChainSelfTest.Run", "[CHAIN]"),
    ("device", "DragonBoatVR.EditorTools.DeviceConstraintsSelfTest.Run", "[DEVICE]"),
    ("acquisition", "DragonBoatVR.EditorTools.AcquisitionSelfTest.Run", "[ADC]"),
    ("registration", "DragonBoatVR.EditorTools.RegistrationSelfTest.Run", "[REGISTRATION]"),
    ("metrics", "DragonBoatVR.EditorTools.StrokeMetricsSelfTest.Run", "[METRICS]"),
]

RESULT_PATTERN = re.compile(r"\bRESULT\s*=\s*([A-Za-z0-9_-]+)\b")
ERROR_PATTERN = re.compile(
    r"\berror\s+CS\d+\b|"
    r"scripts?\s+have\s+compiler\s+errors?|"
    r"compilation\s+(?:failed|failure)|failed\s+to\s+compile|"
    r"aborting\s+batchmode\s+due\s+to\s+failure|"
    r"\b(?:unhandled|uncaught)[\s_]*exception\b|"
    r"\b(?:[A-Za-z_][\w.]*Exception):|"
    r"the referenced script[^\r\n]*\bis missing\b|"
    r"\bmissing\s*\(\s*mono\s*script\s*\)",
    re.IGNORECASE,
)
# 关闭期良性噪声:Unity 命名管道服务器在域退出/重载时接受连接会抛
# IOException(管道正在被关闭)。自检本体 RESULT=PASS 时这类行不构成
# 失败证据——交接文档 2.2 的"修复运行器误判"。
BENIGN_NOISE_PATTERN = re.compile(
    r"accepting client connection|"
    r"pipe(?:line)?\s+(?:is being closed|has ended|broken|closed)|"
    r"管道(?:正在被关闭|已关闭|被关闭)",
    re.IGNORECASE,
)


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def read_lines(path):
    path = Path(path)
    if not path.is_file():
        return []
    return path.read_bytes().decode("utf-8", errors="replace").splitlines()


def select_checks(selectors):
    """Reject every unmatched selector, including a typo beside a valid name."""
    if not selectors:
        return list(CHECKS), []
    unknown = [s for s in selectors if not s or not any(c[0].startswith(s) for c in CHECKS)]
    selected = [c for c in CHECKS if any(s and c[0].startswith(s) for s in selectors)]
    return selected, unknown


def resolve_unity(explicit=None):
    requested = explicit or os.environ.get("UNITY_EDITOR_PATH") or os.environ.get("UNITY_PATH")
    if requested:
        found = shutil.which(requested)
        return Path(found or requested).expanduser().resolve()
    # The usual local layout on this workstation; follows the workspace drive.
    # Prefer the known Editor: PATH may contain the unrelated Unity CLI, whose
    # unity.exe does not accept Editor flags such as -batchmode/-projectPath.
    local = WORKSPACE.parent / "Unity" / "Editor" / "Unity.exe"
    if local.is_file():
        return local.resolve()
    hub = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Unity" / "Hub" / "Editor"
    if hub.is_dir():
        candidates = sorted(hub.glob("*/Editor/Unity.exe"), reverse=True)
        if candidates:
            return candidates[0].resolve()
    found = shutil.which("Unity") or shutil.which("Unity.exe")
    if found:
        return Path(found).resolve()
    return None


def assess_log(lines, prefix, exit_code, extra_lines=(), timed_out=False, launch_error=None):
    """A PASS marker alone is evidence, never an override for another failure."""
    markers = []
    own_pattern = re.compile(re.escape(prefix) + r"\s+RESULT\s*=\s*([A-Za-z0-9_-]+)\b")
    for line in lines:
        for match in RESULT_PATTERN.finditer(line):
            own = own_pattern.search(line)
            markers.append({"value": match.group(1), "line": line,
                            "own_prefix": bool(own and own.start(1) == match.start(1))})
    errors = sorted({line.strip() for line in list(lines) + list(extra_lines)
                     if ERROR_PATTERN.search(line) and not BENIGN_NOISE_PATTERN.search(line)})
    reasons = []
    if timed_out:
        reasons.append("timeout")
    if launch_error:
        reasons.append("launch_error: " + launch_error)
    if exit_code != 0:
        reasons.append("Unity exit code is " + str(exit_code))
    if errors:
        reasons.append("compilation, exception or missing-script error in current output")
    if len(markers) != 1:
        reasons.append("expected exactly one RESULT marker, found " + str(len(markers)))
    elif not markers[0]["own_prefix"]:
        reasons.append("RESULT marker does not belong to " + prefix)
    elif markers[0]["value"] != "PASS":
        reasons.append("self-check reported RESULT=" + markers[0]["value"])
    return {"result": "FAIL" if reasons else "PASS", "reasons": reasons,
            "result_markers": markers, "errors": errors,
            "key_output": [line.split(prefix, 1)[1].strip() for line in lines if prefix in line]}


def run_one(name, method, prefix, unity, artifact_dir, timeout=420):
    artifact_dir = Path(artifact_dir)
    log = artifact_dir / (name + ".unity.log")
    process_log = artifact_dir / (name + ".process.log")
    command = [str(unity), "-batchmode", "-nographics", "-quit",
               "-job-worker-count", "2", "-projectPath", str(PROJECT),
               "-executeMethod", method, "-logFile", str(log)]
    item = {"name": name, "method": method, "prefix": prefix, "command": command,
            "unity_log": str(log), "process_log": str(process_log),
            "started_at": utc_now(), "exit_code": None, "timed_out": False}
    started = time.monotonic()
    launch_error = None
    print("  -> " + name + " ...", end="", flush=True)
    # Never delete or consume existing evidence, even if called outside main.
    if log.exists() or process_log.exists():
        item.update({"result": "FAIL", "reasons": ["refusing pre-existing log artifacts"],
                     "result_markers": [], "errors": [], "key_output": []})
    else:
        try:
            with process_log.open("xb") as output:
                completed = subprocess.run(command, timeout=timeout, stdout=output,
                                           stderr=subprocess.STDOUT, check=False)
            item["exit_code"] = completed.returncode
        except subprocess.TimeoutExpired:
            item["timed_out"] = True
        except OSError as exc:
            launch_error = str(exc)
        item.update(assess_log(read_lines(log), prefix, item["exit_code"],
                               read_lines(process_log), item["timed_out"], launch_error))
        if not log.is_file():
            item["result"] = "FAIL"
            item["reasons"].append("current Unity log was not created")
    item["duration_seconds"] = round(time.monotonic() - started, 3)
    item["completed_at"] = utc_now()
    print(" " + item["result"] + " (" + str(item["duration_seconds"]) + "s)")
    return item


def positive_timeout(value):
    seconds = float(value)
    if seconds <= 0 or not seconds < float("inf"):
        raise argparse.ArgumentTypeError("timeout must be a finite positive number")
    return seconds


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("selectors", nargs="*", help="check name prefixes: " + ", ".join(c[0] for c in CHECKS))
    parser.add_argument("--unity", help="Unity editor path; otherwise UNITY_EDITOR_PATH / UNITY_PATH")
    parser.add_argument("--timeout", type=positive_timeout, default=420, help="seconds per check (default: 420)")
    args = parser.parse_args(argv)
    selected, unknown = select_checks(args.selectors)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    artifact_dir = ARTIFACT_ROOT / (stamp + "_" + uuid.uuid4().hex[:8])
    artifact_dir.mkdir(parents=True, exist_ok=False)
    unity = resolve_unity(args.unity)
    report = {"schema_version": 1, "started_at": utc_now(), "workspace": str(WORKSPACE),
              "project": str(PROJECT), "unity": str(unity) if unity else None,
              "artifact_dir": str(artifact_dir), "selectors": args.selectors,
              "selected_checks": [c[0] for c in selected], "unknown_selectors": unknown,
              "configuration_errors": [], "checks": []}
    if unknown:
        report["configuration_errors"].append("unknown selectors: " + ", ".join(repr(s) for s in unknown))
    if not selected:
        report["configuration_errors"].append("zero checks selected")
    if not PROJECT.is_dir():
        report["configuration_errors"].append("project directory does not exist: " + str(PROJECT))
    if unity is None or not unity.is_file():
        report["configuration_errors"].append("Unity executable not found; use --unity or UNITY_EDITOR_PATH")
    print("DragonBoatVR Unity self-checks")
    print("ARTIFACTS=" + str(artifact_dir))
    if not report["configuration_errors"]:
        try:
            # Hold one nonblocking project lock across the complete check suite.
            with unity_project_guard(PROJECT):
                for index, (name, method, prefix) in enumerate(selected):
                    if index:
                        # A manual Editor may have opened between suite checks.
                        ensure_unity_project_available(PROJECT)
                    report["checks"].append(run_one(name, method, prefix, unity, artifact_dir, args.timeout))
        except UnityExecutionError as exc:
            report["configuration_errors"].append(str(exc))
    report["passed"] = sum(item["result"] == "PASS" for item in report["checks"])
    report["total"] = len(selected)
    passed = (bool(selected) and not report["configuration_errors"]
              and len(report["checks"]) == len(selected) and report["passed"] == len(selected))
    report["result"] = "PASS" if passed else "FAIL"
    report["exit_code"] = 2 if report["configuration_errors"] else (0 if passed else 1)
    report["completed_at"] = utc_now()
    results_path = artifact_dir / "results.json"
    results_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for error in report["configuration_errors"]:
        print("  ERROR: " + error)
    for item in report["checks"]:
        print("  " + item["result"] + " " + item["name"])
        for reason in item["reasons"]:
            print("    " + reason)
        for error in item["errors"][:5]:
            print("    " + error)
        for line in item["key_output"]:
            print("    " + line)
    print(str(report["passed"]) + "/" + str(report["total"]) + " passed; RESULT=" + report["result"])
    print("RESULTS_JSON=" + str(results_path))
    return report["exit_code"]


if __name__ == "__main__":
    sys.exit(main())
