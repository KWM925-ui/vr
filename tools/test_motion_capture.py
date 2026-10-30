"""Reject incomplete or misleading screen/audio evidence; no Unity process."""
import copy
import hashlib
import json
from pathlib import Path
import shutil
import tempfile
import unittest
import wave

from PIL import Image, ImageDraw

from motion_capture import verify_environment_lighting, verify_motion_capture, verify_motion_source_alignment


class MotionEvidenceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fixture = tempfile.TemporaryDirectory(prefix='dragonboat-motion-fixture-')
        root = Path(cls.fixture.name)
        report = {'schemaVersion': 1, 'writtenFrames': 120,
                  'firstAudioBufferDspTime': 2.0, 'firstAudioBufferWallTime': 100.0,
                  'audioSampleRate': 48000, 'audioSampleFrames': 48000 * 8, 'frames': []}
        for index in range(120):
            picture = Image.new('RGB', (960, 600), '#5383ad')
            draw = ImageDraw.Draw(picture)
            draw.rectangle((0, 300, 959, 599), fill='#163946')
            draw.polygon([(460, 270), (270, 599), (800, 599)], fill='#bc9060')
            draw.rectangle((index * 7, 310, index * 7 + 40, 355), fill='#f5e6bb')
            name = f'motion_{index:05d}.jpg'
            picture.save(root / name, quality=78)
            report['frames'].append({'index': index, 'file': name,
                'sha256': hashlib.sha256((root / name).read_bytes()).hexdigest(),
                'unityFrame': 10 + index * 3, 'width': 960, 'height': 600, 'stage': 'Normal',
                'wallTime': 100.0 + index * .06, 'captureCompletedWallTime': 100.015 + index * .06,
                'dspTime': 2.0 + index * .06, 'simulationTime': index * .06,
                'phase': (index * .045) % 1.0})
        (root / 'motion_capture.json').write_text(json.dumps(report), encoding='utf-8')
        with wave.open(str(root / 'listener_mix.wav'), 'wb') as audio:
            audio.setparams((2, 2, 48000, 0, 'NONE', 'not compressed'))
            audio.writeframes(bytes(report['audioSampleFrames'] * 4))

    @classmethod
    def tearDownClass(cls):
        cls.fixture.cleanup()

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='dragonboat-motion-test-')
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        shutil.copytree(self.fixture.name, self.root, dirs_exist_ok=True)
        self.report = json.loads((self.root / 'motion_capture.json').read_text(encoding='utf-8'))

    def save_report(self):
        (self.root / 'motion_capture.json').write_text(json.dumps(self.report), encoding='utf-8')

    def rejected(self, message):
        self.save_report()
        with self.assertRaisesRegex(ValueError, message):
            verify_motion_capture(self.root)

    def test_actual_timed_sequence_is_accepted(self):
        result = verify_motion_capture(self.root)
        self.assertEqual(result['frames'], 120)
        self.assertEqual(result['result'], 'PASS')
        self.assertAlmostEqual(result['normal_span_seconds'], 7.14)

    def test_repeated_rendered_frame_is_rejected(self):
        self.report['frames'][40]['unityFrame'] = self.report['frames'][39]['unityFrame']
        self.rejected('Repeated/reordered')

    def test_reversed_wall_clock_is_rejected(self):
        self.report['frames'][40]['wallTime'] = self.report['frames'][39]['wallTime'] - .001
        self.rejected('Repeated/reordered')

    def test_audio_clock_reversal_is_rejected(self):
        self.report['frames'][40]['dspTime'] = self.report['frames'][39]['dspTime'] - .001
        self.rejected('Audio clock moved backwards')

    def test_audio_offset_is_rejected(self):
        self.report['firstAudioBufferWallTime'] += .15
        self.rejected('clocks drifted apart')

    def test_jpeg_replacement_is_rejected(self):
        shutil.copyfile(self.root / 'motion_00000.jpg', self.root / 'motion_00001.jpg')
        self.rejected('JPEG hash differs')

    def test_repeated_picture_with_updated_hash_is_rejected(self):
        # Valid times and self-consistent checksums cannot disguise a still image.
        digest = self.report['frames'][0]['sha256']
        for frame in self.report['frames'][1:]:
            shutil.copyfile(self.root / 'motion_00000.jpg', self.root / frame['file'])
            frame['sha256'] = digest
        self.rejected('frames are duplicates')

    def test_missing_image_record_is_rejected(self):
        del self.report['frames'][40]
        self.report['writtenFrames'] -= 1
        self.rejected('Image order/path differs')

    def test_unwritten_image_is_rejected(self):
        self.report['writtenFrames'] -= 1
        self.rejected('Incomplete motion image sequence')

    def test_visual_gap_is_rejected_even_with_monotonic_clocks(self):
        for frame in self.report['frames'][40:]:
            for key in ('wallTime', 'captureCompletedWallTime', 'dspTime', 'simulationTime'):
                frame[key] += .15
        self.rejected('gap longer than 120 ms')

    def test_wrong_audio_length_is_rejected(self):
        self.report['audioSampleFrames'] += 48000
        self.rejected('Audio/image sample identity differs')

    def test_nonfinite_clock_is_rejected(self):
        self.report['frames'][40]['dspTime'] = float('nan')
        self.rejected('Nonfinite motion clock/phase')

    def use_async_capture(self):
        self.report.update(schemaVersion=2, captureMode='async_gpu_readback', maximumQueuedBytes=7680000)
        for frame in self.report['frames']:
            frame['captureSubmittedWallTime'] = frame['wallTime'] + .002
            frame['readbackCompletedWallTime'] = frame['wallTime'] + .08
            frame['captureCompletedWallTime'] = frame['wallTime'] + .09

    def test_async_completion_does_not_retimestamp_presented_images(self):
        self.use_async_capture()
        self.save_report()
        result = verify_motion_capture(self.root)
        self.assertEqual(result['result'], 'PASS')
        self.assertAlmostEqual(result['normal_maximum_image_gap_seconds'], .06)
        self.assertAlmostEqual(result['maximum_capture_submission_seconds'], .002)
        self.assertAlmostEqual(result['maximum_gpu_readback_seconds'], .08)

    def test_async_completion_before_readback_is_rejected(self):
        self.use_async_capture()
        self.report['frames'][40]['readbackCompletedWallTime'] += 1
        self.rejected('GPU capture clocks')

    def test_async_missing_readback_clock_is_rejected(self):
        self.use_async_capture()
        del self.report['frames'][40]['readbackCompletedWallTime']
        self.rejected('GPU capture clocks')

    def test_async_raw_pixel_queue_stays_bounded(self):
        self.use_async_capture()
        self.report['maximumQueuedBytes'] = 8 * 1024 * 1024 + 1
        self.rejected('bounded capture queue')


