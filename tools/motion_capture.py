"""Audit real end-of-frame JPEG capture and prepare an offline timed audio/image viewer."""
import hashlib
import json
import math
from pathlib import Path
import statistics
import wave


def require(value, message):
    if not value:
        raise ValueError(message)


def finite(value):
    return type(value) in (int, float) and math.isfinite(value)


def verify_environment_lighting(observation):
    """A named bright sky is not evidence of a nonzero diffuse lighting probe."""
    require(observation.get('skybox') and observation.get('ambientMode') == 0,
            'Expected skybox environment lighting was not observed')
    result = {}
    for name in ('ambientRadiance', 'paddleRadiance'):
        samples = observation.get(name, [])
        require(len(samples) == 6, 'Missing six-direction environment probe')
        luminances = []
        for value in samples:
            require(all(finite(value.get(channel)) for channel in 'rgb'), 'Nonfinite environment probe')
            luminances.append(sum(max(0., value[channel]) * weight
                                  for channel, weight in zip('rgb', (.2126, .7152, .0722))))
        require(max(luminances) > 1e-5, name + ' is black despite the active skybox')
        result[name + '_luminance'] = luminances
    return result


def verify_motion_source_alignment(report, observations):
    """Pair image metadata with the real camera callbacks and completed physics.

    Camera callbacks are recorded only for valid encoder-owned poses. Startup,
    timeout and stopped images remain in the capture, but are not claimed as
    source-validated poses. Rendered boat transforms use native interpolation;
    comparing those to a discrete Rigidbody pose would reject correct rendering.
    """
    frames = {frame['unityFrame']: frame for frame in report['frames']}
    samples = {}
    for frame in frames.values():
        require(type(frame.get('inputValid')) is bool, 'Missing captured input validity')
        samples.setdefault((frame['stage'], frame['simulationTime']), []).append(frame)
    physics = set()
    latest_physics = None
    cross_stage_sources = {}
    cameras = {number: set() for number in frames}
    maximum_position_error = maximum_angle_error = 0.

    def components(value, axes):
        require(isinstance(value, dict) and all(finite(value.get(k)) for k in axes),
                'Missing or nonfinite captured pose')
        return [value[k] for k in axes]

    def quaternion(value):
        q = components(value, 'xyzw')
        length = math.sqrt(sum(x*x for x in q))
        require(length > 1e-8, 'Invalid captured rotation')
        return [x/length for x in q]

    def check_physics_label(frame, row):
        source = row['frame']
        require(frame['inputValid'] == source['paddles'][0]['valid'],
                'Captured input validity differs from its physics sample')
        require(frame['simulationTime'] == source['timeSeconds'],
                'Captured physics time differs from the latest completed sample')
        require(frame['feedbackSequence'] == source['closedLoop']['feedbackSequence']
                and abs(frame['phase'] - row['driverPhase01']) <= 1e-6,
                'Captured feedback identity or phase differs from physics')

    for row in observations:
        kind = row.get('recordType')
        if kind == 'frame':
            latest_physics = row
            source = row['frame']
            for frame in samples.get((row['stage'], source['timeSeconds']), []):
                check_physics_label(frame, row)
        elif kind in ('stopped', 'restart_requested'):
            latest_physics = None
        elif kind == 'render' and row.get('unityFrameCount') in frames:
            number = row['unityFrameCount']
            frame = frames[number]
            # Update may change the test stage between completed fixed steps.
            # Source identity comes from the ordered physics -> camera stream,
            # not from the stage label. Never match an older or future sample.
            require(latest_physics is not None,
                    'Captured camera has no preceding completed physics sample')
            check_physics_label(frame, latest_physics)
            require(type(latest_physics.get('unityFrameCount')) is int
                    and latest_physics['unityFrameCount'] <= number,
                    'Captured camera precedes its completed physics frame')
            physics.add(number)
            require(frame['inputValid'] and frame['stage'] == row['stage']
                    and frame['feedbackSequence'] == row['feedbackSequence'],
                    'Captured feedback identity differs from its rendered frame')
            if latest_physics['stage'] != row['stage']:
                cross_stage_sources[number] = {
                    'image_index': frame['index'], 'camera_unity_frame': number,
                    'physics_unity_frame': latest_physics['unityFrameCount'],
                    'physics_stage': latest_physics['stage'], 'camera_stage': row['stage'],
                    'simulation_time': frame['simulationTime'],
                    'feedback_sequence': frame['feedbackSequence'],
                }
            camera = row['cameraName']
            require(camera in ('MainCamera', 'TwinCamera') and camera not in cameras[number],
                    'Repeated or unexpected captured source camera')
            cameras[number].add(camera)
            position = components(frame['bladeLocal'], 'xyz')
            error = math.dist(position, components(row['actualBladeLocal'], 'xyz'))
            q, rendered = quaternion(frame['paddleRotationLocal']), quaternion(row['actualRotationLocal'])
            angle = math.degrees(2 * math.acos(min(1., abs(sum(a*b for a, b in zip(q, rendered))))))
            require(error <= .0001 and angle <= .05, 'Captured paddle pose differs from its actual render')
            maximum_position_error = max(maximum_position_error, error)
            maximum_angle_error = max(maximum_angle_error, angle)
            length = report.get('gripToBladeLength')
            require(finite(length) and length > 0, 'Missing captured rigid paddle length')
            x, y, z, w = q
            up = (2*(x*y-z*w), 1-2*(x*x+z*z), 2*(y*z+x*w))
            grip = [p + length*axis for p, axis in zip(position, up)]
            require(math.dist(grip, components(frame['upperGripLocal'], 'xyz')) <= .0001,
                    'Captured grip is detached from its rigid paddle')
    valid = {number for number, frame in frames.items() if frame['inputValid']}
    require(valid and valid <= physics, 'Valid captured frame has no matching completed physics sample')
    require(all(cameras[number] == {'MainCamera', 'TwinCamera'} for number in valid),
            'Valid captured frame is missing an actual presentation camera')
    return {'valid_frames_matched': len(valid), 'invalid_input_frames': len(frames)-len(valid),
            'cross_stage_physics_sources': list(cross_stage_sources.values()),
            'maximum_paddle_position_error_m': maximum_position_error,
            'maximum_paddle_rotation_error_deg': maximum_angle_error,
            'scope': 'Valid image labels match both actual camera callbacks and the latest preceding completed physics; '
                     'invalid-input images are retained without claiming valid source poses.'}


