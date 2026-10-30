"""Standard-library regression checks; no Unity process is launched."""
from __future__ import annotations

from contextlib import contextmanager, redirect_stdout
import io
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest import mock

import run_all_checks
import run_runtime_check
import unity_execution


TOOLS = Path(__file__).resolve().parent
WORKER = r"""
import sys
from pathlib import Path
sys.path.insert(0, sys.argv[1])
from unity_execution import unity_project_guard, UnityProjectBusyError
try:
    with unity_project_guard(Path(sys.argv[2]), process_probe=lambda project: []):
        print('ACQUIRED', flush=True)
        if sys.argv[3] == 'hold':
            sys.stdin.readline()
        elif sys.argv[3] == 'exception':
            raise RuntimeError('intentional exception while holding the lock')
    print('RELEASED', flush=True)
except UnityProjectBusyError:
    print('BUSY', flush=True)
    sys.exit(3)
except RuntimeError:
    print('EXCEPTION', flush=True)
    sys.exit(4)
"""


class UnityExecutionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="unity-guard-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.project = self.root / "项目 with spaces"
        self.project.mkdir()

    def start_worker(self, project=None, mode="hold"):
        return subprocess.Popen(
            [sys.executable, "-B", "-u", "-c", WORKER, str(TOOLS), str(project or self.project), mode],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", env={**os.environ, "PYTHONIOENCODING": "utf-8"},
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )

    @contextmanager
    def held_project(self, project=None):
        worker = self.start_worker(project)
        try:
            # Bound the handshake on Windows too, where select() cannot read pipes.
            first_line = queue.Queue()
            threading.Thread(target=lambda: first_line.put(worker.stdout.readline()), daemon=True).start()
            self.assertEqual(first_line.get(timeout=10).strip(), "ACQUIRED")
            yield worker
        finally:
            if worker.poll() is None:
                worker.terminate()
            worker.communicate(timeout=10)

    def attempt(self, project=None, mode="once"):
        worker = self.start_worker(project, mode)
        try:
            output, error = worker.communicate(timeout=10)
            return worker.returncode, output, error
        finally:
            if worker.poll() is None:
                worker.kill()
                worker.communicate(timeout=10)

    def test_two_python_processes_cannot_hold_same_project(self):
        with self.held_project():
            code, output, error = self.attempt()
        self.assertEqual(code, 3, error)
        self.assertEqual(output.strip(), "BUSY")

    def test_different_projects_are_independent(self):
        other = self.root / "other-project"
        other.mkdir()
        with self.held_project():
            code, output, error = self.attempt(other)
        self.assertEqual(code, 0, error)
        self.assertIn("RELEASED", output)

    def test_exception_releases_lock_and_leftover_file_does_not_block(self):
        code, output, error = self.attempt(mode="exception")
        self.assertEqual(code, 4, error)
        self.assertIn("EXCEPTION", output)
        self.assertTrue((self.project / unity_execution.LOCK_FILENAME).is_file())
        code, output, error = self.attempt()
        self.assertEqual(code, 0, error)
        self.assertIn("RELEASED", output)

    def test_terminated_owner_releases_lock(self):
        with self.held_project() as worker:
            worker.terminate()
            worker.wait(timeout=10)
            code, output, error = self.attempt()
            self.assertEqual(code, 0, error)
            self.assertIn("RELEASED", output)

    def test_process_probe_busy_and_failure_release_lock(self):
        with self.assertRaisesRegex(unity_execution.UnityProjectBusyError, "4242"):
            with unity_execution.unity_project_guard(self.project, process_probe=lambda project: [4242]):
                self.fail("Must not enter while a manual Unity Editor owns this project")
        with self.assertRaisesRegex(unity_execution.UnityExecutionError, "inspection failed"):
            with unity_execution.unity_project_guard(
                    self.project, process_probe=mock.Mock(side_effect=unity_execution.UnityExecutionError("inspection failed"))):
                self.fail("Must not enter after failed process inspection")
        code, output, error = self.attempt()
        self.assertEqual(code, 0, error)

    @unittest.skipUnless(os.name == "nt", "Native Windows command-line parser")
    def test_windows_query_matches_quoted_project_and_not_other_projects(self):
        records = [
            {"ProcessId": 123, "CommandLine": subprocess.list2cmdline(["C:/Unity/Unity.exe", "-projectPath", str(self.project)])},
            {"ProcessId": 456, "CommandLine": subprocess.list2cmdline(["C:/Unity/Unity.exe", "-projectPath", str(self.root / "different")])},
            {"ProcessId": 789, "CommandLine": subprocess.list2cmdline(["C:/Unity/Unity.exe", "-PROJECTPATH=" + str(self.project).upper()])},
        ]
        completed = subprocess.CompletedProcess([], 0, json.dumps(records), "")
        with mock.patch.object(unity_execution.subprocess, "run", return_value=completed) as query:
            self.assertEqual(unity_execution.running_unity_processes(self.project), [123, 789])
        self.assertEqual(query.call_args.args[0][0], "powershell.exe")
        self.assertEqual(query.call_args.kwargs["timeout"], 10)
        self.assertNotIn(str(self.project), query.call_args.args[0][-1])

    @unittest.skipUnless(os.name == "nt", "Windows process inspection")
    def test_windows_query_rejects_unreadable_process_or_query_failure(self):
        for completed in (
                subprocess.CompletedProcess([], 0, json.dumps({"ProcessId": 42, "CommandLine": None}), ""),
                subprocess.CompletedProcess([], 0, json.dumps({"ProcessId": 43, "CommandLine": "C:/Unity/Unity.exe"}), ""),
                subprocess.CompletedProcess([], 1, "", "query error"),
                subprocess.CompletedProcess([], 0, "not json", "")):
            with self.subTest(result=completed.returncode, stdout=completed.stdout):
                with mock.patch.object(unity_execution.subprocess, "run", return_value=completed):
                    with self.assertRaises(unity_execution.UnityExecutionError):
                        unity_execution.running_unity_processes(self.project)

    def assert_runner_fails_without_launch(self, runner, expected_reason):
        workspace = self.root / runner.__name__
        workspace.mkdir(exist_ok=True)
        fake_unity = workspace / "Unity.exe"
        fake_unity.write_bytes(b"not executable; must never be launched")
        with mock.patch.object(runner, "PROJECT", self.project), \
                mock.patch.object(runner, "WORKSPACE", workspace), \
                mock.patch.object(runner, "resolve_unity", return_value=fake_unity), \
                mock.patch.object(runner.subprocess, "run") as launch, redirect_stdout(io.StringIO()):
            if runner is run_all_checks:
                artifacts = workspace / "check_results"
                with mock.patch.object(runner, "ARTIFACT_ROOT", artifacts):
                    exit_code = runner.main(["physics"])
                result_path, = artifacts.glob("*/results.json")
            else:
                exit_code = runner.main([])
                result_path, = (workspace / "tools/runtime_checks").glob("*/verification.json")
            launch.assert_not_called()
        report = json.loads(result_path.read_text(encoding="utf-8"))
        self.assertNotEqual(exit_code, 0)
        self.assertEqual(report["result"], "FAIL")
        reasons = report.get("configuration_errors", report.get("reasons", []))
        self.assertTrue(any(expected_reason in reason for reason in reasons), reasons)
        self.assertEqual(report.get("checks", []), [])
        return report

    def test_both_runners_preserve_fail_evidence_when_other_process_holds_lock(self):
        with self.held_project():
            for runner in (run_all_checks, run_runtime_check):
                with self.subTest(runner=runner.__name__):
                    self.assert_runner_fails_without_launch(runner, "another test runner")

    def test_both_runners_do_not_launch_when_manual_unity_owns_project(self):
        with mock.patch.object(unity_execution, "running_unity_processes", return_value=[31415]):
            for runner in (run_all_checks, run_runtime_check):
                with self.subTest(runner=runner.__name__):
                    self.assert_runner_fails_without_launch(runner, "existing Unity.exe PID(s) 31415")

    def test_both_runners_hold_lock_during_their_unity_operation(self):
        def fake_launch(*args, **kwargs):
            code, output, error = self.attempt()
            self.assertEqual(code, 3, error)
            self.assertEqual(output.strip(), "BUSY")
            return subprocess.CompletedProcess(args[0], 0)

        fake_unity = self.root / "Unity.exe"
        fake_unity.write_bytes(b"never launched")
        with mock.patch.object(unity_execution, "running_unity_processes", return_value=[]):
            for runner in (run_all_checks, run_runtime_check):
                with self.subTest(runner=runner.__name__), \
                        mock.patch.object(runner, "PROJECT", self.project), \
                        mock.patch.object(runner, "WORKSPACE", self.root), \
                        mock.patch.object(runner, "resolve_unity", return_value=fake_unity), \
                        mock.patch.object(runner.subprocess, "run", side_effect=fake_launch) as launch, \
                        redirect_stdout(io.StringIO()):
                    if runner is run_all_checks:
                        with mock.patch.object(runner, "ARTIFACT_ROOT", self.root / "check_results"):
                            runner.main(["physics", "waves"])
                        self.assertEqual(launch.call_count, 2)
                    else:
                        runner.main([])
                        self.assertEqual(launch.call_count, 1)
                # A completed runner also releases its OS lock on the normal path.
                code, output, error = self.attempt()
                self.assertEqual(code, 0, error)

    def test_suite_rechecks_manual_editors_between_unity_launches(self):
        fake_unity = self.root / "Unity.exe"
        fake_unity.write_bytes(b"never launched")
        artifacts = self.root / "check_results"
        with mock.patch.object(run_all_checks, "PROJECT", self.project), \
                mock.patch.object(run_all_checks, "WORKSPACE", self.root), \
                mock.patch.object(run_all_checks, "ARTIFACT_ROOT", artifacts), \
                mock.patch.object(run_all_checks, "resolve_unity", return_value=fake_unity), \
                mock.patch.object(unity_execution, "running_unity_processes", side_effect=[[], [777]]) as probe, \
                mock.patch.object(run_all_checks.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as launch, \
                redirect_stdout(io.StringIO()):
            exit_code = run_all_checks.main(["physics", "waves"])
        self.assertEqual(launch.call_count, 1)
        self.assertEqual(probe.call_count, 2)
        result_path, = artifacts.glob("*/results.json")
        report = json.loads(result_path.read_text(encoding="utf-8"))
        self.assertNotEqual(exit_code, 0)
        self.assertEqual(report["result"], "FAIL")
        self.assertTrue(any("PID(s) 777" in reason for reason in report["configuration_errors"]))


if __name__ == "__main__":
    unittest.main()
