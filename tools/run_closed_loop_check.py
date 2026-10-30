"""Validate the production UDP mechanical loop in Editor Play or the shipped Player.

The verifier and Editor require Python >= 3.10. Player acceptance requires the
bundled isolated controller runtime and runs with a PATH containing no Python.

Usage: python tools/run_closed_loop_check.py [--player Builds/DragonBoatVR/DragonBoatVR.exe]
       python tools/run_closed_loop_check.py --verify ARTIFACT_DIRECTORY
"""
import argparse
import base64
import ctypes
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import uuid
import zlib
import wave
from array import array

from run_all_checks import PROJECT, WORKSPACE, assess_log, read_lines, resolve_unity
from run_runtime_check import source_hashes
from unity_execution import unity_project_guard

REQUIRED = {
    'normal_player_loop_three_actual_strokes', 'independent_udp_process_and_raw_encoder',
    'received_motor_feedback_drives_frame', 'loaded_actual_motion_differs_from_desired',
    'platform_received_pose_and_length_feedback', 'command_disconnect_100ms_zero_input_and_force',
    'commands_resume_recovers_feedback', 'experience_stop_and_clean_restart',
    'normal_render_and_completed_evidence',
    'execution_pose_reaches_render',
    'presentation_views_use_sky_and_own_reflection',
    'script_intent_continuity_on_recovery',
    'water_interaction_follows_completed_physics',
    'outgoing_actuator_commands_use_fresh_observations',
}

def require(condition, message):
    if not condition:
        raise ValueError(message)

def strict_object(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, 'duplicate JSON field: ' + key)
        value[key] = item
    return value

def parse(text):
    def finite_float(token):
        value = float(token)
        require(math.isfinite(value), 'nonfinite JSON number: ' + token)
        return value
    return json.loads(text, object_pairs_hook=strict_object,
                      parse_float=finite_float,
                      parse_constant=lambda x: (_ for _ in ()).throw(ValueError('nonfinite JSON: ' + x)))

def vector(value):
    return [value[k] for k in ('x', 'y', 'z')] if isinstance(value, dict) else value

def near(a, b, tolerance=1e-4):
    return len(a) == len(b) and all(math.isfinite(x) and math.isfinite(y) and abs(x-y) <= tolerance for x, y in zip(a,b))

def float32(values):
    # UDP JSON doubles cross a declared C# float[] boundary. Compare that exact
    # quantization, rather than widening the geometry or sensor tolerances.
    return [struct.unpack('<f', struct.pack('<f', x))[0] for x in values]

def magnitude(value):
    values = vector(value)
    require(len(values) == 3 and all(math.isfinite(x) for x in values), 'invalid finite vector')
    return math.sqrt(sum(x*x for x in values))

def assert_zero_input(sample, raw, filtered, request):
    require(sample['valid'] is False and request['valid'] is False, 'expired/stopped input or request remains valid')
    require(all(magnitude(v) <= 1e-5 for v in (raw, filtered, request['forceWorldN'], request['torqueAtGripWorldNm'])),
            'expired/stopped input still produces force or torque')
    require(abs(request.get('dissipativePowerW', 0.0)) <= 1e-5,
            'expired/stopped input retained drag dissipation')


def envelope(encoded, session, kind, sequence=None):
    require(isinstance(encoded, str) and bool(encoded), 'missing original ' + kind + ' packet')
    raw = base64.b64decode(encoded, validate=True)
    require(28 <= len(raw) <= 65507, 'invalid ' + kind + ' packet size')
    magic, version, actual, nonce, size = struct.unpack_from('<4sIIQI', raw)
    require(magic == b'DBCL' and version == 1 and nonce == int(session)
            and (sequence is None or sequence == actual), kind + ' packet identity mismatch')
    require(size + 28 == len(raw) and struct.unpack_from('<I', raw, len(raw)-4)[0] == zlib.crc32(raw[:-4]),
            kind + ' packet CRC/length mismatch')
    payload = parse(raw[24:-4].decode('utf-8'))
    require(payload.get('kind') == kind, 'wrong packet kind')
    if kind == 'step': require(payload.get('commandSequence') == actual, 'command body/envelope sequence mismatch')
    return actual, payload


def age(value, name, absent=False):
    require(type(value) in (int, float) and math.isfinite(value)
            and (value >= 0 or (absent and value == -1)), 'invalid ' + name + ' age')
    return value


def verify_dispatch(frame, feedback, commands):
    t = frame['closedLoop']
    for name in ('commandSent', 'commandSourceFresh', 'deviceFeedbackFresh'):
        require(type(t.get(name)) is bool, 'missing output-boundary evidence: ' + name)
    received = age(t.get('deviceFeedbackReceivedAgeSeconds'), 'device observation', True)
    ack = age(t.get('deviceFeedbackAckAgeSeconds'), 'device acknowledgement', True)
    available = feedback is not None and feedback['valid'] and not feedback['watchdog']
    device_fresh = bool(available and 0 <= received <= .1 and 0 <= ack <= .1)
    require(t['deviceFeedbackFresh'] == device_fresh, 'device output freshness differs from its own observation ages')
    if feedback is not None:
        require(received >= t['receivedAgeSeconds'] and ack >= t['commandAckAgeSeconds'],
                'device output observation clock predates physics input')
    if not t['commandSent']:
        require(not t.get('commandPacketBase64') and not t['commandSourceFresh'], 'unsent command retains send evidence')
        return False
    seq, command = envelope(t.get('commandPacketBase64'), t['sessionId'], 'step', t.get('commandSequence'))
    key = (t['sessionId'], seq)
    require(key not in commands, 'a sent command sequence was reused')
    commands[key] = t['commandPacketBase64']
    require(command.get('sourceFeedbackSequence') == (t['feedbackSequence'] if feedback is not None else 0)
            and command.get('sourceSampleTick') == (feedback['plantSampleTick'] if feedback is not None else 0),
            'outgoing command used another source identity or sample tick')
    received = age(t.get('commandSourceReceivedAgeSeconds'), 'dispatch observation', True)
    ack = age(t.get('commandSourceAckAgeSeconds'), 'dispatch acknowledgement', True)
    if feedback is not None:
        require(received >= t['receivedAgeSeconds'] and ack >= t['commandAckAgeSeconds'],
                'command dispatch clock predates physics input')
    fresh = bool(available and t['encoderConsumed'] and 0 <= received <= .1 and 0 <= ack <= .1)
    require(t['commandSourceFresh'] == fresh, 'outgoing source freshness differs from source ages')
    require(all(type(command.get(name)) is bool for name in ('active', 'cableValid', 'platformValid')),
            'outgoing command validity fields are missing')
    if not fresh:
        require(not command['cableValid'] and not command['platformValid'], 'expired source authorized an outgoing actuator command')
        require(magnitude(command['desiredGripVelocity']) == 0, 'expired source advanced the hand intention')
        require(magnitude(command.get('desiredGripAngularVelocity', [0.]*3)) == 0,
                'expired source advanced the angular hand intention')
    if command['cableValid']:
        request = frame['feedbackRequests'][0]
        require(request['valid'], 'outgoing cable command bypassed rejected request')
        q = rotation_values(t['observationBoatRotation'])
        inverse = [-q[0], -q[1], -q[2], q[3]]
        require(near(command['requestForce'], rotate(inverse, vector(request['forceWorldN'])))
                and near(command['requestTorque'], rotate(inverse, vector(request['torqueAtGripWorldNm']))),
                'outgoing requested wrench differs from the source water reaction')
    else:
        require(all(x == 0 for x in command['tensionTargets'])
                and magnitude(command['requestForce']) == magnitude(command['requestTorque']) == 0,
                'invalid outgoing cable command retained active targets')
    if command['platformValid']:
        require(frame['platformRequest']['valid'], 'outgoing platform command bypassed rejected mapping')
    return not fresh


def verify_received_authorization(t, feedback, commands, observations):
    _, command = envelope(t.get('acknowledgedCommandPacketBase64'), t['sessionId'], 'step', feedback['commandSequence'])
    known = commands.get((t['sessionId'], feedback['commandSequence']))
    first = next((sequence for (session, sequence) in commands if session == t['sessionId']), None)
    require(known is not None or first is None or ((feedback['commandSequence']-first)&0xffffffff) >= 0x80000000,
            'receiver acknowledged an unobserved outgoing command')
    require(known is None or known == t['acknowledgedCommandPacketBase64'], 'acknowledged command differs from the actual outgoing packet')
    require(feedback.get('commandSourceFeedbackSequence') == command.get('sourceFeedbackSequence')
            and feedback.get('commandSourceSampleTick') == command.get('sourceSampleTick'), 'receiver used another command source')
    source_age = age(feedback.get('commandSourceAgeSeconds'), 'receiver source', True)
    command_age = age(feedback.get('commandAgeSeconds'), 'receiver command')
    actuating = command['active'] and (command['cableValid'] or command['platformValid'])
    if source_age >= 0:
        _, source = envelope(t.get('acknowledgedSourcePacketBase64'), t['sessionId'], 'feedback', command['sourceFeedbackSequence'])
        known_source = observations.get((t['sessionId'], command['sourceFeedbackSequence']))
        require(known_source is None or known_source == t['acknowledgedSourcePacketBase64'],
                'acknowledged source differs from the original received packet')
        verify_sample_clock(source, command['sourceFeedbackSequence'],
                            base64.b64decode(t['acknowledgedSourcePacketBase64']), None)
        require(source['plantSampleTick'] == command['sourceSampleTick'], 'receiver source sample tick differs from original packet')
        require(abs(feedback['controllerWallTimeSeconds'] - source['controllerWallTimeSeconds'] - source_age) <= 2e-6,
                'receiver source age differs from original observation clock')
        require(source_age + 2e-6 >= command_age, 'command receipt predates its source observation')
        if actuating:
            require(source['valid'] and not source['watchdog'] and source_age - command_age <= .1,
                    'receiver accepted an expired or invalid source command')
    elif actuating:
        require(False, 'receiver accepted an actuator command without an issued source')
    execution = bool(feedback['valid'] and not feedback['watchdog'] and command_age <= .1
                     and (source_age == -1 or source_age <= .1))
    require(feedback.get('commandExecutionFresh') is execution
            and feedback.get('cableExecutionEnabled') is (execution and command['cableValid'])
            and feedback.get('platformExecutionEnabled') is (execution and command['platformValid']),
            'receiver execution flags bypassed source or command expiry')


