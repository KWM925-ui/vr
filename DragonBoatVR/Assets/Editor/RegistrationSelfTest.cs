using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    /// <summary>Deterministic software fixtures only; no fixture is a real hardware measurement.</summary>
    public static class RegistrationSelfTest
    {
        static int passed, failed;
        static readonly Vector3 Translation = new Vector3(1.37f, -0.42f, 2.18f);
        static readonly Quaternion Rotation = Quaternion.Euler(23f, -47f, 31f);
        static readonly Vector3[] Geometry = {
            new Vector3(-0.6f, 0.1f, -0.3f), new Vector3(0.7f, -0.2f, 0.15f),
            new Vector3(0.05f, 0.8f, -0.5f), new Vector3(-0.2f, -0.4f, 0.9f),
            new Vector3(0.4f, 0.65f, 0.55f), new Vector3(-0.45f, 0.3f, 0.7f) };

        [MenuItem("DragonBoatVR/空间刚体配准与头部补偿自检")]
        public static void Run()
        {
            passed = failed = 0;
            Check("known nonzero 6DoF / planar / 180-degree transforms", TestKnownTransforms);
            Check("bounded deterministic measurement noise", TestNoise);
            Check("collinear / coincident / duplicate / missing / nonfinite points", TestInvalidGeometry);
            Check("reflection / scale / RMS / point residual thresholds", TestResidualRejection);
            Check("metadata / defaults / caller and result snapshot isolation", TestEvidenceAndOwnership);
            Check("project path / save-load / changed transforms and diagnostics", TestPersistence);
            Check("saved noise rechecked with current strict thresholds", TestCurrentThresholds);
            Check("tracking-stage to world position and full rotation", TestStageToWorld);
            Check("registration signature follows stage mapping and parent transforms", TestStageSignature);
            Check("head/platform common rigid motion cancellation", TestCommonMotion);
            Check("default disabled / declared measured / desktop-only preview gates", TestTrackingEvidence);
            Check("TTL / loss / source / order / invalid clocks / scale", TestTrackingValidity);
            Check("independent head/platform clocks cannot refresh or hide a stale source", TestIndependentTrackingTimes);
            Check("independent transform inputs / invalid boat / clear", TestTrackingTransforms);
            Debug.Log($"[REGISTRATION] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[REGISTRATION] RESULT=PASS software registration and tracking fixtures; hardware unverified"
                : "[REGISTRATION] RESULT=FAIL see individual checks");
            if (failed != 0) throw new InvalidOperationException($"Registration self-test failed: {failed} check groups.");
        }

        static void Check(string name, Action test)
        {
            try { test(); passed++; Debug.Log($"[REGISTRATION] {name}=True"); }
            catch (Exception error) { failed++; Debug.LogError($"[REGISTRATION] {name}: {error}"); }
        }
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        static T Copy<T>(T input) => JsonUtility.FromJson<T>(JsonUtility.ToJson(input));
        static Matrix4x4 Pose(Vector3 position, Quaternion rotation) => Matrix4x4.TRS(position, rotation, Vector3.one);
        static bool MatrixNear(Matrix4x4 actual, Matrix4x4 expected, float tolerance = 1e-5f)
        {
            for (int i = 0; i < 16; i++)
                if (!SimulationValue.Finite(actual[i]) || Mathf.Abs(actual[i] - expected[i]) > tolerance) return false;
            return true;
        }
        static bool RotationNear(Quaternion actual, Quaternion expected, float tolerance = 1e-5f)
        { return MatrixNear(Matrix4x4.Rotate(actual), Matrix4x4.Rotate(expected), tolerance); }
        static void ExpectInvalid(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, message);
        }

        sealed class Fixture : IDisposable
        {
            readonly GameObject root = new GameObject("RegistrationSelfTest (analytical fixture)");
            public readonly SpatialCalibrator calibrator;
            public readonly HeadPoseAttachment attachment;
            public readonly Transform stage, boat, camera, trackedHead, platform;
            public Fixture()
            {
                root.hideFlags = HideFlags.HideAndDontSave;
                calibrator = root.AddComponent<SpatialCalibrator>();
                stage = Child("stage"); boat = Child("boat"); camera = Child("camera output");
                trackedHead = Child("independent head input"); platform = Child("platform input");
                calibrator.trackingStage = stage;
                attachment = camera.gameObject.AddComponent<HeadPoseAttachment>();
                attachment.registration = calibrator; attachment.virtualBoat = boat;
                attachment.trackedHead = trackedHead; attachment.physicalPlatform = platform;
                boat.SetPositionAndRotation(new Vector3(-2, 0.8f, 4), Quaternion.Euler(-13, 29, 17));
            }
            Transform Child(string name)
            {
                var child = new GameObject(name);
                child.transform.SetParent(root.transform, false);
                return child.transform;
            }
            public void Dispose() { UnityEngine.Object.DestroyImmediate(root); }
        }

        static RegistrationPointSet Points(Quaternion rotation, Vector3 translation, bool measured = false)
        {
            var data = new RegistrationPointSet {
                measured = measured,
                measuredAtUtc = measured ? "2026-10-21T00:00:00.0000000Z" : null,
                provenance = "SELFTEST analytical correspondences only; no real tracking hardware measured",
                points = new RegistrationPointPair[Geometry.Length] };
            for (int i = 0; i < Geometry.Length; i++) data.points[i] = new RegistrationPointPair {
                id = "fixture-" + i, platformMeters = Geometry[i], stageMeters = rotation * Geometry[i] + translation };
            return data;
        }
        static RegistrationPointSet NoisyPoints()
        {
            var data = Points(Rotation, Translation);
            for (int i = 0; i < data.points.Length; i++) data.points[i].stageMeters += new Vector3(
                i % 2 == 0 ? 0.001f : -0.001f, i % 3 == 0 ? -0.0007f : 0.0004f,
                i % 3 == 1 ? 0.0006f : -0.0003f);
            return data;
        }
        static void AssertFit(RegistrationPointSet data, Quaternion rotation, Vector3 translation)
        {
            var fit = RigidRegistration.Fit(data.points, new RegistrationOptions());
            Require(fit.accepted && fit.status == "RIGID_FIT_ACCEPTED", "known proper rigid fit rejected: " + fit.status);
            Require(MatrixNear(fit.Matrix, Pose(translation, rotation)), "recovered transform differs from known 6DoF");
            Require(fit.rmsResidualMeters < 1e-5f && fit.maximumResidualMeters < 1e-5f, "exact residual");
            Require(fit.pointCount == data.points.Length && fit.pointResidualMeters.Length == data.points.Length, "per-point diagnostics");
            Require(RigidRegistration.IsRigid(fit.Matrix), "fit must remain proper unit-scale rigid transform");
        }
        static void AssertRejected(RegistrationPointSet data, string reason, RegistrationOptions options = null)
        {
            var fit = RigidRegistration.Fit(data.points, options ?? new RegistrationOptions());
            Require(!fit.accepted && fit.status == reason, "expected " + reason + ", got " + fit.status);
        }

        static void TestKnownTransforms()
        {
            AssertFit(Points(Rotation, Translation), Rotation, Translation);
            var halfTurn = Quaternion.AngleAxis(180f, new Vector3(1, 2, -3).normalized);
            AssertFit(Points(halfTurn, Translation), halfTurn, Translation);
            var plane = Points(Rotation, Translation);
            plane.points = new[] {
                new RegistrationPointPair { id = "a", platformMeters = Vector3.zero },
                new RegistrationPointPair { id = "b", platformMeters = Vector3.right },
                new RegistrationPointPair { id = "c", platformMeters = Vector3.forward },
                new RegistrationPointPair { id = "d", platformMeters = new Vector3(0.8f, 0, 1.3f) } };
            foreach (var point in plane.points) point.stageMeters = Rotation * point.platformMeters + Translation;
            AssertFit(plane, Rotation, Translation);
        }
        static void TestNoise()
        {
            var fit = RigidRegistration.Fit(NoisyPoints().points, new RegistrationOptions());
            Require(fit.accepted && fit.rmsResidualMeters > 0.0001f && fit.rmsResidualMeters < 0.002f
                && fit.maximumResidualMeters < 0.003f, "bounded noisy residual");
            Require(Vector3.Distance(fit.translation, Translation) < 0.002f
                && RotationNear(fit.rotation, Rotation, 0.005f), "noisy recovery remains near known transform");
        }
        static void TestInvalidGeometry()
        {
            var data = Points(Rotation, Translation);
            foreach (var point in data.points) { point.platformMeters = Vector3.one; point.stageMeters = Translation; }
            AssertRejected(data, "DEGENERATE_PLATFORM_GEOMETRY");
            data = Points(Rotation, Translation);
            for (int i = 0; i < data.points.Length; i++) {
                data.points[i].platformMeters = new Vector3(i, 2 * i, -i);
                data.points[i].stageMeters = Rotation * data.points[i].platformMeters + Translation; }
            AssertRejected(data, "DEGENERATE_PLATFORM_GEOMETRY");
            data = Points(Rotation, Translation);
            for (int i = 0; i < data.points.Length; i++) data.points[i].stageMeters = Vector3.right * i;
            AssertRejected(data, "DEGENERATE_STAGE_GEOMETRY");
            data = Points(Rotation, Translation); data.points[1].id = data.points[0].id;
            AssertRejected(data, "MISSING_OR_DUPLICATE_POINT_ID");
            data = Points(Rotation, Translation); data.points[0] = null;
            AssertRejected(data, "MISSING_OR_DUPLICATE_POINT_ID");
            data = Points(Rotation, Translation); data.points[0].id = " ";
            AssertRejected(data, "MISSING_OR_DUPLICATE_POINT_ID");
            data = Points(Rotation, Translation); data.points[0].stageMeters.x = float.NaN;
            AssertRejected(data, "NONFINITE_POINT");
            data = Points(Rotation, Translation); data.points = new RegistrationPointPair[2];
            AssertRejected(data, "AT_LEAST_THREE_POINTS_REQUIRED");
            data.points = null; AssertRejected(data, "AT_LEAST_THREE_POINTS_REQUIRED");
            AssertRejected(Points(Rotation, Translation), "INVALID_THRESHOLDS",
                new RegistrationOptions { maximumRmsResidualMeters = float.NaN });
        }
        static void TestResidualRejection()
        {
            var reflection = Points(Quaternion.identity, Translation);
            foreach (var point in reflection.points) point.stageMeters.x = -point.platformMeters.x + Translation.x;
            AssertRejected(reflection, "RMS_RESIDUAL_EXCEEDED");
            var scale = Points(Rotation, Translation);
            foreach (var point in scale.points) point.stageMeters = Rotation * (point.platformMeters * 1.3f) + Translation;
            AssertRejected(scale, "RMS_RESIDUAL_EXCEEDED");
            Require(!RigidRegistration.IsRigid(Matrix4x4.Scale(new Vector3(-1, 1, 1)))
                && !RigidRegistration.IsRigid(Matrix4x4.Scale(Vector3.one * 1.01f)), "reflection and scale matrices rejected");
            AssertRejected(NoisyPoints(), "RMS_RESIDUAL_EXCEEDED",
                new RegistrationOptions { maximumRmsResidualMeters = 0.0001f });
            AssertRejected(NoisyPoints(), "POINT_RESIDUAL_EXCEEDED",
                new RegistrationOptions { maximumRmsResidualMeters = 1f, maximumPointResidualMeters = 0.0001f });
        }

        static void TestEvidenceAndOwnership()
        {
            using (var f = new Fixture())
            {
                Require(!f.calibrator.HasUsableCalibration && !f.calibrator.calibrated
                    && !f.calibrator.loadSavedCalibrationOnStart && !f.calibrator.allowUnmeasuredDesktopPreview,
                    "default registration must be disabled");
                var data = Points(Rotation, Translation);
                Require(f.calibrator.SetCorrespondences(data) && !f.calibrator.HasUsableCalibration
                    && !f.calibrator.IsMeasuredRegistration && f.calibrator.Status == "UNMEASURED_FIT_NOT_ENABLED",
                    "numerical fit cannot grant measured use");
                ExpectInvalid(() => f.calibrator.PlatformToWorld(Vector3.zero), "disabled mapping must throw");
                data.measured = true; data.points[0].stageMeters += Vector3.one;
                Require(!f.calibrator.IsMeasuredRegistration, "caller input must be isolated");
                var fit = f.calibrator.LastFit; fit.accepted = false; fit.rotation = Quaternion.identity;
                fit.translation = Vector3.zero; fit.pointResidualMeters[0] = 99;
                f.calibrator.allowUnmeasuredDesktopPreview = true;
                Require(f.calibrator.HasUsableCalibration && !f.calibrator.IsMeasuredRegistration
                    && MatrixNear(f.calibrator.PlatformToStage, Pose(Translation, Rotation))
                    && f.calibrator.LastFit.pointResidualMeters[0] < 1e-5f, "diagnostic fit snapshot must be isolated");
                data = Points(Rotation, Translation, true); data.measuredAtUtc = null;
                Require(!f.calibrator.SetCorrespondences(data)
                    && f.calibrator.Status == "MEASURED_POINT_SET_REQUIRES_UTC_ACQUISITION_TIME", "measured time required");
                data.measuredAtUtc = "2026-10-21T08:00:00+08:00";
                Require(!f.calibrator.SetCorrespondences(data), "measured time must be UTC");
                data = Points(Rotation, Translation, true); data.provenance = " ";
                Require(!f.calibrator.SetCorrespondences(data), "provenance required");
                data = Points(Rotation, Translation); data.units = "cm";
                Require(!f.calibrator.SetCorrespondences(data), "meter units required");
                data = Points(Rotation, Translation); data.stageFrame = data.platformFrame;
                Require(!f.calibrator.SetCorrespondences(data), "distinct named frames required");
                Require(f.calibrator.SetCorrespondences(Points(Rotation, Translation, true))
                    && f.calibrator.IsMeasuredRegistration && f.calibrator.HasUsableCalibration
                    && f.calibrator.Status == "ACCEPTED_DECLARED_MEASURED_POINT_SET", "declared measured fixture status");
            }
        }

        static string ScratchPath(string name) => "Temp/RegistrationSelfTest/" + Guid.NewGuid().ToString("N") + "/" + name + ".json";
        static void WriteRecord(string path, RegistrationRecord record)
        { File.WriteAllText(SpatialCalibrator.ResolveProjectPath(path), JsonUtility.ToJson(record, true)); }
        static void TestPersistence()
        {
            using (var f = new Fixture())
            {
                Require(!f.calibrator.SaveCalibration(ScratchPath("empty")), "cannot save without fit");
                ExpectInvalid(() => SpatialCalibrator.ResolveProjectPath("../registration-escape.json"), "project path escape");
                ExpectInvalid(() => SpatialCalibrator.ResolveProjectPath("Temp/result.txt"), "JSON extension");
                string path = ScratchPath("saved");
                Require(f.calibrator.SetCorrespondences(Points(Rotation, Translation, true))
                    && f.calibrator.SaveCalibration(path), "save known registration");
                string original = File.ReadAllText(SpatialCalibrator.ResolveProjectPath(path));
                f.calibrator.ClearCalibration();
                Require(f.calibrator.LoadCalibration(path) && f.calibrator.HasUsableCalibration
                    && f.calibrator.IsMeasuredRegistration && MatrixNear(f.calibrator.PlatformToStage, Pose(Translation, Rotation)),
                    "saved registration round trip");
                Action<RegistrationRecord>[] changes = {
                    r => r.fit.translation.x += 0.03f,
                    r => r.fit.rotation = Quaternion.Euler(0, 5, 0) * r.fit.rotation,
                    r => { var q = r.fit.rotation; r.fit.rotation = new Quaternion(q.x * 2, q.y * 2, q.z * 2, q.w * 2); },
                    r => r.fit.rmsResidualMeters += 0.03f,
                    r => r.fit.maximumResidualMeters += 0.03f,
                    r => r.fit.pointResidualMeters[0] += 0.03f,
                    r => r.fit.platformSecondAxisSpreadMeters += 0.03f,
                    r => r.fit.pointCount++,
                    r => r.fit.accepted = false,
                    r => r.pointSet.points[0].stageMeters += Vector3.one,
                    r => r.pointSet.provenance = "",
                    r => r.schemaVersion = 999 };
                for (int i = 0; i < changes.Length; i++)
                {
                    var record = JsonUtility.FromJson<RegistrationRecord>(original); changes[i](record); WriteRecord(path, record);
                    Require(!f.calibrator.LoadCalibration(path) && !f.calibrator.HasUsableCalibration
                        && !f.calibrator.calibrated && MatrixNear(f.calibrator.PlatformToStage, Matrix4x4.identity),
                        "tampered record must clear accepted state, case " + i);
                }
                var sameRotation = JsonUtility.FromJson<RegistrationRecord>(original);
                var rotation = sameRotation.fit.rotation;
                sameRotation.fit.rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                WriteRecord(path, sameRotation);
                Require(f.calibrator.LoadCalibration(path), "quaternion sign is the same physical rotation");
                File.WriteAllText(SpatialCalibrator.ResolveProjectPath(path), "not JSON");
                Require(!f.calibrator.LoadCalibration(path) && !f.calibrator.HasUsableCalibration, "malformed record fails closed");
            }
        }
        static void TestCurrentThresholds()
        {
            using (var f = new Fixture())
            {
                string path = ScratchPath("noisy");
                Require(f.calibrator.SetCorrespondences(NoisyPoints()) && f.calibrator.SaveCalibration(path), "save accepted noisy fit");
                f.calibrator.thresholds.maximumRmsResidualMeters = 0.0001f;
                Require(!f.calibrator.LoadCalibration(path) && !f.calibrator.HasUsableCalibration
                    && f.calibrator.Status == "RMS_RESIDUAL_EXCEEDED", "current thresholds override saved success");
            }
        }
        static void TestStageToWorld()
        {
            using (var f = new Fixture())
            {
                f.stage.SetPositionAndRotation(new Vector3(4, -2, 7), Quaternion.Euler(-11, 34, -21));
                Require(f.calibrator.SetCorrespondences(Points(Rotation, Translation, true)), "stage calibration");
                Vector3 probe = new Vector3(0.35f, 0.8f, -0.6f);
                var expected = f.stage.localToWorldMatrix * Pose(Translation, Rotation);
                Require(Vector3.Distance(f.calibrator.PlatformToWorld(probe), expected.MultiplyPoint3x4(probe)) < 1e-5f,
                    "stage and calibration position composition");
                var localRotation = Quaternion.Euler(7, 13, -18);
                Require(RotationNear(f.calibrator.PlatformRotationToWorld(localRotation), f.stage.rotation * Rotation * localRotation),
                    "stage and calibration full orientation composition");
                f.stage.localScale = new Vector3(1, 1.1f, 1);
                Require(!f.calibrator.HasUsableCalibration, "stage scaling invalidates usability");
                ExpectInvalid(() => f.calibrator.PlatformToWorld(probe), "scaled-stage position mapping rejected");
                ExpectInvalid(() => f.calibrator.PlatformRotationToWorld(localRotation), "scaled-stage rotation mapping rejected");
                f.stage.localScale = Vector3.one;
                ExpectInvalid(() => f.calibrator.PlatformToWorld(new Vector3(float.NaN, 0, 0)), "nonfinite mapping input");
            }
        }

        static void TestStageSignature()
        {
            using (var f = new Fixture())
            {
                f.stage.SetPositionAndRotation(new Vector3(4.125f, -2.25f, 7.5f), Quaternion.Euler(-11, 34, -21));
                Require(f.calibrator.SetCorrespondences(Points(Rotation, Translation, true)), "signature calibration");
                string original = f.calibrator.ConfigurationSignature;
                Vector3 position = f.stage.localPosition;
                Quaternion rotation = f.stage.localRotation;
                Vector3 probe = new Vector3(0.35f, 0.8f, -0.6f);
                Vector3 originalMapped = f.calibrator.PlatformToWorld(probe);
                Require(original == f.calibrator.ConfigurationSignature, "unchanged stage signature is stable");

                f.stage.localPosition += Vector3.right * 0.03f;
                Require(original != f.calibrator.ConfigurationSignature
                    && Vector3.Distance(originalMapped, f.calibrator.PlatformToWorld(probe)) > 0.02f,
                    "stage translation changes mapping identity");
                f.stage.localPosition = position;
                f.stage.localRotation = Quaternion.Euler(0, 3, 0) * rotation;
                Require(original != f.calibrator.ConfigurationSignature
                    && Vector3.Distance(originalMapped, f.calibrator.PlatformToWorld(probe)) > 0.001f,
                    "stage rotation changes mapping identity");
                f.stage.localRotation = rotation;
                f.stage.localScale = new Vector3(1, 1.1f, 1);
                Require(original != f.calibrator.ConfigurationSignature && !f.calibrator.HasUsableCalibration,
                    "stage scale changes identity and revokes rigid mapping");
                f.stage.localScale = Vector3.one;
                Require(original == f.calibrator.ConfigurationSignature, "restored mapping restores signature");

                f.stage.SetParent(f.platform, false);
                Require(original == f.calibrator.ConfigurationSignature, "equivalent identity parent preserves mapping");
                f.platform.localPosition = Vector3.up * 0.07f;
                Require(original != f.calibrator.ConfigurationSignature, "parent translation changes mapping identity");
                f.platform.localPosition = Vector3.zero;
                f.platform.localRotation = Quaternion.Euler(2, 0, 0);
                Require(original != f.calibrator.ConfigurationSignature, "parent rotation changes mapping identity");
                f.platform.localRotation = Quaternion.identity;
                f.platform.localScale = new Vector3(1.03f, 1, 1);
                Require(original != f.calibrator.ConfigurationSignature && !f.calibrator.HasUsableCalibration,
                    "parent scale and inherited shear change identity and revoke mapping");
                f.platform.localScale = Vector3.one;
                Require(original == f.calibrator.ConfigurationSignature, "restored parent restores signature");

                var culture = System.Globalization.CultureInfo.CurrentCulture;
                try
                {
                    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
                    Require(original == f.calibrator.ConfigurationSignature, "stage signature is culture independent");
                }
                finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
            }
        }

        static readonly Matrix4x4 PlatformPose = Pose(new Vector3(1.1f, -0.3f, 2.7f), Quaternion.Euler(21, -39, 16));
        static readonly Matrix4x4 RelativeHead = Pose(new Vector3(0.2f, 1.25f, -0.15f), Quaternion.Euler(9, 17, -6));
        static void TestCommonMotion()
        {
            using (var f = new Fixture())
            {
                var worldBoat = f.boat.localToWorldMatrix;
                var expected = worldBoat * RelativeHead;
                var commonMotion = Pose(new Vector3(-4, 2, 6), Quaternion.Euler(37, 82, -24));
                Require(MatrixNear(HeadPoseAttachment.Compose(worldBoat, PlatformPose, PlatformPose * RelativeHead), expected)
                    && MatrixNear(HeadPoseAttachment.Compose(worldBoat, commonMotion * PlatformPose,
                        commonMotion * PlatformPose * RelativeHead), expected), "common physical 6DoF motion must cancel");
                Require(f.calibrator.SetCorrespondences(Points(Rotation, Translation, true)), "measured fixture");
                f.attachment.useTrackedPose = true;
                Require(f.attachment.SubmitTrackingSample(PlatformPose * RelativeHead, PlatformPose, 100,
                    HeadTrackingSource.ExternalTracking, true, true) && f.attachment.ApplyLatestPose(100.02), "first pose applied");
                var before = f.camera.localToWorldMatrix;
                Require(MatrixNear(before, expected), "component applies full relative pose");
                Require(f.attachment.SubmitTrackingSample(commonMotion * PlatformPose * RelativeHead,
                    commonMotion * PlatformPose, 100.03, HeadTrackingSource.ExternalTracking, true, true)
                    && f.attachment.ApplyLatestPose(100.04) && MatrixNear(f.camera.localToWorldMatrix, before),
                    "common physical movement preserves camera attachment");
            }
        }
        static bool Submit(HeadPoseAttachment attachment, double time, HeadTrackingSource source = HeadTrackingSource.ExternalTracking)
            => attachment.SubmitTrackingSample(PlatformPose * RelativeHead, PlatformPose, time, source, true, true);
        static void TestTrackingEvidence()
        {
            using (var f = new Fixture())
            {
                Require(!f.attachment.useTrackedPose && !f.attachment.allowUnmeasuredDesktopPreview && !f.attachment.PoseApplied,
                    "tracking defaults disabled");
                Require(Submit(f.attachment, 100, HeadTrackingSource.DesktopSimulation)
                    && !f.attachment.ApplyLatestPose(100.01) && f.attachment.PoseStatus == "DISABLED_NO_TRACKING_PROVIDER", "disabled despite valid sample");
                f.attachment.useTrackedPose = true; f.attachment.stageRegistrationMeasured = true;
                Require(!f.attachment.ApplyLatestPose(100.01) && !f.attachment.stageRegistrationMeasured
                    && f.attachment.PoseStatus == "MEASURED_REGISTRATION_REQUIRED", "legacy boolean cannot grant measured evidence");
                Require(f.calibrator.SetCorrespondences(Points(Rotation, Translation)), "unmeasured fit accepted numerically");
                f.calibrator.allowUnmeasuredDesktopPreview = true;
                Require(!f.attachment.ApplyLatestPose(100.01), "enabled unmeasured calibration still needs head preview opt-in");
                f.attachment.allowUnmeasuredDesktopPreview = true;
                Require(f.attachment.ApplyLatestPose(100.01) && f.attachment.PoseApplied
                    && !f.attachment.stageRegistrationMeasured && f.attachment.PoseStatus == "DESKTOP_TRACKING_PREVIEW", "explicit desktop preview");
                Require(Submit(f.attachment, 100.02) && !f.attachment.ApplyLatestPose(100.03), "desktop option cannot grant external tracking");
                Require(f.calibrator.SetCorrespondences(Points(Rotation, Translation, true))
                    && f.attachment.ApplyLatestPose(100.03) && f.attachment.stageRegistrationMeasured
                    && f.attachment.PoseStatus == "EXTERNAL_TRACKING_APPLIED_XR_HARDWARE_UNVERIFIED", "declared measured gate and hardware status");
                f.stage.localScale = Vector3.one * 1.1f;
                Require(!f.attachment.ApplyLatestPose(100.04) && !f.attachment.PoseApplied, "invalid stage revokes external use");
            }
        }
        static void TestTrackingValidity()
        {
            using (var f = new Fixture())
            {
                f.attachment.useTrackedPose = true; f.attachment.allowUnmeasuredDesktopPreview = true;
                Require(Submit(f.attachment, 100, HeadTrackingSource.DesktopSimulation) && f.attachment.ApplyLatestPose(100.05), "fresh pose");
                var before = f.camera.localToWorldMatrix;
                Require(!f.attachment.ApplyLatestPose(100.101) && !f.attachment.PoseApplied
                    && f.attachment.PoseStatus == "TRACKING_STALE_OR_INVALID_CLOCK"
                    && MatrixNear(f.camera.localToWorldMatrix, before), "TTL expires without moving camera");
                Require(!f.attachment.ApplyLatestPose(99.9) && !f.attachment.ApplyLatestPose(double.NaN)
                    && !f.attachment.ApplyLatestPose(double.PositiveInfinity), "invalid apply clock");
                Require(!f.attachment.SubmitTrackingSample(PlatformPose * RelativeHead, PlatformPose, 100.2,
                    HeadTrackingSource.DesktopSimulation, false, true)
                    && f.attachment.PoseStatus == "HEAD_TRACKING_LOST" && !f.attachment.ApplyLatestPose(100.2), "head loss invalidates");
                Require(Submit(f.attachment, 100.3, HeadTrackingSource.DesktopSimulation), "recover head");
                Require(!f.attachment.SubmitTrackingSample(PlatformPose * RelativeHead, PlatformPose, 100.4,
                    HeadTrackingSource.DesktopSimulation, true, false)
                    && f.attachment.PoseStatus == "PLATFORM_TRACKING_LOST" && !f.attachment.ApplyLatestPose(100.4), "platform loss invalidates");
                Require(Submit(f.attachment, 100.5, HeadTrackingSource.DesktopSimulation), "recover platform");
                Require(!Submit(f.attachment, 100.5, HeadTrackingSource.DesktopSimulation)
                    && !f.attachment.ApplyLatestPose(100.51), "duplicate cannot refresh tracking");
                Require(!Submit(f.attachment, 100.49, HeadTrackingSource.DesktopSimulation), "out-of-order tracking");
                Require(!Submit(f.attachment, 100.6, HeadTrackingSource.None)
                    && !Submit(f.attachment, 100.6, (HeadTrackingSource)999)
                    && !Submit(f.attachment, double.NaN, HeadTrackingSource.DesktopSimulation)
                    && !Submit(f.attachment, double.PositiveInfinity, HeadTrackingSource.DesktopSimulation)
                    && !Submit(f.attachment, -1, HeadTrackingSource.DesktopSimulation), "source and sample clocks");
                var scaled = Matrix4x4.Scale(Vector3.one * 1.1f);
                Require(!f.attachment.SubmitTrackingSample(scaled, PlatformPose, 100.7, HeadTrackingSource.DesktopSimulation, true, true)
                    && !f.attachment.SubmitTrackingSample(PlatformPose * RelativeHead, scaled, 100.7,
                        HeadTrackingSource.DesktopSimulation, true, true), "nonrigid tracking samples");
                Require(Submit(f.attachment, 100.8, HeadTrackingSource.DesktopSimulation), "valid sample after invalid input");
                f.attachment.maximumTrackingAgeSeconds = float.NaN;
                Require(!f.attachment.ApplyLatestPose(100.81), "invalid TTL rejected");
                f.attachment.maximumTrackingAgeSeconds = 0;
                Require(!f.attachment.ApplyLatestPose(100.81), "zero TTL rejected");
            }
        }
        static void TestIndependentTrackingTimes()
        {
            using (var f = new Fixture())
            {
                f.attachment.useTrackedPose = true; f.attachment.allowUnmeasuredDesktopPreview = true;
                var head = PlatformPose * RelativeHead;
                Require(f.attachment.SubmitIndependentTrackingSample(head, 100, PlatformPose, 99.95,
                    HeadTrackingSource.DesktopSimulation, true, true) && f.attachment.ApplyLatestPose(100.01),
                    "separate fresh sources may form a pair");
                Require(Math.Abs(f.attachment.HeadTrackingAgeSeconds - 0.01) < 1e-8
                    && Math.Abs(f.attachment.PlatformTrackingAgeSeconds - 0.06) < 1e-8,
                    "each observation keeps its original age");
                var before = f.camera.localToWorldMatrix;
                Require(f.attachment.SubmitIndependentTrackingSample(head, 100.06, PlatformPose, 99.95,
                    HeadTrackingSource.DesktopSimulation, true, true) && !f.attachment.ApplyLatestPose(100.061)
                    && MatrixNear(before, f.camera.localToWorldMatrix),
                    "fresh head cannot keep an expired platform alive");
                Require(!f.attachment.SubmitIndependentTrackingSample(head, 100.07,
                    Matrix4x4.Translate(Vector3.right) * PlatformPose, 99.95,
                    HeadTrackingSource.DesktopSimulation, true, true), "changed platform cannot reuse its old timestamp");
                Require(!f.attachment.SubmitIndependentTrackingSample(head, 100.07, PlatformPose, 99.94,
                    HeadTrackingSource.DesktopSimulation, true, true), "platform clock cannot regress");
                Require(f.attachment.SubmitIndependentTrackingSample(head, 100.08, PlatformPose, 100.09,
                    HeadTrackingSource.DesktopSimulation, true, true) && !f.attachment.ApplyLatestPose(100.085),
                    "an older positive age cannot hide a future platform timestamp");
                Require(f.attachment.SubmitIndependentTrackingSample(head, 100.10, PlatformPose, 100.09,
                    HeadTrackingSource.DesktopSimulation, true, true) && !f.attachment.ApplyLatestPose(100.095),
                    "an older platform cannot hide a future head timestamp");
                Require(f.attachment.SubmitIndependentTrackingSample(head, 100.21, PlatformPose, 100.20,
                    HeadTrackingSource.DesktopSimulation, true, true) && f.attachment.ApplyLatestPose(100.22),
                    "both clocks recover with new observations");
            }
        }

        static void TestTrackingTransforms()
        {
            using (var f = new Fixture())
            {
                f.attachment.useTrackedPose = true; f.attachment.allowUnmeasuredDesktopPreview = true;
                f.platform.SetPositionAndRotation(PlatformPose.GetColumn(3),
                    Quaternion.LookRotation(PlatformPose.GetColumn(2), PlatformPose.GetColumn(1)));
                var head = PlatformPose * RelativeHead;
                f.trackedHead.SetPositionAndRotation(head.GetColumn(3), Quaternion.LookRotation(head.GetColumn(2), head.GetColumn(1)));
                Require(f.attachment.SubmitTransformTracking(100, HeadTrackingSource.DesktopSimulation, true, true)
                    && f.attachment.ApplyLatestPose(100.01), "explicit independent transform validity");
                f.attachment.virtualBoat = f.camera;
                Require(!f.attachment.ApplyLatestPose(100.02) && f.attachment.PoseStatus == "VALID_VIRTUAL_BOAT_REQUIRED", "output cannot be boat input");
                f.attachment.virtualBoat = f.boat; f.boat.localScale = Vector3.one * 2;
                Require(!f.attachment.ApplyLatestPose(100.02), "nonrigid virtual boat rejected");
                f.boat.localScale = Vector3.one;
                f.attachment.trackedHead = f.camera;
                Require(!f.attachment.SubmitTransformTracking(100.03, HeadTrackingSource.DesktopSimulation, true, true), "output cannot be tracked head input");
                f.attachment.trackedHead = f.platform;
                Require(!f.attachment.SubmitTransformTracking(100.04, HeadTrackingSource.DesktopSimulation, true, true), "head and platform inputs must differ");
                f.attachment.trackedHead = f.trackedHead; f.attachment.physicalPlatform = f.camera;
                Require(!f.attachment.SubmitTransformTracking(100.05, HeadTrackingSource.DesktopSimulation, true, true), "output cannot be platform input");
                f.attachment.ClearTracking();
                Require(!f.attachment.PoseApplied && f.attachment.TrackingSource == HeadTrackingSource.None
                    && f.attachment.TrackingAgeSeconds == -1 && !f.attachment.ApplyLatestPose(100.06), "clear invalidates sample and evidence");
                var reflection = Matrix4x4.Scale(new Vector3(-1, 1, 1));
                ExpectInvalid(() => HeadPoseAttachment.Compose(f.boat.localToWorldMatrix, reflection, head), "compose rejects reflection");
            }
        }
    }
}
