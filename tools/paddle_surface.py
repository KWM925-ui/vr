"""Numerical thin-plate paddle surface and the project's Gerstner water field.

CAD input is the union of its projected triangles, preserving holes and counting
one blade face. The strip mesh approximates this silhouette; it is not a native
SolidWorks surface or a measured hydrodynamic model. No device or Unity is run.
Drag uses the existing angle-dependent coefficient law and three-point triangle
quadrature. Forces, distributed moments and water-relative dissipation remain
separate; a moving water field may supply mechanical power in world coordinates.
"""

import math

import numpy as np

from cad_paddle_geometry import check_triangles, projected_sections


def three(value):
    result = np.asarray([value[k] for k in ("x", "y", "z")] if isinstance(value, dict) else value, dtype=float)
    if result.shape != (3,) or not np.isfinite(result).all():
        raise ValueError("finite three-vector required")
    return result


def rotation_matrix(quaternion):
    q = np.asarray([quaternion[k] for k in ("x", "y", "z", "w")] if isinstance(quaternion, dict) else quaternion, dtype=float)
    if q.shape != (4,) or not np.isfinite(q).all() or abs(np.linalg.norm(q) - 1) > 2e-5:
        raise ValueError("unit quaternion required")
    x, y, z, w = q / np.linalg.norm(q)
    return np.array([[1 - 2 * (y*y + z*z), 2 * (x*y - z*w), 2 * (x*z + y*w)],
                     [2 * (x*y + z*w), 1 - 2 * (x*x + z*z), 2 * (y*z - x*w)],
                     [2 * (x*z - y*w), 2 * (y*z + x*w), 1 - 2 * (x*x + y*y)]])


def rectangle_mesh(width, length, nx=4, ny=12):
    if not all(math.isfinite(v) and v > 0 for v in (width, length)):
        raise ValueError("positive rectangle dimensions required")
    if any(type(n) is not int or n < 1 for n in (nx, ny)):
        raise ValueError("positive grid divisions required")
    x, y = np.meshgrid(np.linspace(-width / 2, width / 2, nx + 1),
                       np.linspace(-length / 2, length / 2, ny + 1))
    grid = np.stack((x, y, np.zeros_like(x)), axis=-1)
    a, b, c, d = grid[:-1, :-1], grid[:-1, 1:], grid[1:, :-1], grid[1:, 1:]
    return np.stack((np.stack((a, b, d), axis=-2), np.stack((a, d, c), axis=-2)), axis=-3).reshape(-1, 3, 3)


def silhouette_mesh(triangles, bottom_y, top_y, slices, nx=4):
    """Single-face strip mesh in native XY, with the original through holes.

    Each interval is held constant across one strip. Refine slices to quantify
    this approximation; centring one rectangle at each strip would fill holes.
    """
    if type(nx) is not int or nx < 1:
        raise ValueError("positive width subdivisions required")
    _, dy, intervals = projected_sections(triangles, bottom_y, top_y, slices, include_intervals=True)
    mesh = []
    for i, section in enumerate(intervals):
        y0, y1 = bottom_y + i * dy, bottom_y + (i + 1) * dy
        for left, right in section:
            xs = np.linspace(left, right, nx + 1)
            for x0, x1 in zip(xs, xs[1:]):
                a, b, c, d = [x0, y0, 0.], [x1, y0, 0.], [x0, y1, 0.], [x1, y1, 0.]
                mesh.extend(((a, b, d), (a, d, c)))
    return check_triangles(mesh)


def clip_wet_triangles(triangles, depths):
    """Clip using linearly interpolated vertex depths, as in the Unity solver."""
    triangles = check_triangles(triangles)
    depths = np.asarray(depths, dtype=float)
    if depths.shape != triangles.shape[:2] or not np.isfinite(depths).all():
        raise ValueError("one finite water depth per triangle vertex required")
    wet = depths > 0
    counts = wet.sum(axis=1)
    pieces = [triangles[counts == 3]]
    for count in (1, 2):
        mask = counts == count
        if not np.any(mask):
            continue
        points, d = triangles[mask], depths[mask]
        # Select the sole wet vertex (one wet), or sole dry vertex (two wet).
        first = np.argmax(wet[mask] if count == 1 else ~wet[mask], axis=1)
        order = (first[:, None] + [0, 1, 2]) % 3
        points = np.take_along_axis(points, order[:, :, None], axis=1)
        d = np.take_along_axis(d, order, axis=1)
        a, b, c = points[:, 0], points[:, 1], points[:, 2]
        ab = a + (d[:, 0] / (d[:, 0] - d[:, 1]))[:, None] * (b - a)
        ac = a + (d[:, 0] / (d[:, 0] - d[:, 2]))[:, None] * (c - a)
        if count == 1:
            pieces.append(np.stack((a, ab, ac), axis=1))
        else:
            pieces.extend((np.stack((b, c, ac), axis=1), np.stack((b, ac, ab), axis=1)))
    return np.concatenate(pieces, axis=0)


