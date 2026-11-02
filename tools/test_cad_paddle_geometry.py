"""Independent geometric cases for CAD silhouette and solid integrations."""

import unittest

import numpy as np

from cad_paddle_geometry import projected_sections, section_integrals, solid_properties, relative_component_pose


def rectangle(x0, x1, y0, y1, z=0.):
    return np.array([[[x0, y0, z], [x1, y0, z], [x1, y1, z]],
                     [[x0, y0, z], [x1, y1, z], [x0, y1, z]]])


class PaddleGeometryTests(unittest.TestCase):
    def test_relative_part_pose_keeps_points_and_ignores_global_placement(self):
        def component(rotation, translation):
            return {"total_transform": [*rotation.T.ravel(), *translation, 1., 0., 0., 0.]}
        quarter_turn = np.array([[0., -1., 0.], [1., 0., 0.], [0., 0., 1.]])
        reference = component(quarter_turn, [3., 4., 5.])
        moving = component(np.eye(3), [2., 6., 8.])
        r, t = relative_component_pose(reference, moving)
        np.testing.assert_allclose(t, [2., 1., 3.])
        np.testing.assert_allclose(r @ [1., 2., 3.] + t, [4., 0., 6.])
        # Rigidly moving the whole assembly cannot change the installation pose.
        reference2 = component(quarter_turn @ quarter_turn, quarter_turn @ [3., 4., 5.] + [7., 8., 9.])
        moving2 = component(quarter_turn, quarter_turn @ [2., 6., 8.] + [7., 8., 9.])
        r2, t2 = relative_component_pose(reference2, moving2)
        np.testing.assert_allclose(r2, r, atol=1e-12)
        np.testing.assert_allclose(t2, t, atol=1e-12)
        with self.assertRaises(ValueError):
            relative_component_pose(reference, component(np.diag([1., 1., -1.]), [0., 0., 0.]))
        with self.assertRaises(ValueError):
            relative_component_pose(reference, component(np.eye(3), [0., np.nan, 0.]))

    def test_front_back_overlap_counts_one_projected_face(self):
        mesh = np.concatenate((rectangle(-.1, .1, -.4, 0, -.01), rectangle(-.1, .1, -.4, 0, .01)))
        strips, dy = projected_sections(mesh, -.4, 0., 400)
        area, mx, my = section_integrals(strips, dy)
        self.assertAlmostEqual(area[-1], .08, places=12)
        self.assertAlmostEqual(mx[-1] / area[-1], 0., places=12)
        self.assertAlmostEqual(my[-1] / area[-1], .2, places=12)
        self.assertAlmostEqual(area[99], .02, places=12)

    def test_hole_is_preserved_and_disjoint_intervals_are_united(self):
        # 0.2 x 0.4 face with a central 0.04 x 0.1 through opening.
        face = np.concatenate((rectangle(-.1, -.02, -.4, 0), rectangle(.02, .1, -.4, 0),
                               rectangle(-.02, .02, -.4, -.25), rectangle(-.02, .02, -.15, 0)))
        mesh = np.concatenate((face, face + [0, 0, .01]))
        strips, dy = projected_sections(mesh, -.4, 0., 400)
        area, mx, my = section_integrals(strips, dy)
        self.assertAlmostEqual(area[-1], .08 - .004, places=12)
        self.assertAlmostEqual(mx[-1], 0., places=12)
        self.assertAlmostEqual(my[-1] / area[-1], .2, places=12)

    def test_tapered_shape_has_expected_area_centre(self):
        mesh = np.array([[[-.1, 0, 0], [.1, 0, 0], [0, .6, 0]]])
        strips, dy = projected_sections(mesh, 0., .6, 6000)
        area, _, my = section_integrals(strips, dy)
        self.assertAlmostEqual(area[-1], .06, places=12)
        self.assertAlmostEqual(my[-1] / area[-1], .2, places=7)

    def test_tetrahedron_volume_centre_and_inertia(self):
        o, x, y, z = np.eye(4, 3, k=-1)
        mesh = np.array([[o, y, x], [o, x, z], [o, z, y], [x, y, z]])
        result = solid_properties(mesh)
        self.assertAlmostEqual(result["volumeM3"], 1 / 6, places=12)
        np.testing.assert_allclose(result["uniformDensityCentrePartM"], [.25, .25, .25])
        expected = np.full((3, 3), 1 / 480)
        np.fill_diagonal(expected, 1 / 80)
        np.testing.assert_allclose(result["centralVolumeInertiaM5"], expected)
        self.assertIsNone(result["massKg"])
        shifted = solid_properties(mesh * 2 + [3, -4, 1])
        self.assertAlmostEqual(shifted["volumeM3"], 8 / 6, places=12)
        np.testing.assert_allclose(shifted["uniformDensityCentrePartM"], [3.5, -3.5, 1.5])
        np.testing.assert_allclose(shifted["centralVolumeInertiaM5"], expected * 32)
        with self.assertRaises(ValueError):
            solid_properties(mesh[:-1])


if __name__ == "__main__":
    unittest.main()
