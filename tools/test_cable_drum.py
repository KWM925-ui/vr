"""Unit/sign, native groove identification and recording interval checks."""

import copy
import math
import unittest

import numpy as np

from cable_drum import groove_from_surfaces, ideal_shaft_load, interval_winding, single_layer_radius


def native_groove():
    # A synthetic 20 mm groove, independent of the project's 95 mm wheel.
    surfaces = []
    for i, (radius, x0, x1) in enumerate(((.03, 0., .004), (.02, .004, .007), (.03, .007, .01))):
        surfaces.append(dict(face=i, cylinder=[0., 0., 0., 1., 0., 0., radius],
                             box=[x0, -radius, -radius, x1, radius, radius],
                             area_m2=2 * math.pi * radius * (x1 - x0)))
    for i, x in enumerate((.004, .007), 3):
        surfaces.append(dict(face=i, plane=[1., 0., 0., x, 0., 0.],
                             box=[x, -.03, -.03, x, .03, .03]))
    # An off-axis large hole cannot become the drum radius.
    surfaces.append(dict(face=5, cylinder=[0., .1, 0., 1., 0., 0., .05],
                         box=[0., .05, -.05, .01, .15, .05], area_m2=.005))
    return dict(bodies=[dict(index=0, surfaces=surfaces)],
                features=[dict(name="reference", type="3DProfileFeature",
                               sketch=dict(segments=[dict(curve=dict(circle=[.0055, 0., 0., 1., 0., 0., .02]))]))])


class CableDrumTests(unittest.TestCase):
    def test_native_brep_and_sketch_agree_without_using_filename_or_holes(self):
        groove = groove_from_surfaces(native_groove())
        self.assertAlmostEqual(groove["bottom_radius_m"], .02)
        self.assertAlmostEqual(groove["groove_width_m"], .003)
        self.assertAlmostEqual(groove["groove_depth_m"], .01)
        self.assertEqual(groove["bottom_face"], 1)

    def test_missing_or_contradictory_native_evidence_is_rejected(self):
        for change in ("partial_cylinder", "wrong_circle", "missing_side", "two_grooves"):
            part = native_groove()
            if change == "partial_cylinder":
                part["bodies"][0]["surfaces"][1]["area_m2"] /= 2
            elif change == "wrong_circle":
                part["features"][0]["sketch"]["segments"][0]["curve"]["circle"][6] = .025
            elif change == "missing_side":
                del part["bodies"][0]["surfaces"][3]
            else:
                part["bodies"].append(copy.deepcopy(part["bodies"][0]))
            with self.subTest(change=change), self.assertRaises(ValueError):
                groove_from_surfaces(part)

    def test_round_cable_centre_and_fit_are_explicit(self):
        groove = groove_from_surfaces(native_groove())
        self.assertAlmostEqual(single_layer_radius(groove, .002), .021)
        for diameter in (0., -.001, .004, math.nan, math.inf):
            with self.subTest(diameter=diameter), self.assertRaises(ValueError):
                single_layer_radius(groove, diameter)

    def test_load_and_reeling_obey_virtual_work_in_both_quadrants(self):
        # Radius .1 m: 10 N at 1 rad/s has 1 Nm torque and 1 W power.
        winding = ideal_shaft_load(.1, 10., -.1)
        payout = ideal_shaft_load(.1, 10., .1)
        self.assertEqual(winding["holding_torque_nm"], 1.)
        self.assertEqual(winding["winding_angular_rate_rad_s"], 1.)
        self.assertEqual(winding["ideal_transmitted_power_w"], 1.)
        self.assertEqual(payout["ideal_transmitted_power_w"], -1.)
        doubled = ideal_shaft_load(.2, 10., -.1)
        self.assertEqual(doubled["holding_torque_nm"], 2.)
        self.assertEqual(doubled["winding_angular_rate_rad_s"], .5)
        self.assertEqual(doubled["ideal_transmitted_power_w"], 1.)

    def test_interval_mean_does_not_cross_sessions_or_hide_sample_gaps(self):
        a = dict(segment_key=(1, "a", "config"), controller_s=1., tick=200, lengths_m=[1., 2., 3.])
        b = dict(a, controller_s=1.1, tick=220, lengths_m=[.9, 2., 3.2])
        result = interval_winding(a, b, .1)
        np.testing.assert_allclose(result["winding_radians"], [1., 0., -2.])
        np.testing.assert_allclose(result["mean_winding_rpm"], [300 / math.pi, 0., -600 / math.pi])
        self.assertEqual(result["ticks"], 20)
        self.assertIsNone(interval_winding(None, b, .1))
        self.assertIsNone(interval_winding(a, dict(b, segment_key=(1, "b", "config")), .1))
        with self.assertRaises(ValueError):
            interval_winding(a, a, .1)

    def test_nonphysical_scalar_inputs_are_rejected(self):
        for radius, tension, rate in ((0., 1., 1.), (.1, -1., 1.), (.1, 1., math.nan), (math.inf, 1., 1.)):
            with self.subTest(radius=radius, tension=tension, rate=rate), self.assertRaises(ValueError):
                ideal_shaft_load(radius, tension, rate)


if __name__ == "__main__":
    unittest.main()
