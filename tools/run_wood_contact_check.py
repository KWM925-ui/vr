"""Observe isolated wood contacts in a real delivered Player and independently audit its PCM output."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import subprocess
import tempfile
import uuid
import wave

import numpy as np
from PIL import Image, ImageStat
from run_all_checks import PROJECT, WORKSPACE
from run_runtime_check import source_hashes
from run_closed_loop_check import player_environment, player_hashes, publish_runtime_artifacts, verify_audio_capture
from unity_execution import unity_project_guard

STAGES = ['ShaftTouch', 'ShaftRelease', 'BladeTouch', 'BladeRelease', 'HullCollision']


def require(ok, message):
    if not ok:
        raise ValueError(message)


def vector(value):
    result = np.array([value[axis] for axis in 'xyz'], dtype=float)
    require(np.isfinite(result).all(), 'Nonfinite vector')
    return result


def same_rotation(first, second):
    a = np.array([first[k] for k in 'xyzw'], dtype=float)
    b = np.array([second[k] for k in 'xyzw'], dtype=float)
    return np.isfinite(a).all() and np.isfinite(b).all() and abs(np.linalg.norm(a)-1) < 1e-5 \
        and abs(np.linalg.norm(b)-1) < 1e-5 and min(np.linalg.norm(a-b), np.linalg.norm(a+b)) < 1e-5


def verify(directory, report=None):
    directory = Path(directory)
    if report is None:
        report = json.loads((directory / 'contact_result.json').read_text(encoding='utf-8-sig'))
    require(report.get('schemaVersion') == 2 and report.get('result') == 'PASS' and not report.get('errors'),
            'Contact observer reported failure: ' + str(report.get('errors')))
    parts = report.get('hullParts', [])
    if report.get('meshHullRequested') or parts:
        require(report['hullColliderKind'] == 'MeshCollider' and report['hullSourceTriangles'] > 100,
                'Actual displayed mesh did not supply the native hull collision shape')
        source = report.get('hullSource')
        require(source and source.get('schemaVersion') == 2 and 2 <= len(parts) <= 1024,
                'Missing bounded collision compound or source provenance')
        require(len(parts) == len(source['parts']) and len({p['instanceId'] for p in parts}) == len(parts),
                'Missing or duplicated actual native convex parts')
        for part, original in zip(parts, source['parts']):
            require(part['name'] == original['name'] and part['vertices'] == original['vertices']
                    and part['triangles'] == original['triangles'], 'Convex part differs from its generated source')
            require(4 <= part['vertices'] <= 120 and 4 <= part['triangles'] <= 236,
                    'An individual native convex part exceeds the complete cooking budget')
            require(part['enabled'] and part['convex'] and part['sameBody'] and not part['isTrigger']
                    and np.linalg.norm(vector(part['boundsSize'])) > 1e-5, 'Inactive, empty or detached contact part')
            require(abs(part['contactOffsetM'] - report['hullContactOffsetM']) < 1e-6, 'Inconsistent compound contact envelope')
        require(sum(p['vertices'] for p in parts) == report['hullCollisionVertices'] == source['collisionVertices']
                and sum(p['triangles'] for p in parts) == report['hullCollisionTriangles'] == source['collisionTriangles'],
                'Compound totals differ from actual per-part observations')
        log = (directory / 'unity.log').read_text(encoding='utf-8-sig', errors='replace').lower()
        require('partial hull' not in log and "couldn't create a convex mesh" not in log,
                'Native cooking substituted an incomplete collision hull')
        require(np.linalg.norm(vector(report['inertiaBeforeContactShape']) - vector(report['inertiaAfterContactShape'])) < 1e-4,
                'Contact shape replacement changed the equivalent-body inertia')
        require(np.linalg.norm(vector(report['centreBeforeContactShape']) - vector(report['centreAfterContactShape'])) < 1e-6,
                'Contact shape replacement changed the independent centre of mass')
        require(same_rotation(report['inertiaRotationBeforeContactShape'], report['inertiaRotationAfterContactShape']),
                'Contact shape replacement rotated the independent inertia frame')
    physics = report['physics']
    renders = report['render']
    require(1000 <= len(physics) <= 4000 and len(renders) >= 100, 'Incomplete bounded physical/render observations')
    require(list(dict.fromkeys(row['stage'] for row in physics)) == STAGES, 'Missing or reordered contact stages')
    previous_time = -1
    previous_count = 0
    events = []
    stage_counts = {}
    for row in physics:
        require(row['automaticPhysics'] and not row['bodyKinematic'] and row['valid'],
                'Contact fixture left normal automatic physics or valid input')
        require(math.isfinite(row['simulationTime']) and row['simulationTime'] > previous_time,
                'Duplicated physical observation or invalid simulation time')
        previous_time = row['simulationTime']
        count = row['woodImpacts']
        require(type(count) is int and previous_count <= count <= previous_count + 1, 'Invalid impact counter transition')
        require(0 <= row['displayedPositionErrorM'] <= report['positionToleranceM'], 'Displayed pose differs from decoded input')
        vector(row['bladeLocal']); vector(row['relativeVelocityLocal'])
        vector(row['boatPosition']); vector(row['boatVelocity']); vector(row['boatAngularVelocity'])
        require(np.linalg.norm(vector(row['inertiaTensor']) - vector(report['inertiaBeforeContactShape'])) < 1e-4
                and np.linalg.norm(vector(row['centreOfMass']) - vector(report['centreBeforeContactShape'])) < 1e-6,
                'A subsequent physical step changed the equivalent mass model')
        require(same_rotation(row['inertiaRotation'], report['inertiaRotationBeforeContactShape']),
                'A subsequent physics step rotated the independent inertia frame')
        if count > previous_count:
            events.append(row)
            require(0 <= row['simulationTime'] - row['lastImpactTime'] <= 0.021,
                    'Contact event lost its physical source timestamp')
            if row['stage'] in ('ShaftTouch', 'BladeTouch'):
                require(row['relativeVelocityLocal']['x'] < -0.07, 'Wood hit was emitted without an approaching paddle')
                require(np.linalg.norm(vector(row['lastContactLocal']) - vector(report['railPointLocal'])) < 0.09,
                        'Paddle hit is not located at the displayed hull rail')
        previous_count = count
        stage_counts.setdefault(row['stage'], []).append(row)
    for stage in STAGES:
        require(len(stage_counts[stage]) >= 210, 'Short contact stage: ' + stage)
    counts = {stage: sum(event['stage'] == stage for event in events) for stage in STAGES}
    require(counts['ShaftTouch'] == counts['BladeTouch'] == 1, 'Shaft/blade contact missing or held contact repeated')
    require(counts['ShaftRelease'] == counts['BladeRelease'] == 0, 'Separating paddle emitted an impact')
    collisions = report['collisions']
    require(1 <= len(collisions) == counts['HullCollision'] <= 4, 'PhysX hull contacts do not match audible event count')
    momentum = []
    for row in collisions:
        if parts:
            require(any(part['instanceId'] == row['hullColliderInstanceId'] and part['name'] == row['hullMeshName'] for part in parts),
                    'Contact did not originate on an observed native hull part')
        normal = vector(row['normal'])
        approach_speed = abs(float(vector(row['relativeVelocity']) @ normal))
        require(approach_speed > 0.07, 'Hull contact has no measured approaching motion')
        # The freely moving striker has no gravity, damping or scripted force.
        # Verify the actual momentum transfer, independently of Collision.impulse.
        # Keep Unity's raw impulse too: some CCD callbacks report zero there.
        # PhysX generates contacts inside contactOffset before the surfaces touch.
        # Derive the observation interval from that spatial envelope and measured
        # approach speed; do not assume the first contact callback has solved an impulse.
        interval = (report['hullContactOffsetM'] + report['strikerContactOffsetM']) / approach_speed + 2 * report['fixedStepSeconds']
        response = [frame for frame in physics if frame['strikerPresent']
                    and 0 <= frame['simulationTime'] - row['simulationTime'] <= interval]
        require(response, 'No post-contact physical striker observations')
        delta_speed = max(float((vector(frame['strikerVelocity']) - vector(row['strikerVelocityBeforeStep'])) @ normal)
                          for frame in response)
        require(type(row['strikerMassKg']) in (int, float) and row['strikerMassKg'] > 0
                and delta_speed * row['strikerMassKg'] > 1e-4,
                'Hull contact did not change the freely moving striker momentum')
        momentum.append({'reported_first_impulse_ns': float(np.linalg.norm(vector(row['impulse']))),
                         'first_separation_m': row['separationM'], 'observation_window_ms': interval * 1000,
                         'measured_normal_momentum_change_ns': delta_speed * row['strikerMassKg']})
        require(any(event['stage'] == 'HullCollision' and abs(event['simulationTime'] - row['simulationTime']) < 0.021
                    for event in events), 'Hull sound is not tied to an actual PhysX contact')
    require(all(row['creakSourceRms'] < 1e-6 for row in renders), 'Creak output contaminated isolated contact audio')
    for stage in ('ShaftTouch', 'BladeTouch', 'HullCollision'):
        require(max((row['impactSourceRms'] for row in renders if row['stage'] == stage), default=0) > 1e-6,
                'Contact source remained silent: ' + stage)
    sound = verify_audio_capture(directory, report)
    with wave.open(str(directory / 'listener_mix.wav'), 'rb') as stream:
        samples = np.frombuffer(stream.readframes(stream.getnframes()), dtype='<i2').astype(float) / 32767.
        samples = samples.reshape(-1, stream.getnchannels())
        rate = stream.getframerate()
    first_dsp = report['firstAudioBufferDspTime']
    require(math.isfinite(first_dsp) and samples.shape[0] / rate >= 10.5, 'Short/missing listener capture timeline')
    event_audio = []
    for event in events:
        time = event['dspTime'] - first_dsp
        start = max(0, int((time - .06) * rate))
        end = min(samples.shape[0], int((time + .5) * rate))
        require(end > start, 'Contact is outside actual captured audio')
        segment = samples[start:end]
        rms = float(np.sqrt(np.mean(segment * segment)))
        peak = float(np.max(np.abs(segment)))
        require(rms > 1e-6 and peak > 1e-4, 'Final listener is silent at contact onset')
        active = np.flatnonzero(np.max(np.abs(segment), axis=1) > 1e-4)
        event_audio.append({'stage': event['stage'], 'event_seconds': time, 'rms': rms, 'peak': peak,
                            'first_audible_relative_ms': (start + int(active[0])) / rate * 1000 - time * 1000})
    require([row['stage'] for row in report['screenshots']] == STAGES, 'Missing presented contact screenshots')
    for shot in report['screenshots']:
        path = directory / shot['file']
        require(path.parent == directory and hashlib.sha256(path.read_bytes()).hexdigest() == shot['sha256'],
                'Screenshot path/hash mismatch')
        with Image.open(path) as picture:
            require(min(picture.size) >= 200 and max(ImageStat.Stat(picture.convert('RGB')).stddev) > 3,
                    'Presented contact image is blank or too small')
    return {'result': 'PASS', 'physical_frames': len(physics), 'render_frames': len(renders),
            'native_hull_parts': len(parts),
            'events_by_stage': counts, 'actual_hull_contacts': len(collisions),
            'hull_momentum_response': momentum, 'listener_audio': sound, 'contact_audio_windows': event_audio,
            'audio_timing_scope': 'Engine DSP sample timeline with buffered callbacks; not measured speaker/display latency',
            'dsp_buffer_frames': report.get('dspBufferFrames')}


def write_review(directory, report, summary):
    items = []
    for event in summary['contact_audio_windows']:
        items.append('<article><h2>' + event['stage'] + '</h2><img src="' + event['stage']
                     + '.png"><p>事件时间 ' + f"{event['event_seconds']:.3f} s"
                     + '</p><button onclick="playAt(' + str(max(0, event['event_seconds'] - .3))
                     + ')">播放这一段</button></article>')
    page = '''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>木质接触运行证据</title>
<style>body{font:16px system-ui;background:#18202a;color:#ecf3fa;max-width:1080px;margin:auto;padding:24px}img{width:100%}article{margin:26px 0}button{padding:10px}audio{width:100%}</style>
<h1>桨杆、桨叶与船体的木质接触</h1><p>专用输入夹具的实际 Player 画面与 AudioListener 录音。只隔离碰撞声，使用正常音量；水声和吱呀声在本次诊断中静音。不能据此认定全部手感或主观听感已通过。</p>
<audio id="audio" src="listener_mix.wav" controls></audio><script>function playAt(t){let a=document.getElementById('audio');a.currentTime=t;a.play()}</script>'''
    (directory / 'contact_review.html').write_text(page + ''.join(items) + '</html>\n', encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--player', type=Path, default=WORKSPACE / 'Builds/DragonBoatVR/DragonBoatVR.exe')
    parser.add_argument('--verify', type=Path)
    parser.add_argument('--mesh-hull', action='store_true', help='Evaluate the native displayed-mesh hull contact replacement in this diagnostic run')
    args = parser.parse_args()
    if args.verify:
        print(json.dumps(verify(args.verify), indent=2))
        return 0
    out = WORKSPACE / 'tools/wood_contact_checks' / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ') + '_' + uuid.uuid4().hex[:8])
    out.mkdir(parents=True, exist_ok=False)
    print('ARTIFACTS=' + str(out), flush=True)
    result = {'result': 'FAIL', 'reasons': [], 'source_sha256': source_hashes()}
    live = Path(tempfile.mkdtemp(prefix='DragonBoatVR-wood-contact-')).resolve()
    player = args.player.resolve()
    try:
        build = json.loads((player.parent / 'build_manifest.json').read_text(encoding='utf-8'))
        require(result['source_sha256'] == build['source_sha256'], 'Player was built from different source files')
        result['player_files_sha256'] = player_hashes(player)
        command = [str(player), '-kinematic-fixture', '-woodContactArtifacts', str(live),
                   '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '760', '-logFile', str(live / 'unity.log')]
        if args.mesh_hull:
            command.append('-meshHullContact')
        result['command'] = command
        with unity_project_guard(PROJECT):
            process = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT,
                                     env=player_environment(), cwd=str(player.parent), timeout=65,
                                     creationflags=subprocess.CREATE_NO_WINDOW)
        result['exit_code'] = process.returncode
        result['published_artifacts_sha256'] = publish_runtime_artifacts(live, out)
        require(process.returncode == 0, 'Player contact observer exited unsuccessfully')
        summary = verify(out)
        result['independent'] = summary
        report = json.loads((out / 'contact_result.json').read_text(encoding='utf-8-sig'))
        write_review(out, report, summary)
    except Exception as error:
        result['reasons'].append(str(error))
        if live.exists():
            try:
                for source in live.iterdir():
                    if source.is_file() and not (out / source.name).exists():
                        (out / source.name).write_bytes(source.read_bytes())
            except Exception as publication_error:
                result['reasons'].append('Failure evidence publication: ' + str(publication_error))
    result['source_unchanged'] = result['source_sha256'] == source_hashes()
    if not result['source_unchanged']:
        result['reasons'].append('Project sources changed during contact verification')
    if 'player_files_sha256' in result:
        result['player_unchanged'] = result['player_files_sha256'] == player_hashes(player)
        if not result['player_unchanged']:
            result['reasons'].append('Player files changed during verification')
    # Only remove the owned, flat staging directory after byte-for-byte publication.
    if live.parent == Path(tempfile.gettempdir()).resolve() and live.name.startswith('DragonBoatVR-wood-contact-'):
        contents = list(live.iterdir())
        if all(item.is_file() and (out / item.name).is_file()
               and item.read_bytes() == (out / item.name).read_bytes() for item in contents):
            for item in contents:
                item.unlink()
            live.rmdir()
        else:
            result['retained_staging_directory'] = str(live)
    result['result'] = 'FAIL' if result['reasons'] else 'PASS'
    (out / 'verification.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in ('result', 'reasons', 'source_unchanged')}, ensure_ascii=True), flush=True)
    if 'independent' in result:
        print(json.dumps(result['independent'], indent=2), flush=True)
    return 0 if result['result'] == 'PASS' else 1


if __name__ == '__main__':
    raise SystemExit(main())
