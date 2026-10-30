"""Offline negative evidence checks. Creates only temporary fixtures; never starts Unity."""
import base64
import copy
import hashlib
import json
import math
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest import mock
import zlib
import wave

import run_closed_loop_check as verifier


ZERO = {'x': 0.0, 'y': 0.0, 'z': 0.0}
IDENTITY = {**ZERO, 'w': 1.0}


def packet(payload, seq, session):
    body = json.dumps(payload, separators=(',', ':')).encode()
    raw = struct.pack('<4sIIQI', b'DBCL', 1, seq, session, len(body)) + body
    return base64.b64encode(raw + struct.pack('<I', zlib.crc32(raw))).decode()


def fixture():
    rows = []
    endpoints = {}
    def frame(stage, index, seq, session, sim_time, valid=True):
        y = .3 if index % 100 < 30 else -.3
        local = {'x': .6, 'y': y, 'z': 0.0}
        raw_counts = [6000, round(y*10000), 0, 0, 0, 0]
        sample_tick = seq*2 + (60 if stage in ('Disconnected', 'Recovery') else 0)
        sample_time = sample_tick*.005
        wall_time = sample_time+.001+(seq%7)*.0001
        endpoint = endpoints.setdefault(session, {'command': {'kind': 'step', 'commandSequence': 0,
            'active': True, 'cableValid': False, 'platformValid': False,
            'sourceFeedbackSequence': 0, 'sourceSampleTick': 0}, 'source': None, 'received': wall_time-.001})
        acknowledged = endpoint['command']
        source = endpoint['source']
        command_age = wall_time-endpoint['received']
        source_age = wall_time-source['controllerWallTimeSeconds'] if source is not None else -1.
        valid = command_age <= .1
        execution = valid and (source_age == -1 or source_age <= .1)
        force = [1.0, 0.0, 0.0] if valid else [0.0]*3
        device_time = int(sample_time*1_000_000)
        feedback = {'kind': 'feedback', 'commandSequence': acknowledged['commandSequence'], 'valid': valid, 'watchdog': not valid,
                    'commandSourceFeedbackSequence': acknowledged['sourceFeedbackSequence'],
                    'commandSourceSampleTick': acknowledged['sourceSampleTick'],
                    'commandSourceAgeSeconds': source_age, 'commandAgeSeconds': command_age,
                    'commandExecutionFresh': execution, 'cableExecutionEnabled': execution and acknowledged['cableValid'],
                    'platformExecutionEnabled': execution and acknowledged['platformValid'],
                    'controllerTimeSeconds': sample_time, 'controllerWallTimeSeconds': wall_time,
                    'plantSampleTick': sample_tick, 'plantStepSeconds': .005, 'plantDroppedTicks': 0,
                    'rawEncoderCounts': raw_counts, 'tensionN': [1., 2., 3.] if valid else [0.]*3,
                    'cableLengthsMm': [2066.1829773984227, 1496.6867782774618, 1116.4959109033255],
                    'forceLocal': force, 'torqueLocal': [0.]*3, 'platformLengthsMm': [600.]*6,
                    'platformPosition': [0.]*3, 'platformEuler': [0.]*3, 'countsPerMeter': 10000., 'countsPerDegree': 100.}
        encoder = struct.pack('<IIIQ6if', 0x44424550, 1, seq, device_time, *raw_counts, 1.0)
        encoder += struct.pack('<I', zlib.crc32(encoder))
        feedback['encoderPacketBase64'] = base64.b64encode(encoder).decode()
        t = {'feedbackFresh': valid, 'encoderConsumed': valid, 'sessionId': str(session),
             'intentPaused': not valid,
             'hostProcessId': 11, 'controllerProcessId': 111 if session == 1 else 222,
             'feedbackSequence': seq, 'acknowledgedCommandSequence': acknowledged['commandSequence'],
             'acknowledgedCommandPacketBase64': packet(acknowledged, acknowledged['commandSequence'], session),
             'acknowledgedSourcePacketBase64': endpoint.get('sourcePacket'),
             'commandSent': stage != 'Disconnected', 'commandSourceFresh': valid and stage != 'Disconnected',
             'deviceFeedbackFresh': valid, 'deviceFeedbackReceivedAgeSeconds': .011 if valid else .301,
             'deviceFeedbackAckAgeSeconds': .011 if valid else .301,
             'commandSourceReceivedAgeSeconds': .011 if valid else .301,
             'commandSourceAckAgeSeconds': .011 if valid else .301,
             'commandSequence': acknowledged['commandSequence'], 'commandPacketBase64': None,
             'receivedAgeSeconds': .01 if valid else .3, 'commandAckAgeSeconds': .01 if valid else .3,
             'feedbackPacketBase64': packet(feedback, seq, session), 'encoderPacketBase64': base64.b64encode(encoder).decode(),
             'actualBladePositionLocal': local, 'desiredBladePositionLocal': {**local, 'x': .61},
             'observationBoatPosition': ZERO, 'observationBoatRotation': IDENTITY, 'feedback': feedback}
        request = {'valid': valid, 'forceWorldN': dict(zip(('x', 'y', 'z'), force)), 'torqueAtGripWorldNm': ZERO}
        if t['commandSent']:
            target = {'kind': 'step', 'commandSequence': acknowledged['commandSequence']+1,
                'sourceFeedbackSequence': seq, 'sourceSampleTick': sample_tick,
                'active': True, 'cableValid': valid, 'platformValid': valid,
                'requestForce': force, 'requestTorque': [0.]*3, 'desiredGripVelocity': [0.]*3,
                'tensionTargets': [1., 2., 3.] if valid else [0.]*3}
            t['commandSequence'] = target['commandSequence']
            t['commandPacketBase64'] = packet(target, target['commandSequence'], session)
            endpoint.update(command=target, source=feedback, sourcePacket=t['feedbackPacketBase64'], received=wall_time+.0005)
        water = {'x': 2. if valid else 0., 'y': 0., 'z': 0.}
        f = {'timeSeconds': sim_time, 'fixedStepSeconds': .01, 'closedLoop': t, 'boatPosition': ZERO, 'boatVelocity': ZERO,
             'paddles': [{'valid': valid, 'sequence': seq, 'source': 5, 'deviceTimeUs': device_time,
                          'bladePosition': local, 'bladeRotation': IDENTITY}],
             'rawWaterForceN': [water], 'filteredFeedbackForceN': [water], 'feedbackRequests': [request],
             'platformRequest': {'valid': valid},
             'encoderTelemetry': [{'rawCounts': raw_counts, 'packetAccepted': True, 'crcValid': True, 'packetBytes': 52,
                                   'sourceSequence': seq, 'deviceTimeUs': device_time, 'decodedLocalPosition': local}],
             'cableFeedback': {'tensionsN': feedback['tensionN'], 'lengthsMm': verifier.float32(feedback['cableLengthsMm']),
                               'forceWorldN': request['forceWorldN'], 'torqueAtGripWorldNm': ZERO},
             'platformFeedback': {'actuatorLengthsMm': [600.]*6, 'translationM': ZERO, 'eulerDegrees': ZERO}}
        return {'recordType': 'frame', 'stage': stage, 'realTimeSeconds': len(rows)*.01, 'physicsAutomatic': True,
                'driverPhase01': 0.0,
                'experienceActive': True, 'bodyKinematic': False, 'bodyPosition': ZERO, 'bodyVelocity': ZERO,
                'wettedFraction': 0. if y > 0 else 1., 'frame': f}
    for i in range(801): rows.append(frame('Normal', i, i+1, 1, (i+1)*.01))
    for i in range(16): rows.append(frame('Disconnected', i, 802+i, 1, 8.02+i*.01, False))
    for i in range(51): rows.append(frame('Recovery', i, 818+i, 1, 8.18+i*.01))
    for i in range(4):
        restart = i == 3
        rows.append({'recordType': 'restart_requested' if restart else 'stopped', 'stage': 'Stopped',
                     'realTimeSeconds': len(rows)*.01, 'experienceActive': restart, 'bodyKinematic': not restart,
                     'lastFrameCleared': True, 'controllerAlive': False, 'bodyVelocity': ZERO, 'bodyPosition': ZERO,
                     'bodyRotation': IDENTITY, 'waveTimeSeconds': 0., 'paddle': {'valid': False}, 'rawForceN': ZERO,
                     'filteredForceN': ZERO, 'request': {'valid': False, 'forceWorldN': ZERO, 'torqueAtGripWorldNm': ZERO}})
    for i in range(151): rows.append(frame('Restarted', i, i+1, 2, (i+1)*.01))
    report = {'result': 'PASS', 'errors': [], 'checks': [{'name': x, 'passed': True} for x in verifier.REQUIRED],
              'inputTtlSeconds': .1, 'feedbackTtlSeconds': .1, 'sourceFramesComplete': True, 'hostProcessId': 11,
              'countsPerMeter': 10000., 'countsPerDegree': 100., 'initialBoatPosition': ZERO,
              'initialBoatRotation': IDENTITY, 'initialWaveTime': 0., 'initialSessionId': '1', 'initialControllerPid': 111,
              'restartedSessionId': '2', 'restartedControllerPid': 222, 'screenshot': 'scene.png',
              'platformGeometry': {'useExplicitJointGeometry': True, 'explicitBaseJointsLocal': [ZERO]*6,
                                   'explicitTopJointsLocal': [ZERO]*6, 'neutralHeight': .6,
                                   'minActuatorLengthsMm': [300.]*6, 'maxActuatorLengthsMm': [1100.]*6},
              'processes': [{'observedAlive': True, 'hostProcessId': 11, 'sessionId': str(n), 'controllerProcessId': pid}
                            for n, pid in [(1, 111), (2, 222)]]}
    return report, rows


