"""Serialize cooperating Unity runners without closing any existing Editor.

Usage: ``with unity_project_guard(project): subprocess.run(unity_command, ...)``.
Keep the context open for the entire Unity operation. Acquisition never waits.
The persistent lock file is only an OS lock target, not a busy/owner marker;
closing the process releases the lock even after an exception or termination.
On Windows, a read-only process query also rejects an Editor using the project.
Other platforms currently provide the cooperating-runner lock only.
"""
from __future__ import annotations

from contextlib import contextmanager
import errno
import json
import os
from pathlib import Path
import subprocess


LOCK_FILENAME = ".unity-execution.lock"


class UnityExecutionError(RuntimeError):
    """The runner must retain FAIL evidence and must not launch Unity."""


class UnityProjectBusyError(UnityExecutionError):
    """Another runner or an already-open Unity Editor owns this project."""


def _windows_argv(command_line):
    # shlex uses POSIX quoting and misreads Windows backslashes/quoted paths.
    import ctypes
    from ctypes import wintypes

    shell32 = ctypes.WinDLL("shell32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    shell32.CommandLineToArgvW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(ctypes.c_int)]
    shell32.CommandLineToArgvW.restype = ctypes.POINTER(wintypes.LPWSTR)
    kernel32.LocalFree.argtypes = [ctypes.c_void_p]
    kernel32.LocalFree.restype = ctypes.c_void_p
    count = ctypes.c_int()
    arguments = shell32.CommandLineToArgvW(command_line, ctypes.byref(count))
    if not arguments:
        raise UnityExecutionError("Cannot parse an existing Unity.exe command line.")
    try:
        return [arguments[index] for index in range(count.value)]
    finally:
        kernel32.LocalFree(arguments)


def _project_key(project):
    return os.path.normcase(str(Path(project).expanduser().resolve()))


def running_unity_processes(project):
    """Return PIDs whose Unity.exe -projectPath resolves to this project.

    Windows inspection is bounded and read-only. An unavailable query or an
    unreadable command line fails closed instead of starting an unchecked Editor.
    Full process command lines are never included in errors or saved evidence.
    """
    if os.name != "nt":
        return []
    script = (
        "$ErrorActionPreference = 'Stop'; "
        "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); "
        "@(Get-CimInstance Win32_Process -Filter \"Name = 'Unity.exe'\" | "
        "Select-Object ProcessId, CommandLine) | ConvertTo-Json -Compress"
    )
    try:
        result = subprocess.run(
            ["powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True, encoding="utf-8", timeout=10, check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        if result.returncode != 0:
            raise UnityExecutionError("Cannot inspect existing Unity.exe processes (PowerShell query failed).")
        records = json.loads(result.stdout.strip() or "[]")
        if isinstance(records, dict):
            records = [records]
        if not isinstance(records, list):
            raise ValueError("invalid process list")
        matches = []
        target = _project_key(project)
        for record in records:
            if not isinstance(record, dict):
                raise ValueError("invalid process record")
            pid = int(record["ProcessId"])
            command_line = record.get("CommandLine")
            if not isinstance(command_line, str) or not command_line.strip():
                raise UnityExecutionError("Cannot inspect the command line of Unity.exe PID " + str(pid) + ".")
            arguments = _windows_argv(command_line)
            project_specified = False
            for index, argument in enumerate(arguments):
                flag = argument.lower()
                if flag == "-projectpath":
                    candidate = arguments[index + 1] if index + 1 < len(arguments) else ""
                elif flag.startswith("-projectpath="):
                    candidate = argument.split("=", 1)[1]
                else:
                    continue
                project_specified = True
                # Resolving a relative path against this runner's cwd could miss
                # the real project: Win32_Process does not expose the Editor cwd.
                if not candidate or not Path(candidate).is_absolute():
                    raise UnityExecutionError("Cannot resolve -projectPath of Unity.exe PID " + str(pid) + ".")
                if _project_key(candidate) == target:
                    matches.append(pid)
                    break
            if not project_specified:
                raise UnityExecutionError("Cannot determine the project of Unity.exe PID " + str(pid)
                                          + ": its command line has no -projectPath.")
        return matches
    except UnityExecutionError:
        raise
    except (OSError, subprocess.SubprocessError, ValueError, TypeError, KeyError) as exc:
        raise UnityExecutionError("Cannot inspect existing Unity.exe processes (" + type(exc).__name__ + ").") from exc


def ensure_unity_project_available(project, *, process_probe=None):
    """Recheck Editors before another launch while retaining a project guard."""
    probe = running_unity_processes if process_probe is None else process_probe
    pids = list(probe(project))
    if pids:
        raise UnityProjectBusyError("Unity project is busy: existing Unity.exe PID(s) "
                                   + ", ".join(str(pid) for pid in pids) + " use " + str(project)
                                   + ". Close that Editor yourself before running checks.")


@contextmanager
def unity_project_guard(project, *, process_probe=None):
    """Hold the project lock, or immediately raise UnityExecutionError.

    ``process_probe(project) -> iterable of PIDs`` is injectable for isolated
    tests. Omit it in runners to inspect real Windows Unity.exe processes.
    Every runner for a project must use this helper and the same lock target.
    """
    project = Path(project).expanduser().resolve()
    if not project.is_dir():
        raise UnityExecutionError("Unity project directory does not exist: " + str(project))
    lock_path = project / LOCK_FILENAME
    try:
        lock_file = lock_path.open("a+b")
    except OSError as exc:
        raise UnityExecutionError("Cannot open Unity execution lock for " + str(project) + ".") from exc
    acquired = False
    try:
        # A one-byte region also works on Windows. Never truncate or unlink the
        # target: replacing a locked file would allow two independent owners.
        try:
            if lock_file.seek(0, os.SEEK_END) == 0:
                lock_file.write(b"\0")
                lock_file.flush()
            lock_file.seek(0)
        except OSError as exc:
            raise UnityExecutionError("Cannot initialize Unity execution lock for " + str(project) + ".") from exc
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(lock_file.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(lock_file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            acquired = True
        except OSError as exc:
            if exc.errno in (errno.EACCES, errno.EAGAIN, errno.EDEADLK):
                raise UnityProjectBusyError("Unity project is busy: another test runner holds the execution lock for " + str(project) + ".") from exc
            raise UnityExecutionError("Cannot acquire Unity execution lock for " + str(project) + ".") from exc
        ensure_unity_project_available(project, process_probe=process_probe)
        yield
    finally:
        try:
            if acquired:
                lock_file.seek(0)
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(lock_file.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(lock_file.fileno(), fcntl.LOCK_UN)
        finally:
            lock_file.close()