def verify_drag_feedback(telemetry, feedback):
    """Recompute current local setpoints from original packets, separate from
    lagged actual tensions. Never import the production controller's helper."""
    _, command = envelope(telemetry['acknowledgedCommandPacketBase64'], telemetry['sessionId'],
                          'step', feedback['commandSequence'])
    enabled = feedback['cableExecutionEnabled'] and command['dragFeedbackEnabled']
    require(type(feedback.get('dragFeedbackActive')) is bool and feedback['dragFeedbackActive'] == enabled,
            'local drag activation bypassed cable authorization')
    magnitude(feedback['gripVelocity']); magnitude(feedback['gripAngularVelocity'])
    gain = 1.0
    if enabled:
        dissipation = command['dragDissipationW']
        require(math.isfinite(dissipation) and dissipation > 0, 'invalid source drag dissipation')
        power_change = sum(w * (actual-source) for w, actual, source in
            zip(command['requestForce'] + command['requestTorque'],
                feedback['gripVelocity'] + feedback['gripAngularVelocity'],
                command['sourceGripVelocity'] + command['sourceGripAngularVelocity']))
        gain = min(1.0, max(0.0, 1.0-power_change/dissipation))**2
    require(math.isfinite(feedback.get('dragTargetScale', float('nan')))
            and abs(feedback['dragTargetScale']-gain) <= 1e-9,
            'local drag scale differs from the actual motion and original source work')
    requested = command['tensionTargets']
    limits = command['maxTensions']; floors = command['minTensions']
    require(len(requested) == len(limits) == len(floors)
            and all(0 <= low <= high for low, high in zip(floors, limits)), 'invalid drag tension bounds')
    effective = [0.0]*len(requested) if not feedback['cableExecutionEnabled'] else [
        min(high, max(0.0, low+(target-low)*gain if enabled else target))
        for low, high, target in zip(floors, limits, requested)]
    require(near(feedback.get('effectiveTensionTargets', []), effective, 1e-8),
            'reported effective target bypassed drag reduction or motor limits')
    # Actual tension may still be above this target: its separate response and
    # slew are part of the plant, not an instantaneous force projection.
    return bool(enabled), bool(enabled and gain < 1.0-1e-8)


def verify_drag_source(frame, feedback, command, previous):
    """Reconstruct measured grip twist from distinct original encoder poses."""
    consumed = frame['closedLoop']['encoderConsumed']
    current = previous
    if consumed:
        require(feedback is not None, 'drag source has no encoder observation')
        sample = feedback['controllerTimeSeconds']
        local = [x/feedback['countsPerMeter'] for x in feedback['rawEncoderCounts'][:3]]
        rotation = euler_q([x/feedback['countsPerDegree'] for x in feedback['rawEncoderCounts'][3:]])
        if previous is None or sample != previous['sample']:
            velocity = spin = None
            if previous is not None:
                elapsed = sample-previous['sample']
                velocity = spin = [0.0]*3
                if 1e-6 < elapsed <= .1:
                    velocity = [(a-b)/elapsed for a, b in zip(local, previous['local'])]
                    old = previous['rotation']
                    delta = mul(rotation, [-old[0], -old[1], -old[2], old[3]])
                    if delta[3] < 0: delta = [-x for x in delta]
                    sine = math.sqrt(sum(x*x for x in delta[:3]))
                    scale = 2*math.atan2(sine, delta[3])/sine/elapsed if sine > 1e-12 else 0.0
                    spin = [x*scale for x in delta[:3]]
            current = dict(sample=sample, local=local, rotation=rotation, velocity=velocity, spin=spin)
    if command is None:
        return current
    require(type(command.get('dragFeedbackEnabled')) is bool, 'missing local drag command contract')
    source_velocity = command.get('sourceGripVelocity', []); source_spin = command.get('sourceGripAngularVelocity', [])
    magnitude(source_velocity); magnitude(source_spin)
    power = frame['feedbackRequests'][0].get('dissipativePowerW')
    require(type(power) in (int, float) and math.isfinite(power) and power >= 0, 'missing physical drag work')
    enabled = command['active'] and command['cableValid'] and power > 1e-8
    require(command['dragFeedbackEnabled'] == enabled, 'drag command bypassed its physical source')
    require(abs(command.get('dragDissipationW', -1) - (power if command['cableValid'] else 0)) <= 1e-6,
            'drag command work differs from the physical source')
    if not command['cableValid']:
        require(magnitude(source_velocity) == magnitude(source_spin) == 0,
                'invalid drag command retained a motion source')
    elif consumed and current is not None and current['velocity'] is not None:
        arm = rotate(current['rotation'], [0, command['shaftLength'], 0])
        wx, wy, wz = current['spin']; ax, ay, az = arm
        rotational = [wy*az-wz*ay, wz*ax-wx*az, wx*ay-wy*ax]
        grip = [a+b for a,b in zip(current['velocity'], rotational)]
        require(near(source_velocity, grip, .002) and near(source_spin, current['spin'], .002),
                'drag source twist differs from distinct encoder observations')
    return current


def verify_motor_wrench(frame, feedback):
    t, cable = frame['closedLoop'], frame['cableFeedback']
    for source, target in (('forceLocal', 'forceWorldN'), ('torqueLocal', 'torqueAtGripWorldNm')):
        expected = rotate(t['observationBoatRotation'], float32(feedback[source])) if t['deviceFeedbackFresh'] else [0.]*3
        require(math.dist(expected, vector(cable[target])) <= .0001, 'received motor wrench vector differs from frame: ' + target)

def verify_sample_clock(feedback, sequence, packet_bytes, previous):
    """Validate the state clock independently of the host's freshness verdict."""
    names=('controllerTimeSeconds','controllerWallTimeSeconds','plantSampleTick','plantStepSeconds','plantDroppedTicks')
    require(all(name in feedback for name in names), 'controller sample/wall clock evidence missing')
    sample,wall=feedback['controllerTimeSeconds'],feedback['controllerWallTimeSeconds']
    tick,step,dropped=feedback['plantSampleTick'],feedback['plantStepSeconds'],feedback['plantDroppedTicks']
    require(type(tick) is int and tick>=0 and type(dropped) is int and dropped>=0, 'invalid plant sample/drop tick count')
    require(all(type(x) in (float,int) and math.isfinite(x) and x>=0 for x in (sample,wall,step)), 'invalid controller clock value')
    require(abs(step-.005)<=1e-12, 'plant step differs from the 5 ms integration contract')
    require(math.isclose(sample,tick*step,rel_tol=1e-12,abs_tol=1e-12), 'sample clock does not match actually integrated plant ticks')
    require(dropped==0, 'plant scheduler dropped unintegrated wall time')
    # With no dropped ticks the most recent integrated state is on the 5 ms
    # grid at or before this independent monotonic observation, never after it.
    require(-1e-9<=wall-sample<step+1e-9, 'controller wall/sample clocks disagree with the integration schedule')
    encoded=base64.b64decode(feedback['encoderPacketBase64'],validate=True)
    require(len(encoded)==52 and struct.unpack_from('<I',encoded,48)[0]==zlib.crc32(encoded[:48]), 'received DBEP clock packet CRC/length mismatch')
    require(struct.unpack_from('<III',encoded)==(0x44424550,1,sequence), 'received DBEP clock packet identity mismatch')
    device_time=struct.unpack_from('<Q',encoded,12)[0]
    require(device_time==int(sample*1_000_000), 'DBEP device time differs from plant sample clock')
    if previous is not None:
        if sequence==previous['sequence']:
            require(packet_bytes==previous['packet'], 'held feedback identity has changed packet bytes or sample clock')
            previous['held_frames']+=1
            return previous
        require(0<(sequence-previous['sequence'])&0xffffffff<0x80000000, 'out of order feedback consumed')
        require(tick>previous['tick'] and sample>previous['sample'] and wall>previous['wall'],
                'new feedback repeats or reverses its plant sample or wall clock')
        require(device_time>previous['device_time'], 'new feedback repeats or reverses its DBEP timestamp')
    return {'sequence':sequence,'packet':packet_bytes,'tick':tick,'sample':sample,'wall':wall,'device_time':device_time,
            'distinct_packets':1+(previous['distinct_packets'] if previous else 0),
            'held_frames':previous['held_frames'] if previous else 0,
            'wall_phase_observations':int(wall!=sample)+(previous['wall_phase_observations'] if previous else 0),
            'maximum_wall_phase_seconds':max(wall-sample,previous['maximum_wall_phase_seconds'] if previous else 0),
            'first_sample':previous['first_sample'] if previous else sample,
            'first_wall':previous['first_wall'] if previous else wall}

def png_evidence(path):
    data = path.read_bytes()
    require(data[:8] == b'\x89PNG\r\n\x1a\n', 'invalid screenshot PNG signature')
    offset, compressed, dimensions, ended = 8, bytearray(), None, False
    while offset < len(data):
        require(offset + 12 <= len(data), 'truncated screenshot chunk')
        size = struct.unpack_from('>I', data, offset)[0]
        kind = data[offset+4:offset+8]
        end = offset + 12 + size
        require(end <= len(data), 'truncated screenshot payload')
        payload = data[offset+8:offset+8+size]
        require(struct.unpack_from('>I', data, offset+8+size)[0] == zlib.crc32(kind+payload), 'screenshot chunk CRC mismatch')
        if kind == b'IHDR':
            require(dimensions is None and size == 13, 'invalid screenshot IHDR')
            dimensions = struct.unpack_from('>II', payload)
        if kind == b'IDAT': compressed.extend(payload)
        if kind == b'IEND': ended = True; require(end == len(data), 'data after screenshot IEND')
        offset = end
    require(ended and dimensions and min(dimensions) >= 200, 'missing or undersized rendered screenshot')
    pixels = zlib.decompress(compressed)
    require(len(pixels) >= dimensions[0]*dimensions[1] and len(set(pixels)) >= 16, 'blank or incomplete rendered screenshot')
    return {'width': dimensions[0], 'height': dimensions[1], 'sha256': hashlib.sha256(data).hexdigest()}