class MotionSourceAlignmentTests(unittest.TestCase):
    def setUp(self):
        position = dict(x=.7, y=-.2, z=.1)
        rotation = dict(x=0., y=0., z=0., w=1.)
        self.frame = dict(index=0, unityFrame=10, stage='Normal', simulationTime=1.,
                          feedbackSequence=40, phase=.25, inputValid=True,
                          bladeLocal=position, paddleRotationLocal=rotation,
                          upperGripLocal=dict(x=.7, y=.8, z=.1))
        self.report = dict(gripToBladeLength=1., frames=[self.frame])
        self.rows = [dict(recordType='frame', stage='Normal', driverPhase01=.25, unityFrameCount=9,
                         frame=dict(timeSeconds=1., paddles=[dict(valid=True)],
                                    closedLoop=dict(feedbackSequence=40)))]
        for camera in ('MainCamera', 'TwinCamera'):
            self.rows.append(dict(recordType='render', stage='Normal', unityFrameCount=10,
                                  cameraName=camera, feedbackSequence=40,
                                  actualBladeLocal=copy.deepcopy(position),
                                  actualRotationLocal=copy.deepcopy(rotation)))

    def test_actual_callback_and_physics_pair_is_accepted(self):
        # Native Rigidbody interpolation need not equal a discrete physics pose.
        self.frame['boatPosition'] = dict(x=.002, y=.12, z=0.)
        self.rows[0]['frame']['boatPosition'] = dict(x=.006, y=.12, z=0.)
        result = verify_motion_source_alignment(self.report, self.rows)
        self.assertEqual(result['valid_frames_matched'], 1)
        self.assertEqual(result['maximum_paddle_position_error_m'], 0.)

    def test_update_stage_change_keeps_the_last_completed_physics_sample(self):
        # Real run 221421_88337484: Update enters Disconnected before the
        # next fixed step; both cameras still display the completed Normal step.
        self.frame['stage'] = 'Disconnected'
        for row in self.rows[1:]:
            row['stage'] = 'Disconnected'
        result = verify_motion_source_alignment(self.report, self.rows)
        self.assertEqual(result['valid_frames_matched'], 1)

    def test_earlier_physics_label_cannot_skip_a_newer_completed_step(self):
        newer = copy.deepcopy(self.rows[0])
        newer['unityFrameCount'] = 10
        newer['frame']['timeSeconds'] = 1.01
        # A held encoder packet can have identical sequence, phase and pose
        # across physical steps. The image must still name the latest step.
        self.rows.insert(1, newer)
        with self.assertRaises(ValueError):
            verify_motion_source_alignment(self.report, self.rows)

    def test_future_physics_record_cannot_validate_an_earlier_camera(self):
        with self.assertRaises(ValueError):
            verify_motion_source_alignment(self.report, self.rows[1:] + self.rows[:1])

    def test_stop_and_restart_clear_the_previous_completed_source(self):
        for kind in ('stopped', 'restart_requested'):
            with self.subTest(kind=kind):
                rows = copy.deepcopy(self.rows)
                rows.insert(1, dict(recordType=kind, stage='Stopped', unityFrameCount=10))
                with self.assertRaises(ValueError):
                    verify_motion_source_alignment(self.report, rows)

    def test_image_stage_must_still_match_its_actual_cameras(self):
        self.frame['stage'] = 'Disconnected'
        with self.assertRaises(ValueError):
            verify_motion_source_alignment(self.report, self.rows)

    def test_wrong_image_feedback_or_physics_phase_is_rejected(self):
        for key, value in [('feedbackSequence', 12385), ('phase', .75), ('simulationTime', 2.)]:
            with self.subTest(field=key):
                report = copy.deepcopy(self.report)
                report['frames'][0][key] = value
                with self.assertRaises(ValueError):
                    verify_motion_source_alignment(report, self.rows)

    def test_displaced_or_rotated_paddle_label_is_rejected(self):
        for key, value in [('bladeLocal', dict(x=1.2, y=-.2, z=.1)),
                           ('paddleRotationLocal', dict(x=0., y=.70710678, z=0., w=.70710678)),
                           ('upperGripLocal', dict(x=.7, y=1.3, z=.1))]:
            with self.subTest(field=key):
                report = copy.deepcopy(self.report)
                report['frames'][0][key] = value
                with self.assertRaises(ValueError):
                    verify_motion_source_alignment(report, self.rows)

    def test_missing_or_repeated_camera_is_rejected(self):
        for rows in (self.rows[:-1], self.rows + [self.rows[-1]]):
            with self.assertRaises(ValueError):
                verify_motion_source_alignment(self.report, rows)

    def test_false_invalid_label_cannot_skip_source_verification(self):
        self.frame['inputValid'] = False
        with self.assertRaisesRegex(ValueError, 'validity differs'):
            verify_motion_source_alignment(self.report, self.rows)

    def test_unobserved_invalid_startup_image_is_retained_and_identified(self):
        startup = copy.deepcopy(self.frame)
        startup.update(index=-1, unityFrame=1, simulationTime=0., inputValid=False)
        self.report['frames'].insert(0, startup)
        result = verify_motion_source_alignment(self.report, self.rows)
        self.assertEqual(result['valid_frames_matched'], 1)
        self.assertEqual(result['invalid_input_frames'], 1)


class EnvironmentProbeTests(unittest.TestCase):
    def probe(self, ambient=.3, paddle=.3):
        return {'skybox': 'actual HDRI sky', 'ambientMode': 0,
                'ambientRadiance': [dict(r=ambient, g=ambient, b=ambient)] * 6,
                'paddleRadiance': [dict(r=paddle, g=paddle, b=paddle)] * 6}

    def test_nonzero_actual_probes_are_accepted(self):
        self.assertEqual(len(verify_environment_lighting(self.probe())['paddleRadiance_luminance']), 6)

    def test_skybox_name_does_not_disguise_a_black_probe(self):
        with self.assertRaisesRegex(ValueError, 'ambientRadiance is black'):
            verify_environment_lighting(self.probe(ambient=0.))

    def test_lit_environment_does_not_disguise_an_unlit_paddle_probe(self):
        with self.assertRaisesRegex(ValueError, 'paddleRadiance is black'):
            verify_environment_lighting(self.probe(paddle=0.))


if __name__ == '__main__':
    unittest.main()
