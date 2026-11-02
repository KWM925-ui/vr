"""Independent range-geometry cases, including global ambiguity and degeneracy."""

import unittest

import numpy as np

from cable_pose import pose_error_jacobians, position_candidates, range_sensitivity, sphere_centres


class CablePoseTests(unittest.TestCase):
    def test_three_ranges_keep_both_mirror_positions(self):
        anchors = np.array([[0., 0., 0.], [2., 0., 0.], [0., 2., 0.]])
        position = np.array([.4, .6, .8])
        lengths = np.linalg.norm(position - anchors, axis=1)
        result = position_candidates(anchors, np.zeros_like(anchors), np.eye(3), lengths)
        self.assertEqual(result["status"], "TWO_POSITION_SOLUTIONS")
        self.assertEqual(len(result["candidates"]), 2)
        for point in ([.4, .6, .8], [.4, .6, -.8]):
            self.assertLess(min(np.linalg.norm(p - point) for p in result["candidates"]), 1e-12)

    def test_fourth_nonplanar_range_resolves_the_mirror(self):
        anchors = np.array([[0., 0., 0.], [2., 0., 0.], [0., 2., 0.], [0., 0., 2.]])
        position = np.array([.4, .6, .8])
        result = position_candidates(anchors, np.zeros_like(anchors), np.eye(3), np.linalg.norm(position - anchors, axis=1))
        self.assertEqual(result["status"], "UNIQUE_POSITION_SOLUTION")
        np.testing.assert_allclose(result["candidates"][0], position, atol=1e-12)
        # A fourth measurement in the same plane does not remove the mirror.
        anchors[-1] = [2., 2., 0.]
        result = position_candidates(anchors, np.zeros_like(anchors), np.eye(3), np.linalg.norm(position - anchors, axis=1))
        self.assertEqual(len(result["candidates"]), 2)

    def test_known_orientation_and_attachment_offsets_are_used(self):
        anchors = np.array([[0., 0., 0.], [2., 0., 0.], [0., 2., 0.]])
        attachments = np.array([[.1, .2, 0.], [.3, -.1, .1], [-.1, .4, -.2]])
        rotation = np.array([[0., -1., 0.], [1., 0., 0.], [0., 0., 1.]])
        position = np.array([.4, .6, .8])
        lengths = np.linalg.norm(anchors - (position + attachments @ rotation.T), axis=1)
        result = position_candidates(anchors, attachments, rotation, lengths)
        self.assertLess(min(np.linalg.norm(p - position) for p in result["candidates"]), 1e-12)
        # Rigidly changing the global origin must not affect the local geometry.
        shift = np.array([10000., -20000., 30000.])
        shifted = position_candidates(anchors + shift, attachments, rotation, lengths)
        self.assertLess(min(np.linalg.norm(p - position - shift) for p in shifted["candidates"]), 1e-7)

    def test_single_sensor_and_collinear_centres_do_not_invent_a_position(self):
        for anchors in (np.zeros((1, 3)), np.array([[0., 0., 0.], [1., 0., 0.], [2., 0., 0.]])):
            result = position_candidates(anchors, np.zeros_like(anchors), np.eye(3), np.linalg.norm(anchors - [0., 1., 0.], axis=1))
            self.assertEqual(result["candidates"], [])
            self.assertLess(result["difference_rank"], 2)

    def test_tangent_intersection_has_no_vertical_first_order_observability(self):
        anchors = np.array([[0., 0., 0.], [2., 0., 0.], [0., 2., 0.]])
        position = np.array([.4, .6, 0.])
        result = position_candidates(anchors, np.zeros_like(anchors), np.eye(3), np.linalg.norm(position - anchors, axis=1))
        self.assertEqual(result["status"], "TANGENT_POSITION_SOLUTION")
        sensitivity = range_sensitivity(position, anchors)
        self.assertEqual(sensitivity["rank"], 2)
        self.assertIsNone(sensitivity["amplification"])

    def test_local_length_jacobian_matches_independent_finite_displacements(self):
        anchors = np.array([[0., 0., 0.], [2., 0., 0.], [0., 2., 0.]])
        position = np.array([.4, .6, .8])
        result = range_sensitivity(position, anchors)
        direction = np.array([.3, -.2, .7])
        h = 1e-6
        finite = (np.linalg.norm(position + h * direction - anchors, axis=1) - np.linalg.norm(position - h * direction - anchors, axis=1)) / (2 * h)
        np.testing.assert_allclose(finite, result["jacobian"] @ direction, atol=1e-9)
        self.assertEqual(result["rank"], 3)

    def test_inconsistent_lengths_and_reflection_are_rejected(self):
        anchors = np.array([[0., 0., 0.], [2., 0., 0.], [0., 2., 0.]])
        with self.assertRaises(ValueError):
            position_candidates(anchors, np.zeros_like(anchors), np.eye(3), [.1, .1, .1])
        with self.assertRaises(ValueError):
            sphere_centres(anchors, np.zeros_like(anchors), np.diag([1., 1., -1.]))
        with self.assertRaises(ValueError):
            position_candidates(np.zeros((3, 3)), np.zeros((3, 3)), np.eye(3), [1., 2., 1.])
        with self.assertRaises(ValueError):
            collinear = np.array([[0., 0., 0.], [1., 0., 0.], [2., 0., 0.]])
            position_candidates(collinear, np.zeros_like(collinear), np.eye(3), [.1, .1, .1])

    def test_attitude_error_response_matches_independent_reconstruction(self):
        anchors = np.array([[0., 0., 0.], [2., 0., 0.], [0., 2., 0.]])
        attachments = np.array([[.1, .2, 0.], [.3, -.1, .1], [-.1, .4, -.2]])
        rotation = np.array([[0., -1., 0.], [1., 0., 0.], [0., 0., 1.]])
        position = np.array([.4, .6, .8])
        lengths = np.linalg.norm(anchors - (position + attachments @ rotation.T), axis=1)
        result = pose_error_jacobians(position, anchors, attachments, rotation)
        h = 1e-6
        candidates = []
        for angle in (-h, h):
            c, s = np.cos(angle), np.sin(angle)
            perturbation = np.array([[c, 0., s], [0., 1., 0.], [-s, 0., c]])
            changed = position_candidates(anchors, attachments, perturbation @ rotation, lengths)
            candidates.append(min(changed["candidates"], key=lambda p: np.linalg.norm(p - position)))
        finite = (candidates[1] - candidates[0]) / (2 * h)
        np.testing.assert_allclose(finite, result["position_from_attitude_m_per_rad"] @ [0., 1., 0.], atol=1e-9)

    def test_attitude_sensitivity_respects_common_attachment_and_singular_position(self):
        anchors = np.array([[0., 0., 0.], [2., 0., 0.], [0., 2., 0.]])
        offset = np.array([.2, -.3, .1])
        attachments = np.tile(offset, (3, 1))
        result = pose_error_jacobians([.4, .6, .8], anchors, attachments, np.eye(3))
        theta = np.array([.1, .2, -.3])
        # One common attachment stays fixed when grip translation cancels its
        # attitude-induced displacement. This analytic result needs no solver.
        np.testing.assert_allclose(result["position_from_attitude_m_per_rad"] @ theta, np.cross(offset, theta), atol=1e-12)
        self.assertAlmostEqual(result["worst_attitude_position_gain_m_per_rad"], np.linalg.norm(offset))
        singular = pose_error_jacobians([.4, .6, 0.], anchors, np.zeros_like(anchors), np.eye(3))
        self.assertFalse(singular["position_observable"])
        self.assertIsNone(singular["position_from_attitude_m_per_rad"])


if __name__ == "__main__":
    unittest.main()