def player_hashes(executable):
    root = executable.resolve().parent
    result = {}
    for path in sorted(p for p in root.rglob('*') if p.is_file()):
        digest = hashlib.sha256()
        with path.open('rb') as stream:
            for chunk in iter(lambda: stream.read(1024*1024), b''): digest.update(chunk)
        result[path.relative_to(root).as_posix()] = digest.hexdigest()
    return result

def rotate(q, v):
    x, y, z, w = [q[k] for k in ('x','y','z','w')] if isinstance(q, dict) else q
    tx, ty, tz = 2*(y*v[2]-z*v[1]), 2*(z*v[0]-x*v[2]), 2*(x*v[1]-y*v[0])
    return [v[0]+w*tx+y*tz-z*ty, v[1]+w*ty+z*tx-x*tz, v[2]+w*tz+x*ty-y*tx]

def mul(a,b):
    x,y,z,w=a; X,Y,Z,W=b
    return [w*X+x*W+y*Z-z*Y,w*Y-x*Z+y*W+z*X,w*Z+x*Y-y*X+z*W,w*W-x*X-y*Y-z*Z]


def rotation_values(q):
    values = [q[k] for k in ('x', 'y', 'z', 'w')] if isinstance(q, dict) else q
    require(len(values) == 4 and all(math.isfinite(x) for x in values), 'invalid render quaternion')
    length = math.sqrt(sum(x*x for x in values))
    require(abs(length-1) < 1e-5, 'nonunit render quaternion')
    return [x/length for x in values]


def rotation_error(a, b):
    a, b = rotation_values(a), rotation_values(b)
    return math.degrees(2*math.acos(min(1, abs(sum(x*y for x, y in zip(a, b))))))


def verify_intent_reference(phase, telemetry, feedback, command, strokes_per_minute, previous):
    """Independently reconstruct reference cadence/derivatives from raw source time.

    Extra Unity catch-up calls must preserve the same timestamped reference.
    Pauses or gaps beyond 100 ms start a baseline without catching up.
    """
    paused = telemetry['intentPaused']
    require(type(paused) is bool, 'missing intent pause state')
    sample = feedback['controllerTimeSeconds'] if feedback is not None and not paused else None
    elapsed = sample-previous['sample'] if previous and sample is not None and previous['sample'] is not None else None
    advances = elapsed is not None and 1e-6 < elapsed <= .1
    repeated = elapsed == 0 and previous is not None
    if previous:
        step = (phase-previous['phase']+.5)%1-.5
        expected = strokes_per_minute/60*elapsed if advances else 0.
        require(abs(step-expected) <= 1e-6, 'intent phase differs from independent physical sample clock')
    velocity = previous['velocity'] if repeated else [0.]*3
    spin = previous['spin'] if repeated else [0.]*3
    grip = previous['grip'] if previous else None
    rotation = previous['rotation'] if previous else None
    if command is not None:
        require('desiredGripAngularVelocity' in command, 'angular hand reference missing from original command')
        actual_velocity = command['desiredGripVelocity']
        actual_spin = command['desiredGripAngularVelocity']
        magnitude(actual_velocity); magnitude(actual_spin)
        next_grip = command['desiredGripPosition']
        magnitude(next_grip)
        next_rotation = rotation_values(command['desiredGripRotation'])
        if advances and grip is not None:
            velocity = [(x-y)/elapsed for x,y in zip(next_grip,grip)]
            delta = mul(next_rotation, [-rotation[0],-rotation[1],-rotation[2],rotation[3]])
            if delta[3] < 0: delta = [-x for x in delta]
            sine = math.sqrt(sum(x*x for x in delta[:3]))
            factor = 2*math.atan2(sine,delta[3])/sine/elapsed if sine > 1e-12 else 0.
            spin = [x*factor for x in delta[:3]]
            for values,maximum in ((velocity,4.),(spin,30.)):
                size = magnitude(values)
                if size > maximum:
                    values[:] = [x*maximum/size for x in values]
        elif repeated and grip is not None:
            require(near(grip,next_grip,1e-6) and rotation_error(rotation,next_rotation) <= .0001,
                    'same physical sample changed the desired pose')
        elif previous is None:
            # The first recorded frame may follow earlier unrecorded startup
            # observations. It establishes only the independent audit baseline.
            velocity,spin = actual_velocity[:],actual_spin[:]
        enabled = telemetry['commandSourceFresh'] and command['active'] and (command['cableValid'] or command['platformValid'])
        if not enabled or paused:
            require(magnitude(actual_velocity) == magnitude(actual_spin) == 0,
                    'inactive or paused command retained hand feedforward')
        elif previous:
            require(near(actual_velocity,velocity,.0001), 'linear hand reference uses another derivative clock')
            require(near(actual_spin,spin,.0001), 'angular hand reference uses another derivative clock')
        grip,rotation = next_grip,next_rotation
    return dict(phase=phase,sample=sample,grip=grip,rotation=rotation,velocity=velocity,spin=spin)


def verify_render(observation, frame):
    require(observation.get('presentsToScreen') is True, 'offscreen render cannot establish presentation')
    require(frame is not None, 'render has no preceding physics observation')
    telemetry, sample = frame['closedLoop'], frame['paddles'][0]
    require(sample['valid'] and telemetry['encoderConsumed'] and telemetry['feedbackFresh'], 'render used invalid input')
    require(observation['feedbackSequence'] == telemetry['feedbackSequence'], 'render sequence differs from physics input')
    q = rotation_values(telemetry['observationBoatRotation'])
    inverse = [-q[0], -q[1], -q[2], q[3]]
    expected_position = rotate(inverse, [a-b for a, b in zip(vector(sample['bladePosition']), vector(telemetry['observationBoatPosition']))])
    expected_rotation = mul(inverse, rotation_values(sample['bladeRotation']))
    require(math.dist(vector(observation['expectedBladeLocal']), expected_position) <= 1e-5
            and rotation_error(observation['expectedRotationLocal'], expected_rotation) <= .05,
            'render expected pose was not the accepted physics pose')
    position_error = math.dist(vector(observation['actualBladeLocal']), expected_position)
    angle_error = rotation_error(observation['actualRotationLocal'], expected_rotation)
    require(math.isfinite(position_error) and position_error <= .0001 and angle_error <= .05,
            'visible paddle differs from received execution pose')
    return position_error, angle_error


def verify_presentation_render(observation):
    require(observation.get('cameraName') in ('MainCamera', 'TwinCamera'), 'unexpected presentation camera')
    require(observation.get('cameraClearFlags') == 1 and observation.get('skyboxAvailable') is True,
            'presentation camera does not render the scene sky')
    require(observation.get('cameraId') not in (None, 0)
            and observation.get('reflectionSourceCameraId') == observation['cameraId']
            and observation.get('reflectionFrame') == observation.get('unityFrameCount')
            and type(observation.get('reflectionFrame')) is int,
            'presentation camera used another view or an old frame reflection')


def interaction_count(observation, name):
    value = observation.get(name)
    require(type(value) is int and value >= 0, 'invalid interaction counter: ' + name)
    return value


def verify_interaction_reset(observation, previous):
    for name in ('waterEntries', 'waterExits', 'splashSounds', 'dripSounds', 'foamEpoch'):
        interaction_count(observation, name)
    require(observation['foamEpoch'] > 0, 'invalid interaction reset epoch')
    if previous is not None:
        require(observation['foamEpoch'] > previous['foamEpoch'], 'interaction reset epoch did not advance')
        require(observation['splashSounds'] >= previous['splashSounds']
                and observation['dripSounds'] >= previous['dripSounds'], 'audio lifetime counters reversed on reset')


def verify_interaction_render(observation, physics, reset):
    require(reset is not None and physics is not None, 'interaction render has no reset or physics source')
    require(observation.get('interactionAvailable') is True, 'interaction presentation is unavailable')
    audio_rms = observation.get('paddleAudioRms')
    require(type(audio_rms) in (int,float) and math.isfinite(audio_rms) and audio_rms >= 0,
            'invalid paddle audio RMS observation')
    for name in ('waterEntries', 'waterExits', 'splashSounds', 'dripSounds', 'foamEpoch',
                 'foamObservedFrames', 'foamPaddlePaintEvents', 'foamWakePaintEvents'):
        interaction_count(observation, name)
    for name in ('waterEntries', 'waterExits'):
        require(observation[name] == physics[name], 'render water event count differs from preceding physics: ' + name)
        require(observation[name] >= reset[name], 'water event counter reversed without a reset')
    require(observation['splashSounds'] - reset['splashSounds']
            == observation['waterEntries'] - reset['waterEntries'], 'splash sounds do not match water entry events')
    require(observation['dripSounds'] - reset['dripSounds']
            == observation['waterExits'] - reset['waterExits'], 'drip sounds do not match water exit events')
    require(observation['foamEpoch'] == reset['foamEpoch'], 'foam render used the wrong timeline epoch')
    require(observation['foamObservedFrames'] > 0, 'foam has no completed physics observation')
    require(type(observation.get('foamSourceTime')) in (int,float)
            and observation['foamSourceTime'] == physics['frame']['timeSeconds'],
            'foam source time differs from preceding physics')


def verify_motion_audio_render(observation, physics):
    require(observation.get('motionAudioSourceTime') == physics['frame']['timeSeconds'],
            'motion audio source differs from completed physics')
    require(interaction_count(observation, 'motionAudioFrames') > 0, 'motion audio has no physical observations')
    for name in ('woodImpacts', 'woodCreaks'):
        interaction_count(observation, name)
    for name in ('strokeWaterLevel', 'hullWaterLevel', 'strokeAudioRms', 'woodAudioRms'):
        value = observation.get(name)
        require(type(value) in (int, float) and math.isfinite(value) and value >= 0,
                'invalid motion audio observation: ' + name)
    require(observation['strokeWaterLevel'] <= 1 and observation['hullWaterLevel'] <= 1,
            'motion audio gain exceeds its range')
    if not physics['frame']['paddles'][0]['valid'] or physics['wettedFraction'] == 0:
        require(observation['strokeWaterLevel'] == 0, 'dry or invalid paddle retained water sound target')


