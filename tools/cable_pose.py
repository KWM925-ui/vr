"""Position from taut straight-span lengths and a separately known orientation.

Inputs are calibrated free-span lengths in metres, not motor turns or counts.
Fixed guide points and rigid paddle attachment points share the stated frames.
Pulley contact, winding radii, cable stretch and IMU mounting are not inferred.
Three lengths can give two positions; this module returns both, never silently
using an arbitrary intersection as the measured paddle position.
"""

import math

import numpy as np


def sphere_centres(anchors, attachments, rotation):
    anchors, attachments, rotation = (np.asarray(v, dtype=float) for v in (anchors, attachments, rotation))
    if (anchors.ndim != 2 or anchors.shape[1] != 3 or attachments.shape != anchors.shape
            or len(anchors) < 1 or not all(np.isfinite(v).all() for v in (anchors, attachments, rotation))):
        raise ValueError("matching finite N x 3 anchors and attachments required")
    if rotation.shape != (3, 3) or not np.allclose(rotation.T @ rotation, np.eye(3), atol=1e-8, rtol=0) or not np.isclose(
            np.linalg.det(rotation), 1., atol=1e-8, rtol=0):
        raise ValueError("proper rigid orientation required")
    return anchors - attachments @ rotation.T


def position_candidates(anchors, attachments, rotation, lengths, *, residual_tolerance_m=1e-7):
    """Intersect range spheres after subtracting the known rotated attachments.

    A tolerance is a consistency check, not a sensor-noise estimate. Underdeter-
    mined circles/spheres return no selected point. Noisy inconsistent ranges
    must be dealt with by an estimator using a declared measurement model.
    """
    centres = sphere_centres(anchors, attachments, rotation)
    lengths = np.asarray(lengths, dtype=float)
    if lengths.shape != (len(centres),) or not np.isfinite(lengths).all() or np.any(lengths <= 0):
        raise ValueError("one positive finite free-span length per cable required")
    if not math.isfinite(residual_tolerance_m) or residual_tolerance_m < 0:
        raise ValueError("nonnegative finite consistency tolerance required")
    # Rebase before squaring, preserving precision under large frame translations.
    offsets = centres[1:] - centres[0]
    matrix = 2 * offsets
    rhs = np.einsum("ij,ij->i", offsets, offsets) + lengths[0]**2 - lengths[1:]**2
    if not len(offsets):
        return dict(status="SPHERE_UNDERDETERMINED", candidates=[], difference_rank=0,
                    centres=centres, maximum_range_residual_m=None)
    _, singular, vh = np.linalg.svd(matrix, full_matrices=True)
    cutoff = max(matrix.shape) * np.finfo(float).eps * singular[0] if len(singular) else 0.
    rank = int(np.sum(singular > cutoff))
    point = np.linalg.lstsq(matrix, rhs, rcond=None)[0]
    if rank < 2:
        height2 = lengths[0]**2 - np.dot(point, point)
        numeric_tolerance = 64 * np.finfo(float).eps * max(1., lengths[0]**2, np.dot(point, point))
        if height2 < -numeric_tolerance:
            raise ValueError("underdetermined range spheres have no real intersection")
        # Probe consistency without returning an arbitrary point on the circle.
        probe = centres[0] + point + vh[-1] * math.sqrt(max(0., float(height2)))
        residual = float(np.max(np.abs(np.linalg.norm(probe - centres, axis=1) - lengths)))
        if residual > residual_tolerance_m:
            raise ValueError("inconsistent collinear or concentric range spheres")
        return dict(status="UNDERDETERMINED_OR_SINGULAR_RANGE_SET", candidates=[], difference_rank=rank,
                    centres=centres, maximum_range_residual_m=residual)
    if rank == 2:
        height2 = lengths[0]**2 - np.dot(point, point)
        numeric_tolerance = 64 * np.finfo(float).eps * max(1., lengths[0]**2, np.dot(point, point))
        if height2 < -numeric_tolerance:
            raise ValueError("range spheres have no real intersection")
        height = math.sqrt(max(0., float(height2)))
        normal = vh[-1]
        candidates = [centres[0] + point + normal * height]
        if height > math.sqrt(numeric_tolerance):
            candidates.append(centres[0] + point - normal * height)
        status = "TWO_POSITION_SOLUTIONS" if len(candidates) == 2 else "TANGENT_POSITION_SOLUTION"
    else:
        candidates = [centres[0] + point]
        status = "UNIQUE_POSITION_SOLUTION"
    residual = max(float(np.max(np.abs(np.linalg.norm(p - centres, axis=1) - lengths))) for p in candidates)
    if residual > residual_tolerance_m:
        raise ValueError(f"range intersection residual {residual:g} m exceeds the declared consistency tolerance")
    return dict(status=status, candidates=candidates, difference_rank=rank,
                centres=centres, maximum_range_residual_m=residual)


