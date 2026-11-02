"""Compute paddle design geometry from the already extracted native CAD mesh.

Example: python.exe tools/cad_paddle_geometry.py --output tmp/paddle_design
Writes numerical geometry and an upright immersion curve, without changing the
Unity prototype or opening SolidWorks. Needs numpy; never downloads anything.
The curve is the union of projected triangles, not the sum of both blade faces.
Its centroid is a uniform-pressure area centre, not a measured pressure centre.
"""

import argparse
import csv
import hashlib
import json
import math
from pathlib import Path

import numpy as np

from build_cad_tendon_profile import transform
from cad_sources import CAD, ROOT, native_source


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def check_triangles(triangles):
    triangles = np.asarray(triangles, dtype=float)
    if triangles.ndim != 3 or triangles.shape[1:] != (3, 3) or not len(triangles):
        raise ValueError("expected a nonempty N x 3 x 3 array of metre coordinates")
    if not np.isfinite(triangles).all():
        raise ValueError("nonfinite CAD mesh")
    return triangles


def solid_properties(triangles):
    """Signed tetrahedral integration, after checking closed, consistent topology.

    A 10 nm weld is used only for topology comparison of shared float32 vertices;
    the input geometry used in all integrals remains unchanged.
    """
    triangles = check_triangles(triangles)
    _, inverse = np.unique(np.round(triangles.reshape(-1, 3), 8), axis=0, return_inverse=True)
    faces = inverse.reshape(-1, 3)
    edges = np.concatenate((faces[:, [0, 1]], faces[:, [1, 2]], faces[:, [2, 0]]))
    if np.any(edges[:, 0] == edges[:, 1]):
        raise ValueError("mesh has degenerate edges at the topology weld tolerance")
    undirected, counts = np.unique(np.sort(edges, axis=1), axis=0, return_counts=True)
    forward = edges[edges[:, 0] < edges[:, 1]]
    forward = forward[np.lexsort((forward[:, 1], forward[:, 0]))]
    if np.any(counts != 2) or not np.array_equal(forward, undirected):
        raise ValueError("mesh is open or inconsistently wound; solid volume is unavailable")
    cross = np.cross(triangles[:, 1], triangles[:, 2])
    volumes = np.einsum("ij,ij->i", triangles[:, 0], cross) / 6
    volume = float(volumes.sum())
    if volume <= 0:
        raise ValueError("nonpositive oriented mesh volume")
    sums = triangles.sum(axis=1)
    centre = np.sum(volumes[:, None] * sums / 4, axis=0) / volume
    outer_sums = np.einsum("ni,nj->nij", sums, sums)
    vertex_outers = np.einsum("nki,nkj->nij", triangles, triangles)
    second = np.sum(volumes[:, None, None] * (outer_sums + vertex_outers) / 20, axis=0)
    second -= volume * np.outer(centre, centre)
    inertia = np.trace(second) * np.eye(3) - second
    area = np.linalg.norm(np.cross(triangles[:, 1] - triangles[:, 0],
                                  triangles[:, 2] - triangles[:, 0]), axis=1).sum() / 2
    return {"volumeM3": volume, "uniformDensityCentrePartM": centre.tolist(),
            "centralVolumeInertiaM5": inertia.tolist(), "surfaceAreaM2": float(area),
            "topologyClosed": True, "topologyWeldToleranceM": 1e-8,
            "massKg": None, "materialDensityKgPerM3": None}