def verify_audio_capture(out, report):
    require(report.get('audioCapture') == 'listener_mix.wav', 'missing listener audio capture')
    path = out / 'listener_mix.wav'
    require(path.is_file(), 'listener audio file missing')
    require(hashlib.sha256(path.read_bytes()).hexdigest() == report.get('audioCaptureSha256'), 'listener audio SHA256 mismatch')
    with wave.open(str(path), 'rb') as stream:
        frames, channels, rate = stream.getnframes(), stream.getnchannels(), stream.getframerate()
        require(stream.getsampwidth() == 2 and stream.getcomptype() == 'NONE', 'listener audio is not PCM16')
        raw = stream.readframes(frames)
    require(1 <= channels <= 8 and 8000 <= rate <= 192000 and frames >= rate * 2
            and frames == report.get('audioSampleFrames') and channels == report.get('audioChannels')
            and rate == report.get('audioSampleRate') and len(raw) == frames * channels * 2,
            'listener audio format, duration or sample count differs')
    samples = array('h'); samples.frombytes(raw)
    if sys.byteorder != 'little': samples.byteswap()
    peak = max(abs(x) for x in samples) / 32767.0
    rms = math.sqrt(sum(x * x for x in samples) / len(samples)) / 32767.0
    require(rms > 1e-6, 'listener output is silent')
    declared = report.get('audioPeak')
    require(type(declared) in (int, float) and math.isfinite(declared) and 0 < declared <= 1
            and abs(peak - declared) <= 1 / 32767.0 and report.get('audioClippedSamples') == 0,
            'listener output is clipped or its peak summary differs')
    return {'sample_frames': frames, 'channels': channels, 'sample_rate': rate, 'peak': peak, 'rms': rms}


def euler_q(e):
    a,b,c=[x*math.pi/360 for x in e]
    return mul(mul([0,math.sin(b),0,math.cos(b)],[math.sin(a),0,0,math.cos(a)]),[0,0,math.sin(c),math.cos(c)])

def process_alive(pid):
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.OpenProcess(0x1000, False, pid)
    if not handle: return False
    code = ctypes.c_ulong()
    try: return bool(kernel.GetExitCodeProcess(handle, ctypes.byref(code))) and code.value == 259
    finally: kernel.CloseHandle(handle)

def process_image(pid):
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.QueryFullProcessImageNameW.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_ulong)]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.OpenProcess(0x1000, False, pid)
    if not handle: return None
    try:
        buffer = ctypes.create_unicode_buffer(32768)
        size = ctypes.c_ulong(len(buffer))
        return buffer.value if kernel.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size)) else None
    finally: kernel.CloseHandle(handle)


def owned_player_window(pid):
    """Find only the visible top-level window of the process this runner owns."""
    user = ctypes.WinDLL('user32', use_last_error=True)
    callback_type = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p)
    user.EnumWindows.argtypes = [callback_type, ctypes.c_void_p]
    user.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
    user.IsWindowVisible.argtypes = [ctypes.c_void_p]
    user.GetClassNameW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
    class Rect(ctypes.Structure):
        _fields_ = [('left',ctypes.c_long),('top',ctypes.c_long),('right',ctypes.c_long),('bottom',ctypes.c_long)]
    user.GetClientRect.argtypes = [ctypes.c_void_p, ctypes.POINTER(Rect)]
    windows = []
    @callback_type
    def visit(window, unused):
        owner = ctypes.c_ulong()
        user.GetWindowThreadProcessId(window, ctypes.byref(owner))
        if owner.value == pid and user.IsWindowVisible(window):
            rect = Rect(); name = ctypes.create_unicode_buffer(256)
            user.GetClientRect(window, ctypes.byref(rect)); user.GetClassNameW(window, name, 256)
            area = (rect.right-rect.left)*(rect.bottom-rect.top)
            if area >= 256*256: windows.append((name.value == 'UnityWndClass', area, window))
        return 1
    user.EnumWindows(visit, None)
    return max(windows)[2] if windows else None


def show_owned_player_window(pid, window, minimized):
    user = ctypes.WinDLL('user32', use_last_error=True)
    user.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
    user.ShowWindowAsync.argtypes = [ctypes.c_void_p, ctypes.c_int]
    owner = ctypes.c_ulong()
    user.GetWindowThreadProcessId(window, ctypes.byref(owner))
    require(owner.value == pid, 'background probe window does not belong to this Player')
    user.ShowWindowAsync(window, 6 if minimized else 9)  # Minimize / restore this owned window only.
    if not minimized:
        # SW_RESTORE alone need not return keyboard focus. Attach input queues
        # transiently so the actual owned window, not a splash/other app, is raised.
        kernel = ctypes.WinDLL('kernel32')
        kernel.GetCurrentThreadId.restype = ctypes.c_ulong
        user.GetForegroundWindow.restype = ctypes.c_void_p
        user.AttachThreadInput.argtypes = [ctypes.c_ulong, ctypes.c_ulong, ctypes.c_int]
        user.SetForegroundWindow.argtypes = [ctypes.c_void_p]
        user.BringWindowToTop.argtypes = [ctypes.c_void_p]
        thread = kernel.GetCurrentThreadId()
        foreground = user.GetForegroundWindow()
        foreground_thread = user.GetWindowThreadProcessId(foreground, None) if foreground else 0
        attached = foreground_thread != 0 and foreground_thread != thread \
            and bool(user.AttachThreadInput(thread, foreground_thread, True))
        try:
            user.BringWindowToTop(window)
            user.SetForegroundWindow(window)
        finally:
            if attached: user.AttachThreadInput(thread, foreground_thread, False)


def player_window_is_minimized(window):
    user = ctypes.WinDLL('user32', use_last_error=True)
    user.IsIconic.argtypes = [ctypes.c_void_p]
    return bool(user.IsIconic(window))


def verify_background(out):
    report = parse((out/'result.json').read_text(encoding='utf-8-sig'))
    require(report.get('runInBackgroundAtStart') is True, 'Player background execution is disabled')
    events = report.get('focusEvents', [])
    require(events and events[0].get('initialState') is True, 'initial focus observation missing')
    require(all(type(e.get('isFocused')) is bool and math.isfinite(e['realTimeSeconds']) for e in events)
            and all(a['realTimeSeconds']<=b['realTimeSeconds'] for a,b in zip(events,events[1:])),
            'invalid focus observation order')
    lost = [e for e in events if e['stage']=='Normal' and not e['isFocused']]
    intervals = [(e['realTimeSeconds'], next((n['realTimeSeconds'] for n in events
                 if n['realTimeSeconds']>e['realTimeSeconds'] and n['isFocused']), None)) for e in lost]
    intervals = [(start,end) for start,end in intervals if end is not None and end-start>=.5]
    require(intervals, 'normal run did not observe a focus loss and recovery')
    frames = []
    with (out/'observations.jsonl').open(encoding='utf-8-sig') as stream:
        for line in stream:
            row = parse(line)
            if row['recordType']!='frame' or row['stage']!='Normal': continue
            f = row['frame']; t = f['closedLoop']
            if any(start<=row['realTimeSeconds']<end for start,end in intervals) and f['paddles'][0]['valid'] \
                    and t['feedbackFresh'] and t['encoderConsumed']:
                frames.append((f['timeSeconds'], t['feedbackSequence']))
    require(len(frames)>=30 and len({seq for _,seq in frames})>=10
            and max(t for t,_ in frames)-min(t for t,_ in frames)>=.5,
            'fresh closed-loop physics did not continue while the Player was unfocused')
    return {'focus_intervals':intervals, 'fresh_physics_frames':len(frames),
            'distinct_feedback_sequences':len({seq for _,seq in frames})}


def stall_owned_host(proc, controllers):
    """Pause only this Popen's original process handle; always resume in finally."""
    require(sys.platform == 'win32' and proc.poll() is None, 'host stall needs a live owned Windows Player')
    require(controllers and all(pid != proc.pid and process_alive(pid) for pid in controllers),
            'host stall needs an independently running controller')
    ntdll = ctypes.WinDLL('ntdll')
    for name in ('NtSuspendProcess', 'NtResumeProcess'):
        function = getattr(ntdll, name)
        function.argtypes = [ctypes.c_void_p]
        function.restype = ctypes.c_long
    handle = ctypes.c_void_p(int(proc._handle))
    started = time.monotonic()
    require(ntdll.NtSuspendProcess(handle) == 0, 'owned host suspension failed')
    probe = {'requested':True, 'performed':True, 'host_pid':proc.pid, 'resumed':False}
    try:
        time.sleep(.35)
        probe['controller_pids_alive_during_stall'] = [pid for pid in controllers if process_alive(pid)]
    finally:
        status = ntdll.NtResumeProcess(handle)
        probe['resumed'] = status == 0
        probe['duration_seconds'] = time.monotonic() - started
        require(status == 0, 'owned host resume failed')
    return probe


