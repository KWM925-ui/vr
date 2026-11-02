"""Analytic optima and the sign of each cable's wrench."""

import unittest

import numpy as np

from cable_allocation import optimum, recorded_cable_matrix


class CableAllocationTests(unittest.TestCase):
    def test_orthogonal_projection_clips_each_independent_tension(self):
        error, tension = optimum(np.eye(3), [-3., 12., 2.], [1., 1., 1.], [10., 10., 10.])
        np.testing.assert_allclose(tension, [1., 10., 2.])
        self.assertAlmostEqual(error, np.sqrt(20))

    def test_upward_cables_cannot_create_a_downward_force(self):
        matrix = np.array([[0., 0., 0.], [1., 1., 1.], [0., 0., 0.]])
        error, tension = optimum(matrix, [0., -20., 0.], [5., 5., 5.], [300., 300., 300.])
        self.assertEqual(error, 35.)
        np.testing.assert_allclose(tension, [5., 5., 5.])

    def test_recorded_matrix_uses_attachment_moment_about_grip(self):
        feedback = dict(gripPosition=[0., 1., 0.], rawEncoderCounts=[0] * 6, countsPerDegree=100)
        command = dict(shaftLength=1., anchors=[1., 0., 1., -1., 0., 1., 0., 2., 0.],
                       attachments=[1., 0., 0., -1., 0., 0., 0., 0., 0.])
        matrix = recorded_cable_matrix(feedback, command)
        np.testing.assert_allclose(matrix[:, 0], [0., 0., 1., -1., -1., 0.])
        np.testing.assert_allclose(matrix[:, 1], [0., 0., 1., -1., 1., 0.])
        np.testing.assert_allclose(matrix[:, 2], [0., 1., 0., 0., 0., 0.])


if __name__ == "__main__":
    unittest.main()
