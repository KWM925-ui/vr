"""Independent checks of actual continuous river changes and their observed effects."""
from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path

SCENARIOS = [
    ('XiangjiangOrangeIsland', 'Calm'),
    ('XiangjiangOrangeIsland', 'Windy'),
    ('ShundeFengjian', 'Windy'),
    ('ShundeFengjian', 'Calm'),
]


def require(ok, message):
    if not ok:
        raise ValueError(message)


def vector(value, axes='xyz'):
    result = [value[k] for k in axes]
    require(all(type(v) in (int, float) and math.isfinite(v) for v in result), 'Nonfinite environment vector')
    return result


def sample(value):
    require(isinstance(value, dict), 'Missing observed runtime environment')
    index = value.get('scenarioIndex')
    require(type(index) is int and 0 <= index < len(SCENARIOS), 'Unexpected environment case')
    scene, condition = SCENARIOS[index]
    require((value.get('selectedScene'), value.get('selectedCondition')) == (scene, condition),
            'Selected environment differs from requested case')
    require(value.get('sceneRootsPresent') is True and value.get('xiangjiangActive') == (index < 2)
            and value.get('fengjianActive') == (index >= 2), 'Actual visible scene roots do not match the selected river')
    for key in ('simulationTime', 'intensity', 'intensityRatePerSecond', 'waveTime', 'targetCurrentSpeedMps', 'targetCurrentHeadingDegrees'):
        require(type(value.get(key)) in (int, float) and math.isfinite(value[key]), 'Invalid environment observation: ' + key)
    require(type(value.get('transitioning')) is bool, 'Missing observed transition state')
    vector(value['backgroundCurrentMps'], 'xy'); vector(value['boatPosition']); vector(value['boatRotation'], 'xyzw')
    return index