def verify_host_stall(out, probe):
    require(probe.get('performed') is True and probe.get('resumed') is True
            and .3 <= probe.get('duration_seconds', 0) < 2,
            'owned host suspension/resumption evidence missing')
    report = parse((out/'result.json').read_text(encoding='utf-8-sig'))
    require(probe.get('host_pid') == report['hostProcessId']
            and report['initialControllerPid'] in probe.get('controller_pids_alive_during_stall', []),
            'controller did not stay independently alive during the host stall')
    rows = []
    with (out/'observations.jsonl').open(encoding='utf-8-sig') as stream:
        for line in stream:
            row = parse(line)
            if row['recordType'] == 'frame' and row['stage'] == 'Normal': rows.append(row)
    gaps = [(i, rows[i]['realTimeSeconds'] - rows[i-1]['realTimeSeconds'])
            for i in range(1, len(rows)) if rows[i]['realTimeSeconds'] - rows[i-1]['realTimeSeconds'] >= .25]
    require(gaps, 'no observed physics gap during the host suspension')
    i, gap = max(gaps, key=lambda item:item[1])
    previous = rows[i-1]['frame']['closedLoop']
    _, before = envelope(previous['feedbackPacketBase64'], previous['sessionId'], 'feedback')
    expired = []
    # A suspension can interrupt an already consumed physics step. Preserve that
    # historical sample and inspect the next returned device observations as well.
    for row in rows[i:]:
        if row['realTimeSeconds'] - rows[i]['realTimeSeconds'] > .15: break
        frame = row['frame']; t = frame['closedLoop']
        _, f = envelope(t['feedbackPacketBase64'], t['sessionId'], 'feedback')
        if f['watchdog'] and f['controllerWallTimeSeconds'] - before['controllerWallTimeSeconds'] >= .25:
            require(f['plantSampleTick'] > before['plantSampleTick']
                    and not f['commandExecutionFresh'] and not f['cableExecutionEnabled']
                    and not f['platformExecutionEnabled'], 'stalled host retained actuator authorization')
            # Boat waves may still generate a valid *computed* mapping request.
            # Execution is authorized separately; check the actual device output.
            require(not frame['paddles'][0]['valid'] and not frame['platformFeedback']['valid'],
                    'post-stall expired observation still drives input or device output')
            if t['commandSent']:
                _, command = envelope(t['commandPacketBase64'], t['sessionId'], 'step')
                require(not command['cableValid'] and not command['platformValid']
                        and all(x == 0 for x in command['tensionTargets']), 'post-stall dispatch retained active targets')
            expired.append(t['feedbackSequence'])
    require(expired, 'controller watchdog was not observed after the real host stall')
    recovered = [row for row in rows[i:] if row['frame']['closedLoop']['feedbackFresh']
                 and row['frame']['closedLoop']['encoderConsumed'] and row['frame']['paddles'][0]['valid']]
    require(len(recovered) >= 50 and len({r['frame']['closedLoop']['feedbackSequence'] for r in recovered}) >= 20,
            'fresh closed-loop motion did not recover after the host stall')
    return {'observed_host_gap_seconds':gap, 'watchdog_feedback_sequences':sorted(set(expired)),
            'recovered_fresh_frames':len(recovered)}


def controller_runtime(configuration):
    configured = parse(configuration.read_text(encoding='utf-8-sig'))['pythonExecutable']
    bundled = configuration.parent/'controller_runtime/python.exe'
    if configured.lower() == 'python.exe' and bundled.is_file():
        return str(bundled.resolve()), True
    candidate = Path(configured)
    if not candidate.is_absolute() and ('/' in configured or '\\' in configured):
        candidate = configuration.parent/candidate
    return str(candidate), candidate.resolve() == bundled.resolve() and candidate.is_file()

def player_environment():
    environment = {key: value for key, value in os.environ.items()
                   if not key.upper().startswith('PYTHON') and key.upper() not in ('VIRTUAL_ENV', 'CONDA_PREFIX', 'PATH')}
    windows = Path(os.environ.get('SystemRoot', r'C:\Windows'))
    environment['PATH'] = os.pathsep.join(str(p) for p in (windows/'System32', windows, windows/'System32/Wbem'))
    require(shutil.which('python.exe', path=environment['PATH']) is None, 'isolated Player PATH still resolves python.exe')
    return environment

def verify_startup_visual_geometry(report):
    observations = report.get('startupVisualGeometry', [])
    require(len(observations) == 4, 'missing first-four-step visual collider observations')
    previous_time = -1.0
    seen = 0
    native_ids = None
    for index, observation in enumerate(observations):
        stamp = age(observation.get('fixedTimeSeconds'), 'startup fixed time')
        require(stamp > previous_time and (index > 0 or stamp <= .02), 'startup probe began late or repeated a physics step')
        previous_time = stamp
        require(observation.get('automaticPhysics') is True, 'startup did not use ordinary automatic physics')
        magnitude(observation['boatPosition']); magnitude(observation['boatVelocity'])
        if report.get('schemaVersion', 1) >= 6:
            source = report.get('hullSource')
            require(source and source.get('schemaVersion') == 2 and isinstance(source.get('parts'), list)
                    and 2 <= len(source['parts']) <= 1024, 'missing production native compound source at startup')
            hulls = observation.get('hulls')
            require(isinstance(hulls, list) and hulls, 'missing actual native hull observations')
            ids = {h['instanceId'] for h in hulls}
            require(len(ids) == len(hulls) and (native_ids is None or native_ids == ids), 'native hulls changed during startup')
            native_ids = ids
            for hull in hulls:
                require(hull['sameBody'] and magnitude(hull['boundsSize']) > 1e-5, 'detached or empty hull at startup')
            expected = {p['name']: p for p in source['parts']}
            require(len(hulls) == len(expected) == len(source['parts']) and {h['name'] for h in hulls} == set(expected),
                    'startup missed a generated native contact part')
            for hull in hulls:
                part = expected[hull['name']]
                require(hull['kind'] == 'MeshCollider' and hull['convex']
                        and 4 <= hull['vertices'] == part['vertices'] <= 120
                        and 4 <= hull['triangles'] == part['triangles'] <= 236, 'invalid native part at startup')
        colliders = observation.get('colliders')
        require(isinstance(colliders, list), 'missing startup collider list')
        for collider in colliders:
            require(collider.get('owner') in ('cable', 'platform') and collider.get('name'), 'unknown diagnostic visual collider')
            for key in ('enabled', 'active', 'collidesWithBoatLayer', 'overlapsHull'):
                require(type(collider.get(key)) is bool, 'invalid startup collider state')
            age(collider.get('penetrationM'), 'visual penetration')
            if report.get('schemaVersion', 1) >= 6:
                require(collider.get('checkedHullColliders') == (len(hulls) if collider['enabled'] and collider['active'] else 0),
                        'active diagnostic visual did not check every native hull part')
            magnitude(collider['worldPosition']); magnitude(collider['separationDirection'])
            require(not (collider['enabled'] and collider['active']),
                    'diagnostic visual collider active during physics startup: ' + collider['name'])
            require(not collider['overlapsHull'] and collider['penetrationM'] == 0,
                    'diagnostic visual geometry penetrated the boat')
            seen += 1
    return {'physics_steps': len(observations), 'pending_disabled_colliders': seen,
            'native_hull_parts': len(native_ids) if native_ids is not None else None,
            'active_visual_colliders': 0, 'scope': 'First four normal physics steps; no manual stepping or pose correction'}


