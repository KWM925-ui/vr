"""Run the simulated real-paddle actions in the normal Unity Play loop with graphics.
python.exe tools/run_paddle_demo.py
"""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import uuid
from run_all_checks import PROJECT, WORKSPACE, resolve_unity, assess_log, read_lines
from run_runtime_check import source_hashes
from unity_execution import unity_project_guard

EXPECTED_ACTIONS = ['AirFreeMotion', 'TiltedPartialEntry', 'IncreasingImmersion', 'PullBackward', 'PushForward',
                    'RotateAtFixedCentre', 'CompleteExit', 'StillUnderwater', 'SensorMounting',
                    'SparseUdpRotation', 'SparseUdpExpiry', 'PaddleWaterEffects', 'BoatCoastWake',
                    'ExperienceControls']


def assess_action_evidence(evidence):
    if not isinstance(evidence, dict):
        return ['action evidence must be a JSON object']
    actions = evidence.get('actions')
    if not isinstance(actions, list) or any(not isinstance(action, dict) for action in actions):
        return ['action evidence must contain an array of action objects']
    reasons = []
    if evidence.get('result') != 'PASS' or evidence.get('errors'):
        reasons.append('action evidence reports failure or errors')
    if [action.get('action') for action in actions] != EXPECTED_ACTIONS:
        reasons.append('action evidence set is missing, unexpected or duplicated')
    for action in actions:
        frames = action.get('observedFrames')
        if (action.get('passed') is not True or action.get('failure')
                or type(frames) is not int or frames <= 0):
            reasons.append('action lacks successful observed frames: ' + str(action.get('action')))
    return reasons


def main():
    artifacts = WORKSPACE / 'tools/paddle_demo' / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '_' + uuid.uuid4().hex[:8])
    artifacts.mkdir(parents=True, exist_ok=False)
    print('ARTIFACTS=' + str(artifacts), flush=True)
    command = [str(resolve_unity()), '-batchmode', '-job-worker-count', '2', '-kinematic-fixture', '-projectPath', str(PROJECT),
               '-executeMethod', 'DragonBoatVR.EditorTools.PaddleUserDemo.Run', '-paddleArtifacts', str(artifacts),
               '-logFile', str(artifacts / 'unity.log')]
    report = {'command': command, 'result': 'FAIL', 'source_sha256': source_hashes()}
    try:
        with unity_project_guard(PROJECT):
            with (artifacts / 'process.log').open('xb') as output:
                completed = subprocess.run(command, stdout=output, stderr=subprocess.STDOUT, timeout=180,
                                           creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        report.update(assess_log(read_lines(artifacts / 'unity.log'), '[PADDLEDEMO]', completed.returncode,
                                read_lines(artifacts / 'process.log')))
        evidence = json.loads((artifacts / 'result.json').read_text(encoding='utf-8'))
        report['reasons'].extend(assess_action_evidence(evidence))
        report['screenshots'] = []
        for name in evidence['screenshots']:
            data = (artifacts / name).read_bytes()
            width, height = struct.unpack('>II', data[16:24])
            if data[:8] != b'\x89PNG\r\n\x1a\n' or min(width, height) < 200:
                report['reasons'].append('invalid normal-render screenshot: ' + name)
            report['screenshots'].append({'file': name, 'width': width, 'height': height,
                                          'sha256': hashlib.sha256(data).hexdigest()})
        report['actions'] = evidence['actions']
        sparse = {action['action']: action for action in evidence['actions'] if action['action'].startswith('SparseUdp')}
        rotation, expiry = sparse.get('SparseUdpRotation', {}), sparse.get('SparseUdpExpiry', {})
        if (rotation.get('sentPackets', 0) < 30 or rotation.get('newPacketFrames', 0) < 25
                or rotation.get('heldMotionFrames', 0) < 25 or rotation.get('minimumAngularVelocityRadS', 0) < 0.5
                or rotation.get('maximumTorqueXNm', 0) >= -0.01):
            report['reasons'].append('real sparse UDP packets and held-frame resisting moment were not observed')
        if (expiry.get('firstExpiredAcceptedAgeMs', -1) < 100
                or expiry.get('stoppedHeldFrames', 0) < 1 or expiry.get('invalidFrames', 0) < 30):
            report['reasons'].append('stopped sparse UDP did not meet the unchanged 100 ms receive-age expiry gate')
    except Exception as error:
        report.setdefault('reasons', []).append(str(error))
    report['source_unchanged'] = report['source_sha256'] == source_hashes()
    if not report['source_unchanged']:
        report.setdefault('reasons', []).append('project sources changed during verification')
    report['result'] = 'FAIL' if report.get('reasons') else 'PASS'
    (artifacts / 'verification.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    for action in report.get('actions', []):
        if isinstance(action, dict):
            print(('PASS ' if action.get('passed') is True else 'FAIL ') + str(action.get('action'))
                  + ' ' + str(action.get('failure') or ''))
    for reason in report.get('reasons', []): print('ERROR: ' + reason)
    print('RESULT=' + report['result'], flush=True)
    return 0 if report['result'] == 'PASS' else 1

if __name__ == '__main__':
    raise SystemExit(main())
