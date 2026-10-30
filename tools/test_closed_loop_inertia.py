"""Independent analytic limits for the moving-platform mechanical model."""
import math
import unittest
from test_closed_loop_controller import controller, command, advance


class MovingFrameTests(unittest.TestCase):
    def assertVector(self, actual, expected, tolerance=1e-9):
        self.assertLessEqual(math.dist(actual, expected), tolerance, (actual, expected))

    def terms(self, rotation=(0, 0, 0, 1), acceleration=(0, 0, 0), omega=(0, 0, 0),
              alpha=(0, 0, 0), position=(0, 0, 0), velocity=(0, 0, 0), spin=(0, 0, 0)):
        return controller.moving_frame_acceleration(rotation, acceleration, omega, alpha, position, velocity, spin)

    def test_translation_and_rotated_axes(self):
        self.assertVector(self.terms(acceleration=(2, -3, 4))[0], (-2, 3, -4))
        rotation = controller.q_from_vector([0, 0, math.pi / 2])
        self.assertVector(self.terms(rotation=rotation, acceleration=(2, 0, 0))[0], (0, 2, 0))

    def test_centrifugal_coriolis_and_euler_separately(self):
        self.assertVector(self.terms(omega=(0, 0, 2), position=(3, 0, 0))[0], (12, 0, 0))
        self.assertVector(self.terms(omega=(0, 0, 2), velocity=(3, 0, 0))[0], (0, -12, 0))
        translation, angular = self.terms(alpha=(0, 0, 2), position=(3, 0, 0))
        self.assertVector(translation, (0, -6, 0))
        self.assertVector(angular, (0, 0, -2))
        self.assertVector(self.terms(omega=(0, 0, 2), spin=(3, 0, 0))[1], (0, -6, 0))

    def test_common_base_rotation_does_not_change_relative_dynamics(self):
        rotation = controller.q_from_unity_euler([12, -24, 7])
        base_change = controller.q_from_unity_euler([-32, 16, 41])
        vectors = ([1, 2, -3], [-2, 1, 0.5], [0.3, -0.6, 0.1])
        original = self.terms(rotation, *vectors, position=[0.4, 0.8, -0.2], velocity=[1, -1, 2], spin=[0.2, 0.4, -0.1])
        transformed = self.terms(controller.q_multiply(base_change, rotation),
            *[controller.q_rotate(base_change, v) for v in vectors],
            position=[0.4, 0.8, -0.2], velocity=[1, -1, 2], spin=[0.2, 0.4, -0.1])
        for actual, expected in zip(transformed, original):
            self.assertVector(actual, expected)

    def test_free_particle_fixed_in_inertial_space(self):
        # A stationary inertial point viewed from a uniform rotating frame has
        # relative velocity -omega cross r and acceleration -omega^2 r.
        actual, _ = self.terms(omega=(0, 0, 2), position=(3, 0, 0), velocity=(0, -6, 0))
        self.assertVector(actual, (-12, 0, 0))

    def test_platform_hold_includes_braking_then_zero_acceleration(self):
        model = controller.ControllerModel()
        payload = command(paired_geometry=True)
        model.set_command(controller.validate_step(payload), 0)
        model.platform_velocity = [0.02, 0.01, 0]
        model.platform_angular_velocity = [0, 0, 0.03]
        model.step(controller.DT, 0.2)  # Existing wall-clock watchdog expires.
        self.assertTrue(model.watchdog(0.2))
        self.assertVector(model.platform_acceleration, [-4, -2, 0])
        self.assertVector(model.platform_angular_acceleration, [0, 0, -6])
        self.assertVector(model.platform_induced_acceleration, [0.4, 2, 0])
        self.assertVector(model.platform_induced_angular_acceleration, [0, 0, 6])
        model.step(controller.DT, 0.205)
        self.assertVector(model.platform_induced_acceleration, [0, 0, 0])
        self.assertVector(model.platform_induced_angular_acceleration, [0, 0, 0])

    def test_passive_hand_relaxes_without_energy_growth(self):
        model, payload = controller.ControllerModel(), command(paired_geometry=True)
        model.set_command(controller.validate_step(payload), 0)
        model.position[0] += 0.02
        model.rotation = controller.q_from_vector([0, 0, 0.1])
        def energy():
            return (0.5 * payload['gripMassKg'] * sum(v*v for v in model.velocity)
                + 0.5 * payload['gripStiffnessNPerM'] * math.dist(model.position, payload['desiredGripPosition'])**2
                + 0.5 * payload['angularInertia'] * sum(v*v for v in model.angular_velocity)
                + 0.5 * payload['angularStiffness'] * controller.norm(controller.q_to_vector(model.rotation))**2)
        initial = previous = energy()
        for index in range(400):
            now = (index + 1) * controller.DT
            if index % 10 == 0:
                model.set_command(controller.validate_step(payload), now)
            model.step(controller.DT, now)
            current = energy()
            self.assertLessEqual(current, previous + 1e-12)
            previous = current
            self.assertFalse(model.motion_limited)
            self.assertFalse(model.workspace_constrained)
        self.assertLess(energy(), initial * 1e-6)

    def test_angular_compliance_matches_torque_over_stiffness(self):
        model, payload = controller.ControllerModel(), command(paired_geometry=True)
        model.set_command(controller.validate_step(payload), 0)
        # A known applied torque isolates the impedance equation from allocator
        # geometry. It does not assert that the current cables can create it.
        model.cable_wrench = lambda: ([0.0]*3, [0.0, 0.0, 10.0], [1200.0])
        advance(model, payload, 2.0)
        angle = controller.q_to_vector(model.rotation)
        self.assertVector(angle, [0, 0, 10/payload['angularStiffness']], 1e-7)
        self.assertVector(model.angular_velocity, [0, 0, 0], 1e-6)
        self.assertFalse(model.motion_limited)

    def test_corotating_hand_damper_does_not_brake_matching_motion(self):
        model, payload = controller.ControllerModel(), command(paired_geometry=True)
        payload['desiredGripAngularVelocity'] = [0.0, 1.0, 0.0]
        model.set_command(controller.validate_step(payload), 0)
        model.angular_velocity = [0.0, 1.0, 0.0]
        # Equal hand/rod orientation and angular velocity means no relative
        # spring/damper torque. With zero cable/platform torque omega is constant.
        model.step(controller.DT, controller.DT)
        self.assertVector(model.angular_velocity, [0.0, 1.0, 0.0], 1e-12)
        self.assertVector(controller.q_to_vector(model.rotation), [0.0, controller.DT, 0.0], 1e-12)

    def test_expired_source_cannot_supply_angular_feedforward(self):
        model, payload = controller.ControllerModel(), command(paired_geometry=True)
        payload['desiredGripAngularVelocity'] = [0.0, 1.0, 0.0]
        model.set_command(controller.validate_step(payload), 0, source_time=-0.2)
        model.angular_velocity = [0.0, 1.0, 0.0]
        model.step(controller.DT, controller.DT)
        self.assertFalse(model.execution_fresh(controller.DT))
        self.assertVector(model.angular_velocity, [0.0, 1/(1+controller.DT*3/0.15), 0.0], 1e-12)

    def test_angular_reference_contract_and_legacy_stationary_reference(self):
        payload = command(paired_geometry=True)
        del payload['desiredGripAngularVelocity']
        self.assertEqual(controller.validate_step(payload)['desiredGripAngularVelocity'], [0.0]*3)
        for value in (None, [0, 0], [False, 0, 0], [float('nan'), 0, 0]):
            with self.subTest(value=value), self.assertRaises(ValueError):
                controller.validate_step(dict(payload, desiredGripAngularVelocity=value))
        payload.update(cableValid=False, platformValid=False, desiredGripAngularVelocity=[0, 1, 0])
        with self.assertRaisesRegex(ValueError, 'zero/hold heartbeat'):
            controller.CommandSourceWindow().authorize(controller.validate_step(payload), 0)

    def test_sinusoidal_angular_reference_converges_to_analytic_solution(self):
        frequency, amplitude = 2*math.pi*.75, .1
        errors = []
        for step in (controller.DT, controller.DT/2):
            model, payload = controller.ControllerModel(), command(paired_geometry=True)
            model.set_command(controller.validate_step(payload), 0)
            inertia, damping, stiffness = payload['angularInertia'], payload['angularDamping'], payload['angularStiffness']
            response = complex(stiffness, damping*frequency) / complex(stiffness-inertia*frequency**2, damping*frequency)
            squared = []
            for index in range(round(4/step)):
                now = (index+1)*step
                payload['desiredGripRotation'] = controller.q_from_vector([0, amplitude*math.sin(now*frequency), 0])
                payload['desiredGripAngularVelocity'] = [0, amplitude*frequency*math.cos(now*frequency), 0]
                model.set_command(controller.validate_step(payload), now)
                model.step(step, now)
                if now > 2:
                    expected = amplitude*(response.real*math.sin(now*frequency)+response.imag*math.cos(now*frequency))
                    squared.append((controller.q_to_vector(model.rotation)[1]-expected)**2)
                self.assertFalse(model.motion_limited)
            errors.append(math.sqrt(sum(squared)/len(squared)))
        self.assertLess(errors[0], .003)
        self.assertLess(errors[1], errors[0]*.55)

    def test_sinusoidal_grip_response_converges_to_analytic_solution(self):
        frequency, amplitude = 2*math.pi*.75, .1
        errors = []
        for step in (controller.DT, controller.DT/2):
            model, payload = controller.ControllerModel(), command(paired_geometry=True)
            model.set_command(controller.validate_step(payload), 0)
            mass, damping, stiffness = payload['gripMassKg'], payload['gripDampingNsPerM'], payload['gripStiffnessNPerM']
            response = complex(stiffness, damping*frequency) / complex(stiffness-mass*frequency**2, damping*frequency)
            squared = []
            for index in range(round(4/step)):
                now = (index+1)*step
                payload['desiredGripPosition'][0] = amplitude*math.sin(now*frequency)
                payload['desiredGripVelocity'][0] = amplitude*frequency*math.cos(now*frequency)
                model.set_command(controller.validate_step(payload), now)
                model.step(step, now)
                if now > 2:
                    expected = amplitude*(response.real*math.sin(now*frequency)+response.imag*math.cos(now*frequency))
                    squared.append((model.position[0]-expected)**2)
                self.assertFalse(model.motion_limited)
            errors.append(math.sqrt(sum(squared)/len(squared)))
        self.assertLess(errors[0], .003)  # Below one 5 ms target displacement plus integration error.
        self.assertLess(errors[1], errors[0]*.55)  # First-order integration must converge under refinement.