def range_sensitivity(position, centres):
    """Local length-to-position amplification with orientation held fixed.

    ||delta_position||_2 <= (1/s_min) ||delta_lengths||_2 to first order.
    It is NOT physical accuracy: length noise, orientation errors, calibration,
    routing and branch ambiguity are additional independent uncertainties.
    """
    position, centres = np.asarray(position, dtype=float), np.asarray(centres, dtype=float)
    if position.shape != (3,) or centres.ndim != 2 or centres.shape[1] != 3 or not len(centres) or not np.isfinite(position).all() or not np.isfinite(centres).all():
        raise ValueError("finite position and N x 3 centres required")
    offsets = position - centres
    lengths = np.linalg.norm(offsets, axis=1)
    if np.any(lengths <= 1e-12):
        raise ValueError("zero cable length has no defined direction")
    jacobian = offsets / lengths[:, None]
    singular = np.linalg.svd(jacobian, compute_uv=False)
    cutoff = max(jacobian.shape) * np.finfo(float).eps * singular[0]
    rank = int(np.sum(singular > cutoff))
    return dict(jacobian=jacobian, singular_values=singular, rank=rank,
                amplification=None if rank < 3 else float(1 / singular[-1]),
                condition_number=None if rank < 3 else float(singular[0] / singular[-1]))


def pose_error_jacobians(position, anchors, attachments, rotation):
    """Local effects of length errors AND externally supplied attitude errors.

    With u_i pointing from each fixed guide to its moving attachment and b_i
    the rotated grip-to-attachment offset,
        delta_length_i = u_i . delta_position + (b_i x u_i) . delta_theta.
    delta_theta is a small rotation vector in the fixed guide frame (radians),
    not three raw Euler-angle increments. Inverting this gives the effect of
    an erroneous attitude while holding the measured lengths fixed. Results
    concern one local branch; they cannot remove the global mirror ambiguity.
    For more than three lengths this is an unweighted least-squares response.
    """
    centres = sphere_centres(anchors, attachments, rotation)
    sensitivity = range_sensitivity(position, centres)
    rotated_offsets = np.asarray(attachments, dtype=float) @ np.asarray(rotation, dtype=float).T
    length_from_attitude = np.cross(rotated_offsets, sensitivity["jacobian"])
    result = dict(length_jacobian=sensitivity["jacobian"],
                  attitude_jacobian_m_per_rad=length_from_attitude,
                  position_from_length=None, position_from_attitude_m_per_rad=None,
                  worst_attitude_position_gain_m_per_rad=None, position_observable=sensitivity["rank"] == 3)
    if result["position_observable"]:
        position_from_length = np.linalg.pinv(sensitivity["jacobian"])
        attitude_response = -position_from_length @ length_from_attitude
        result.update(position_from_length=position_from_length,
                      position_from_attitude_m_per_rad=attitude_response,
                      worst_attitude_position_gain_m_per_rad=float(np.linalg.norm(attitude_response, 2)))
    return result