class WaterSurface:
    """Double-precision readback of WaveField.cs, using its fixed-point inverse.

    wave_time is the recorded wave phase clock, already advanced by timeScale.
    The velocity still includes timeScale and the intensity time derivative.
    """

    def __init__(self, waves=(), *, intensity=1., intensity_rate=0., time_scale=1.,
                 horizontal=True, iterations=3, base_height=0., current=(0., 0., 0.)):
        if not all(math.isfinite(v) for v in (intensity, intensity_rate, time_scale, base_height)):
            raise ValueError("finite water parameters required")
        if intensity < 0 or time_scale < 0 or type(iterations) is not int or iterations < 1:
            raise ValueError("invalid wave intensity, clock scale or inverse iterations")
        self.intensity, self.intensity_rate, self.time_scale = intensity, intensity_rate, time_scale
        self.base_height, self.current, self.iterations = base_height, three(current), iterations
        rows = []
        for wave in waves:
            direction = wave["direction"]
            direction = np.array([direction["x"], direction["y"]] if isinstance(direction, dict) else direction, dtype=float)
            wavelength, amplitude = float(wave["wavelength"]), float(wave["amplitude"])
            phase, steepness = float(wave.get("phaseOffset", 0.)), float(wave.get("steepness", 0.))
            if direction.shape != (2,) or not np.isfinite(direction).all() or not all(
                    math.isfinite(v) for v in (wavelength, amplitude, phase, steepness)):
                raise ValueError("invalid wave component")
            if wavelength <= 0 or amplitude <= 0:
                continue
            direction = direction / np.linalg.norm(direction) if np.dot(direction, direction) > 1e-6 else np.array([1., 0.])
            rows.append([*direction, 2 * math.pi / wavelength, amplitude, phase, steepness if horizontal else 0.])
        terms = np.array(rows, dtype=float).reshape(-1, 6)
        self.directions, self.k = terms[:, :2], terms[:, 2]
        self.amplitude, self.phase, self.q = terms[:, 3], terms[:, 4], terms[:, 5]
        self.omega = np.sqrt(9.81 * self.k)

    def phase_terms(self, rest, wave_time):
        phase = (rest @ self.directions.T) * self.k - self.omega * wave_time + self.phase
        return np.sin(phase), np.cos(phase)

    def sample(self, points, wave_time):
        points = np.asarray(points, dtype=float)
        if points.ndim != 2 or points.shape[1] != 3 or not np.isfinite(points).all() or not math.isfinite(wave_time):
            raise ValueError("finite N x 3 water query and time required")
        world = points[:, [0, 2]]
        rest, active = world.copy(), np.ones(len(points), dtype=bool)
        amplitude = self.amplitude * self.intensity
        for _ in range(self.iterations):
            _, cosine = self.phase_terms(rest, wave_time)
            next_rest = world - (cosine * (self.q * amplitude)) @ self.directions
            error = np.abs(next_rest - rest).sum(axis=1)
            rest[active] = next_rest[active]
            active &= error >= 1e-4
            if not np.any(active):
                break
        sine, cosine = self.phase_terms(rest, wave_time)
        height = self.base_height + sine @ amplitude
        amplitude_rate = self.amplitude * self.intensity_rate
        phase_rate = amplitude * self.omega * self.time_scale
        horizontal_rate = cosine * amplitude_rate + sine * phase_rate
        velocity = np.tile(self.current, (len(points), 1))
        velocity[:, [0, 2]] += (horizontal_rate * self.q) @ self.directions
        velocity[:, 1] += sine @ amplitude_rate - cosine @ phase_rate
        return height, velocity


def integrate_drag(surface, water, wave_time, paddle, *, density, cd_flat, cd_edge, grip=None):
    """Force on the paddle and torque about the declared grip, all in world axes.

    surface is a one-face thin-plate mesh in the paddle sample's local frame.
    Its local +Z is the blade normal. The sample position is the velocity's
    reference point; it need not be the new silhouette's centroid.
    """
    surface = check_triangles(surface)
    if not all(math.isfinite(v) for v in (density, cd_flat, cd_edge)) or density <= 0 or min(cd_flat, cd_edge) < 0:
        raise ValueError("positive density and nonnegative drag coefficients required")
    position, velocity, omega = three(paddle["bladePosition"]), three(paddle["bladeVelocity"]), three(paddle["bladeAngularVelocity"])
    rotation = rotation_matrix(paddle["bladeRotation"])
    grip = position if grip is None else three(grip)
    triangles = surface @ rotation.T + position
    heights, _ = water.sample(triangles.reshape(-1, 3), wave_time)
    wet = clip_wet_triangles(triangles, heights.reshape(-1, 3) - triangles[:, :, 1])
    result = dict(wet_area_m2=0., wet_area_centroid_world_m=None, force_world_n=np.zeros(3),
                  torque_at_grip_world_nm=np.zeros(3), relative_water_dissipation_w=0., power_into_paddle_world_w=0.)
    if not len(wet):
        return result
    areas = np.linalg.norm(np.cross(wet[:, 1] - wet[:, 0], wet[:, 2] - wet[:, 0]), axis=1) / 2
    use = areas > 1e-12
    wet, areas = wet[use], areas[use]
    if not len(wet):
        return result
    weights = np.array([[4., 1., 1.], [1., 4., 1.], [1., 1., 4.]]) / 6
    quadrature = np.einsum("ij,njk->nik", weights, wet).reshape(-1, 3)
    _, water_velocity = water.sample(quadrature, wave_time)
    point_velocity = velocity + np.cross(omega, quadrature - position)
    relative = point_velocity - water_velocity
    speed = np.linalg.norm(relative, axis=1)
    sine = np.clip(np.abs(relative @ rotation[:, 2]) / np.maximum(speed, 1e-30), 0., 1.)
    cd = cd_flat * sine**2 + cd_edge * (1 - sine**2)
    force = -(.5 * density * np.repeat(areas / 3, 3) * cd * np.where(speed >= 1e-6, speed, 0.))[:, None] * relative
    result.update(wet_area_m2=float(areas.sum()),
                  wet_area_centroid_world_m=np.sum(areas[:, None] * wet.mean(axis=1), axis=0) / areas.sum(),
                  force_world_n=force.sum(axis=0),
                  torque_at_grip_world_nm=np.cross(quadrature - grip, force).sum(axis=0),
                  relative_water_dissipation_w=float(-np.sum(force * relative)),
                  power_into_paddle_world_w=float(np.sum(force * point_velocity)))
    return result