def verify(out):
    report = parse((out/'result.json').read_text(encoding='utf-8-sig'))
    motion_audio = report.get('schemaVersion', 1) >= 2
    sampled_intent = report.get('schemaVersion', 1) >= 3
    local_drag = report.get('schemaVersion', 1) >= 4
    if sampled_intent:
        require(report.get('intentClock') == 'controller_sample', 'intent clock provenance missing')
    require(report['result']=='PASS' and not report['errors'], 'runtime report failed: '+str(report.get('errors')))
    checks=report['checks']
    require(len(checks)==len(REQUIRED) and {c['name'] for c in checks}==REQUIRED and all(c['passed'] is True for c in checks), 'check set incomplete')
    require(abs(report['inputTtlSeconds']-.1)<1e-6 and abs(report['feedbackTtlSeconds']-.1)<1e-6, '100 ms timeout configuration changed')
    require(report['sourceFramesComplete'] is True, 'writer did not report durable completion')
    counts={'lines':0,'frames':0,'fresh':0,'expired':0,'watchdog_zero':0,'stopped':0,'restart_requested':0,
            'normal_fresh':0,'recovery_fresh':0,'restarted_fresh':0,'normal_entries':0,'restart_entries':0,'loaded_deviation':0,
            'render':0,'normal_render':0,'presentation_views':{'MainCamera':0,'TwinCamera':0},
            'interaction_resets':0,'interaction_normal_entries':0,'interaction_paddle_paint_events':0,
            'interaction_wake_paint_events':0,'max_paddle_audio_rms':0.0,
            'max_stroke_audio_rms':0.0,'max_wood_audio_rms':0.0}
    previous={}; sample_clocks={}; sessions={}; commands={}; observations={}; geometry=report['platformGeometry']; maximum_length_error=0.0
    counts.update(command_packets=0, dispatch_expiries=0)
    if report.get('schemaVersion', 1) >= 5:
        counts['startup_visual_geometry'] = verify_startup_visual_geometry(report)
    if local_drag: counts.update(drag_active_frames=0, drag_reduced_frames=0)
    stage_order={'Normal':0,'Disconnected':1,'Recovery':2,'Stopped':3,'Restarted':4}
    last_stage=-1; last_wall=None; stage_times={}; armed={'Normal':False,'Restarted':False}; last_y={}
    watchdog_key=None; watchdog_tensions=None; watchdog_zero_tail=0; watchdog_decay=[]
    digest=hashlib.sha256()
    latest_frame=None; max_render_position=0.0; max_render_angle=0.0
    latest_physics=None; interaction_reset=None; initial_interaction_reset=None; previous_water=None
    intent_phases={}; intent_references={}; drag_sources={}; paused_intents=0; maximum_phase_step=0.0
    with (out/'observations.jsonl').open('rb') as stream:
        for line in stream:
            require(line.endswith(b'\n') and line.strip() and not line.startswith(b'\xef\xbb\xbf'), 'torn, blank or BOM observation line')
            digest.update(line); counts['lines']+=1; obs=parse(line.decode('utf-8'))
            stage=obs['stage']; kind=obs['recordType']
            require(stage in stage_order and stage_order[stage]>=last_stage, 'unknown or out-of-order runtime stage')
            last_stage=stage_order[stage]
            if kind=='render':
                position_error, angle_error=verify_render(obs,latest_frame)
                verify_interaction_render(obs,latest_physics,interaction_reset)
                if motion_audio: verify_motion_audio_render(obs,latest_physics)
                max_render_position=max(max_render_position,position_error)
                max_render_angle=max(max_render_angle,angle_error)
                counts['render']+=1
                if stage=='Normal':
                    verify_presentation_render(obs)
                    counts['normal_render']+=1
                    counts['presentation_views'][obs['cameraName']]+=1
                    counts['interaction_normal_entries']=obs['waterEntries']-initial_interaction_reset['waterEntries']
                    counts['interaction_paddle_paint_events']=obs['foamPaddlePaintEvents']
                    counts['interaction_wake_paint_events']=obs['foamWakePaintEvents']
                    counts['max_paddle_audio_rms']=max(counts['max_paddle_audio_rms'],obs['paddleAudioRms'])
                    if motion_audio:
                        counts['max_stroke_audio_rms']=max(counts['max_stroke_audio_rms'],obs['strokeAudioRms'])
                        counts['max_wood_audio_rms']=max(counts['max_wood_audio_rms'],obs['woodAudioRms'])
                continue
            require(math.isfinite(obs['realTimeSeconds']) and (last_wall is None or obs['realTimeSeconds']>=last_wall), 'unordered observation clock')
            last_wall=obs['realTimeSeconds']
            if kind=='interaction_reset':
                verify_interaction_reset(obs,interaction_reset)
                expected_resets=('Normal','Recovery','Stopped')
                require(counts['interaction_resets'] < len(expected_resets)
                        and stage == expected_resets[counts['interaction_resets']],
                        'unexpected interaction reset lifecycle')
                interaction_reset=obs
                if initial_interaction_reset is None: initial_interaction_reset=obs
                previous_water=(obs['waterEntries'],obs['waterExits'])
                latest_frame=latest_physics=None
                counts['interaction_resets']+=1
                continue
            if kind in ('stopped','restart_requested'):
                require(stage=='Stopped', 'stop/reset evidence has wrong stage')
                assert_zero_input(obs['paddle'],obs['rawForceN'],obs['filteredForceN'],obs['request'])
                require(obs['lastFrameCleared'] is True and magnitude(obs['bodyVelocity'])<=1e-5, 'stopped/reset state retains frame or velocity')
                require(obs['controllerAlive'] is False, 'old controller survives stop/reset')
                if kind=='stopped':
                    require(obs['experienceActive'] is False and obs['bodyKinematic'] is True, 'stopped experience remains active')
                    counts['stopped']+=1
                else:
                    require(obs['experienceActive'] is True and obs['bodyKinematic'] is False, 'reset did not resume dynamic experience')
                    require(near(vector(obs['bodyPosition']),vector(report['initialBoatPosition']),1e-5), 'reset boat position differs from initial position')
                    require(near([obs['bodyRotation'][k] for k in ('x','y','z','w')], [report['initialBoatRotation'][k] for k in ('x','y','z','w')],1e-5), 'reset boat rotation differs from initial rotation')
                    require(abs(obs['waveTimeSeconds']-report['initialWaveTime'])<=1e-5, 'reset wave clock differs from initial clock')
                    counts['restart_requested']+=1
                continue
            require(kind=='frame' and stage!='Stopped', 'unexpected evidence record type')
            require(obs['physicsAutomatic'] is True and obs['experienceActive'] is True and obs['bodyKinematic'] is False, 'frame did not observe normal dynamic physics')
            frame=obs['frame']; t=frame['closedLoop']; sample=frame['paddles'][0]; counts['frames']+=1
            latest_frame=frame
            require(interaction_reset is not None, 'physics has no interaction reset baseline')
            water=(interaction_count(obs,'waterEntries'),interaction_count(obs,'waterExits'))
            require(all(value>=prior for value,prior in zip(water,previous_water)),
                    'physics water event counter reversed without a reset')
            previous_water=water; latest_physics=obs
            session=t['sessionId']; phase=obs['driverPhase01']
            require(math.isfinite(phase) and 0<=phase<1, 'invalid script intent phase')
            if session in intent_phases:
                phase_step=(phase-intent_phases[session]+.5)%1-.5
                if not sampled_intent:
                    require(-1e-6<=phase_step<=report['intentStrokesPerMinute']/60*frame['fixedStepSeconds']+1e-6,
                            'script jumped across an unobserved stroke interval')
                require(not t['intentPaused'] or abs(phase_step)<=1e-6, 'paused script intention still advanced')
                maximum_phase_step=max(maximum_phase_step,phase_step)
            if t['intentPaused']: paused_intents+=1
            intent_phases[session]=phase
            require(near(vector(obs['bodyPosition']),vector(frame['boatPosition']),1e-5) and near(vector(obs['bodyVelocity']),vector(frame['boatVelocity']),1e-5), 'frame differs from post-physics body observation')
            times=stage_times.setdefault(stage,[]); times.append(frame['timeSeconds'])
            if len(times)>1: require(times[-1]>times[-2], 'fixed frame simulation time repeated or reversed')
            if stage=='Restarted' and len(times)==1:
                require(counts['restart_requested']==1 and 0<times[0]<=frame['fixedStepSeconds']*2+1e-6, 'restart did not begin a clean simulation timeline')
            f=None; seq=None; session=t['sessionId']
            if t.get('feedbackPacketBase64'):
                packet=base64.b64decode(t['feedbackPacketBase64'],validate=True)
                require(len(packet)>=28, 'short UDP feedback packet')
                magic,version,seq,nonce,size=struct.unpack_from('<4sIIQI',packet)
                require(magic==b'DBCL' and version==1 and nonce==int(session) and seq==t['feedbackSequence'],'UDP identity mismatch')
                require(len(packet)==size+28 and struct.unpack_from('<I',packet,len(packet)-4)[0]==zlib.crc32(packet[:-4]),'UDP CRC/length mismatch')
                f=parse(packet[24:-4].decode('utf-8'))
                require(f['kind']=='feedback' and f['commandSequence']==t['acknowledgedCommandSequence'],'ack identity mismatch')
                if session in previous: require((seq-previous[session])&0xffffffff < 0x80000000,'out of order feedback consumed')
                previous[session]=seq
                sample_clocks[session]=verify_sample_clock(f,seq,packet,sample_clocks.get(session))
                observations[(session,seq)]=t['feedbackPacketBase64']
                for name in ('controllerTimeSeconds','controllerWallTimeSeconds','plantSampleTick','plantStepSeconds','plantDroppedTicks'):
                    # Unity JsonUtility's decimal rendering can differ by one binary
                    # double ULP. Raw DBCL/DBEP and integer ticks remain authoritative.
                    tolerance=2*math.ulp(f[name]) if name in ('controllerTimeSeconds','controllerWallTimeSeconds','plantStepSeconds') else 0
                    require(name in t['feedback'] and abs(t['feedback'][name]-f[name])<=tolerance,
                            'consumed controller clock differs from received UDP packet: '+name)
            if verify_dispatch(frame, f, commands): counts['dispatch_expiries'] += 1
            if t['commandSent']: counts['command_packets'] += 1
            if sampled_intent:
                command = envelope(t['commandPacketBase64'],session,'step',t['commandSequence'])[1] if t['commandSent'] else None
                intent_references[session] = verify_intent_reference(phase,t,f,command,report['intentStrokesPerMinute'],intent_references.get(session))
                if local_drag:
                    drag_sources[session] = verify_drag_source(frame,f,command,drag_sources.get(session))
            if f is not None:
                verify_received_authorization(t, f, commands, observations)
                verify_motor_wrench(frame, f)
                if local_drag:
                    active, reduced = verify_drag_feedback(t, f)
                    counts['drag_active_frames'] += int(active)
                    counts['drag_reduced_frames'] += int(reduced)
            stale=t['receivedAgeSeconds']>.1 or t['commandAckAgeSeconds']>.1 or (f is not None and (f['watchdog'] or not f['valid']))
            if stale:
                require(t['feedbackFresh'] is False, 'stale feedback incorrectly marked fresh')
                assert_zero_input(sample,frame['rawWaterForceN'][0],frame['filteredFeedbackForceN'][0],frame['feedbackRequests'][0])
                if stage=='Disconnected': counts['expired']+=1
            if stage=='Disconnected' and f is not None and f['watchdog']:
                tensions=f['tensionN']
                require(isinstance(tensions,list) and len(tensions)>0 and all(math.isfinite(x) and x>=0 for x in tensions), 'remote watchdog reports invalid or negative tension')
                settled=all(x<=1e-5 for x in tensions) and magnitude(f['forceLocal'])<=1e-5 and magnitude(f['torqueLocal'])<=1e-5
                if settled: counts['watchdog_zero']+=1
                if watchdog_key!=(session,seq):
                    if watchdog_tensions is not None:
                        require(len(tensions)==len(watchdog_tensions) and all(x<=prior+1e-12 for x,prior in zip(tensions,watchdog_tensions)), 'remote watchdog tension increased instead of decaying')
                    # The external plant retains its actual lagged motor state;
                    # only the host's input/force gate above is instantaneous.
                    watchdog_zero_tail=watchdog_zero_tail+1 if settled else 0
                    watchdog_decay.append({'session':session,'sequence':seq,'command_ack_age_seconds':t['commandAckAgeSeconds'],
                                           'tension_n':tensions,'force_n':f['forceLocal'],'torque_nm':f['torqueLocal'],'settled':settled})
                    watchdog_key=(session,seq); watchdog_tensions=tensions
            fresh=t['feedbackFresh'] is True and t['encoderConsumed'] is True and sample['valid'] is True
            if sample['valid']: require(fresh, 'valid physics input bypasses consumed fresh feedback')
            if not fresh: continue
            require(f is not None and f['valid'] is True and f['watchdog'] is False and 0<=t['receivedAgeSeconds']<=.1 and 0<=t['commandAckAgeSeconds']<=.1, 'invalid feedback consumed as fresh')
            require(t['hostProcessId']==report['hostProcessId'] and t['controllerProcessId']>0 and t['controllerProcessId']!=report['hostProcessId'],'controller process identity invalid')
            require(sessions.setdefault(session,t['controllerProcessId'])==t['controllerProcessId'], 'controller PID changed within one session')
            counts['fresh']+=1
            if stage=='Normal': counts['normal_fresh']+=1
            if stage=='Recovery': counts['recovery_fresh']+=1
            if stage=='Restarted': counts['restarted_fresh']+=1
            require(t['encoderPacketBase64']==f['encoderPacketBase64'], 'encoder packet was locally regenerated instead of consumed from UDP feedback')
            encoder=base64.b64decode(t['encoderPacketBase64'],validate=True)
            require(len(encoder)==52 and struct.unpack_from('<I',encoder,48)[0]==zlib.crc32(encoder[:48]),'encoder packet invalid')
            require(struct.unpack_from('<III',encoder)==(0x44424550,1,seq),'encoder header mismatch')
            raw=list(struct.unpack_from('<6i',encoder,20)); enc=frame['encoderTelemetry'][0]
            device_time=struct.unpack_from('<Q',encoder,12)[0]
            require(sample.get('deviceTimeUs')==enc.get('deviceTimeUs')==device_time, 'decoded or physics sample time differs from received DBEP timestamp')
            require(raw==f['rawEncoderCounts']==enc['rawCounts'],'raw counts did not reach decoder')
            require(enc['packetAccepted'] is True and enc['crcValid'] is True and enc['packetBytes']==52 and enc['sourceSequence']==seq, 'encoder decode evidence invalid')
            require(sample['sequence']==seq and sample['source']==5,'physics did not consume encoder sample')
            require(f['countsPerMeter']==report['countsPerMeter'] and f['countsPerDegree']==report['countsPerDegree'], 'encoder scale differs from configured resolution')
            local=[x/report['countsPerMeter'] for x in raw[:3]]
            require(near(local,vector(t['actualBladePositionLocal'])) and near(local,vector(enc['decodedLocalPosition'])),'actual pose differs from encoder counts')
            world=rotate(t['observationBoatRotation'],local)
            world=[x+y for x,y in zip(world,vector(t['observationBoatPosition']))]
            require(near(world,vector(sample['bladePosition']),0.0002),'world pose differs from raw decoded input')
            boat_q=[t['observationBoatRotation'][k] for k in ('x','y','z','w')]
            expected_q=mul(boat_q,euler_q([x/report['countsPerDegree'] for x in raw[3:]]))
            actual_q=[sample['bladeRotation'][k] for k in ('x','y','z','w')]
            require(near(expected_q,actual_q) or near([-x for x in expected_q],actual_q), 'world rotation differs from raw decoded encoder angles')
            cable=frame['cableFeedback']; platform=frame['platformFeedback']
            require(near(float32(f['tensionN']),cable['tensionsN']) and near(float32(f['cableLengthsMm']),cable['lengthsMm']),'motor feedback bypassed received packet')
            require(near(float32(f['platformLengthsMm']),platform['actuatorLengthsMm']) and near(float32(f['platformPosition']),vector(platform['translationM'])) and near(float32(f['platformEuler']),vector(platform['eulerDegrees'])),'platform feedback bypassed received packet')
            deviation=magnitude([x-y for x,y in zip(vector(t['desiredBladePositionLocal']),local)])
            water_force=magnitude(frame['rawWaterForceN'][0])
            if water_force>1 and magnitude(f['forceLocal'])>.1 and deviation>.0001: counts['loaded_deviation']+=1
            if stage in armed:
                if local[1]>.1 and obs['wettedFraction']<.01: armed[stage]=True
                if armed[stage] and stage in last_y and local[1]<last_y[stage] and local[1]<-.1 and obs['wettedFraction']>.2 and water_force>1:
                    counts['normal_entries' if stage=='Normal' else 'restart_entries']+=1; armed[stage]=False
                last_y[stage]=local[1]
            q=euler_q(f['platformEuler'])
            for i in range(6):
                if geometry['useExplicitJointGeometry']:
                    b=vector(geometry['explicitBaseJointsLocal'][i]); top=vector(geometry['explicitTopJointsLocal'][i])
                else:
                    a=math.radians(i*60); z=math.radians(i*60+30)
                    b=[math.cos(a)*geometry['baseRadius'],0,math.sin(a)*geometry['baseRadius']]
                    top=[math.cos(z)*geometry['topRadius'],0,math.sin(z)*geometry['topRadius']]
                arm=rotate(q,top)
                v=[f['platformPosition'][k]+arm[k]-b[k]+(geometry['neutralHeight'] if k==1 else 0) for k in range(3)]
                err=abs(math.sqrt(sum(x*x for x in v))*1000-f['platformLengthsMm'][i])
                maximum_length_error=max(maximum_length_error,err)
                require(err<=0.501,'platform actual lengths and FK pose inconsistent')
                require(geometry['minActuatorLengthsMm'][i]<=f['platformLengthsMm'][i]<=geometry['maxActuatorLengthsMm'][i], 'platform actuator exceeds configured length limit')
    require(counts['command_packets'] >= 100 and counts['command_packets'] == report['commandPackets']
            and counts['dispatch_expiries'] == report['commandSourceExpiredPackets']
            and report['commandSourceViolations'] == 0, 'outgoing command evidence incomplete')
    require(digest.hexdigest()==report['observationsSha256'],'observation SHA256 mismatch')
    require(counts['lines']==report['queuedLines']==report['writtenLines'] and counts['frames']==report['observedFrames'],'lost observation lines or frames')
    require(counts['render']==report['executionRenderObservations'] and counts['normal_render']>=30
            and report['executionRenderViolations']==0, 'execution render evidence incomplete')
    require(counts['render']==report['interactionObservations'] and report['interactionViolations']==0
            and counts['interaction_resets']==3, 'interaction evidence incomplete')
    require(counts['max_paddle_audio_rms']>1e-6,
            'paddle audio output remained silent during the normal interaction window')
    if motion_audio:
        require(report.get('motionAudioViolations') == 0 and report.get('normalWoodCreaks', 0) > 0,
                'motion audio source/wood activity incomplete')
        require(counts['max_stroke_audio_rms'] == report.get('maxStrokeAudioRms') > 1e-6
                and counts['max_wood_audio_rms'] == report.get('maxWoodAudioRms') > 1e-6,
                'motion audio RMS summary differs or a source remained silent')
        counts['listener_audio'] = verify_audio_capture(out, report)
    require(type(report.get('maxPaddleAudioRms')) in (int,float)
            and math.isfinite(report['maxPaddleAudioRms'])
            and report['maxPaddleAudioRms']==counts['max_paddle_audio_rms'],
            'paddle audio RMS summary differs from normal render observations')
    require(counts['interaction_normal_entries']==report['interactionNormalEntries']>=3
            and counts['interaction_paddle_paint_events']==report['interactionPaddlePaintEvents']>0
            and counts['interaction_wake_paint_events']==report['interactionWakePaintEvents']>0,
            'normal water/audio/foam activity was not independently observed')
    counts['maximum_render_position_error_m']=max_render_position
    counts['maximum_render_rotation_error_deg']=max_render_angle
    require(counts['presentation_views']['MainCamera']==report['presentationMainFrames']>=30
            and counts['presentation_views']['TwinCamera']==report['presentationTwinFrames']>=30
            and report['presentationViolations']==0, 'both presentation views were not independently verified')
    require(paused_intents>=10 and report['intentContinuityViolations']==0, 'missing continuous pause/recovery evidence')
    counts['paused_intents']=paused_intents; counts['maximum_intent_phase_step']=maximum_phase_step
    require(counts['normal_fresh']>=300 and counts['normal_entries']>=3 and stage_times['Normal'][-1]-stage_times['Normal'][0]>=7.99, 'fewer than three observed wet strokes in the default runtime window')
    require(counts['expired']>=10 and counts['watchdog_zero']>=5 and counts['loaded_deviation']>=20, 'insufficient expiry, remote watchdog or loaded-motion evidence')
    if local_drag:
        require(counts['drag_active_frames'] >= 10 and counts['drag_reduced_frames'] >= 1,
                'local drag reduction was not observed in the actual controller feedback')
    require(watchdog_zero_tail>=5, 'remote watchdog did not settle its actual motor wrench for five final distinct feedback packets')
    require(counts['recovery_fresh']>=50 and counts['restarted_fresh']>=100 and counts['restart_entries']>=1 and counts['stopped']>=3 and counts['restart_requested']==1, 'stop, recovery or restart evidence incomplete')
    require(len(sessions)==2 and sessions.get(report['initialSessionId'])==report['initialControllerPid'] and sessions.get(report['restartedSessionId'])==report['restartedControllerPid'] and report['initialSessionId']!=report['restartedSessionId'] and report['initialControllerPid']!=report['restartedControllerPid'], 'fresh controller session not established')
    processes=report['processes']
    require(len(processes)>=2 and all(p['observedAlive'] is True and p['hostProcessId']==report['hostProcessId'] and sessions.get(p['sessionId'])==p['controllerProcessId'] for p in processes), 'process evidence does not match consumed UDP sessions')
    counts['sessions']=sessions;counts['maximum_independent_fk_error_mm']=maximum_length_error
    require(set(sample_clocks)==set(sessions) and all(value['wall_phase_observations']>0 for value in sample_clocks.values()),
            'independent controller wall clock observations missing')
    counts['sample_clocks']={session:{key:value for key,value in state.items() if key not in ('packet',)}
                             for session,state in sample_clocks.items()}
    counts['watchdog_zero_tail_distinct']=watchdog_zero_tail;counts['watchdog_decay']=watchdog_decay
    counts['screenshot']=png_evidence(out/report['screenshot'])
    return counts

