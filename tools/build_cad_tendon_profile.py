"""Build a software tendon profile from the user's native CAD evidence.

Reads the existing native extraction; does not open or modify SolidWorks.
Hole axes come from the paddle B-rep, not the displaced drawn cord endpoints.
Guide centres are an ideal point-guide approximation. Registration to the
virtual boat and the two inferred guide-to-cord assignments remain explicit
design assumptions, not hardware calibration.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
from pathlib import Path

import numpy as np

from cad_sources import CAD, ROOT, native_source

PROJECT = ROOT / "DragonBoatVR"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def vector(value):
    return np.array([value[k] for k in ("x", "y", "z")], dtype=float)


def xyz(value):
    return dict(zip(("x", "y", "z"), map(float, value)))


def transform(component):
    value = component["total_transform"]
    rotation = np.array(value[:9]).reshape(3, 3).T * value[12]
    if not np.allclose(rotation.T @ rotation, np.eye(3), atol=1e-8) or not np.isclose(
            np.linalg.det(rotation), 1., atol=1e-8):
        raise ValueError("CAD component transform is not a rigid metre transform")
    return rotation, np.array(value[9:12])


def build(base_path, reference_grip):
    assembly_path = CAD / "native_state_probe/after_rebuild.json"
    part_path = CAD / "key_parts/018f5bb8e110e62c.json"
    reconciliation_path = CAD / "reference_reconciliation/reconciliation.json"
    # Native file manifests are evidence, not a mutable source of geometry.
    observations = read(CAD / "cad_observations.json")
    reconciliation = read(reconciliation_path)
    expected = reconciliation["identity"]["native_state_probe/after_rebuild.json"]
    if sha(assembly_path) != expected:
        raise ValueError("Native assembly extraction changed after reference reconciliation")
    if sha(reconciliation_path) != observations["source_identity"]["reference_reconciliation_sha256"]:
        raise ValueError("CAD reconciliation identity changed")
    components = {c["name"]: c for c in read(assembly_path)["components"]}
    paddle = read(part_path)
    if sha(native_source(paddle["source"])) != paddle["sha256"]:
        raise ValueError("Native paddle differs from the extracted B-rep")
    native_assembly = ROOT / "10月11日修改/2.1 铝型材框架.SLDASM"
    if sha(native_assembly) != observations["source_identity"]["assembly_native_sha256"]:
        raise ValueError("Native assembly differs from the reviewed design")

    # The unbounded cylinder-axis origin may lie outside the part. Intersect
    # each axis with the blade/shaft symmetry plane z=0; never use that origin
    # as the eyelet centre. The 4 mm radii identify the four 8 mm design holes.
    holes = []
    for body in paddle["bodies"]:
        for face in body["surfaces"]:
            cylinder = face.get("cylinder")
            if cylinder is None or abs(cylinder[6] - .004) > 1e-9:
                continue
            origin, axis = np.array(cylinder[:3]), np.array(cylinder[3:6])
            if abs(abs(axis[2]) - 1.) > 1e-8:
                raise ValueError("Unexpected paddle hole axis")
            point = origin - axis * (origin[2] / axis[2])
            existing = next((h for h in holes if np.linalg.norm(point - h["point"]) < 1e-8), None)
            if existing is None:
                holes.append(dict(point=point, faces=[face["face"]], radiusM=cylinder[6]))
            else:
                existing["faces"].append(face["face"])
    if len(holes) != 4:
        raise ValueError("Expected shaft, two side holes and the excluded centre hole")
    shaft = max(holes, key=lambda h: h["point"][1])
    lower = [h for h in holes if h is not shaft]
    positive = max(lower, key=lambda h: h["point"][0])
    negative = min(lower, key=lambda h: h["point"][0])
    centre = min(lower, key=lambda h: abs(h["point"][0]))
    # Exact nominal tip comes from the 10 mm corner surfaces, independent of
    # the expanded B-rep bounding box's 0.1 micrometre modelling tolerance.
    corners = [f["cylinder"] for b in paddle["bodies"] for f in b["surfaces"]
               if "cylinder" in f and abs(f["cylinder"][6] - .01) < 1e-9]
    tips = [c[1] - c[6] for c in corners]
    if len(tips) != 2 or not np.isclose(tips[0], tips[1], atol=1e-10):
        raise ValueError("Paddle bottom corners disagree")
    tip_y = tips[0]
    base = read(base_path)
    if len(base["tendons"]) != 3 or not base["attachmentCoordinatesFromBladeTip"]:
        raise ValueError("Expected the existing three-cord, blade-tip-relative profile")
    if not np.isclose(shaft["point"][1] - tip_y, .46, atol=1e-9):
        raise ValueError("CAD shaft hole conflicts with the user-confirmed 0.46 m")
    if not np.isclose(base["tendons"][0]["attachmentFromBladeTip"]["y"], .46, atol=1e-9):
        raise ValueError("Current user-confirmed shaft attachment changed")

    # A level, right-side software registration: CAD +X -> boat +Y,
    # CAD +Y -> boat +X, CAD +Z -> boat -Z. No scale or fitted force weighting.
    cad_to_boat = np.array([[0., 1., 0.], [1., 0., 0.], [0., 0., -1.]])
    paddle_rotation, paddle_origin = transform(components["1.34 船桨-2"])
    paddle_geometry_path = PROJECT / "Assets/StreamingAssets/paddle_geometry.json"
    shaft_length = read(paddle_geometry_path)["referenceToBladeCentreM"]
    blade_tip = vector(base["bladeTipLocal"])
    reference_blade = np.array(reference_grip) - np.array([0., shaft_length, 0.])
    reference_rotation = cad_to_boat @ paddle_rotation
    part_origin_from_blade = blade_tip - np.array([0., tip_y, 0.])
    registration_translation = (reference_blade + reference_rotation @ part_origin_from_blade
                                - cad_to_boat @ paddle_origin)
    native_names = ["1.18 万向轮-5", "1.18 万向轮-2", "1.18 万向轮-1"]
    selected_holes = [shaft, positive, negative]
    original = copy.deepcopy(base)
    base["measured"] = False
    base["geometryAssumed"] = True
    base["motorMappingConfirmed"] = False
    base["provenance"] = (
        "CAD_DESIGN_POINT_GUIDES_NOT_HARDWARE_CALIBRATION: user native 2.1 assembly and "
        "1.34 paddle B-rep; shaft hole 0.46 m above tip, side holes +/-0.08 m and 0.01 m "
        "above tip. Guide wheel centres approximate final point guides. Level boat "
        "registration and guide 5/2/1 to shaft/positive/negative hole assignment are "
        "design assumptions; only guide 2 has a matched named final-cord reference. "
        "Same three channels, 5 N pretension, 300 N bounds and 0.6 m wrench scale; "
        "centre measurement cord excluded. See tendon_cad_provenance.json.")
    records = []
    for i, (tendon, hole, name) in enumerate(zip(base["tendons"], selected_holes, native_names)):
        component = components[name]
        if not component["loaded"] or component["hidden"]:
            raise ValueError("Selected guide is not loaded and visible")
        # The native guide part origin is its wheel centre, independently
        # confirmed for guide -2 by the named-reference reconciliation.
        _, guide = transform(component)
        if name.endswith("-2"):
            reviewed = next(c for c in reconciliation["cords"] if c["component"] == "细线1-2")
            expected_guide = reviewed["explicit_existing_guide_review"]["guide_wheel_center_assembly_m"]
            if not np.allclose(guide, expected_guide, atol=1e-10):
                raise ValueError("Named guide centre disagrees with native reference")
        relative = hole["point"] - np.array([0., tip_y, 0.])
        tendon["anchorLocal"] = xyz(cad_to_boat @ guide + registration_translation)
        tendon["attachmentFromBladeTip"] = xyz(relative)
        tendon["attachLocal"] = xyz(blade_tip + relative)
        tendon["attachmentRegion"] = "shaft CAD bore" if i == 0 else "blade side CAD bore"
        tendon["lateralOffsetConfirmed"] = False  # design geometry, no physical measurement
        cad_attachment = cad_to_boat @ (paddle_rotation @ hole["point"] + paddle_origin) + registration_translation
        runtime_attachment = reference_blade + reference_rotation @ vector(tendon["attachLocal"])
        if np.linalg.norm(cad_attachment - runtime_attachment) > 1e-9:
            raise AssertionError("CAD and runtime attachment registration disagree")
        records.append(dict(motorId=tendon["motorId"], sourceGuide=name,
                            guideAssemblyM=guide.tolist(), holePartM=hole["point"].tolist(),
                            holeBrepFaces=hole["faces"], boreRadiusM=hole["radiusM"],
                            holeAssemblyM=(paddle_rotation @ hole["point"] + paddle_origin).tolist(),
                            anchorBoatM=vector(tendon["anchorLocal"]).tolist(),
                            attachmentRegistrationErrorM=float(np.linalg.norm(cad_attachment - runtime_attachment)),
                            attachmentFromBladeTipM=relative.tolist(),
                            guideAssignment="named reference" if i == 1 else "design inference"))
    inputs = [assembly_path, part_path, reconciliation_path, CAD / "cad_observations.json",
              native_source(paddle["source"]), native_assembly, base_path,
              paddle_geometry_path, Path(__file__), ROOT / "tools/cad_sources.py"]
    provenance = dict(schemaVersion=1, scope="CAD_DESIGN_SOFTWARE_PROFILE_NOT_MEASURED",
                      sourceSha256={str(p.relative_to(ROOT)): sha(p) for p in inputs},
                      registration=dict(rotationRows=cad_to_boat.tolist(),
                                        translationM=registration_translation.tolist(),
                                        virtualReferenceGripM=list(reference_grip),
                                        virtualReferenceBladeM=reference_blade.tolist(),
                                        virtualReferenceBladeRotationRows=reference_rotation.tolist(),
                                        basis="level right-side placement; not a measured platform mounting"),
                      nativePaddleTipY=tip_y, tendons=records,
                      excludedCentreHole=dict(pointPartM=centre["point"].tolist(), faces=centre["faces"]),
                      nativeCordSketchesUsedAsAnchors=False,
                      retained=dict(charLengthM=base["charLengthM"],
                                    minTensionsN=[t["preTensionN"] for t in base["tendons"]],
                                    maxTensionsN=[t["maxTensionN"] for t in base["tendons"]]),
                      originalProfileSha256=sha(base_path))
    for old, new in zip(original["tendons"], base["tendons"]):
        if any(old[k] != new[k] for k in ("motorId", "preTensionN", "maxTensionN", "enabled")):
            raise AssertionError("CAD import changed channel or tension constraints")
    return base, provenance


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--base", type=Path, default=PROJECT / "Assets/StreamingAssets/tendon_rig.json")
    parser.add_argument("--reference-grip", type=float, nargs=3,
                        default=[.44755950570106506, 1.1659258604049683, 0.])
    args = parser.parse_args()
    profile, evidence = build(args.base.resolve(), args.reference_grip)
    args.output.mkdir(parents=True, exist_ok=True)
    profile_path = args.output / "tendon_rig_cad.json"
    profile_path.write_text(json.dumps(profile, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    evidence["generatedProfileSha256"] = sha(profile_path)
    (args.output / "tendon_cad_provenance.json").write_text(
        json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(dict(result="CAD_DESIGN_PROFILE_BUILT", profile=str(profile_path),
                          guideCount=len(profile["tendons"]), measured=False), ensure_ascii=False))


if __name__ == "__main__":
    main()
