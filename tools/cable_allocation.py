"""Offline bounded three-cable projection, independent of the runtime allocator.

Only diagnostic mathematics lives here; there is no network or actuator path.
"""

import itertools

import numpy as np

from run_closed_loop_check import euler_q, rotate


def optimum(matrix, target, lower, upper):
    matrix, target, lower, upper = (np.asarray(v, dtype=float) for v in (matrix, target, lower, upper))
    if (matrix.ndim != 2 or matrix.shape[1] != 3 or matrix.shape[0] < 1
            or target.shape != (matrix.shape[0],) or lower.shape != (3,) or upper.shape != (3,)
            or not all(np.isfinite(v).all() for v in (matrix, target, lower, upper))
            or np.any(lower < 0) or np.any(lower > upper)):
        raise ValueError("finite three-cable matrix, matching target and ordered nonnegative bounds required")
    best = None
    for state in itertools.product((-1, 0, 1), repeat=3):
        tensions = np.array([lower[i] if s == -1 else upper[i] if s == 1 else 0.
                             for i, s in enumerate(state)])
        free = [i for i, s in enumerate(state) if s == 0]
        if free:
            tensions[free] = np.linalg.lstsq(matrix[:, free], target - matrix @ tensions, rcond=None)[0]
        if np.any(tensions < lower - 1e-7) or np.any(tensions > upper + 1e-7):
            continue
        error = float(np.linalg.norm(matrix @ tensions - target))
        if best is None or error < best[0]:
            best = error, tensions.copy()
    if best is None:
        raise ValueError("bounded projection found no feasible face")
    return best


def recorded_cable_matrix(feedback, command):
    """Same saved geometry as the old offline review, without its tmp imports.

    Grip position is the software observation. Orientation retains the recorded
    encoder quantization; it is not the controller's exact internal quaternion.
    """
    rotation = euler_q([x / feedback["countsPerDegree"] for x in feedback["rawEncoderCounts"][3:]])
    grip = np.asarray(feedback["gripPosition"], dtype=float)
    blade = grip - rotate(rotation, [0., command["shaftLength"], 0.])
    anchors = np.reshape(command["anchors"], (-1, 3))
    attachments = np.reshape(command["attachments"], (-1, 3))
    if anchors.shape != (3, 3) or attachments.shape != anchors.shape or grip.shape != (3,):
        raise ValueError("three matching cables and a grip position required")
    columns = []
    for anchor, attachment in zip(anchors, attachments):
        point = blade + rotate(rotation, attachment)
        offset = anchor - point
        length = np.linalg.norm(offset)
        if not np.isfinite(length) or length <= 1e-12:
            raise ValueError("invalid cable direction")
        direction = offset / length
        columns.append(np.r_[direction, np.cross(point - grip, direction)])
    return np.array(columns).T