def projected_sections(triangles, bottom_y, top_y, slices, *, include_intervals=False):
    """Return height midpoint, projected width, and x first moment of each strip.

    At each height, all triangle intervals are united. This preserves through
    holes and does not double-count the front/back faces or overlapping details.
    Integration in Y uses midpoint quadrature with a reported convergence check.
    Optional per-strip intervals retain holes for a downstream single-face mesh.
    """
    triangles = check_triangles(triangles)
    if not math.isfinite(bottom_y) or not math.isfinite(top_y) or bottom_y >= top_y:
        raise ValueError("finite, increasing blade Y bounds required")
    if type(slices) is not int or slices < 2:
        raise ValueError("at least two slices required")
    xy = triangles[:, :, :2]
    ab, ac = xy[:, 1] - xy[:, 0], xy[:, 2] - xy[:, 0]
    xy = xy[np.abs(ab[:, 0] * ac[:, 1] - ab[:, 1] * ac[:, 0]) > 1e-18]
    low, high = xy[:, :, 1].min(axis=1), xy[:, :, 1].max(axis=1)
    step = (top_y - bottom_y) / slices
    result = np.zeros((slices, 3))
    result[:, 0] = (np.arange(slices) + .5) * step
    interval_sections = [[] for _ in range(slices)] if include_intervals else None
    for i, height in enumerate(result[:, 0]):
        y = bottom_y + height
        a = xy[(low <= y) & (high > y)]
        if not len(a):
            continue
        b = np.roll(a, -1, axis=1)
        crossing = ((a[:, :, 1] <= y) & (b[:, :, 1] > y)) | ((b[:, :, 1] <= y) & (a[:, :, 1] > y))
        with np.errstate(divide="ignore", invalid="ignore"):
            xs = a[:, :, 0] + (y - a[:, :, 1]) * (b[:, :, 0] - a[:, :, 0]) / (b[:, :, 1] - a[:, :, 1])
        lefts = np.where(crossing, xs, np.inf).min(axis=1)
        rights = np.where(crossing, xs, -np.inf).max(axis=1)
        left = right = None
        intervals = []
        for lo, hi in sorted(zip(lefts, rights)):
            if lo >= hi:
                continue
            if left is None:
                left, right = lo, hi
            elif lo <= right:
                right = max(right, hi)
            else:
                intervals.append((left, right))
                left, right = lo, hi
        if left is not None:
            intervals.append((left, right))
        result[i, 1] = sum(hi - lo for lo, hi in intervals)
        result[i, 2] = sum((hi * hi - lo * lo) / 2 for lo, hi in intervals)
        if include_intervals:
            interval_sections[i] = intervals
    if include_intervals:
        return result, step, interval_sections
    return result, step


def section_integrals(sections, step):
    area = np.cumsum(sections[:, 1]) * step
    x_moment = np.cumsum(sections[:, 2]) * step
    height_moment = np.cumsum(sections[:, 1] * sections[:, 0]) * step
    if area[-1] <= 0:
        raise ValueError("empty blade projection")
    return area, x_moment, height_moment


def native_blade_bounds(part):
    surfaces = [face for body in part["bodies"] for face in body["surfaces"]]
    shaft = [face for face in surfaces if "cylinder" in face
             and np.allclose(np.abs(face["cylinder"][3:6]), [0, 1, 0], atol=1e-8)
             and np.isclose(face["cylinder"][6], .015, atol=1e-9)]
    if len(shaft) != 1:
        raise ValueError("native shaft surface is ambiguous")
    # The matching planar end surfaces give the exact shaft/blade junction.
    planes = [face["plane"][4] for face in surfaces if "plane" in face
              and np.isclose(abs(face["plane"][1]), 1., atol=1e-8)
              and abs(face["plane"][4] - shaft[0]["box"][1]) < 1e-6]
    corners = [face["cylinder"] for face in surfaces if "cylinder" in face
               and np.isclose(face["cylinder"][6], .01, atol=1e-9)]
    tips = [c[1] - c[6] for c in corners]
    if not planes or len(tips) != 2 or np.ptp(planes) > 1e-9 or np.ptp(tips) > 1e-9:
        raise ValueError("native junction or tip references disagree")
    return float(tips[0]), float(planes[0])


def relative_component_pose(reference, moving):
    """Moving CAD part coordinates -> reference CAD part coordinates.

    Uses the existing SolidWorks transform convention. A part's coordinate
    axes and origin do not establish the sensitive axes/origin of an IMU.
    """
    reference_r, reference_t = transform(reference)
    moving_r, moving_t = transform(moving)
    if not np.isfinite(reference_t).all() or not np.isfinite(moving_t).all():
        raise ValueError("nonfinite CAD component translation")
    return reference_r.T @ moving_r, reference_r.T @ (moving_t - reference_t)


