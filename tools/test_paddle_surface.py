"""Analytical checks for submerged geometry, distributed drag and moving water."""

import math
import unittest

import numpy as np

from paddle_surface import WaterSurface, integrate_drag, rectangle_mesh, rotation_matrix, silhouette_mesh


def paddle(velocity=(0., 0., -2.), omega=(0., 0., 0.), rotation=(0., 0., 0., 1.), position=(0., 0., 0.)):
    return dict(bladePosition=position, bladeVelocity=velocity, bladeAngularVelocity=omega, bladeRotation=rotation)


def drag(mesh, state=None, water=None, grip=(0., 1., 0.)):
    return integrate_drag(mesh, WaterSurface(base_height=1.) if water is None else water, 0.,
                          paddle() if state is None else state, density=1000., cd_flat=1.28, cd_edge=.05, grip=grip)


class PaddleSurfaceTests(unittest.TestCase):
    def test_uniform_drag_and_grip_moment_match_analytic_rectangle(self):
        result = drag(rectangle_mesh(.2, .4))
        self.assertAlmostEqual(result["wet_area_m2"], .08)
        np.testing.assert_allclose(result["force_world_n"], [0., 0., 204.8], atol=1e-10)
        np.testing.assert_allclose(result["torque_at_grip_world_nm"], [-204.8, 0., 0.], atol=1e-10)
        self.assertAlmostEqual(result["relative_water_dissipation_w"], 409.6)
        self.assertAlmostEqual(result["power_into_paddle_world_w"], -409.6)

    def test_partial_immersion_clips_area_and_moves_force_lever(self):
        result = drag(rectangle_mesh(.2, .4, ny=1), water=WaterSurface())
        self.assertAlmostEqual(result["wet_area_m2"], .04)
        np.testing.assert_allclose(result["wet_area_centroid_world_m"], [0., -.1, 0.], atol=1e-12)
        np.testing.assert_allclose(result["force_world_n"], [0., 0., 102.4], atol=1e-10)
        np.testing.assert_allclose(result["torque_at_grip_world_nm"], [-112.64, 0., 0.], atol=1e-10)
        dry = drag(rectangle_mesh(.2, .4), water=WaterSurface(base_height=-1.))
        self.assertEqual(dry["wet_area_m2"], 0.)
        np.testing.assert_array_equal(dry["force_world_n"], np.zeros(3))

    def test_pure_rotation_has_zero_force_and_nonzero_resisting_moment(self):
        omega = 4.
        result = drag(rectangle_mesh(.2, .4, nx=16), paddle(velocity=(0., 0., 0.), omega=(0., omega, 0.)), grip=(0., 0., 0.))
        # M_y = -rho*Cd*omega^2*length*half_width^4/4 for this plate.
        expected = -1000 * 1.28 * omega**2 * .4 * .1**4 / 4
        np.testing.assert_allclose(result["force_world_n"], np.zeros(3), atol=1e-10)
        np.testing.assert_allclose(result["torque_at_grip_world_nm"], [0., expected, 0.], atol=1e-10)
        self.assertAlmostEqual(result["power_into_paddle_world_w"], expected * omega)

    def test_uniform_water_translation_preserves_drag_but_can_supply_world_work(self):
        mesh = rectangle_mesh(.2, .4)
        still = drag(mesh)
        moving = drag(mesh, paddle(velocity=(0., 0., 1.)), WaterSurface(base_height=1., current=(0., 0., 3.)))
        np.testing.assert_allclose(moving["force_world_n"], still["force_world_n"])
        self.assertAlmostEqual(moving["relative_water_dissipation_w"], still["relative_water_dissipation_w"])
        self.assertAlmostEqual(moving["power_into_paddle_world_w"], 204.8)

    def test_yaw_rotation_preserves_power_and_rotates_the_wrench(self):
        mesh = rectangle_mesh(.2, .4)
        q = [0., math.sin(.37), 0., math.cos(.37)]
        r = rotation_matrix(q)
        original = drag(mesh, paddle(omega=(.1, .2, .3)))
        moved = drag(mesh, paddle(velocity=r @ [0., 0., -2.], omega=r @ [.1, .2, .3], rotation=q), grip=r @ [0., 1., 0.])
        np.testing.assert_allclose(moved["force_world_n"], r @ original["force_world_n"], atol=1e-10)
        np.testing.assert_allclose(moved["torque_at_grip_world_nm"], r @ original["torque_at_grip_world_nm"], atol=1e-10)
        self.assertAlmostEqual(moved["relative_water_dissipation_w"], original["relative_water_dissipation_w"])

    def test_cad_projection_keeps_a_hole_and_counts_front_and_back_once(self):
        parts = [rectangle_mesh(.08, .4) + [-.06, 0., 0.], rectangle_mesh(.08, .4) + [.06, 0., 0.],
                 rectangle_mesh(.04, .15) + [0., -.125, 0.], rectangle_mesh(.04, .15) + [0., .125, 0.]]
        face = np.concatenate(parts)
        solid_faces = np.concatenate((face, face + [0., 0., .01]))
        mesh = silhouette_mesh(solid_faces, -.2, .2, 400)
        result = drag(mesh)
        expected_area = .2 * .4 - .04 * .1
        self.assertAlmostEqual(result["wet_area_m2"], expected_area)
        self.assertAlmostEqual(result["force_world_n"][2], .5 * 1000 * 1.28 * expected_area * 4)

    def test_wave_velocity_includes_clock_scale_and_amplitude_transition(self):
        wave = dict(direction=[1., 0.], wavelength=2 * math.pi, amplitude=.1)
        water = WaterSurface([wave], time_scale=2., intensity_rate=.5, horizontal=False)
        height, velocity = water.sample(np.array([[0., 0., 0.], [math.pi / 2, 0., 0.]]), 0.)
        np.testing.assert_allclose(height, [0., .1], atol=1e-12)
        np.testing.assert_allclose(velocity[:, 1], [-.2 * math.sqrt(9.81), .05], atol=1e-12)

    def test_invalid_pose_and_coefficients_are_not_computed(self):
        with self.assertRaises(ValueError):
            drag(rectangle_mesh(.2, .4), paddle(rotation=[0., 0., 0., 0.]))
        with self.assertRaises(ValueError):
            integrate_drag(rectangle_mesh(.2, .4), WaterSurface(), 0., paddle(), density=1000., cd_flat=-1., cd_edge=.05)


if __name__ == "__main__":
    unittest.main()
