using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DragonBoatVR.EditorTools
{
    public static class AcquisitionSelfTest
    {
        static int passed, failed;

        [MenuItem("DragonBoatVR/PCB 四路采集接口自检")]
        public static void Run()
        {
            passed = failed = 0;
            Check("four channels preserved / unassigned / raw-only", TestIndependent);
            Check("zero-scale and measured-point fit / provenance", TestCalibration);
            Check("packet and channel duplicate / reorder / sequence wrap", TestOrder);
            Check("per-channel TTL / missing / clock provenance", TestFreshness);
            Check("finite / saturation / ranges / source / schema", TestInvalid);
            Check("copy isolation / replay / EOF invalidation", TestReplay);
            Check("loopback opt-in / JSON receive / paired snapshot", TestLoopback);
            Check("JSON explicit ADC values / duplicate keys / parser limits", TestJsonPacketEvidence);
            Check("recorded JSON snapshot/profile mandatory evidence", TestJsonFrameEvidence);
            Check("recorded transitions / unchanged sample / TTL / independent clocks", TestRecordedTransitions);
            Check("cached profile identity / exact legacy bytes / isolated mutations / transaction failure", TestProfileSignature);
            Check("reception diagnostic value snapshot / no side effects / listener lifecycle / transactional reload", TestReceptionDiagnostics);
            Debug.Log($"[ADC] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[ADC] RESULT=PASS PC acquisition contract and calibration fixtures; hardware unverified"
                : "[ADC] RESULT=FAIL see individual checks");
        }

        static void Check(string name, Func<bool> test)
        {
            try { bool ok = test(); if (ok) passed++; else failed++; Debug.Log($"[ADC] {name}={ok}"); }
            catch (Exception e) { failed++; Debug.LogError($"[ADC] {name}: {e}"); }
        }
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        static bool TestReceptionDiagnostics()
        {
            var lazy = new GameObject("lazy reception diagnostics fixture").AddComponent<SensorAcquisitionBridge>();
            lazy.loadProfileOnStart = false;
            var publicProfile = lazy.profile;
            string publicBytes = JsonUtility.ToJson(publicProfile);
            try
            {
                var initial = lazy.CaptureReceptionDiagnostics();
                Require(!initial.profileReady && !initial.loopbackConfigured && initial.listeningPort == 0
                    && initial.sourceId == null && initial.profileVersion == null && initial.acceptedPackets == 0
                    && initial.rejectedPackets == 0 && initial.rejectedChannels == 0 && initial.lastRejection == null,
                    "Lazy diagnostics must report uninitialized state without preparing a profile.");
                for (int i = 0; i < 8; i++) Require(lazy.CaptureReceptionDiagnostics().Matches(initial), "Lazy capture changed state.");
                Require(ReferenceEquals(publicProfile, lazy.profile) && JsonUtility.ToJson(lazy.profile) == publicBytes,
                    "Lazy capture changed the public configuration.");
                var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Require(typeof(SensorAcquisitionBridge).GetField("_profile", fields).GetValue(lazy) == null
                    && typeof(SensorAcquisitionBridge).GetField("_profileSignature", fields).GetValue(lazy) == null,
                    "Lazy capture initialized the private profile or cached identity.");
            }
            finally { Destroy(lazy); }

            var b = Bridge();
            try
            {
                var disabled = b.CaptureReceptionDiagnostics();
                Require(disabled.profileReady && !disabled.loopbackConfigured && disabled.listeningPort == 0
                    && disabled.sourceId == "unassigned-pcb" && disabled.profileVersion == b.CurrentProfile.version,
                    "Configured disabled receiver was misreported.");
                Require(!b.StartListening() && b.CaptureReceptionDiagnostics().Matches(disabled), "Disabled receiver started or changed diagnostics.");
                var enabled = b.CurrentProfile; enabled.enableLoopbackUdp = true; enabled.version = "diagnostics-fixture-v1";
                using (var free = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    enabled.loopbackPort = ((IPEndPoint)free.Client.LocalEndPoint).Port;
                b.Configure(enabled);
                var configured = b.CaptureReceptionDiagnostics();
                Require(configured.profileReady && configured.loopbackConfigured && configured.listeningPort == 0
                    && configured.sourceId == enabled.sourceId && configured.profileVersion == enabled.version,
                    "Configured opt-in must remain distinct from an active listener.");
                Require(b.StartListening() && b.CaptureReceptionDiagnostics().listeningPort == enabled.loopbackPort,
                    "Listening endpoint was not observed.");

                long now = Stopwatch.GetTimestamp();
                Require(b.Accept(Packet(1), now), "Diagnostics initial packet failed.");
                var accepted = b.CaptureReceptionDiagnostics();
                Require(accepted.acceptedPackets == 1 && accepted.rejectedPackets == 0 && accepted.rejectedChannels == 0
                    && accepted.lastRejection == "", "Accepted packet diagnostics differ.");
                Require(!b.Accept(Packet(1), Later(now, .001)), "Diagnostics duplicate packet accepted.");
                var duplicate = b.CaptureReceptionDiagnostics();
                Require(duplicate.acceptedPackets == 1 && duplicate.rejectedPackets == 1 && duplicate.rejectedChannels == 0
                    && duplicate.lastRejection == "PACKET_DUPLICATE_OR_OLD", "Packet rejection diagnostics differ.");
                var wrongSource = Packet(2); wrongSource.sourceId = "another-board";
                Require(!b.Accept(wrongSource, Later(now, .001)), "Diagnostics wrong source accepted.");
                var wrong = b.CaptureReceptionDiagnostics();
                Require(wrong.rejectedPackets == 2 && wrong.lastRejection == "INVALID_PACKET_OR_SOURCE", "Source rejection diagnostics differ.");
                var partial = Packet(2); partial.channels[0].sampleSequence = 1;
                Require(b.Accept(partial, Later(now, .002)), "Partial packet diagnostics rejected the whole packet.");
                var rejectedChannel = b.CaptureReceptionDiagnostics();
                Require(rejectedChannel.acceptedPackets == 2 && rejectedChannel.rejectedPackets == 2
                    && rejectedChannel.rejectedChannels == 1 && rejectedChannel.lastRejection == "CHANNEL_DUPLICATE_OR_OLD:CH1",
                    "Packet acceptance and channel rejection counters were conflated.");
                Require(b.Accept(Packet(3), Later(now, .003)), "Diagnostics subsequent packet failed.");
                var prior = b.CaptureReceptionDiagnostics();
                Require(prior.acceptedPackets == 3 && prior.rejectedPackets == 2 && prior.rejectedChannels == 1
                    && prior.lastRejection == rejectedChannel.lastRejection, "Successful packet erased the most recent rejection.");
                string signature = b.ConfigurationSignature;
                long capturedAt = Later(now, .004);
                string observations = JsonUtility.ToJson(b.Snapshot(capturedAt));
                for (int i = 0; i < 32; i++) Require(b.CaptureReceptionDiagnostics().Matches(prior), "Capture changed reception state.");
                Require(ReferenceEquals(signature, b.ConfigurationSignature) && JsonUtility.ToJson(b.Snapshot(capturedAt)) == observations,
                    "Capture changed configuration, observations or their receive ages.");
                var mutated = prior;
                mutated.profileReady = false; mutated.loopbackConfigured = false; mutated.listeningPort = -1;
                mutated.acceptedPackets = mutated.rejectedPackets = mutated.rejectedChannels = -1;
                mutated.sourceId = "caller-mutation"; mutated.profileVersion = "caller-version"; mutated.lastRejection = "caller-rejection";
                Require(!mutated.Matches(prior) && b.CaptureReceptionDiagnostics().Matches(prior)
                    && JsonUtility.ToJson(b.Snapshot(capturedAt)) == observations, "Returned value mutation affected active state.");

                enabled.version = "diagnostics-fixture-v2"; enabled.channels[0].mapping = "diagnostics-reloaded";
                b.ConfigureLoopback(enabled);
                var reset = b.CaptureReceptionDiagnostics();
                var empty = b.Snapshot(capturedAt);
                Require(reset.profileReady && reset.loopbackConfigured && reset.listeningPort == enabled.loopbackPort
                    && reset.profileVersion == enabled.version && reset.acceptedPackets == 0 && reset.rejectedPackets == 0
                    && reset.rejectedChannels == 0 && reset.lastRejection == "" && empty.rawValidCount == 0 && !empty.samePacket,
                    "Same-port reload did not reset diagnostics and observations.");
                foreach (var channel in empty.channels) Require(!channel.present, "Same-port reload retained a channel observation.");
                Require(prior.acceptedPackets == 3 && prior.rejectedPackets == 2 && prior.rejectedChannels == 1
                    && prior.profileVersion == "diagnostics-fixture-v1", "Later reload mutated a retained diagnostic value.");
                Require(b.Accept(Packet(1), capturedAt) && !b.Accept(Packet(1), capturedAt), "Reload failed to reset packet sequence state.");
                prior = b.CaptureReceptionDiagnostics(); signature = b.ConfigurationSignature;
                observations = JsonUtility.ToJson(b.Snapshot(capturedAt));
                var invalid = b.CurrentProfile; invalid.channels[1].channelId = "CH1";
                bool refused = false; try { b.ConfigureLoopback(invalid); } catch (ArgumentException) { refused = true; }
                Require(refused && b.CaptureReceptionDiagnostics().Matches(prior) && b.ConfigurationSignature == signature
                    && JsonUtility.ToJson(b.Snapshot(capturedAt)) == observations, "Invalid reload changed live diagnostics or observations.");
                using (var occupied = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                {
                    var replacement = b.CurrentProfile; replacement.version = "must-not-install";
                    replacement.loopbackPort = ((IPEndPoint)occupied.Client.LocalEndPoint).Port;
                    refused = false; try { b.ConfigureLoopback(replacement); } catch (SocketException) { refused = true; }
                    Require(refused && b.CaptureReceptionDiagnostics().Matches(prior) && b.ConfigurationSignature == signature
                        && JsonUtility.ToJson(b.Snapshot(capturedAt)) == observations, "Occupied-port reload changed live state.");
                }
                b.StopListening();
                var stopped = b.CaptureReceptionDiagnostics();
                var expectedStopped = prior; expectedStopped.listeningPort = 0;
                Require(stopped.Matches(expectedStopped) && JsonUtility.ToJson(b.Snapshot(capturedAt)) == observations,
                    "Stop failed to report configured-but-not-listening or changed observations and counters.");
                Require(b.StartListening(), "Diagnostics concurrency listener failed to start.");
                TestConcurrentDiagnostics(b, prior);
                Require(b.CaptureReceptionDiagnostics().Matches(prior) && b.ConfigurationSignature == signature
                    && JsonUtility.ToJson(b.Snapshot(capturedAt)) == observations, "Concurrent capture/lifecycle changed retained state.");
                return true;
            }
            finally { Destroy(b); }
        }
        static void TestConcurrentDiagnostics(SensorAcquisitionBridge bridge, AcquisitionReceptionDiagnostics listening)
        {
            Exception captureFailure = null;
            using (var ready = new ManualResetEventSlim(false))
            using (var begin = new ManualResetEventSlim(false))
            {
                // The worker only reads managed reception state and .NET sockets; all Unity calls and listener changes stay on the main thread.
                var worker = new Thread(() =>
                {
                    try
                    {
                        ready.Set();
                        Require(begin.Wait(5000), "Concurrent diagnostics start timed out.");
                        for (int i = 0; i < 512; i++)
                        {
                            var captured = bridge.CaptureReceptionDiagnostics();
                            Require(captured.listeningPort == 0 || captured.listeningPort == listening.listeningPort,
                                "Concurrent capture observed an invalid listener endpoint.");
                            captured.listeningPort = listening.listeningPort;
                            Require(captured.Matches(listening), "Concurrent capture observed torn profile or counters.");
                        }
                    }
                    catch (Exception e) { captureFailure = e; }
                }) { IsBackground = true };
                worker.Start();
                try
                {
                    Require(ready.Wait(5000), "Concurrent diagnostics worker did not become ready.");
                    begin.Set();
                    for (int i = 0; i < 16; i++)
                    { bridge.StopListening(); Require(bridge.StartListening(), "Concurrent diagnostics restart failed."); }
                }
                finally { begin.Set(); Require(worker.Join(5000), "Concurrent diagnostics worker did not finish within its bound."); }
                if (captureFailure != null) throw new Exception("Concurrent diagnostics capture failed.", captureFailure);
            }
        }
        static bool TestProfileSignature()
        {
            var input = CalibratedProfile();
            input.sourceId = "profile-cache-fixture";
            input.channels[0].maximumAgeSeconds = 0.12345678901234567;
            input.channels[0].calibration.scaleNPerRaw = 2.9999999329447746;
            input.channels[1].calibration.points = new[] { new AcquisitionCalibrationPoint { raw = 0.12345678901234567, forceN = 2.9999999329447746 },
                new AcquisitionCalibrationPoint { raw = 1.2345678901234567, forceN = 3.9999999329447746 } };
            input.channels[2].calibration.evidence = "line\nquote\"\\path";
            input.channels[3].calibration.points = new AcquisitionCalibrationPoint[0];
            var lazy = new GameObject("lazy profile identity fixture").AddComponent<SensorAcquisitionBridge>();
            lazy.loadProfileOnStart = false; lazy.profile = input;
            try { Require(lazy.ConfigurationSignature == JsonUtility.ToJson(lazy.CurrentProfile), "Lazy profile signature differs from legacy bytes."); }
            finally { Destroy(lazy); }
            var b = Bridge(input);
            try
            {
                string expected = JsonUtility.ToJson(b.CurrentProfile);
                Require(b.ConfigurationSignature == expected && object.ReferenceEquals(b.ConfigurationSignature, b.ConfigurationSignature),
                    "Cached signature must match the old exact representation and reuse the immutable string.");
                input.channels[0].mapping = "external-input-mutation";
                b.profile.channels[0].mapping = "inspector-copy-mutation";
                var returned = b.CurrentProfile; returned.channels[1].calibration.points[0].forceN = 999;
                Require(b.ConfigurationSignature == expected && JsonUtility.ToJson(b.CurrentProfile) == expected,
                    "Caller, public profile or returned clone changed the active private profile.");
                var invalid = b.CurrentProfile; invalid.channels[1].channelId = "CH1";
                bool refused = false; try { b.Configure(invalid); } catch (ArgumentException) { refused = true; }
                Require(refused && b.ConfigurationSignature == expected, "Rejected configuration changed cached identity.");
                var changed = b.CurrentProfile; changed.channels[0].mapping = "installed-mutation";
                b.Configure(changed);
                Require(b.ConfigurationSignature != expected && b.ConfigurationSignature == JsonUtility.ToJson(b.CurrentProfile),
                    "Installed configuration failed to update exact identity.");
                changed = b.CurrentProfile; changed.enableLoopbackUdp = true; changed.loopbackPort = 0;
                refused = false; try { b.ConfigureLoopback(changed); } catch (ArgumentException) { refused = true; }
                Require(refused && b.ConfigurationSignature == JsonUtility.ToJson(b.CurrentProfile), "Rejected listener changed identity.");
                using (var occupied = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                {
                    changed.loopbackPort = ((IPEndPoint)occupied.Client.LocalEndPoint).Port;
                    string prior = b.ConfigurationSignature;
                    refused = false; try { b.ConfigureLoopback(changed); } catch (SocketException) { refused = true; }
                    Require(refused && b.ConfigurationSignature == prior && b.LocalPort == 0, "Occupied port changed profile identity.");
                }
                using (var free = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                    changed.loopbackPort = ((IPEndPoint)free.Client.LocalEndPoint).Port;
                b.ConfigureLoopback(changed);
                Require(b.LocalPort == changed.loopbackPort && b.ConfigurationSignature == JsonUtility.ToJson(b.CurrentProfile),
                    "Successful listener install changed the legacy byte representation.");
                changed.channels[0].mapping = "same-port-profile-reload";
                b.ConfigureLoopback(changed);
                Require(b.LocalPort == changed.loopbackPort && b.ConfigurationSignature == JsonUtility.ToJson(b.CurrentProfile),
                    "Same-port reload failed to atomically update profile identity.");
                BenchmarkProfileIdentity(b);
                return true;
            }
            finally { Destroy(b); }
        }
        static void BenchmarkProfileIdentity(SensorAcquisitionBridge bridge)
        {
            const int iterations = 256;
            string expected = JsonUtility.ToJson(bridge.CurrentProfile);
            // Some Unity Mono versions expose the allocation API but always return zero.
            // Probe it before using its readings as an acceptance measurement.
            long probeStart = GC.GetAllocatedBytesForCurrentThread();
            var allocationProbe = new byte[4096];
            long probeBytes = GC.GetAllocatedBytesForCurrentThread() - probeStart;
            GC.KeepAlive(allocationProbe);
            bool allocationCounterObserved = probeBytes >= allocationProbe.Length;
            for (int i = 0; i < 8; i++)
            { Require(bridge.ConfigurationSignature == expected, "Warm cache differs."); JsonUtility.ToJson(bridge.CurrentProfile); }
            long baselineBytes = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) Require(JsonUtility.ToJson(bridge.CurrentProfile) == expected, "Legacy identity changed.");
            timer.Stop();
            long legacyBytes = GC.GetAllocatedBytesForCurrentThread() - baselineBytes;
            double legacySeconds = timer.Elapsed.TotalSeconds;
            baselineBytes = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
            for (int i = 0; i < iterations; i++) Require(bridge.ConfigurationSignature == expected, "Cached identity changed.");
            timer.Stop();
            long cachedBytes = GC.GetAllocatedBytesForCurrentThread() - baselineBytes;
            Debug.Log($"[ADC] profile identity benchmark iterations={iterations} allocationCounterObserved={allocationCounterObserved} probeBytes={probeBytes} legacyBytes={legacyBytes} cachedBytes={cachedBytes} legacySeconds={legacySeconds:R} cachedSeconds={timer.Elapsed.TotalSeconds:R}; getter only, allocation counts unverified when probe unavailable, not whole-frame or deadline guarantee");
            if (allocationCounterObserved)
                Require(cachedBytes == 0 && legacyBytes > 0, "Warmed cached profile identity must avoid per-call managed allocations.");
        }
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-8;
        static long Later(long ticks, double seconds) => ticks + (long)(seconds * Stopwatch.Frequency);
        static SensorAcquisitionBridge Bridge(AcquisitionProfile profile = null)
        {
            var bridge = new GameObject("AcquisitionTest").AddComponent<SensorAcquisitionBridge>();
            bridge.loadProfileOnStart = false;
            bridge.Configure(profile ?? AcquisitionProfile.Default());
            return bridge;
        }
        static void Destroy(SensorAcquisitionBridge bridge) { bridge.StopListening(); UnityEngine.Object.DestroyImmediate(bridge.gameObject); }
        static AcquisitionPacket Packet(uint sequence, params int[] indices)
        {
            if (indices.Length == 0) indices = new[] { 0, 1, 2, 3 };
            var result = new AcquisitionPacket { format = AcquisitionPacket.Format, sourceId = "unassigned-pcb",
                sourceKind = AcquisitionSourceKind.Fixture, sequenceValid = true, sequence = sequence,
                deviceTimeValid = true, deviceTimeUs = 987654321000UL + sequence,
                channels = new AcquisitionChannelInput[indices.Length] };
            for (int i = 0; i < indices.Length; i++) result.channels[i] = new AcquisitionChannelInput
            {
                channelId = "CH" + (indices[i] + 1), sampleSequenceValid = true, sampleSequence = sequence,
                sampleTimeValid = true, sampleTimeUs = 10000UL + (ulong)indices[i] * 37UL + sequence,
                rawUnits = "raw", hasRaw = true, raw = 100 * (indices[i] + 1), valid = true
            };
            return result;
        }
        static AcquisitionProfile CalibratedProfile()
        {
            var profile = AcquisitionProfile.Default();
            foreach (var c in profile.channels)
            {
                c.rawRangeConfigured = true; c.minimumRaw = -1000; c.maximumRaw = 1000;
                c.calibration = new AcquisitionCalibration { mode = AcquisitionCalibrationMode.ZeroScale,
                    measured = true, sourceId = profile.sourceId, version = "analytical-fixture-v1",
                    evidence = "SELFTEST ONLY: supplied known values, not real sensor measurements",
                    rawZero = 100, scaleNPerRaw = 0.5, forceRangeConfigured = true,
                    minimumForceN = -500, maximumForceN = 500 };
            }
            return profile;
        }
        static bool TestIndependent()
        {
            var b = Bridge();
            try
            {
                long now = Stopwatch.GetTimestamp(); var packet = Packet(1);
                Require(b.Accept(packet, now), "initial accept"); var s = b.Snapshot(now);
                Require(s.valid && s.samePacket && s.rawValidCount == 4 && s.calibratedValidCount == 0, "summary");
                for (int i = 0; i < 4; i++)
                {
                    var c = s.channels[i];
                    Require(c.channelId == "CH" + (i + 1) && c.sourceId == packet.sourceId && c.mapping == "Unassigned", "identity");
                    Require(c.raw == 100 * (i + 1) && c.rawUnits == "raw" && c.rawValid && !c.calibratedValid
                        && c.forceStatus == "UNCALIBRATED" && !c.rawRangeVerified, "raw-only values");
                    Require(c.sampleTimeUs == packet.channels[i].sampleTimeUs && c.deviceTimeUs == packet.deviceTimeUs, "independent timestamps");
                }
                return true;
            }
            finally { Destroy(b); }
        }
        static bool TestCalibration()
        {
            var profile = CalibratedProfile();
            profile.channels[1].calibration.mode = AcquisitionCalibrationMode.LinearFit;
            profile.channels[1].calibration.points = new[] {
                new AcquisitionCalibrationPoint { raw = 0, forceN = -50 },
                new AcquisitionCalibrationPoint { raw = 200, forceN = 50 },
                new AcquisitionCalibrationPoint { raw = 400, forceN = 150 } };
            profile.channels[2].calibration.measured = false;
            profile.channels[3].calibration.sourceId = "another-board";
            var b = Bridge(profile);
            try
            {
                long now = Stopwatch.GetTimestamp(); var p = Packet(1); b.Accept(p, now); var s = b.Snapshot(now);
                Require(s.calibratedValidCount == 2 && Near(s.channels[0].forceN, 0) && Near(s.channels[1].forceN, 50), "known calibration");
                Require(s.channels[0].calibrationVersion == "analytical-fixture-v1" && s.channels[0].calibrationMeasured, "evidence");
                Require(!s.channels[2].calibratedValid && !s.channels[3].calibratedValid, "missing and mismatched evidence");
                p = Packet(2, 1); p.channels[0].raw = 500; b.Accept(p, now); s = b.Snapshot(now);
                Require(s.channels[1].rawValid && s.channels[1].forceStatus == "CALIBRATION_EXTRAPOLATION", "no extrapolation");
                Require(!SensorAcquisitionBridge.TryLinearFit(new[] { new AcquisitionCalibrationPoint { raw = 1, forceN = 2 },
                    new AcquisitionCalibrationPoint { raw = 1, forceN = 3 } }, out _, out _, out _, out _), "degenerate fit");
                return true;
            }
            finally { Destroy(b); }
        }
        static bool TestOrder()
        {
            var b = Bridge();
            try
            {
                long now = Stopwatch.GetTimestamp(); var p = Packet(uint.MaxValue - 1);
                Require(b.Accept(p, now) && !b.Accept(p, Later(now, .01)), "packet duplicate");
                Require(b.Accept(Packet(uint.MaxValue), now) && b.Accept(Packet(0), now), "wrap");
                Require(!b.Accept(Packet(uint.MaxValue - 2), now), "old packet");
                p = Packet(1); p.channels[0].sampleSequence = 0;
                Require(b.Accept(p, Later(now, .01)), "partial new packet"); var s = b.Snapshot(Later(now, .01));
                Require(s.channels[0].packetSequence == 0 && s.channels[0].receivedMonotonicTicks == now
                    && s.channels[1].packetSequence == 1 && !s.samePacket, "duplicate channel must not refresh TTL");
                p = Packet(2, 0); p.channels[0].sampleSequence = uint.MaxValue;
                Require(!b.Accept(p, Later(now, .02)), "old channel");
                p = Packet(3, 0); Require(!b.Accept(p, now - 1), "receive clock reversal");
                return true;
            }
            finally { Destroy(b); }
        }
        static bool TestFreshness()
        {
            var b = Bridge();
            try
            {
                long now = Stopwatch.GetTimestamp(); b.Accept(Packet(1, 0), now); var s = b.Snapshot(now);
                Require(s.rawValidCount == 1 && s.channels[1].rawStatus == "MISSING", "missing channel");
                b.Accept(Packet(2), now);
                var p = Packet(3, 0, 1, 2); p.deviceTimeUs = 1;
                foreach (var c in p.channels) c.sampleTimeUs = 1;
                b.Accept(p, Later(now, .08)); s = b.Snapshot(Later(now, .11));
                Require(s.rawValidCount == 3 && !s.valid && s.channels[3].rawStatus == "STALE"
                    && s.channels[0].rawValid, "single-channel expiry with independent local clocks");
                Require(!b.Snapshot(now - 1).valid, "future receive time rejected in snapshot");
                return true;
            }
            finally { Destroy(b); }
        }
        static bool TestInvalid()
        {
            var b = Bridge(CalibratedProfile());
            try
            {
                long now = Stopwatch.GetTimestamp(); var p = Packet(1);
                p.channels[0].raw = double.NaN; p.channels[1].saturated = true;
                p.channels[2].raw = 1001; p.channels[3].valid = false;
                Require(b.Accept(p, now), "invalid values retained for diagnostics"); var s = b.Snapshot(now);
                Require(!s.valid && s.rawValidCount == 0 && s.calibratedValidCount == 0, "invalid data cannot become force");
                Require(s.channels[0].rawStatus == "NONFINITE_RAW" && s.channels[0].rawText == "NaN"
                    && s.channels[1].rawStatus == "SATURATED" && s.channels[2].rawStatus == "RAW_OUT_OF_RANGE", "fault reason");
                Require(!JsonUtility.ToJson(s).Contains(":NaN"), "snapshot remains JSON serializable");
                p = Packet(2); p.channels[0].hasRaw = false; p.channels[1].rawUnits = "mV";
                p.channels[2].raw = double.PositiveInfinity; b.Accept(p, now); s = b.Snapshot(now);
                Require(s.channels[0].rawStatus == "RAW_MISSING" && s.channels[1].rawStatus == "RAW_UNITS_MISMATCH"
                    && !s.channels[2].calibratedValid, "missing units and infinity");
                p = Packet(3); p.sourceId = "other"; Require(!b.Accept(p, now), "source identity");
                p = Packet(3); p.channels[1].channelId = "CH1"; Require(!b.Accept(p, now), "duplicate channel id");
                p = Packet(3); p.channels[0].channelId = "CH9"; Require(!b.Accept(p, now), "unknown channel");
                Require(!b.AcceptJson("{}", now) && !b.AcceptJson("not json", now), "schema and malformed JSON");
                var profile = CalibratedProfile(); profile.channels[0].calibration.maximumForceN = -1;
                b.Configure(profile); b.Accept(Packet(1), now);
                Require(b.Snapshot(now).channels[0].forceStatus == "FORCE_OUT_OF_RANGE", "force range");
                profile.channels[0].maximumAgeSeconds = double.NaN;
                bool refused = false; try { b.Configure(profile); } catch (ArgumentException) { refused = true; }
                Require(refused, "invalid profile");
                return true;
            }
            finally { Destroy(b); }
        }
        static bool TestReplay()
        {
            var b = Bridge(CalibratedProfile());
            try
            {
                long now = Stopwatch.GetTimestamp(); var p = Packet(1); b.Accept(p, now);
                p.channels[0].raw = -999; var original = b.Snapshot(now);
                Require(original.channels[0].raw == 100, "input ownership");
                var replay = AcquisitionSnapshot.ForReplay(original);
                Require(replay.replayed && replay.valid && replay.channels[1].forceN == original.channels[1].forceN, "recorded validity preserved");
                replay.channels[0].raw = 456; Require(original.channels[0].raw == 100, "replay deep clone");
                var expired = AcquisitionSnapshot.ForReplay(original, true);
                Require(expired.replayed && !expired.valid && expired.rawValidCount == 0 && expired.calibratedValidCount == 0, "EOF summary");
                foreach (var c in expired.channels) Require(!c.valid && !c.rawValid && !c.calibratedValid && c.hasRaw, "EOF channel");
                Require(original.valid && b.Snapshot(now).valid, "replay cannot mutate live state");
                return true;
            }
            finally { Destroy(b); }
        }
        static bool TestLoopback()
        {
            var b = Bridge();
            try
            {
                Require(!b.StartListening() && b.LocalPort == 0, "disabled by default");
                var profile = CalibratedProfile(); profile.enableLoopbackUdp = true; profile.loopbackPort = 0;
                b.Configure(profile); Require(b.StartListening() && b.LocalPort > 0, "explicit loopback bind");
                using (var sender = new UdpClient())
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(Packet(11)));
                    sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, b.LocalPort));
                    var watch = Stopwatch.StartNew(); while (b.acceptedPackets == 0 && watch.ElapsedMilliseconds < 500) b.PollLoopback();
                }
                var s = b.Snapshot(); Require(s.valid && s.samePacket && s.calibratedValidCount == 4, "network-calibration-snapshot chain");
                Require(s.channels[3].raw == 400 && Near(s.channels[3].forceN, 150)
                    && s.channels[3].packetSequence == 11, "no channel collapse");
                return true;
            }
            finally { Destroy(b); }
        }

        static bool EvidenceRejected(Action action)
        { try { action(); return false; } catch (InvalidDataException) { return true; } }
        static string Omit(string json, string name)
        {
            string pattern = "\"" + Regex.Escape(name) + "\":(?:\"(?:\\\\.|[^\"\\\\])*\"|true|false|null|-?[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?),?";
            return Regex.Replace(json, pattern, "").Replace(",}", "}");
        }
        static bool TestJsonPacketEvidence()
        {
            string json = JsonUtility.ToJson(Packet(1));
            AcquisitionJsonEvidence.ValidatePacketJson(json);
            foreach (string name in new[] { "format", "sourceId", "sourceKind", "sequenceValid", "sequence",
                "deviceTimeValid", "deviceTimeUs", "channelId", "sampleSequenceValid", "sampleSequence",
                "sampleTimeValid", "sampleTimeUs", "rawUnits", "hasRaw", "raw", "valid", "saturated" })
                Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidatePacketJson(Omit(json, name))), "packet missing " + name);
            Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidatePacketJson(json.Replace("\"raw\":100", "\"raw\":100,\"raw\":0"))), "duplicate raw key");
            Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidatePacketJson(json.Replace("\"raw\":100", "\"raw\":100,\"r\\u0061w\":0"))), "escaped duplicate key");
            Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidatePacketJson(json.Replace("\"raw\":100", "\"raw\":null")))
                && EvidenceRejected(() => AcquisitionJsonEvidence.ValidatePacketJson(json.Replace("\"raw\":100", "\"raw\":1e999"))), "null/nonfinite raw");
            Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidatePacketJson(json.Replace("\"sequence\":1", "\"sequence\":-1")))
                && EvidenceRejected(() => AcquisitionJsonEvidence.ValidatePacketJson(json.Replace("\"sequence\":1", "\"sequence\":4294967296"))), "unsigned range");
            Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidatePacketJson(json + "{}"))
                && EvidenceRejected(() => AcquisitionJsonEvidence.ValidatePacketJson(json + new string(' ', 16001))), "trailing/size limit");
            string deep = json.Substring(0, json.Length - 1) + ",\"extra\":" + new string('[', 50) + "0" + new string(']', 50) + "}";
            Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidatePacketJson(deep)), "recursive depth limit");
            var unavailable = Packet(2); unavailable.deviceTimeValid = false;
            foreach (var c in unavailable.channels) { c.hasRaw = false; c.valid = false; c.sampleTimeValid = false; }
            string noRaw = Omit(Omit(Omit(JsonUtility.ToJson(unavailable), "raw"), "sampleTimeUs"), "deviceTimeUs");
            AcquisitionJsonEvidence.ValidatePacketJson(noRaw);
            return true;
        }
        static bool TestJsonFrameEvidence()
        {
            var b = Bridge();
            try
            {
                long now = Stopwatch.GetTimestamp(); Require(b.Accept(Packet(1), now), "frame setup");
                var frame = new SimulationFrame { recordType = "header", hasAcquisitionSnapshot = true,
                    acquisitionSnapshot = b.Snapshot(now), acquisitionProfile = b.CurrentProfile };
                string json = JsonUtility.ToJson(frame);
                AcquisitionJsonEvidence.ValidateFrameJson(json);
                foreach (string name in new[] { "schemaVersion", "recordType", "hasAcquisitionSnapshot", "format",
                    "profileVersion", "capturedMonotonicTicks", "replayed", "rawValidCount", "calibratedValidCount", "status",
                    "receivedMonotonicTicks", "ageSeconds", "raw", "rawNumericFinite", "sourceValid", "present",
                    "packetSequence", "sampleSequence", "rawStatus", "forceStatus", "calibrationMeasured",
                    "calibrationEvidence", "version", "maximumAgeSeconds", "rawRangeConfigured", "scaleNPerRaw" })
                    Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidateFrameJson(Omit(json, name))), "frame missing " + name);
                string zeroClock = Omit(Omit(Omit(json, "capturedMonotonicTicks"), "receivedMonotonicTicks"), "ageSeconds");
                Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidateFrameJson(zeroClock)), "default zero clock cannot prove freshness");
                Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidateFrameJson(json.Replace("\"raw\":100", "\"raw\":100,\"raw\":0"))), "frame nested duplicate key");
                frame.recordType = "frame"; frame.acquisitionProfile = null;
                AcquisitionJsonEvidence.ValidateFrameJson(JsonUtility.ToJson(frame));
                frame.acquisitionSnapshot = new AcquisitionSnapshot { sourceId = "unassigned-pcb", channels = new AcquisitionChannelSnapshot[4] };
                Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidateFrameJson(JsonUtility.ToJson(frame))), "empty materialized snapshot denied");
                return true;
            }
            finally { Destroy(b); }
        }
        static bool TestRecordedTransitions()
        {
            var b = Bridge();
            try
            {
                long now = Stopwatch.GetTimestamp(); Require(b.Accept(Packet(uint.MaxValue - 1), now), "transition setup");
                var first = b.Snapshot(now);
                var held = b.Snapshot(Later(now, .05));
                SensorAcquisitionBridge.ValidateRecordedSnapshot(first, b.CurrentProfile);
                SensorAcquisitionBridge.ValidateRecordedSnapshot(held, b.CurrentProfile);
                AcquisitionJsonEvidence.ValidateTransition(first, held);
                var expired = b.Snapshot(Later(now, .11));
                AcquisitionJsonEvidence.ValidateTransition(held, expired);
                Require(!expired.valid, "held sample expires normally");
                var changed = JsonUtility.FromJson<AcquisitionSnapshot>(JsonUtility.ToJson(held));
                changed.channels[0].raw++;
                Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidateTransition(first, changed)), "same sequence raw mutation");
                changed = JsonUtility.FromJson<AcquisitionSnapshot>(JsonUtility.ToJson(held));
                changed.channels[0].receivedMonotonicTicks = changed.capturedMonotonicTicks; changed.channels[0].ageSeconds = 0;
                SensorAcquisitionBridge.ValidateRecordedSnapshot(changed, b.CurrentProfile);
                Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidateTransition(first, changed)), "self-consistent duplicate cannot refresh TTL");
                changed = JsonUtility.FromJson<AcquisitionSnapshot>(JsonUtility.ToJson(first)); changed.capturedMonotonicTicks--;
                Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidateTransition(first, changed)), "capture clock reversal");
                Require(b.Accept(Packet(uint.MaxValue, 0), Later(now, .06)), "partial newer sample");
                var partial = b.Snapshot(Later(now, .07));
                AcquisitionJsonEvidence.ValidateTransition(held, partial);
                Require(partial.channels[1].receivedMonotonicTicks == first.channels[1].receivedMonotonicTicks, "independent unchanged channel clocks");
                Require(b.Accept(Packet(0), Later(now, .08)), "packet and sample wrap");
                var wrapped = b.Snapshot(Later(now, .09)); AcquisitionJsonEvidence.ValidateTransition(partial, wrapped);
                changed = JsonUtility.FromJson<AcquisitionSnapshot>(JsonUtility.ToJson(wrapped));
                changed.channels[0].sampleSequence = uint.MaxValue - 2;
                Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidateTransition(partial, changed)), "old sample across wrap");
                changed = JsonUtility.FromJson<AcquisitionSnapshot>(JsonUtility.ToJson(wrapped));
                changed.channels[0].packetSequence = partial.channels[0].packetSequence;
                Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidateTransition(partial, changed)), "new sample needs newer packet");
                changed = JsonUtility.FromJson<AcquisitionSnapshot>(JsonUtility.ToJson(wrapped)); changed.channels[0].present = false;
                Require(EvidenceRejected(() => AcquisitionJsonEvidence.ValidateTransition(partial, changed)), "present sample cannot disappear");
                return true;
            }
            finally { Destroy(b); }
        }
    }
}