class LocalDragRenderingTests(unittest.TestCase):
    @staticmethod
    def payload():
        p = command(tension=40, paired_geometry=True)
        p.update(platformValid=False, dragFeedbackEnabled=True, dragDissipationW=40.0,
                 sourceGripVelocity=[-1.0, 0.0, 0.0], sourceGripAngularVelocity=[0.0]*3,
                 requestForce=[40.0, 0.0, 0.0], requestTorque=[0.0]*3, minTensions=[5.0])
        return p

    def step(self, payload, velocity, angular=(0, 0, 0), now=controller.DT):
        model = controller.ControllerModel()
        model.set_command(controller.validate_step(payload), 0, source_time=0)
        model.tensions = payload['tensionTargets'][:]
        model.velocity = list(velocity); model.angular_velocity = list(angular)
        model.step(controller.DT, now)
        return model

    def test_stopped_or_reversed_motion_releases_old_drag_through_original_motor(self):
        for velocity in ([0, 0, 0], [1, 0, 0]):
            with self.subTest(velocity=velocity):
                model = self.step(self.payload(), velocity)
                # Only the target changes to the declared 5 N pretension. The
                # real 35 ms motor response is retained, so force is not erased.
                expected = 40 + (5-40)*controller.DT/(.035+controller.DT)
                self.assertAlmostEqual(model.tensions[0], expected, places=10)
                self.assertGreater(model.tensions[0], 5)

    def test_rotational_reversal_uses_grip_torque_and_radians_per_second(self):
        payload = self.payload()
        payload.update(requestForce=[0.0]*3, requestTorque=[40.0, 0.0, 0.0],
                       sourceGripVelocity=[0.0]*3, sourceGripAngularVelocity=[-1.0, 0.0, 0.0])
        model = self.step(payload, [0, 0, 0], [1, 0, 0])
        self.assertAlmostEqual(model.tensions[0], 35.625, places=10)

    def test_moving_water_can_still_accelerate_a_paddle(self):
        payload = self.payload()
        # At the source, a stationary paddle faces a +1 m/s current: +40 N
        # force and 40 W of dissipation. At +0.5 m/s relative speed has halved,
        # so quadratic drag is one quarter, though world power is positive.
        payload['sourceGripVelocity'] = [0.0]*3
        model = self.step(payload, [.5, 0, 0])
        target = 5 + (40-5)*.25
        expected = 40 + (target-40)*controller.DT/(.035+controller.DT)
        self.assertAlmostEqual(model.tensions[0], expected, places=10)
        self.assertGreater(model.force[0], 0)

    def test_unchanged_source_and_increasing_resistance_never_amplify_targets(self):
        for velocity in ([-1, 0, 0], [-2, 0, 0]):
            model = self.step(self.payload(), velocity)
            self.assertEqual(model.tensions, [40.0])

    def test_common_frame_rotation_preserves_target_reduction(self):
        p = self.payload(); p['sourceGripAngularVelocity'] = [0.0, -.4, .3]
        p['requestTorque'] = [0.0, 5.0, -2.0]
        velocity, angular = [-.4, .1, .05], [0, .2, -.1]
        expected = self.step(p, velocity, angular).tensions[0]
        rotation = controller.q_from_unity_euler([23, -31, 41])
        for key in ('sourceGripVelocity', 'sourceGripAngularVelocity', 'requestForce', 'requestTorque'):
            p[key] = controller.q_rotate(rotation, p[key])
        actual = self.step(p, controller.q_rotate(rotation, velocity), controller.q_rotate(rotation, angular)).tensions[0]
        self.assertAlmostEqual(actual, expected, places=10)

    def test_expiry_removes_pretension_target_and_preserves_motor_decay(self):
        model = self.step(self.payload(), [1, 0, 0], now=.101)
        self.assertAlmostEqual(model.tensions[0], 35.0, places=10)
        self.assertFalse(model.execution_fresh(.101))

    def test_release_respects_original_motor_slew_limit(self):
        payload = self.payload(); payload['tensionTargets'] = [100.0]
        model = self.step(payload, [1, 0, 0])
        self.assertEqual(model.tensions, [90.0])

    def test_drag_contract_rejects_malformed_or_unauthorized_rendering(self):
        for change in ({'dragFeedbackEnabled': 1}, {'dragDissipationW': -1},
                       {'dragDissipationW': float('nan')}, {'dragDissipationW': 0},
                       {'sourceGripVelocity': [0, 0]}, {'sourceGripAngularVelocity': [0, False, 0]},
                       {'minTensions': [301]}, {'minTensions': [-1]},
                       {'minTensions': [41]}, {'cableValid': False}, {'active': False}):
            with self.subTest(change=change), self.assertRaises(ValueError):
                controller.validate_step(dict(self.payload(), **change))


if __name__ == '__main__':
    unittest.main(verbosity=2)