def sensor_housing_geometry(projection_centre, bottom, top):
    """Recover the installed sensor housing pose from the resolved assembly."""
    assembly_path = CAD / "native_state_probe/after_rebuild.json"
    observation_path = CAD / "cad_observations.json"
    reconciliation_path = CAD / "reference_reconciliation/reconciliation.json"
    sensor_path = CAD / "key_parts/f8bc15bd15d9c0ee.json"
    native_assembly = ROOT / "10月11日修改/2.1 铝型材框架.SLDASM"
    observations, reconciliation = read(observation_path), read(reconciliation_path)
    if sha(assembly_path) != reconciliation["identity"]["native_state_probe/after_rebuild.json"]:
        raise ValueError("resolved assembly differs from the native reference check")
    identity = observations["source_identity"]
    if sha(reconciliation_path) != identity["reference_reconciliation_sha256"]:
        raise ValueError("native reference check has changed")
    if sha(native_assembly) != identity["assembly_native_sha256"]:
        raise ValueError("native assembly has changed")
    sensor = read(sensor_path)
    sensor_source = native_source(sensor["source"])
    if not sensor_source.is_relative_to(ROOT) or sha(sensor_source) != sensor["sha256"]:
        raise ValueError("sensor housing source is outside the workspace or has changed")
    components = {c["name"]: c for c in read(assembly_path)["components"]}
    paddle, housing = components["1.34 船桨-2"], components["1.76 姿态传感器-1"]
    if any(c["suppression"] != 2 for c in (paddle, housing)):
        raise ValueError("paddle and sensor housing must both be resolved")
    rotation, origin = relative_component_pose(paddle, housing)
    boxes = np.array([b["box"] for b in sensor["bodies"]], dtype=float)
    box_centre = (boxes[:, :3].min(axis=0) + boxes[:, 3:].max(axis=0)) / 2
    box_centre_part = rotation @ box_centre + origin
    centre = np.array(projection_centre)
    result = {
        "scope": "CAD_HOUSING_INSTALLATION_NOT_IMU_CALIBRATION", "measured": False,
        "pointTransform": "paddle_part_point = housingAxesInPaddlePart * housing_part_point + housingOriginInPaddlePartM",
        "housingOriginInPaddlePartM": origin.tolist(),
        "housingAxesInPaddlePart": rotation.tolist(),
        "housingBoundsCentreInPaddlePartM": box_centre_part.tolist(),
        "boundsCentreToProjectionCentrePaddleM": (centre - box_centre_part).tolist(),
        "projectionCentreInHousingPartM": (rotation.T @ (centre - origin)).tolist(),
        "bladeTipInHousingPartM": (rotation.T @ (np.array([0., bottom, 0.]) - origin)).tolist(),
        "bladeRootInHousingPartM": (rotation.T @ (np.array([0., top, 0.]) - origin)).tolist(),
        "imuSensitivePointInHousingM": None, "imuSensitiveAxesInHousing": None,
    }
    sources = [assembly_path, observation_path, reconciliation_path, sensor_path,
               sensor_source, native_assembly, ROOT / "tools/build_cad_tendon_profile.py",
               ROOT / "tools/cad_sources.py"]
    return result, sources