def verify_environment_sweep(directory):
    directory = Path(directory)
    report = json.loads((directory / 'result.json').read_text(encoding='utf-8-sig'))
    sweep = report.get('environmentSweep')
    require(report.get('schemaVersion', 0) >= 6 and report.get('result') == 'PASS'
            and isinstance(sweep, dict) and not report.get('errors'), 'Environment runtime did not complete successfully')
    require(sweep.get('caseDurationSeconds') == 8 and report['normalSimulationSeconds'] >= 32,
            'Incomplete four-case continuous simulation window')
    configuration = sweep['configuration']
    transition = configuration['transitionSeconds']
    require(0 < transition <= 3 and 0 < configuration['calmIntensity'] < configuration['windyIntensity'] <= 2,
            'Invalid or indistinguishable water cases')
    require(configuration['maximumCurrentSpeedMps'] >= .25, 'Current fixture exceeds configured controls')
    changes = sweep['changes']
    require([change['reason'] for change in changes] == ['case_0', 'case_1', 'case_2', 'current_retarget', 'case_3'],
            'Missing, reordered or repeated environment requests')
    start = sweep['startSimulationTime']
    expected_times = [0, 8, 16, 16.7, 24]
    targets = [(configuration['defaultCurrentSpeedMps'], configuration['defaultCurrentHeadingDegrees'] % 360),
               (.20, 60), (.25, 300), (.10, 90), (0, 0)]
    for change, relative, target in zip(changes, expected_times, targets):
        before, after = change['before'], change['after']
        sample(after)
        require(0 <= after['simulationTime'] - start - relative < .35, 'Environment control request was skipped or delayed')
        require(abs(after['targetCurrentSpeedMps'] - target[0]) < 1e-6
                and abs(after['targetCurrentHeadingDegrees'] - target[1]) < 1e-5, 'Current command differs from the requested case')
        for key in ('simulationTime', 'waveTime', 'intensity', 'intensityRatePerSecond'):
            require(abs(after[key] - before[key]) < 1e-7, 'A UI environment request advanced or jumped ' + key)
        for key, axes in [('backgroundCurrentMps', 'xy'), ('boatPosition', 'xyz'), ('boatRotation', 'xyzw')]:
            require(math.dist(vector(before[key], axes), vector(after[key], axes)) < 1e-7,
                    'Environment control teleported ' + key)
    retarget = changes[3]
    require(retarget['before']['transitioning'] and retarget['after']['transitioning'], 'Current was not retargeted during motion')

    physics = [[] for _ in SCENARIOS]
    renders = [[] for _ in SCENARIOS]
    digest = hashlib.sha256()
    previous_time = -math.inf
    with (directory / 'observations.jsonl').open('rb') as stream:
        for line in stream:
            digest.update(line)
            row = json.loads(line)
            if row['stage'] != 'Normal' or row['recordType'] not in ('frame', 'render'):
                continue
            environment = row.get('environment')
            index = sample(environment)
            if row['recordType'] == 'render':
                require(row.get('waterShaderParametersAvailable') is True
                        and abs(row['waterShaderTime'] - environment['waveTime']) < 1e-5
                        and abs(row['waterShaderIntensity'] - environment['intensity']) < 1e-6
                        and math.dist(vector(row['waterShaderCurrentMps'], 'xy'), vector(environment['backgroundCurrentMps'], 'xy')) < 1e-6,
                        'Actually rendered water parameters do not match the physical environment')
                renders[index].append(row)
                continue
            frame = row['frame']
            require(environment['simulationTime'] == frame['timeSeconds'] and environment['simulationTime'] > previous_time,
                    'Environment is detached from its completed physical frame')
            previous_time = environment['simulationTime']
            require(abs(environment['waveTime'] - frame['waveTimeSeconds']) < 1e-5,
                    'Environment and physics use different wave clocks')
            require(math.dist(vector(environment['boatPosition']), vector(frame['boatPosition'])) < 1e-5,
                    'Environment observes a different boat pose')
            require(row['physicsAutomatic'] and row['experienceActive'] and not row['bodyKinematic'],
                    'Environment sweep bypassed the dynamic player loop')
            # Targets can change between completed physics and the following
            # render, so use the most recent request no later than this sample.
            active = [change['after'] for change in changes if change['after']['simulationTime'] <= environment['simulationTime']]
            require(active, 'Environment frame precedes its request')
            target = active[-1]
            settled = environment['simulationTime'] - target['simulationTime'] >= transition + .02
            if settled:
                wanted_intensity = configuration['windyIntensity' if index in (1, 2) else 'calmIntensity']
                heading = math.radians(target['targetCurrentHeadingDegrees'])
                current = [math.sin(heading) * target['targetCurrentSpeedMps'], math.cos(heading) * target['targetCurrentSpeedMps']]
                require(not environment['transitioning'] and abs(environment['intensity'] - wanted_intensity) < 1e-6
                        and abs(environment['intensityRatePerSecond']) < 1e-6
                        and math.dist(vector(environment['backgroundCurrentMps'], 'xy'), current) < 1e-6,
                        'Physics did not settle at the operator-selected water/current')
            physics[index].append(row)
    require(digest.hexdigest() == report['observationsSha256'], 'Environment evidence content hash differs')

    summaries = []
    for index, (frames, views) in enumerate(zip(physics, renders)):
        require(len(frames) >= 700 and frames[-1]['frame']['timeSeconds'] - frames[0]['frame']['timeSeconds'] >= 7.5,
                'Short or missing environment case: ' + str(index))
        fresh = [row for row in frames if row['frame']['closedLoop']['feedbackFresh']
                 and row['frame']['closedLoop']['encoderConsumed'] and row['frame']['paddles'][0]['valid']]
        entries = frames[-1]['waterEntries'] - frames[0]['waterEntries']
        require(len(fresh) >= 300 and entries >= 3, 'Environment case lacks independent feedback and actual wet strokes')
        camera_counts = {name: sum(row['cameraName'] == name for row in views) for name in ('MainCamera', 'TwinCamera')}
        require(min(camera_counts.values()) >= 40, 'Environment case lacks both actually rendered views')
        for row in views:
            require(row['presentsToScreen'] and row['skyboxAvailable'] and row['reflectionSourceCameraId'] == row['cameraId']
                    and row['reflectionFrame'] == row['unityFrameCount'], 'Environment changed without a valid own-camera reflection')
        splash = max((row['paddleAudioRms'] for row in views), default=0)
        stroke = max((row['strokeAudioRms'] for row in views), default=0)
        require(splash > 1e-6 and stroke > 1e-6, 'An environment case has silent paddling output')
        speeds = [math.sqrt(sum(x*x for x in vector(row['frame']['boatVelocity']))) for row in frames]
        heights = [row['frame']['boatPosition']['y'] for row in frames]
        forces = [math.sqrt(sum(x*x for x in vector(row['frame']['rawWaterForceN'][0]))) for row in frames]
        summaries.append({'scene': SCENARIOS[index][0], 'water': SCENARIOS[index][1], 'physics_frames': len(frames),
                          'fresh_encoder_frames': len(fresh), 'actual_water_entries': entries, 'rendered_views': camera_counts,
                          'boat_speed_min_max_mps': [min(speeds), max(speeds)], 'boat_height_min_max_m': [min(heights), max(heights)],
                          'maximum_water_force_n': max(forces), 'maximum_splash_rms': splash, 'maximum_stroke_rms': stroke})

    # Independent finite differences on the two sides of the in-flight retarget.
    rows = physics[2]
    stamp = retarget['after']['simulationTime']
    before = [row for row in rows if row['frame']['timeSeconds'] <= stamp][-2:]
    after = [row for row in rows if row['frame']['timeSeconds'] > stamp][:1]
    require(len(before) == 2 and after, 'Missing physical current samples around retarget')
    def rate(first, last):
        dt = last['frame']['timeSeconds'] - first['frame']['timeSeconds']
        return [(b-a)/dt for a, b in zip(vector(first['environment']['backgroundCurrentMps'], 'xy'),
                                        vector(last['environment']['backgroundCurrentMps'], 'xy'))], dt
    left, dt_before = rate(before[0], before[1]); right, dt_after = rate(before[1], after[0])
    tolerance = 12 * configuration['maximumCurrentSpeedMps'] / (transition * transition) * max(dt_before, dt_after) + 1e-5
    require(math.dist(left, right) <= tolerance, 'Current velocity has a discontinuity during retarget')
    return {'result': 'PASS', 'scope': sweep['scope'], 'cases': summaries,
            'retarget_rate_difference_mps2': math.dist(left, right), 'retarget_rate_tolerance_mps2': tolerance,
            'note': 'Measured software behavior across four cases; not calibrated hydrodynamic fidelity or subjective listening acceptance.'}