def artifact_sha256(path):
    digest=hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024*1024), b''): digest.update(block)
    return digest.hexdigest()


def publish_runtime_artifacts(live, out):
    # Preflight all entries/destinations before copying. On any failure keep
    # the original staging data for diagnosis; never overwrite older evidence.
    sources=tuple(live.iterdir())
    for source in sources:
        require(source.is_file() and not source.is_symlink(), 'unexpected runtime artifact entry')
        require(not (out/source.name).exists(), 'artifact destination already exists')
    published={}
    for source in sources:
        digest=artifact_sha256(source);target=out/source.name
        with source.open('rb') as incoming, target.open('xb') as outgoing:
            shutil.copyfileobj(incoming,outgoing,1024*1024)
            outgoing.flush();os.fsync(outgoing.fileno())
        require(artifact_sha256(target)==digest, 'artifact publication hash mismatch')
        published[source.name]=digest
    return published


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--player',type=Path);parser.add_argument('--verify',type=Path)
    parser.add_argument('--cad-tendon-rig',action='store_true',help='Exercise the explicitly selected unmeasured CAD tendon design in the real startup path')
    parser.add_argument('--capture-motion',action='store_true',help='Capture actual Player screen frames with the audio timeline for continuous visual review')
    parser.add_argument('--environment-sweep',action='store_true',help='Extend normal running to four river/water cases and an in-progress current change')
    parser.add_argument('--background-check',action='store_true',help='Minimize only the owned Player for 1.2 seconds and verify fresh physics continues')
    parser.add_argument('--host-stall-check',action='store_true',help='Suspend only the owned Player for 350 ms; verify independent watchdog and recovery')
    args=parser.parse_args()
    require(sys.version_info >= (3,10), 'Python >= 3.10 is required')
    if args.verify:
        print(json.dumps(verify(args.verify),indent=2))
        if args.background_check: print(json.dumps(verify_background(args.verify),indent=2))
        if args.environment_sweep:
            from environment_sweep import verify_environment_sweep
            print(json.dumps(verify_environment_sweep(args.verify),indent=2))
        if args.capture_motion:
            from motion_capture import verify_motion_capture
            print(json.dumps(verify_motion_capture(args.verify, require_source_alignment=True),indent=2))
        if args.host_stall_check:
            saved = parse((args.verify/'verification.json').read_text(encoding='utf-8-sig'))
            print(json.dumps(verify_host_stall(args.verify, saved['host_stall_probe']), indent=2))
        return 0
    require(not args.background_check or args.player, '--background-check requires --player')
    require(not args.host_stall_check or args.player, '--host-stall-check requires --player')
    require(not args.capture_motion or args.player, '--capture-motion requires --player')
    require(not args.environment_sweep or args.player, '--environment-sweep requires a presented standalone Player')
    require(not (args.capture_motion and (args.background_check or args.host_stall_check)),
            'Continuous visual capture and deliberate window/stall probes require separate runs')
    out=WORKSPACE/'tools/closed_loop_checks'/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S.%fZ')+'_'+uuid.uuid4().hex[:8])
    out.mkdir(parents=True); print('ARTIFACTS='+str(out),flush=True)
    result={'result':'FAIL','reasons':[],'source_sha256':source_hashes(),'process_observations':[],
            'cad_tendon_rig_requested':args.cad_tendon_rig}
    # Full raw packet/frame evidence is several MB/s. Capture on the host's
    # temporary volume, then publish byte-for-byte after the owned host exits;
    # a removable project drive must not stall the observed simulation.
    live=Path(tempfile.mkdtemp(prefix='DragonBoatVR-closed-loop-'))
    result['live_artifacts_directory']=str(live)
    log=live/'unity.log'
    child_environment=None
    if args.player:
        # A standalone batch-mode Player has no presented screen backbuffer;
        # camera callbacks alone cannot prove the delivered window renders.
        executable=args.player.resolve();command=[str(executable),'-screen-fullscreen','0','-screen-width','1280','-screen-height','760']
        result['player_sha256']=hashlib.sha256(executable.read_bytes()).hexdigest()
        result['player_files_sha256']=player_hashes(executable)
        configuration=executable.parent/(executable.stem+'_Data')/'StreamingAssets/closed_loop.json'
    else:
        command=[str(resolve_unity()),'-batchmode','-job-worker-count','2','-projectPath',str(PROJECT),'-executeMethod','DragonBoatVR.EditorTools.ClosedLoopRuntimeLauncher.Run']
        configuration=PROJECT/'Assets/StreamingAssets/closed_loop.json'
    try:
        runtime,bundled=controller_runtime(configuration)
        if args.player:
            require(bundled, 'Player acceptance requires its bundled controller_runtime/python.exe')
            child_environment=player_environment()
            result['player_path_without_python']=child_environment['PATH']
        probe=subprocess.run([runtime,'-c','import json,sys; print(json.dumps({"version":list(sys.version_info[:3]),"executable":sys.executable,"isolated":sys.flags.isolated,"site_import":int("site" in sys.modules)}))'],capture_output=True,text=True,check=True,timeout=10,creationflags=subprocess.CREATE_NO_WINDOW,env=child_environment,cwd=str(configuration.parent))
        result['python_runtime']=parse(probe.stdout)
        result['python_runtime']['bundled']=bundled
        require(tuple(result['python_runtime']['version']) >= (3,10), 'configured controller needs Python >= 3.10')
        if args.player:
            require(result['python_runtime']['isolated']==1 and result['python_runtime']['site_import']==0, 'bundled runtime is not isolated from site/user packages')
            require(Path(result['python_runtime']['executable']).resolve()==Path(runtime).resolve(), 'bundled runtime probe executed a different interpreter')
    except Exception as error:
        result['reasons'].append('controller runtime prerequisite failed: '+str(error))
        (out/'verification.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        print('RESULT=FAIL; '+str(result['reasons']),flush=True)
        return 1
    command+=['-closedLoopArtifacts',str(live),'-logFile',str(log)]
    if args.cad_tendon_rig: command.append('-cad-tendon-rig')
    if args.capture_motion: command.append('-captureMotion')
    if args.environment_sweep: command.append('-environmentSweep')
    result['command']=command; seen=set()
    with unity_project_guard(PROJECT):
        proc=subprocess.Popen(command,stdout=subprocess.DEVNULL,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW,
                              env=child_environment,cwd=str(executable.parent) if args.player else None)
        started=time.monotonic()
        normal_seen=None; minimized_at=None; probe_window=None; restored=False; focus_requested=False
        result['background_probe']={'requested':args.background_check,'observed_minimized':False,'restored':False}
        result['host_stall_probe']={'requested':args.host_stall_check,'performed':False}
        while proc.poll() is None:
            if time.monotonic()-started>150:
                proc.kill();proc.wait();result['reasons'].append('runtime timeout');break
            if log.exists():
                log_text=log.read_text(encoding='utf-8-sig',errors='replace')
                if normal_seen is None and '[CLOSEDLOOP_LIFECYCLE] stage=Normal ' in log_text: normal_seen=time.monotonic()
                if args.background_check:
                    if normal_seen is not None and not focus_requested:
                        probe_window=owned_player_window(proc.pid)
                        if probe_window:
                            show_owned_player_window(proc.pid,probe_window,False); focus_requested=True
                    if normal_seen is not None and minimized_at is None and time.monotonic()-normal_seen>=2:
                        probe_window=owned_player_window(proc.pid)
                        if probe_window:
                            show_owned_player_window(proc.pid,probe_window,True); minimized_at=time.monotonic()
                            result['background_probe']['window_handle']=probe_window
                    if minimized_at is not None and not restored:
                        result['background_probe']['observed_minimized'] |= player_window_is_minimized(probe_window)
                        if time.monotonic()-minimized_at>=1.2:
                            show_owned_player_window(proc.pid,probe_window,False); restored=True
                            result['background_probe']['restored']=True
                for match in re.findall(r'independent controller started pid=(\d+)',log_text):
                    pid=int(match)
                    if pid not in seen:
                        seen.add(pid);result['process_observations'].append({'pid':pid,'host_pid':proc.pid,'alive':process_alive(pid),'executable':process_image(pid)})
                if args.host_stall_check and not result['host_stall_probe']['performed'] and normal_seen is not None \
                        and time.monotonic()-normal_seen >= 4.5 and seen:
                    result['host_stall_probe'] = stall_owned_host(proc, sorted(seen))
            time.sleep(0.2)
    try:
        result['published_artifacts_sha256']=publish_runtime_artifacts(live,out)
        # Delete only our generated flat staging files after verified publication.
        for source in live.iterdir(): source.unlink()
        live.rmdir()
    except Exception as error:
        result['reasons'].append('artifact publication failed; original staging retained: '+str(error))
    log=out/'unity.log'
    assessment=assess_log(read_lines(log),'[CLOSEDLOOP]',proc.returncode)
    result['reasons']+=assessment['reasons']
    if any(re.search(r'Screen position out of view frustum|Assertion failed',line,re.IGNORECASE) for line in read_lines(log)):
        result['reasons'].append('rendering/frustum or assertion error in current runtime log')
    try: result['independent_evidence']=verify(out)
    except Exception as error: result['reasons'].append(str(error))
    if args.environment_sweep:
        try:
            from environment_sweep import verify_environment_sweep
            result['environment_evidence'] = verify_environment_sweep(out)
        except Exception as error: result['reasons'].append('environment evidence: '+str(error))
    if args.capture_motion:
        try:
            from motion_capture import verify_motion_capture, write_motion_review
            result['motion_capture_evidence']=verify_motion_capture(out, require_source_alignment=True)
            write_motion_review(out)
        except Exception as error: result['reasons'].append('motion evidence: '+str(error))
    if args.background_check:
        try:
            require(result['background_probe']['observed_minimized'] and result['background_probe']['restored'],
                    'owned Player window minimize/restore was not observed')
            result['background_evidence']=verify_background(out)
        except Exception as error: result['reasons'].append(str(error))
    if args.host_stall_check:
        try: result['host_stall_evidence'] = verify_host_stall(out, result['host_stall_probe'])
        except Exception as error: result['reasons'].append(str(error))
    result['source_unchanged']=result['source_sha256']==source_hashes()
    if not result['source_unchanged']:result['reasons'].append('sources changed during verification')
    if args.player:
        result['player_files_unchanged']=result['player_files_sha256']==player_hashes(executable)
        if not result['player_files_unchanged']:result['reasons'].append('Player directory contents changed during verification')
    if 'independent_evidence' in result:
        expected=set(result['independent_evidence']['sessions'].values())
        if expected!=seen:result['reasons'].append('live controller PIDs do not match consumed UDP sessions')
        runtime_report=parse((out/'result.json').read_text(encoding='utf-8-sig'))
        if runtime_report['hostProcessId']!=proc.pid:result['reasons'].append('runtime report host PID differs from launched process')
    if len(seen)<2 or not all(p['alive'] and p['pid']!=p['host_pid'] for p in result['process_observations']):result['reasons'].append('independent live processes not observed')
    expected_runtime=Path(result['python_runtime']['executable']).resolve()
    if any(not p['executable'] or Path(p['executable']).resolve()!=expected_runtime for p in result['process_observations']):
        result['reasons'].append('observed controller process executable differs from the verified Python runtime')
    if any(process_alive(pid) for pid in seen):result['reasons'].append('controller process left running after host exit')
    result['result']='FAIL' if result['reasons'] else 'PASS'
    (out/'verification.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print('RESULT='+result['result']+'; '+str(result['reasons']),flush=True)
    return int(result['result']!='PASS')

if __name__=='__main__': raise SystemExit(main())