def calculate_design():
    part_path = CAD / "key_parts/018f5bb8e110e62c.json"
    part = read(part_path)
    source = native_source(part["source"])
    mesh_path = part_path.parent / part["mesh_file"]
    if not source.is_relative_to(ROOT) or not mesh_path.resolve().is_relative_to(ROOT):
        raise ValueError("CAD source is outside this workspace")
    if sha(source) != part["sha256"] or sha(mesh_path) != part["mesh_sha256"]:
        raise ValueError("native CAD or mesh differs from its extraction")
    triangles = np.fromfile(mesh_path, dtype="<f4").reshape(-1, 3, 3).astype(float)
    if len(triangles) != part["triangles"]:
        raise ValueError("mesh triangle count differs from extraction")
    bottom, top = native_blade_bounds(part)
    count = int(math.ceil((top - bottom) / .00005))
    # Use an even count so the coarse and fine integrations cover identical bounds.
    count += count % 2
    fine, step = projected_sections(triangles, bottom, top, count)
    coarse, coarse_step = projected_sections(triangles, bottom, top, count // 2)
    areas, xs, heights = section_integrals(fine, step)
    coarse_area = section_integrals(coarse, coarse_step)[0][-1]
    solid = solid_properties(triangles)
    brep_area = sum(f["area_m2"] for b in part["bodies"] for f in b["surfaces"])
    prototype_path = ROOT / "DragonBoatVR/Assets/StreamingAssets/paddle_geometry.json"
    prototype = read(prototype_path)
    centre = [float(xs[-1] / areas[-1]), float(bottom + heights[-1] / areas[-1]), 0.]
    housing, housing_sources = sensor_housing_geometry(centre, bottom, top)
    data = {
        "schemaVersion": 1, "scope": "CAD_DESIGN_NOT_HARDWARE_MEASUREMENT", "measured": False,
        "sourceSha256": {str(p.relative_to(ROOT)): sha(p) for p in
                         [source, part_path, mesh_path, prototype_path, Path(__file__), *housing_sources]},
        "triangles": len(triangles), "projectionNormalPart": [0., 0., 1.],
        "bladeTipPartY": bottom, "bladeRootPartY": top, "bladeLengthM": top - bottom,
        "projectedBladeAreaM2": float(areas[-1]), "uniformPressureCentreInProjectionPartM": centre,
        "uniformPressureCentreAboveTipM": float(heights[-1] / areas[-1]),
        "curveCondition": "upright blade, horizontal water plane, projection onto native XY; excludes shaft above junction",
        "sampling": {"finePitchM": step, "coarsePitchM": coarse_step,
                     "fineCoarseAreaDifferenceM2": float(abs(areas[-1] - coarse_area)),
                     "fineCoarseRelativeAreaDifference": float(abs(areas[-1] - coarse_area) / areas[-1]),
                     "meshSurfaceVsBrepRelativeDifference": float(solid["surfaceAreaM2"] / brep_area - 1),
                     "scope": "quadrature convergence on this mesh; not native-CAD or physical measurement uncertainty"},
        "wholePaddleSolid": solid,
        "sensorHousingDesign": housing,
        "prototypeComparison": {"areaM2": prototype["bladeAreaM2"], "bladeLengthM": prototype["bladeLengthM"],
                                "fullAreaRelativeDifference": float(areas[-1] / prototype["bladeAreaM2"] - 1)},
    }
    # Sampling centres and first moments remain available for downstream calculations.
    curve = [(0., 0., None, None, 0.)]
    for i in range(count):
        curve.append(((i + 1) * step, float(areas[i]), float(xs[i] / areas[i]) if areas[i] else None,
                      float(heights[i] / areas[i]) if areas[i] else None, float(fine[i, 1])))
    return data, curve


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT):
        parser.error("output must stay in the workspace")
    targets = [output / "paddle_design_geometry.json", output / "paddle_immersion.csv"]
    if any(p.exists() for p in targets):
        parser.error("calculation outputs already exist; choose a new output directory")
    data, curve = calculate_design()
    output.mkdir(parents=True, exist_ok=True)
    with targets[1].open("x", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(("tip_immersion_m", "projected_wet_area_m2", "centroid_part_x_m",
                         "centroid_above_tip_m", "strip_projected_width_m"))
        writer.writerows(curve)
    data["immersionCurveSha256"] = sha(targets[1])
    with targets[0].open("x", encoding="utf-8") as stream:
        json.dump(data, stream, ensure_ascii=False, indent=2, allow_nan=False)
        stream.write("\n")
    print(json.dumps({"scope": data["scope"], "areaM2": data["projectedBladeAreaM2"],
                      "lengthM": data["bladeLengthM"], "centroidAboveTipM": data["uniformPressureCentreAboveTipM"],
                      "volumeM3": data["wholePaddleSolid"]["volumeM3"], "output": str(output)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