def verify_motion_capture(directory, *, require_source_alignment=False):
    from PIL import Image, ImageStat
    directory = Path(directory)
    report = json.loads((directory / 'motion_capture.json').read_text(encoding='utf-8'))
    schema = report.get('schemaVersion')
    require(schema in (1, 2), 'Unsupported motion capture schema')
    if schema == 2:
        require(report.get('captureMode') == 'async_gpu_readback', 'Unsupported asynchronous capture mode')
        require(type(report.get('maximumQueuedBytes')) is int
                and 0 < report['maximumQueuedBytes'] <= 8 * 1024 * 1024,
                'Raw pixels exceeded the bounded capture queue')
    frames = report.get('frames', [])
    require(100 <= len(frames) <= 1800 and report['writtenFrames'] == len(frames), 'Incomplete motion image sequence')
    with wave.open(str(directory / 'listener_mix.wav'), 'rb') as audio:
        require(audio.getnframes() == report['audioSampleFrames'] and audio.getframerate() == report['audioSampleRate'], 'Audio/image sample identity differs')
        duration = audio.getnframes() / audio.getframerate()
    for name in ('firstAudioBufferDspTime', 'firstAudioBufferWallTime'):
        require(finite(report.get(name)), 'Missing actual first audio buffer clock')
    previous = None
    normal = []
    hashes = set()
    max_clock_difference = 0.0
    submission_seconds = []
    readback_seconds = []
    for index, frame in enumerate(frames):
        require(frame['index'] == index and frame['file'] == f'motion_{index:05d}.jpg', 'Image order/path differs')
        for name in ('wallTime', 'captureCompletedWallTime', 'dspTime', 'simulationTime', 'phase'):
            require(finite(frame.get(name)), 'Nonfinite motion clock/phase')
        require(frame['captureCompletedWallTime'] >= frame['wallTime'], 'Capture completion predates image')
        if schema == 2:
            submitted = frame.get('captureSubmittedWallTime')
            readback = frame.get('readbackCompletedWallTime')
            require(finite(submitted) and finite(readback)
                    and frame['wallTime'] <= submitted <= readback <= frame['captureCompletedWallTime'],
                    'GPU capture clocks are missing or out of order')
            submission_seconds.append(submitted - frame['wallTime'])
            readback_seconds.append(readback - frame['wallTime'])
        require(type(frame.get('unityFrame')) is int and frame['unityFrame'] >= 0, 'Invalid Unity frame identity')
        if previous is not None:
            require(frame['unityFrame'] > previous['unityFrame'] and frame['wallTime'] > previous['wallTime'], 'Repeated/reordered rendered frame')
            require(frame['dspTime'] >= previous['dspTime'], 'Audio clock moved backwards')
        previous = frame
        audio_time = frame['dspTime'] - report['firstAudioBufferDspTime']
        require(-.1 <= audio_time <= duration + .1, 'Image is outside actual audio capture')
        clock_difference = abs(audio_time - (frame['wallTime'] - report['firstAudioBufferWallTime']))
        max_clock_difference = max(max_clock_difference, clock_difference)
        require(clock_difference <= .1, 'Audio DSP and image wall clocks drifted apart')
        file = directory / frame['file']
        digest = hashlib.sha256(file.read_bytes()).hexdigest()
        require(digest == frame['sha256'], 'Captured JPEG hash differs')
        hashes.add(digest)
        with Image.open(file) as image:
            require(image.format == 'JPEG' and image.size == (frame['width'], frame['height']), 'Captured JPEG size/format differs')
            require(image.width >= 960 and image.height >= 600, 'Captured image is too small for paddle inspection')
            image.load()
            if frame['stage'] == 'Normal':
                region = image.crop((image.width//8, image.height//3, image.width*7//8, image.height*9//10)).convert('L')
                require(ImageStat.Stat(region).stddev[0] > 8, 'Normal image has no visible scene')
                normal.append(frame)
    require(len(hashes) >= len(frames) * .9, 'Most captured frames are duplicates')
    require(len(normal) >= 100 and normal[-1]['wallTime']-normal[0]['wallTime'] >= 6, 'Continuous normal stroke evidence is too short')
    gaps = [b['wallTime']-a['wallTime'] for a, b in zip(normal, normal[1:])]
    require(max(gaps) <= .12, 'Normal visual capture has a gap longer than 120 ms')
    lighting = (verify_environment_lighting(report['lightingAtLastFrame'])
                if 'lightingAtLastFrame' in report else {'scope': 'lighting was not captured'})
    source_alignment = {'scope': 'Source alignment was not requested'}
    if require_source_alignment:
        with (directory / 'observations.jsonl').open(encoding='utf-8') as stream:
            source_alignment = verify_motion_source_alignment(report, (json.loads(line) for line in stream))
    return {'result': 'PASS', 'frames': len(frames), 'normal_frames': len(normal),
            'normal_span_seconds': normal[-1]['wallTime']-normal[0]['wallTime'],
            'normal_mean_image_gap_seconds': statistics.mean(gaps),
            'normal_maximum_image_gap_seconds': max(gaps),
            'maximum_capture_submission_seconds': max(submission_seconds, default=0.),
            'mean_capture_submission_seconds': statistics.mean(submission_seconds) if submission_seconds else 0.,
            'maximum_gpu_readback_seconds': max(readback_seconds, default=0.),
            'maximum_audio_image_clock_difference_seconds': max_clock_difference,
            'environment_lighting': lighting, 'source_alignment': source_alignment,
            'audio_seconds': duration,
            'scope': 'Recorded frame completeness, clocks and visible pixels; visual and sound quality require separate review'}


def write_motion_review(directory):
    directory = Path(directory)
    report = json.loads((directory / 'motion_capture.json').read_text(encoding='utf-8'))
    frames = [{'t': max(0, f['dspTime']-report['firstAudioBufferDspTime']),
               'file': f['file'], 'stage': f['stage'], 'phase': f['phase'], 'frame': f['unityFrame']}
              for f in report['frames']]
    data = json.dumps(frames, ensure_ascii=False).replace('</', '<\\/')
    html = '''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>龙舟连续动作复核</title>
<style>body{margin:0;background:#18232b;color:#edf2f5;font:16px system-ui}main{max-width:1280px;margin:18px auto}img{width:100%;display:block;background:#000}audio{width:100%;margin:12px 0}p{line-height:1.5}#state{font-variant-numeric:tabular-nums}</style>
<main><p>真实 Player 屏幕帧与实际监听器混音。按采集时间播放；画面约 20 帧/秒，未生成中间姿态。可拖动音频进度逐段检查。</p>
<img id="picture" alt="实际划动截图"><audio id="sound" controls preload="auto" src="listener_mix.wav"></audio>
<p id="state"></p><label>播放速度 <select id="speed"><option value="1">正常</option><option value="0.5">半速</option><option value="0.25">四分之一</option></select></label></main>
<script>const frames=__FRAMES__;const audio=document.getElementById('sound'),picture=document.getElementById('picture'),state=document.getElementById('state');let last=-1;
function update(){let lo=0,hi=frames.length-1;while(lo<hi){const mid=Math.ceil((lo+hi)/2);if(frames[mid].t<=audio.currentTime)lo=mid;else hi=mid-1;}if(lo!==last){last=lo;const f=frames[lo];picture.src=f.file;state.textContent=`${audio.currentTime.toFixed(3)} s · ${f.stage} · 划桨相位 ${f.phase.toFixed(3)} · Unity 帧 ${f.frame}`;for(let i=lo+1;i<Math.min(lo+6,frames.length);i++){const image=new Image();image.src=frames[i].file;}}requestAnimationFrame(update);}document.getElementById('speed').onchange=e=>audio.playbackRate=Number(e.target.value);update();</script></html>'''
    (directory / 'motion_review.html').write_text(html.replace('__FRAMES__', data), encoding='utf-8')