def presentation_fixture(rows):
    """Add the current presentation schema to synthetic offline data only.

    These generated events are test inputs, not historical Unity observations.
    The fixture retains its original physical/UDP rows for the existing tests.
    """
    expanded = []
    entries = exits = splashes = drips = observed = paddle_paint = wake_paint = 0
    epoch = 1
    was_wet = seen_frame = stopped = False
    audio_until = None

    def reset(stage, when):
        return dict(recordType='interaction_reset', stage=stage, realTimeSeconds=when,
                    waterEntries=entries, waterExits=exits, splashSounds=splashes, dripSounds=drips,
                    foamEpoch=epoch)

    expanded.append(reset('Normal', rows[0]['realTimeSeconds']))
    for source in rows:
        row = copy.deepcopy(source)
        if row['recordType'] == 'stopped' and not stopped:
            epoch += 1
            observed = paddle_paint = wake_paint = 0
            expanded.append(reset('Recovery', row['realTimeSeconds']))
            stopped = True
            audio_until = None
        if row['recordType'] == 'restart_requested':
            epoch += 1
            entries = exits = observed = paddle_paint = wake_paint = 0
            was_wet = seen_frame = False
            audio_until = None
            expanded.append(reset('Stopped', row['realTimeSeconds']))
        if row['recordType'] != 'frame':
            expanded.append(row)
            continue
        sample, telemetry = row['frame']['paddles'][0], row['frame']['closedLoop']
        wet = sample['valid'] and sample['bladePosition']['y'] < 0
        if seen_frame and sample['valid']:
            if wet and not was_wet:
                entries += 1
                splashes += 1
                audio_until = row['frame']['timeSeconds'] + .34
            if not wet and was_wet:
                exits += 1
                drips += 1
                audio_until = row['frame']['timeSeconds'] + .24
        was_wet, seen_frame = wet, True
        observed += 1
        paddle_paint += int(wet)
        # A moving-current wake is explicit synthetic presentation evidence; this
        # helper does not simulate a second hydrodynamic or controller model.
        wake_paint += 1
        row.update(waterEntries=entries, waterExits=exits)
        expanded.append(row)
        if not sample['valid']:
            continue
        for camera_id, name in enumerate(('MainCamera', 'TwinCamera'), 1):
            expanded.append({'recordType': 'render', 'stage': row['stage'],
                'presentsToScreen': True, 'cameraName': name, 'cameraId': camera_id,
                'cameraClearFlags': 1, 'skyboxAvailable': True, 'unityFrameCount': 1,
                'reflectionSourceCameraId': camera_id, 'reflectionFrame': 1,
                'feedbackSequence': telemetry['feedbackSequence'],
                'expectedBladeLocal': sample['bladePosition'], 'actualBladeLocal': sample['bladePosition'],
                'expectedRotationLocal': sample['bladeRotation'], 'actualRotationLocal': sample['bladeRotation'],
                'interactionAvailable': True, 'waterEntries': entries, 'waterExits': exits,
                'splashSounds': splashes, 'dripSounds': drips, 'foamEpoch': epoch,
                'paddleAudioRms': .002 if audio_until is not None and row['frame']['timeSeconds'] < audio_until else 0.,
                'foamObservedFrames': observed, 'foamPaddlePaintEvents': paddle_paint,
                'foamWakePaintEvents': wake_paint, 'foamSourceTime': row['frame']['timeSeconds']})
    return expanded


class ClosedLoopVerifierTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls): cls.report, cls.rows = fixture()

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='closed-loop-verifier-')
        self.addCleanup(self.temporary.cleanup)
        self.out = Path(self.temporary.name)
        def chunk(kind, payload): return struct.pack('>I', len(payload)) + kind + payload + struct.pack('>I', zlib.crc32(kind+payload))
        pixels = b''.join(b'\0' + bytes((x+y) % 256 for x in range(600)) for y in range(200))
        png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 200, 200, 8, 2, 0, 0, 0))
        png += chunk(b'IDAT', zlib.compress(pixels)) + chunk(b'IEND', b'')
        (self.out/'scene.png').write_bytes(png)

    def verify_rows(self, rows=None, report=None, torn=False, mutate_presentation=None):
        rows = rows if rows is not None else self.rows
        report = copy.deepcopy(report or self.report)
        report.update(intentStrokesPerMinute=45, intentContinuityViolations=0)
        report.update(commandPackets=sum(row['recordType']=='frame' and row['frame']['closedLoop']['commandSent'] for row in rows),
                      commandSourceExpiredPackets=sum(row['recordType']=='frame' and row['frame']['closedLoop']['commandSent']
                          and not row['frame']['closedLoop']['commandSourceFresh'] for row in rows), commandSourceViolations=0)
        rows = presentation_fixture(rows)
        report.update(executionRenderObservations=sum(row['recordType']=='render' for row in rows), executionRenderViolations=0)
        report.update(presentationMainFrames=sum(row['recordType']=='render' and row['stage']=='Normal'
                      and row['cameraName']=='MainCamera' for row in rows),
                      presentationTwinFrames=sum(row['recordType']=='render' and row['stage']=='Normal'
                      and row['cameraName']=='TwinCamera' for row in rows), presentationViolations=0)
        normal = [row for row in rows if row['recordType']=='render' and row['stage']=='Normal']
        report.update(interactionObservations=report['executionRenderObservations'], interactionViolations=0,
                      interactionNormalEntries=normal[-1]['waterEntries']-rows[0]['waterEntries'],
                      interactionPaddlePaintEvents=normal[-1]['foamPaddlePaintEvents'],
                      interactionWakePaintEvents=normal[-1]['foamWakePaintEvents'])
        report.setdefault('maxPaddleAudioRms', max(row['paddleAudioRms'] for row in normal))
        if mutate_presentation is not None:
            mutate_presentation(rows)
        data = ''.join(json.dumps(row, separators=(',', ':'))+'\n' for row in rows).encode()
        if torn: data = data[:-1]
        report.update(observationsSha256=hashlib.sha256(data).hexdigest(), queuedLines=len(rows), writtenLines=len(rows),
                      observedFrames=sum(row['recordType']=='frame' for row in rows))
        (self.out/'result.json').write_text(json.dumps(report), encoding='utf-8')
        (self.out/'observations.jsonl').write_bytes(data)
        return verifier.verify(self.out)

    def test_complete_evidence_and_exact_float32_boundary(self):
        result = self.verify_rows()
        self.assertGreaterEqual(result['normal_entries'], 3)
        self.assertEqual(result['sessions'], {'1': 111, '2': 222})
        self.assertEqual(result['sample_clocks']['1']['distinct_packets'], 868)
        self.assertGreater(result['sample_clocks']['1']['wall_phase_observations'], 0)
        self.assertEqual(result['interaction_resets'], 3)
        self.assertGreaterEqual(result['interaction_normal_entries'], 3)
        self.assertGreater(result['interaction_paddle_paint_events'], 0)
        self.assertGreater(result['interaction_wake_paint_events'], 0)
        self.assertGreater(result['max_paddle_audio_rms'], 1e-6)

    def test_correct_audio_call_counts_cannot_hide_silent_output(self):
        def silence(rows):
            for row in rows:
                if row['recordType']=='render': row['paddleAudioRms']=0.
        with self.assertRaisesRegex(ValueError, 'audio output remained silent'):
            self.verify_rows(report=dict(self.report,maxPaddleAudioRms=0.), mutate_presentation=silence)

    def test_invalid_audio_rms_observations_are_rejected(self):
        for value in (-.01,float('nan'),True):
            with self.subTest(value=value):
                def corrupt(rows):
                    row=next(row for row in rows if row['recordType']=='render')
                    row['paddleAudioRms']=value
                with self.assertRaisesRegex(ValueError, 'audio RMS|nonfinite'):
                    self.verify_rows(mutate_presentation=corrupt)

    def test_audio_rms_summary_cannot_invent_or_inflate_output(self):
        for value in (0.,.003,True):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, 'audio RMS summary'):
                self.verify_rows(report=dict(self.report,maxPaddleAudioRms=value))

    def test_missing_first_splash_is_rejected(self):
        def omit_first(rows):
            row = next(row for row in rows if row['recordType']=='render' and row['waterEntries']==1)
            row['splashSounds'] -= 1
        with self.assertRaisesRegex(ValueError, 'splash sounds'):
            self.verify_rows(mutate_presentation=omit_first)

    def test_duplicate_splash_is_rejected(self):
        def duplicate(rows):
            row = next(row for row in rows if row['recordType']=='render' and row['waterEntries']==1)
            row['splashSounds'] += 1
        with self.assertRaisesRegex(ValueError, 'splash sounds'):
            self.verify_rows(mutate_presentation=duplicate)

    def test_missing_exit_drip_is_rejected(self):
        def omit_drip(rows):
            row = next(row for row in rows if row['recordType']=='render' and row['waterExits']==1)
            row['dripSounds'] -= 1
        with self.assertRaisesRegex(ValueError, 'drip sounds'):
            self.verify_rows(mutate_presentation=omit_drip)

    def test_stale_foam_source_time_is_rejected(self):
        def delay_foam(rows):
            row = next(row for row in rows if row['recordType']=='render')
            row['foamSourceTime'] -= .01
        with self.assertRaisesRegex(ValueError, 'foam source time'):
            self.verify_rows(mutate_presentation=delay_foam)

    def test_boolean_foam_clock_cannot_impersonate_one_second(self):
        def false_clock(rows):
            row = next(row for row in rows if row['recordType']=='render' and row['foamSourceTime']==1.0)
            row['foamSourceTime'] = True
        with self.assertRaisesRegex(ValueError, 'foam source time'):
            self.verify_rows(mutate_presentation=false_clock)

    def test_wrong_foam_epoch_is_rejected(self):
        def stale_epoch(rows):
            row = next(row for row in rows if row['recordType']=='render' and row['stage']=='Restarted')
            row['foamEpoch'] -= 1
        with self.assertRaisesRegex(ValueError, 'wrong timeline epoch'):
            self.verify_rows(mutate_presentation=stale_epoch)

    def test_render_event_counts_must_match_preceding_physics(self):
        def invent_entry(rows):
            row = next(row for row in rows if row['recordType']=='render')
            row['waterEntries'] += 1
            row['splashSounds'] += 1
        with self.assertRaisesRegex(ValueError, 'preceding physics'):
            self.verify_rows(mutate_presentation=invent_entry)

    def test_missing_interaction_reset_cannot_be_hidden_by_summary(self):
        def omit_initial(rows):
            rows.pop(0)
        with self.assertRaisesRegex(ValueError, 'reset baseline'):
            self.verify_rows(mutate_presentation=omit_initial)

    def test_interaction_summary_must_match_recorded_presentation(self):
        self.verify_rows()
        path = self.out/'result.json'
        report = json.loads(path.read_text(encoding='utf-8'))
        report['interactionObservations'] += 1
        path.write_text(json.dumps(report), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'interaction evidence incomplete'):
            verifier.verify(self.out)

    def test_background_focus_loss_requires_continued_fresh_physics(self):
        report = dict(runInBackgroundAtStart=True, focusEvents=[
            dict(realTimeSeconds=0., isFocused=True, stage='Starting', initialState=True),
            dict(realTimeSeconds=1., isFocused=False, stage='Normal', initialState=False),
            dict(realTimeSeconds=2.2, isFocused=True, stage='Normal', initialState=False)])
        (self.out/'result.json').write_text(json.dumps(report), encoding='utf-8')
        (self.out/'observations.jsonl').write_text(''.join(json.dumps(row)+'\n' for row in self.rows), encoding='utf-8')
        self.assertGreaterEqual(verifier.verify_background(self.out)['fresh_physics_frames'], 100)
        # A responsive window or a restored focus flag alone cannot prove that
        # physics and the UDP consumer kept running during the interruption.
        paused = [row for row in self.rows if not 1 <= row['realTimeSeconds'] < 2.2]
        (self.out/'observations.jsonl').write_text(''.join(json.dumps(row)+'\n' for row in paused), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'did not continue'): verifier.verify_background(self.out)
        report['runInBackgroundAtStart'] = False
        (self.out/'result.json').write_text(json.dumps(report), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'disabled'): verifier.verify_background(self.out)

    def test_render_uses_received_pose_and_rejects_decorative_or_forged_pose(self):
        frame = self.rows[0]['frame']
        sample = frame['paddles'][0]
        render = {'feedbackSequence': frame['closedLoop']['feedbackSequence'], 'presentsToScreen': True,
                  'expectedBladeLocal': sample['bladePosition'], 'actualBladeLocal': sample['bladePosition'],
                  'expectedRotationLocal': IDENTITY, 'actualRotationLocal': IDENTITY}
        self.assertEqual(verifier.verify_render(render, frame), (0.0, 0.0))
        for field in ('expectedBladeLocal', 'actualBladeLocal'):
            altered = copy.deepcopy(render)
            altered[field]['x'] += .01
            with self.assertRaises(ValueError): verifier.verify_render(altered, frame)
        for field in ('expectedRotationLocal', 'actualRotationLocal'):
            altered = copy.deepcopy(render)
            altered[field] = dict(zip(('x','y','z','w'), verifier.euler_q([0,0,2])))
            with self.assertRaises(ValueError): verifier.verify_render(altered, frame)
        altered = dict(render, feedbackSequence=render['feedbackSequence']+1)
        with self.assertRaises(ValueError): verifier.verify_render(altered, frame)
        with self.assertRaises(ValueError): verifier.verify_render(render, None)
        with self.assertRaisesRegex(ValueError, 'offscreen'):
            verifier.verify_render(dict(render, presentsToScreen=False), frame)

    def test_solid_sky_or_other_camera_reflection_is_rejected(self):
        render = dict(cameraName='TwinCamera', cameraId=2, cameraClearFlags=1, skyboxAvailable=True,
                      unityFrameCount=7, reflectionSourceCameraId=2, reflectionFrame=7)
        verifier.verify_presentation_render(render)
        for field, value in (('cameraName','WaterReflectionCamera'), ('cameraClearFlags',2),
                             ('skyboxAvailable',False), ('reflectionSourceCameraId',1), ('reflectionFrame',6)):
            with self.subTest(field=field), self.assertRaises(ValueError):
                verifier.verify_presentation_render(dict(render, **{field:value}))

    def test_unobserved_recovery_phase_jump_is_rejected(self):
        rows = copy.deepcopy(self.rows)
        row = next(row for row in rows if row['stage']=='Recovery')
        row['driverPhase01'] = .3
        with self.assertRaisesRegex(ValueError, 'unobserved stroke'): self.verify_rows(rows)

    def replace_feedback(self, row, mutate):
        telemetry = row['frame']['closedLoop']
        feedback = json.loads(base64.b64decode(telemetry['feedbackPacketBase64'])[24:-4])
        mutate(feedback)
        telemetry['feedback'] = feedback
        telemetry['feedbackPacketBase64'] = packet(feedback, telemetry['feedbackSequence'], int(telemetry['sessionId']))
        return feedback

    def test_missing_or_false_plant_clock_evidence_is_rejected(self):
        cases = {
            'missing state clock': lambda f: f.pop('plantSampleTick'),
            'wall time used for sample': lambda f: f.update(controllerTimeSeconds=f['controllerWallTimeSeconds']),
            'wrong integration step': lambda f: f.update(plantStepSeconds=.01),
            'fractional tick': lambda f: f.update(plantSampleTick=12.5),
            'boolean tick': lambda f: f.update(plantSampleTick=True),
            'dropped state time': lambda f: f.update(plantDroppedTicks=1),
            'wall before integrated state': lambda f: f.update(controllerWallTimeSeconds=f['controllerTimeSeconds']-.001),
            'unreported scheduler gap': lambda f: f.update(controllerWallTimeSeconds=f['controllerTimeSeconds']+.006),
        }
        for name, mutate in cases.items():
            with self.subTest(name=name):
                rows = copy.deepcopy(self.rows)
                self.replace_feedback(rows[5], mutate)
                with self.assertRaises(ValueError): self.verify_rows(rows)

    def test_new_feedback_cannot_relabel_an_old_sample_with_a_new_sequence(self):
        rows = copy.deepcopy(self.rows)
        prior = rows[4]['frame']['closedLoop']['feedback']
        def repeat_sample(feedback):
            feedback['plantSampleTick'] = prior['plantSampleTick']
            feedback['controllerTimeSeconds'] = prior['controllerTimeSeconds']
            feedback['controllerWallTimeSeconds'] = prior['controllerWallTimeSeconds']+.0001
            encoded = bytearray(base64.b64decode(feedback['encoderPacketBase64']))
            struct.pack_into('<Q', encoded, 12, int(feedback['controllerTimeSeconds']*1_000_000))
            struct.pack_into('<I', encoded, 48, zlib.crc32(encoded[:48]))
            feedback['encoderPacketBase64'] = base64.b64encode(encoded).decode()
        feedback = self.replace_feedback(rows[5], repeat_sample)
        rows[5]['frame']['closedLoop']['encoderPacketBase64'] = feedback['encoderPacketBase64']
        with self.assertRaisesRegex(ValueError, 'new feedback repeats or reverses'): self.verify_rows(rows)

    def test_held_packet_is_legal_but_its_bytes_and_clocks_cannot_change(self):
        rows = copy.deepcopy(self.rows)
        outgoing = json.loads(base64.b64decode(rows[5]['frame']['closedLoop']['commandPacketBase64'])[24:-4])
        rows[5]['frame'] = copy.deepcopy(rows[4]['frame'])
        rows[5]['frame']['timeSeconds'] = .06
        held = rows[5]['frame']['closedLoop']
        outgoing.update(sourceFeedbackSequence=held['feedbackSequence'], sourceSampleTick=held['feedback']['plantSampleTick'])
        held['commandSequence'] = outgoing['commandSequence']
        held['commandPacketBase64'] = packet(outgoing, outgoing['commandSequence'], 1)
        next_t = rows[6]['frame']['closedLoop']
        next_t['acknowledgedCommandPacketBase64'] = held['commandPacketBase64']
        next_t['acknowledgedSourcePacketBase64'] = held['feedbackPacketBase64']
        self.replace_feedback(rows[6], lambda f: f.update(commandSourceFeedbackSequence=held['feedbackSequence'],
            commandSourceSampleTick=held['feedback']['plantSampleTick'],
            commandSourceAgeSeconds=f['controllerWallTimeSeconds']-held['feedback']['controllerWallTimeSeconds']))
        rows[7]['frame']['closedLoop']['acknowledgedSourcePacketBase64'] = next_t['feedbackPacketBase64']
        result = self.verify_rows(rows)
        self.assertEqual(result['sample_clocks']['1']['held_frames'], 1)
        self.replace_feedback(rows[5], lambda f: f.update(controllerWallTimeSeconds=f['controllerWallTimeSeconds']+.0001))
        with self.assertRaisesRegex(ValueError, 'held feedback identity'): self.verify_rows(rows)

    def test_original_dbep_time_must_match_the_state_clock_even_with_valid_crcs(self):
        rows = copy.deepcopy(self.rows)
        def wrong_time(feedback):
            encoded = bytearray(base64.b64decode(feedback['encoderPacketBase64']))
            timestamp = struct.unpack_from('<Q', encoded, 12)[0]
            struct.pack_into('<Q', encoded, 12, timestamp+1)
            struct.pack_into('<I', encoded, 48, zlib.crc32(encoded[:48]))
            feedback['encoderPacketBase64'] = base64.b64encode(encoded).decode()
        feedback = self.replace_feedback(rows[5], wrong_time)
        rows[5]['frame']['closedLoop']['encoderPacketBase64'] = feedback['encoderPacketBase64']
        with self.assertRaisesRegex(ValueError, 'DBEP device time differs'): self.verify_rows(rows)

    def test_consumed_sample_and_decoder_timestamps_must_match_received_bytes(self):
        for field in ('paddles', 'encoderTelemetry'):
            with self.subTest(field=field):
                rows = copy.deepcopy(self.rows)
                rows[5]['frame'][field][0]['deviceTimeUs'] += 1
                with self.assertRaisesRegex(ValueError, 'decoded or physics sample time'): self.verify_rows(rows)

    def test_consumed_controller_clock_cannot_differ_from_the_raw_packet(self):
        rows = copy.deepcopy(self.rows)
        rows[5]['frame']['closedLoop']['feedback']['controllerWallTimeSeconds'] += .0001
        with self.assertRaisesRegex(ValueError, 'consumed controller clock'): self.verify_rows(rows)

    def test_wall_clock_cannot_be_manufactured_by_copying_the_sample_clock(self):
        rows = copy.deepcopy(self.rows)
        rewritten = {}
        for row in rows:
            if row['recordType'] == 'frame':
                t = row['frame']['closedLoop']
                source = json.loads(base64.b64decode(t['acknowledgedSourcePacketBase64'])[24:-4]) if t['acknowledgedSourcePacketBase64'] else None
                self.replace_feedback(row, lambda f: f.update(controllerWallTimeSeconds=f['controllerTimeSeconds'],
                    commandSourceAgeSeconds=f['controllerTimeSeconds']-source['controllerTimeSeconds'] if source else -1.))
                rewritten[(t['sessionId'], t['feedbackSequence'])] = t['feedbackPacketBase64']
        for row in rows:
            if row['recordType'] == 'frame':
                t = row['frame']['closedLoop']
                if t['acknowledgedSourcePacketBase64']:
                    t['acknowledgedSourcePacketBase64'] = rewritten[(t['sessionId'], t['feedback']['commandSourceFeedbackSequence'])]
        with self.assertRaisesRegex(ValueError, 'independent controller wall clock'): self.verify_rows(rows)

    def test_negative_state_and_identity_evidence(self):
        cases = {
            'stopped output': lambda rows: rows[868]['rawForceN'].update(x=1),
            'stopped process': lambda rows: rows[868].update(controllerAlive=True),
            'reset pose': lambda rows: rows[871]['bodyPosition'].update(x=99),
            'stale marked fresh': lambda rows: rows[5]['frame']['closedLoop'].update(receivedAgeSeconds=100),
            'wrong post-physics body': lambda rows: rows[5]['bodyPosition'].update(x=99),
            'manual physics': lambda rows: rows[5].update(physicsAutomatic=False),
            'wrong motor force': lambda rows: rows[5]['frame']['cableFeedback']['forceWorldN'].update(x=99),
            'wrong PID': lambda rows: rows[5]['frame']['closedLoop'].update(controllerProcessId=999),
        }
        for name, mutate in cases.items():
            with self.subTest(name=name):
                # Round-trip breaks shared fixture references as real JSONL does.
                rows = [json.loads(json.dumps(row)) for row in self.rows]
                mutate(rows)
                with self.assertRaises(ValueError): self.verify_rows(rows)

    def test_reversed_motor_wrench_cannot_pass_with_the_same_magnitude(self):
        for name, received, consumed in (
                ('force', 'forceLocal', 'forceWorldN'),
                ('torque', 'torqueLocal', 'torqueAtGripWorldNm')):
            with self.subTest(name=name):
                rows = [json.loads(json.dumps(row)) for row in self.rows]
                row = rows[5]
                self.replace_feedback(row, lambda f: f.update({received: [1., 2., 3.]}))
                row['frame']['cableFeedback'][consumed] = dict(x=-1., y=-2., z=-3.)
                with self.assertRaisesRegex(ValueError, 'motor wrench'):
                    self.verify_rows(rows)

    def test_motor_wrench_uses_the_full_rotated_observation_frame(self):
        frame = copy.deepcopy(self.rows[5]['frame'])
        frame['closedLoop']['observationBoatRotation'] = dict(x=0., y=2**-.5, z=0., w=2**-.5)
        feedback = dict(forceLocal=[1., 2., 3.], torqueLocal=[-2., 5., 4.])
        frame['cableFeedback'].update(forceWorldN=dict(x=3., y=2., z=-1.), torqueAtGripWorldNm=dict(x=4., y=5., z=2.))
        verifier.verify_motor_wrench(frame, feedback)
        frame['cableFeedback']['forceWorldN'] = dict(x=1., y=2., z=3.)
        with self.assertRaisesRegex(ValueError, 'motor wrench'):
            verifier.verify_motor_wrench(frame, feedback)

    def test_dispatch_expiry_revokes_both_routes_without_rewriting_valid_input(self):
        frame = copy.deepcopy(self.rows[5]['frame'])
        t = frame['closedLoop']
        command = json.loads(base64.b64decode(t['commandPacketBase64'])[24:-4])
        t.update(commandSourceFresh=False, commandSourceReceivedAgeSeconds=.16, commandSourceAckAgeSeconds=.16,
                 deviceFeedbackFresh=False, deviceFeedbackReceivedAgeSeconds=.161, deviceFeedbackAckAgeSeconds=.161)
        command.update(cableValid=False, requestForce=[0.]*3, requestTorque=[0.]*3, tensionTargets=[0.]*3)
        t['commandPacketBase64'] = packet(command, command['commandSequence'], 1)
        with self.assertRaisesRegex(ValueError, 'expired source authorized'):
            verifier.verify_dispatch(frame, t['feedback'], {})
        command['platformValid'] = False
        t['commandPacketBase64'] = packet(command, command['commandSequence'], 1)
        self.assertTrue(verifier.verify_dispatch(frame, t['feedback'], {}))
        self.assertTrue(frame['paddles'][0]['valid'])
        self.assertTrue(frame['feedbackRequests'][0]['valid'])
        self.assertTrue(t['feedbackFresh'])

    def test_original_command_source_and_acknowledgement_are_required(self):
        for name in ('sourceFeedbackSequence', 'sourceSampleTick', 'commandSequence'):
            rows = [json.loads(json.dumps(row)) for row in self.rows]
            t = rows[5]['frame']['closedLoop']
            command = json.loads(base64.b64decode(t['commandPacketBase64'])[24:-4])
            command[name] += 1
            t['commandPacketBase64'] = packet(command, t['commandSequence'], 1)
            with self.subTest(name=name), self.assertRaises(ValueError): self.verify_rows(rows)
        rows = [json.loads(json.dumps(row)) for row in self.rows]
        t = rows[5]['frame']['closedLoop']
        acknowledged = json.loads(base64.b64decode(t['acknowledgedCommandPacketBase64'])[24:-4])
        acknowledged['platformValid'] = False
        t['acknowledgedCommandPacketBase64'] = packet(acknowledged, t['acknowledgedCommandSequence'], 1)
        with self.assertRaisesRegex(ValueError, 'acknowledged command differs'): self.verify_rows(rows)

    def test_receiver_cannot_claim_execution_after_its_source_expires(self):
        rows = [json.loads(json.dumps(row)) for row in self.rows]
        self.replace_feedback(rows[5], lambda f: f.update(commandSourceAgeSeconds=.2))
        with self.assertRaisesRegex(ValueError, 'source age differs'): self.verify_rows(rows)

    def test_three_strokes_cannot_be_asserted_only_in_summary(self):
        rows = copy.deepcopy(self.rows)
        for row in rows:
            if row['recordType']=='frame' and row['stage']=='Normal': row['wettedFraction'] = .5
        with self.assertRaisesRegex(ValueError, 'three observed wet strokes'): self.verify_rows(rows)

    def watchdog_rows(self, amplitudes):
        rows=copy.deepcopy(self.rows)
        watchdog=[row for row in rows if row['stage']=='Disconnected']
        self.assertEqual(len(watchdog),len(amplitudes))
        for row, amplitude in zip(watchdog,amplitudes):
            telemetry=row['frame']['closedLoop']
            feedback=json.loads(base64.b64decode(telemetry['feedbackPacketBase64'])[24:-4])
            feedback['tensionN']=[amplitude,amplitude/2,amplitude/4]
            feedback['forceLocal']=[amplitude,amplitude/5,0.]
            feedback['torqueLocal']=[0.,amplitude/3,0.]
            telemetry['feedbackPacketBase64']=packet(feedback,telemetry['feedbackSequence'],int(telemetry['sessionId']))
            telemetry['feedback']=feedback
            row['frame']['cableFeedback']['tensionsN']=feedback['tensionN']
        return rows

    def test_watchdog_accepts_observed_decay_then_settling(self):
        rows=self.watchdog_rows([4.,3.,2.,1.,.1,.01,.001,.0001,.00001]+[0.]*7)
        result=self.verify_rows(rows)
        self.assertEqual(result['watchdog_decay'][0]['tension_n'],[4.,2.,1.])
        self.assertEqual(result['watchdog_zero_tail_distinct'],7)
        self.assertEqual(result['watchdog_decay'][-1]['force_n'],[0.,0.,0.])

    def test_watchdog_rejects_frozen_nonzero_or_regrowing_tension(self):
        for amplitudes in ([1.]*16,[4.,3.,2.,1.,.1,2.]+[0.]*10,[-1.]+[0.]*15):
            with self.subTest(amplitudes=amplitudes):
                with self.assertRaises(ValueError): self.verify_rows(self.watchdog_rows(amplitudes))

    def test_repeated_zero_feedback_cannot_fake_five_settled_packets(self):
        rows=copy.deepcopy(self.rows)
        watchdog=[row for row in rows if row['stage']=='Disconnected']
        first=copy.deepcopy(watchdog[0]['frame']['closedLoop'])
        for row in watchdog: row['frame']['closedLoop']=copy.deepcopy(first)
        with self.assertRaisesRegex(ValueError,'five final distinct feedback'): self.verify_rows(rows)

    def test_locally_regenerated_encoder_packet_is_rejected(self):
        rows = copy.deepcopy(self.rows)
        telemetry = rows[5]['frame']['closedLoop']
        packet = bytearray(base64.b64decode(telemetry['encoderPacketBase64']))
        packet[12] ^= 1  # A different timestamp with still-correct pose, identity and CRC.
        struct.pack_into('<I', packet, 48, zlib.crc32(packet[:48]))
        telemetry['encoderPacketBase64'] = base64.b64encode(packet).decode()
        with self.assertRaisesRegex(ValueError, 'locally regenerated'): self.verify_rows(rows)

    def test_missing_stop_and_recovery_cannot_be_hidden_by_pass_markers(self):
        for stage in ['Stopped', 'Recovery']:
            with self.subTest(stage=stage):
                with self.assertRaises(ValueError): self.verify_rows([row for row in self.rows if row['stage'] != stage])

    def test_torn_line_and_false_png_are_rejected(self):
        with self.assertRaisesRegex(ValueError, 'torn'): self.verify_rows(torn=True)
        (self.out/'scene.png').write_bytes(b'not a PNG')
        with self.assertRaisesRegex(ValueError, 'PNG'): self.verify_rows()

    def test_nonfinite_numeric_tokens_are_rejected(self):
        for token in ['NaN', 'Infinity', '1e999']:
            with self.subTest(token=token):
                with self.assertRaises(ValueError): verifier.parse('{"value":'+token+'}')

    def test_player_manifest_includes_code_scene_and_worker(self):
        exe=self.out/'DragonBoatVR.exe'; exe.write_bytes(b'loader')
        for relative in ['DragonBoatVR_Data/Managed/Assembly-CSharp.dll','DragonBoatVR_Data/level0',
                         'DragonBoatVR_Data/StreamingAssets/closed_loop_controller.py']:
            path=self.out/relative; path.parent.mkdir(parents=True,exist_ok=True); path.write_bytes(b'first')
            old=verifier.player_hashes(exe)
            path.write_bytes(b'changed')
            new=verifier.player_hashes(exe)
            self.assertIn(relative,new)
            self.assertNotEqual(old,new)

    def test_controller_selection_prefers_bundled_runtime(self):
        config=self.out/'StreamingAssets/closed_loop.json'
        config.parent.mkdir()
        config.write_text(json.dumps({'pythonExecutable':'python.exe'}),encoding='utf-8')
        self.assertEqual(verifier.controller_runtime(config),('python.exe',False))
        bundled=config.parent/'controller_runtime/python.exe'
        bundled.parent.mkdir(); bundled.write_bytes(b'path-selection fixture; not executable')
        self.assertEqual(verifier.controller_runtime(config),(str(bundled.resolve()),True))

    @unittest.skipUnless(sys.platform == 'win32', 'Windows Player environment')
    def test_player_environment_removes_external_python_configuration(self):
        with mock.patch.dict(os.environ,{'PYTHONPATH':'external-package-path','PYTHONHOME':'external-runtime',
                                        'VIRTUAL_ENV':'external-venv','CONDA_PREFIX':'external-conda'}):
            environment=verifier.player_environment()
        self.assertFalse(any(key.upper().startswith('PYTHON') for key in environment))
        self.assertNotIn('VIRTUAL_ENV',environment)
        self.assertNotIn('CONDA_PREFIX',environment)
        self.assertIsNone(verifier.shutil.which('python.exe',path=environment['PATH']))

    @unittest.skipUnless(sys.platform == 'win32', 'Windows process image query')
    def test_controller_process_image_is_observed_from_the_os(self):
        self.assertEqual(Path(verifier.process_image(os.getpid())).resolve(),Path(sys.executable).resolve())


class HostStallEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.out = Path(self.directory.name)
        (self.out/'result.json').write_text(json.dumps({'hostProcessId':11,'initialControllerPid':22}),encoding='utf-8')
        self.probe = {'performed':True,'resumed':True,'duration_seconds':.35,'host_pid':11,
                      'controller_pids_alive_during_stall':[22]}
        self.rows = []
        for i in range(62):
            wall = 0. if i == 0 else .35+(i-1)*.01
            expired = i == 1
            feedback = {'kind':'feedback','watchdog':expired,'controllerWallTimeSeconds':wall,
                        'plantSampleTick':round(wall/.005),'commandExecutionFresh':not expired,
                        'cableExecutionEnabled':not expired,'platformExecutionEnabled':not expired}
            command = {'kind':'step','commandSequence':i+1,'cableValid':not expired,
                       'platformValid':not expired,'tensionTargets':[0.,0.,0.] if expired else [1.,2.,3.]}
            self.rows.append({'recordType':'frame','stage':'Normal','realTimeSeconds':wall,
                'frame':{'paddles':[{'valid':not expired}], 'platformRequest':{'valid':True},
                         'platformFeedback':{'valid':not expired},'closedLoop':{
                    'sessionId':'1','feedbackSequence':i+1,'feedbackPacketBase64':packet(feedback,i+1,1),
                    'commandSent':True,'commandPacketBase64':packet(command,i+1,1),
                    'feedbackFresh':not expired,'encoderConsumed':not expired}}})

    def verify(self):
        (self.out/'observations.jsonl').write_text(''.join(json.dumps(row)+'\n' for row in self.rows),encoding='utf-8')
        return verifier.verify_host_stall(self.out,self.probe)

    def test_computed_wave_target_does_not_authorize_expired_device_output(self):
        self.assertEqual(self.verify()['recovered_fresh_frames'],60)

    def test_expired_actual_platform_feedback_is_rejected(self):
        self.rows[1]['frame']['platformFeedback']['valid'] = True
        with self.assertRaisesRegex(ValueError,'device output'): self.verify()

    def test_expired_outgoing_platform_command_is_rejected(self):
        t=self.rows[1]['frame']['closedLoop']
        _,command=verifier.envelope(t['commandPacketBase64'],'1','step')
        command['platformValid']=True; t['commandPacketBase64']=packet(command,2,1)
        with self.assertRaisesRegex(ValueError,'active targets'): self.verify()

    def test_missing_recovery_is_rejected(self):
        self.rows=self.rows[:10]
        with self.assertRaisesRegex(ValueError,'did not recover'): self.verify()


class MotionAudioEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.out = Path(self.directory.name)
        self.physics = {'frame': {'timeSeconds': 1.25, 'paddles': [{'valid': True}]}, 'wettedFraction': .5}
        self.observation = {'motionAudioSourceTime': 1.25, 'motionAudioFrames': 125, 'woodImpacts': 0, 'woodCreaks': 1,
                            'strokeWaterLevel': .2, 'hullWaterLevel': .3, 'strokeAudioRms': .01, 'woodAudioRms': .002}

    def capture(self, silent=False):
        frames, rate = 66150, 22050
        cycle = b''.join(struct.pack('<h', 0 if silent else (i - 16) * 100) for i in range(32))
        with wave.open(str(self.out / 'listener_mix.wav'), 'wb') as stream:
            stream.setparams((1, 2, rate, frames, 'NONE', 'not compressed'))
            stream.writeframes((cycle * (frames // 32 + 1))[:frames * 2])
        self.report = {'audioCapture': 'listener_mix.wav', 'audioSampleFrames': frames, 'audioChannels': 1,
                       'audioSampleRate': rate, 'audioClippedSamples': 0, 'audioPeak': 0 if silent else 1600 / 32767.0,
                       'audioCaptureSha256': hashlib.sha256((self.out / 'listener_mix.wav').read_bytes()).hexdigest()}

    def test_actual_pcm_and_same_physics_source_are_accepted(self):
        self.capture()
        verifier.verify_motion_audio_render(self.observation, self.physics)
        self.assertGreater(verifier.verify_audio_capture(self.out, self.report)['rms'], 0)

    def test_old_audio_motion_source_is_rejected(self):
        self.observation['motionAudioSourceTime'] -= .01
        with self.assertRaisesRegex(ValueError, 'source'): verifier.verify_motion_audio_render(self.observation, self.physics)

    def test_dry_stroke_rush_is_rejected(self):
        self.physics['wettedFraction'] = 0
        with self.assertRaisesRegex(ValueError, 'dry'): verifier.verify_motion_audio_render(self.observation, self.physics)

    def test_nonfinite_source_rms_is_rejected(self):
        self.observation['woodAudioRms'] = float('nan')
        with self.assertRaisesRegex(ValueError, 'invalid motion audio'): verifier.verify_motion_audio_render(self.observation, self.physics)

    def test_changed_audio_file_is_rejected(self):
        self.capture()
        with (self.out / 'listener_mix.wav').open('ab') as stream: stream.write(b'changed')
        with self.assertRaisesRegex(ValueError, 'SHA256'): verifier.verify_audio_capture(self.out, self.report)

    def test_silent_pcm_is_rejected(self):
        self.capture(silent=True)
        with self.assertRaisesRegex(ValueError, 'silent'): verifier.verify_audio_capture(self.out, self.report)


class IntentReferenceEvidenceTests(unittest.TestCase):
    def record(self, sample, phase, x=0., angle=0., velocity=0., spin=0., previous=None, paused=False, enabled=True):
        telemetry = dict(intentPaused=paused, commandSourceFresh=enabled)
        feedback = dict(controllerTimeSeconds=sample)
        command = dict(active=True,cableValid=enabled,platformValid=enabled,
            desiredGripPosition=[x,.6,0.],desiredGripRotation=[0.,math.sin(angle/2),0.,math.cos(angle/2)],
            desiredGripVelocity=[velocity,0.,0.],desiredGripAngularVelocity=[0.,spin,0.])
        return verifier.verify_intent_reference(phase,telemetry,feedback,command,45.,previous)

    def test_same_physical_reference_survives_extra_host_calls(self):
        first=self.record(1.,0.)
        moving=self.record(1.02,.015,.02,.04,1.,2.,first)
        for _ in range(5):moving=self.record(1.02,.015,.02,.04,1.,2.,moving)
        self.assertEqual(moving['phase'],.015)

    def test_repeated_physical_sample_cannot_advance_phase(self):
        first=self.record(1.,0.)
        with self.assertRaisesRegex(ValueError,'physical sample clock'):
            self.record(1.,.0075,previous=first)

    def test_wrong_linear_or_angular_derivative_clock_is_rejected(self):
        first=self.record(1.,0.)
        for velocity,spin in ((4.,2.),(1.,4.)):
            with self.subTest(velocity=velocity,spin=spin), self.assertRaisesRegex(ValueError,'derivative clock'):
                self.record(1.02,.015,.02,.04,velocity,spin,first)

    def test_pause_recovery_and_long_gap_hold_the_reference(self):
        first=self.record(1.,0.)
        moving=self.record(1.02,.015,.02,.04,1.,2.,first)
        paused=self.record(1.03,.015,.02,.04,previous=moving,paused=True)
        recovered=self.record(2.,.015,.02,.04,previous=paused)
        self.record(2.02,.03,.04,.08,1.,2.,recovered)
        with self.assertRaisesRegex(ValueError,'physical sample clock'):
            self.record(2.,.03,.02,.04,previous=paused)
        self.record(3.,.015,.02,.04,previous=recovered)

    def test_inactive_angular_feedforward_is_rejected(self):
        first=self.record(1.,0.)
        with self.assertRaisesRegex(ValueError,'retained hand feedforward'):
            self.record(1.,0.,spin=1.,previous=first,paused=True,enabled=False)


class LocalDragEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.command = dict(kind='step', commandSequence=7, active=True, cableValid=True,
            dragFeedbackEnabled=True, dragDissipationW=40., requestForce=[40.,0,0], requestTorque=[0.]*3,
            sourceGripVelocity=[-1.,0,0], sourceGripAngularVelocity=[0.]*3, shaftLength=1.,
            tensionTargets=[40.], minTensions=[5.], maxTensions=[300.])
        self.feedback = dict(commandSequence=7, cableExecutionEnabled=True, dragFeedbackActive=True,
            gripVelocity=[1.,0,0], gripAngularVelocity=[0.]*3, dragTargetScale=0.,
            effectiveTensionTargets=[5.], tensionN=[35.625])

    def verify(self):
        telemetry = dict(sessionId='123', acknowledgedCommandPacketBase64=packet(self.command,7,123))
        return verifier.verify_drag_feedback(telemetry, self.feedback)

    def test_original_packet_reduction_keeps_actual_motor_decay_distinct(self):
        self.assertEqual(self.verify(), (True, True))
        self.assertGreater(self.feedback['tensionN'][0], self.feedback['effectiveTensionTargets'][0])

    def test_wrong_reduction_or_effective_target_is_rejected(self):
        original = copy.deepcopy(self.feedback)
        for change in ({'dragTargetScale': .25}, {'effectiveTensionTargets': [40.]},
                       {'effectiveTensionTargets': [0.]}, {'dragFeedbackActive': False}):
            self.feedback = dict(original, **change)
            with self.subTest(change=change), self.assertRaises(ValueError): self.verify()

    def test_moving_water_world_power_is_permitted(self):
        self.command['sourceGripVelocity'] = [0.]*3
        self.feedback.update(gripVelocity=[.5,0,0], dragTargetScale=.25, effectiveTensionTargets=[13.75])
        self.assertEqual(self.verify(), (True, True))

    def test_expiry_has_zero_target_but_may_have_residual_tension(self):
        self.feedback.update(cableExecutionEnabled=False, dragFeedbackActive=False,
                             dragTargetScale=1., effectiveTensionTargets=[0.])
        self.assertEqual(self.verify(), (False, False))
        self.feedback['effectiveTensionTargets'] = [5.]
        with self.assertRaisesRegex(ValueError,'effective target'): self.verify()

    def source(self, time, counts, previous=None):
        feedback = dict(controllerTimeSeconds=time, countsPerMeter=10000., countsPerDegree=100.,
                        rawEncoderCounts=counts)
        frame = dict(closedLoop=dict(encoderConsumed=True), feedbackRequests=[dict(dissipativePowerW=40.)])
        return verifier.verify_drag_source(frame, feedback, self.command, previous)

    def test_linear_source_is_derived_from_distinct_encoder_counts(self):
        first = self.source(1., [6000,-3000,0,0,0,0])
        self.command['sourceGripVelocity'] = [0.,0.,1.]
        second = self.source(1.02, [6000,-3000,200,0,0,0], first)
        self.source(1.02, [6000,-3000,200,0,0,0], second)
        self.command['sourceGripVelocity'] = [0.,0.,4.]
        with self.assertRaisesRegex(ValueError,'source twist'):
            self.source(1.02, [6000,-3000,200,0,0,0], second)

    def test_small_angle_source_uses_radians_and_controller_interval(self):
        first = self.source(1., [6000,-3000,0,0,0,0])
        self.command['sourceGripVelocity'] = [0.]*3
        self.command['sourceGripAngularVelocity'] = [0., math.radians(.01)/.015, 0.]
        self.source(1.015, [6000,-3000,0,0,1,0], first)
        self.command['sourceGripAngularVelocity'] = [0., .01/.015, 0.]
        with self.assertRaisesRegex(ValueError,'source twist'):
            self.source(1.015, [6000,-3000,0,0,1,0], first)

    def test_source_work_cannot_be_changed_in_dispatch(self):
        self.command['dragDissipationW'] = 80.
        with self.assertRaisesRegex(ValueError,'physical source'):
            self.source(1., [6000,-3000,0,0,0,0])


class ArtifactPublicationTests(unittest.TestCase):
    def test_complete_original_bytes_are_published_and_sources_retained(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);live=root/'live';out=root/'out';live.mkdir();out.mkdir()
            data=b'raw\x00packet\xff\n' * 10001
            (live/'observations.jsonl').write_bytes(data)
            published=verifier.publish_runtime_artifacts(live,out)
            self.assertEqual(published,{'observations.jsonl':hashlib.sha256(data).hexdigest()})
            self.assertEqual((out/'observations.jsonl').read_bytes(),data)
            self.assertEqual((live/'observations.jsonl').read_bytes(),data)

    def test_existing_evidence_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);live=root/'live';out=root/'out';live.mkdir();out.mkdir()
            (live/'result.json').write_bytes(b'new');(out/'result.json').write_bytes(b'old')
            with self.assertRaisesRegex(ValueError,'already exists'): verifier.publish_runtime_artifacts(live,out)
            self.assertEqual((out/'result.json').read_bytes(),b'old')
            self.assertEqual((live/'result.json').read_bytes(),b'new')

    def test_failed_copy_preserves_original_capture(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);live=root/'live';out=root/'out';live.mkdir();out.mkdir()
            (live/'listener_mix.wav').write_bytes(b'original PCM')
            with mock.patch.object(verifier.shutil,'copyfileobj',side_effect=OSError('disk full')):
                with self.assertRaisesRegex(OSError,'disk full'): verifier.publish_runtime_artifacts(live,out)
            self.assertEqual((live/'listener_mix.wav').read_bytes(),b'original PCM')


class StartupVisualGeometryTests(unittest.TestCase):
    def compound_report(self):
        report = self.report()
        report['schemaVersion'] = 6
        report['hullSource'] = {'schemaVersion': 2, 'parts': [
            {'name': 'port', 'vertices': 8, 'triangles': 12}, {'name': 'starboard', 'vertices': 8, 'triangles': 12}]}
        for step in report['startupVisualGeometry']:
            step['hulls'] = [{'name': part['name'], 'kind': 'MeshCollider', 'instanceId': i + 1,
                              'convex': True, 'sameBody': True, 'vertices': 8, 'triangles': 12,
                              'boundsSize': {'x': .1, 'y': .2, 'z': 1.0}}
                             for i, part in enumerate(report['hullSource']['parts'])]
        return report

    def test_compound_is_observed_in_full_on_every_initial_step(self):
        report = self.compound_report()
        self.assertEqual(verifier.verify_startup_visual_geometry(report)['native_hull_parts'], 2)
        report['startupVisualGeometry'][0]['hulls'].pop()
        with self.assertRaisesRegex(ValueError, 'missed a generated'):
            verifier.verify_startup_visual_geometry(report)

    def test_compound_cannot_silently_fall_back_or_replace_a_part(self):
        report = self.compound_report()
        del report['hullSource']
        with self.assertRaisesRegex(ValueError, 'missing production'):
            verifier.verify_startup_visual_geometry(report)
        report = self.compound_report()
        report['startupVisualGeometry'][2]['hulls'][0]['instanceId'] = 9
        with self.assertRaisesRegex(ValueError, 'changed during startup'):
            verifier.verify_startup_visual_geometry(report)

    def test_single_native_part_cannot_exceed_cooking_limit(self):
        report = self.compound_report()
        report['hullSource']['parts'][0]['triangles'] = 256
        for step in report['startupVisualGeometry']:
            step['hulls'][0]['triangles'] = 256
        with self.assertRaisesRegex(ValueError, 'invalid native part'):
            verifier.verify_startup_visual_geometry(report)

    def report(self):
        return {'startupVisualGeometry': [
            {'fixedTimeSeconds': (i + 1) * .01, 'automaticPhysics': True,
             'boatPosition': dict(ZERO), 'boatVelocity': dict(ZERO), 'colliders': []}
            for i in range(4)]}

    def collider(self, enabled):
        return {'name': 'BasePlate', 'owner': 'platform', 'enabled': enabled,
                'active': True, 'collidesWithBoatLayer': True, 'overlapsHull': enabled,
                'penetrationM': .2 if enabled else 0., 'worldPosition': dict(ZERO),
                'separationDirection': dict(ZERO)}

    def test_visuals_without_colliders_and_disabled_pending_destroy_are_valid(self):
        report = self.report()
        verifier.verify_startup_visual_geometry(report)
        report['startupVisualGeometry'][0]['colliders'].append(self.collider(False))
        self.assertEqual(verifier.verify_startup_visual_geometry(report)['pending_disabled_colliders'], 1)

    def test_visible_or_camera_hidden_physics_colliders_are_rejected(self):
        for collides in (True, False):
            report = self.report()
            collider = self.collider(True); collider['collidesWithBoatLayer'] = collides
            report['startupVisualGeometry'][0]['colliders'].append(collider)
            with self.assertRaisesRegex(ValueError, 'visual collider active'):
                verifier.verify_startup_visual_geometry(report)

    def test_missing_or_late_startup_does_not_establish_initial_safety(self):
        with self.assertRaisesRegex(ValueError, 'missing'):
            verifier.verify_startup_visual_geometry({})
        report = self.report()
        for item in report['startupVisualGeometry']: item['fixedTimeSeconds'] += 1
        with self.assertRaisesRegex(ValueError, 'began late'):
            verifier.verify_startup_visual_geometry(report)


if __name__ == '__main__': unittest.main()
