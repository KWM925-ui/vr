"""Build the Windows Player and bundle the local Python standard-library runtime.

No download, install, site-packages or user configuration is copied. The shipped
controller uses an isolated _pth file and does not depend on the recipient's PATH.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import sysconfig
import time
import zipfile

from run_all_checks import PROJECT, WORKSPACE, resolve_unity
from run_runtime_check import source_hashes
from unity_execution import unity_project_guard


def bundle_runtime(streaming):
    if sys.platform != 'win32' or sys.version_info < (3, 10):
        raise RuntimeError('Build with the installed Windows CPython >= 3.10.')
    base = Path(sys.base_prefix)
    target = streaming / 'controller_runtime'
    target.mkdir(parents=True, exist_ok=True)
    version = f'python{sys.version_info.major}{sys.version_info.minor}'
    required = [base / 'python.exe', base / (version + '.dll'), base / 'LICENSE.txt']
    if not all(p.is_file() for p in required):
        raise RuntimeError('Installed CPython is missing its runtime or license.')
    for p in required + list(base.glob('python3.dll')) + list(base.glob('vcruntime*.dll')):
        shutil.copy2(p, target / p.name)
    (target / 'DLLs').mkdir(exist_ok=True)
    for p in (base / 'DLLs').iterdir():
        if p.is_file() and p.suffix.lower() in ('.pyd', '.dll'):
            shutil.copy2(p, target / 'DLLs' / p.name)
    library = Path(sysconfig.get_path('stdlib'))
    excluded = {'site-packages', '__pycache__', 'test', 'tests', 'idlelib', 'tkinter', 'turtledemo', 'ensurepip', 'venv'}
    with zipfile.ZipFile(target / (version + '.zip'), 'w', zipfile.ZIP_DEFLATED) as archive:
        for p in sorted(library.rglob('*.py')):
            rel = p.relative_to(library)
            if not excluded.intersection(rel.parts):
                archive.write(p, str(rel))
    (target / (version + '._pth')).write_text(version + '.zip\nDLLs\n.\n', encoding='utf-8')
    probe = subprocess.run([str(target / 'python.exe'), '-I', '-c',
        'import sys,argparse,json,math,os,select,socket,struct,time,zlib,ctypes; '
        'assert sys.flags.isolated and sys.flags.no_site and "site" not in sys.modules; print(sys.version)'],
        capture_output=True, text=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
    if probe.returncode:
        raise RuntimeError('Bundled controller runtime failed: ' + probe.stderr)
    files = {str(p.relative_to(target)): hashlib.sha256(p.read_bytes()).hexdigest()
             for p in sorted(target.rglob('*')) if p.is_file() and p.name != 'runtime_manifest.json'}
    (target / 'runtime_manifest.json').write_text(json.dumps({'python_version': probe.stdout.strip(),
        'source': 'Local CPython standard library only; no site-packages; isolated _pth',
        'sha256': files}, indent=2) + '\n', encoding='utf-8')
    print('Bundled isolated runtime: ' + probe.stdout.strip(), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path, default=WORKSPACE / 'Builds' / 'DragonBoatVR',
                        help='Explicit build directory; default is the delivered Player')
    out = parser.parse_args().out.resolve()
    logs = WORKSPACE / 'tools' / 'build_results' / time.strftime('%Y%m%dT%H%M%S')
    logs.mkdir(parents=True)
    before = source_hashes()
    with unity_project_guard(PROJECT):
        result = subprocess.run([str(resolve_unity()), '-batchmode', '-nographics', '-job-worker-count', '2',
            '-projectPath', str(PROJECT), '-executeMethod', 'DragonBoatVR.EditorTools.BuildPlayerTool.Run',
            '-buildOutDir', str(out), '-logFile', str(logs / 'build.log')],
            stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, timeout=600,
            creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode != 0:
        raise RuntimeError('Unity build failed: ' + str(logs / 'build.log'))
    if before != source_hashes():
        raise RuntimeError('Sources changed during build; do not accept this artifact.')
    bundle_runtime(out / 'DragonBoatVR_Data' / 'StreamingAssets')
    (out / 'build_manifest.json').write_text(json.dumps({'source_sha256': before,
        'runtime': 'DragonBoatVR_Data/StreamingAssets/controller_runtime/python.exe',
        'scope': 'Unmeasured software simulation; no hardware validation'}, indent=2) + '\n', encoding='utf-8')
    print('BUILD_READY=' + str(out / 'DragonBoatVR.exe'), flush=True)


if __name__ == '__main__':
    main()
