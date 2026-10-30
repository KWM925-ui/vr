"""Mechanical stop boundary tests; these do not establish real joint limits."""
import copy
import math
from pathlib import Path
import unittest

from test_closed_loop_controller import command, controller


def stopped_command():
    payload = command(paired_geometry=True)
    payload.update(hardwareStopsEnabled=True,
                   hardwareStopGeometry=[1, .50756, .55727, .085, .21, .0125, .0225, .02])
    payload['desiredGripPosition'] = [.605, .75, 0.0]
    return payload


def kinetic_energy(model, payload):
    return (.5 * payload['gripMassKg'] * sum(v*v for v in model.velocity)
            + .5 * payload['angularInertia'] * sum(v*v for v in model.angular_velocity))


class HardwareStopTests(unittest.TestCase):
    def test_invalid_stops_are_rejected_and_geometry_is_fixed_per_session(self):
        for change in ({'hardwareStopsEnabled': 1}, {'hardwareStopGeometry': []},
                       {'hardwareStopGeometry': [0, .5, .2, .085, .21, .0125, .0225, .02]},
                       {'hardwareStopGeometry': [1, .5, math.nan, .085, .21, .0125, .0225, .02]},
                       {'hardwareStopGeometry': [1, 1.2, .2, .3, .21, .0125, .0225, .02]}):
            with self.subTest(change=change), self.assertRaises(ValueError):
                controller.validate_step(dict(stopped_command(), **change))
        model = controller.ControllerModel()
        payload = controller.validate_step(stopped_command())
        model.set_command(payload, 0.)
        changed = copy.deepcopy(payload)
        changed['hardwareStopGeometry'][7] += .001
        with self.assertRaisesRegex(ValueError, 'explicit reset'):
            model.set_command(changed, .01)

    def test_contact_impulses_dissipate_energy_and_preserve_free_translation(self):
        payload = stopped_command()
        payload['desiredGripPosition'][0] = .50756 + .085 + .02
        model = controller.ControllerModel()
        model.set_command(controller.validate_step(payload), 0.)
        model.velocity = [-1., .2, .3]
        before = kinetic_energy(model, payload)
        model.constrain_hardware_stops(controller.DT)
        self.assertTrue(model.hardware_stop_active)
        self.assertLess(kinetic_energy(model, payload), before)
        self.assertAlmostEqual(model.velocity[1], .2, places=12)
        self.assertAlmostEqual(model.velocity[2], .3, places=12)
        self.assertGreater(model.hardware_stop_reaction_force[0], 0.)

    def test_cad_preload_changes_actual_motion_but_cannot_cross_the_stop(self):
        profile_path = Path(__file__).resolve().parents[1] / 'DragonBoatVR/Assets/StreamingAssets/tendon_rig_cad.json'
        import json
        profile = json.loads(profile_path.read_text(encoding='utf-8-sig'))
        payload = stopped_command()
        tendons = profile['tendons']
        payload.update(tensionTargets=[t['preTensionN'] for t in tendons],
                       maxTensions=[t['maxTensionN'] for t in tendons],
                       anchors=[t['anchorLocal'][axis] for t in tendons for axis in 'xyz'],
                       attachments=[t['attachLocal'][axis] for t in tendons for axis in 'xyz'])
        payload['desiredGripRotation'] = controller.q_from_vector([0., 0., math.radians(15.)])
        blade = [.75, .2, 0.]
        payload['desiredGripPosition'] = controller.add(blade, controller.q_rotate(payload['desiredGripRotation'], [0., 1., 0.]))
        model = controller.ControllerModel()
        model.set_command(controller.validate_step(payload), 0.)
        stop_frames = 0
        for tick in range(1, 401):
            now = tick * controller.DT
            if tick % 10 == 0:
                payload['commandSequence'] += 1
                model.set_command(controller.validate_step(payload), now)
            model.step(controller.DT, now)
            self.assertFalse(model.fault, model.fault)
            stop_frames += int(model.hardware_stop_active)
            # Independently test the actual blade's eight box corners against
            # the outboard half-space, including the full thickness.
            actual_blade = controller.sub(model.position, controller.q_rotate(model.rotation, [0., 1., 0.]))
            for x in (-.085, .085):
                for y in (-.21, .21):
                    for z in (-.0125, .0125):
                        corner = controller.add(actual_blade, controller.q_rotate(model.rotation, [x, y, z]))
                        self.assertGreaterEqual(corner[0], .50756 + .02 - 1e-7)
            self.assertLessEqual(abs(actual_blade[0]), 1.4 + 1e-7)
            self.assertTrue(all(0. <= tension <= 300. for tension in model.tensions))
        feedback = model.feedback(now, now, 400)
        self.assertTrue(feedback['hardwareStopsEnabled'])
        self.assertGreater(stop_frames, 0)
        self.assertGreater(math.dist(model.position, payload['desiredGripPosition']), .001)
        self.assertGreater(controller.norm(feedback['forceLocal']), 1.)
        self.assertEqual(feedback['rawEncoderCounts'][:3], [round(v * 10000) for v in actual_blade])
        self.assertIn('IDEAL_HARDWARE_STOPS_ASSUMED', feedback['status'])

    def test_left_side_stop_uses_the_same_physical_limits(self):
        payload = stopped_command()
        payload['hardwareStopGeometry'][0] = -1
        payload['desiredGripPosition'][0] *= -1
        model = controller.ControllerModel()
        model.set_command(controller.validate_step(payload), 0.)
        model.velocity = [1., 0., 0.]
        model.step(controller.DT, controller.DT)
        self.assertFalse(model.fault, model.fault)
        blade = controller.sub(model.position, controller.q_rotate(model.rotation, [0., 1., 0.]))
        self.assertLess(blade[0], -.5)
        self.assertTrue(model.hardware_stop_active)


if __name__ == '__main__':
    unittest.main(verbosity=2)
