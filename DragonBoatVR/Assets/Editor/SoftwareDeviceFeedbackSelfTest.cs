using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DragonBoatVR.EditorTools
{
    public static class SoftwareDeviceFeedbackSelfTest
    {
        const uint Sequence = 12345;
        static int passed, failed;

        [MenuItem("DragonBoatVR/软件控制器反馈数值消费自检")]
        public static void Run()
        {
            passed = failed = 0;
            Check("all cable/platform feedback fields decode into independent values", TestFields);
            Check("every changed payload value reaches consumer after CRC recomputation", TestConsumerMutations);
            Check("bad CRC, sequence, flags, counts, lengths and nonfinite payloads reject", TestRejections);
            Check("frame reset and controller rejection cannot retain valid feedback", TestClearing);
            Debug.Log($"[DEVICEFEEDBACK] passed={passed} failed={failed}");
            Debug.Log(failed == 0 ? "[DEVICEFEEDBACK] RESULT=PASS packet-derived software feedback"
                : "[DEVICEFEEDBACK] RESULT=FAIL see individual checks");
            if (failed != 0) throw new InvalidOperationException($"Device feedback self-test failed: {failed} groups.");
        }

        static void Check(string name, Action test)
        {
            try { test(); passed++; Debug.Log($"[DEVICEFEEDBACK] {name}=True"); }
            catch (Exception error) { failed++; Debug.LogError($"[DEVICEFEEDBACK] {name}: {error}"); }
        }
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        static CableDeviceFeedback Cable() => new CableDeviceFeedback
        {
            commandSequence = Sequence, timeSeconds = 12.3456784, simulated = false, valid = true,
            capabilityLimited = true, status = "CABLE_TEST_STATUS",
            lengthsMm = new[] { 311.25f, 472.5f, 681.75f }, tensionsN = new[] { 1.125f, 2.25f, 3.5f },
            forceWorldN = new Vector3(4.25f, -5.5f, 6.75f), torqueAtGripWorldNm = new Vector3(-0.125f, 0.25f, -0.5f),
            forceResidualN = 7.5f, torqueResidualNm = 0.875f
        };
        static PlatformDeviceFeedback Platform() => new PlatformDeviceFeedback
        {
            commandSequence = Sequence, timeSeconds = 12.3456784, simulated = false, valid = true,
            capabilityLimited = true, status = "PLATFORM_TEST_STATUS",
            eulerDegrees = new Vector3(1.25f, -2.5f, 3.75f), translationM = new Vector3(-0.125f, 0.25f, -0.375f),
            actuatorLengthsMm = new[] { 321.5f, 432.25f, 543.125f, 654.5f, 765.25f, 876.125f }
        };

        static byte[] Pack(object value)
        {
            string method = value is CableDeviceFeedback ? "PackCableFeedback" : "PackPlatformFeedback";
            return (byte[])typeof(SoftwareDeviceProtocolTwin).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new[] { value });
        }
        static bool Consume(SoftwareDeviceProtocolTwin target, bool cable, byte[] packet, uint sequence = Sequence)
            => (bool)typeof(SoftwareDeviceProtocolTwin).GetMethod(cable ? "ConsumeCableFeedback" : "ConsumePlatformFeedback",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target,
                    new object[] { packet, sequence, cable ? "CABLE_TEST_STATUS" : "PLATFORM_TEST_STATUS" });

        static float[] Values(CableDeviceFeedback value)
        {
            var values = new List<float>();
            values.AddRange(value.lengthsMm); values.AddRange(value.tensionsN);
            values.AddRange(new[] { value.forceWorldN.x, value.forceWorldN.y, value.forceWorldN.z,
                value.torqueAtGripWorldNm.x, value.torqueAtGripWorldNm.y, value.torqueAtGripWorldNm.z,
                value.forceResidualN, value.torqueResidualNm });
            return values.ToArray();
        }
        static float[] Values(PlatformDeviceFeedback value)
        {
            var values = new List<float> { value.eulerDegrees.x, value.eulerDegrees.y, value.eulerDegrees.z,
                value.translationM.x, value.translationM.y, value.translationM.z };
            values.AddRange(value.actuatorLengthsMm);
            return values.ToArray();
        }
        static void Same(float[] a, float[] b, string message)
        {
            Require(a.Length == b.Length, message + " array length");
            for (int i = 0; i < a.Length; i++) Require(a[i] == b[i], message + " field " + i);
        }

        static void TestFields()
        {
            var sourceCable = Cable();
            byte[] cablePacket = Pack(sourceCable);
            Require(cablePacket.Length == 92, "cable wire length changed");
            Require(SoftwareDeviceProtocolTwin.TryParseCableFeedback(cablePacket, Sequence, out var cable, out bool cableCrc)
                && cableCrc && cable.valid && cable.capabilityLimited && cable.simulated, "cable metadata");
            Require(cable.commandSequence == Sequence && Math.Abs(cable.timeSeconds - sourceCable.timeSeconds) <= 0.00000051,
                "cable sequence/time");
            Same(Values(cable), Values(sourceCable), "cable decoded values");
            Require(!ReferenceEquals(cable, sourceCable) && !ReferenceEquals(cable.lengthsMm, sourceCable.lengthsMm)
                && !ReferenceEquals(cable.tensionsN, sourceCable.tensionsN), "cable aliases upstream memory");
            sourceCable.lengthsMm[0] = 999f;
            cablePacket[32] ^= 0x40;
            Require(cable.lengthsMm[0] == 311.25f, "accepted cable snapshot mutated with its source");

            var sourcePlatform = Platform();
            byte[] platformPacket = Pack(sourcePlatform);
            Require(platformPacket.Length == 84, "platform wire length changed");
            Require(SoftwareDeviceProtocolTwin.TryParsePlatformFeedback(platformPacket, Sequence, out var platform, out bool platformCrc)
                && platformCrc && platform.valid && platform.capabilityLimited && platform.simulated, "platform metadata");
            Require(platform.commandSequence == Sequence && Math.Abs(platform.timeSeconds - sourcePlatform.timeSeconds) <= 0.00000051,
                "platform sequence/time");
            Same(Values(platform), Values(sourcePlatform), "platform decoded values");
            Require(!ReferenceEquals(platform, sourcePlatform)
                && !ReferenceEquals(platform.actuatorLengthsMm, sourcePlatform.actuatorLengthsMm), "platform aliases upstream memory");
            sourcePlatform.actuatorLengthsMm[0] = 999f;
            platformPacket[56] ^= 0x40;
            Require(platform.actuatorLengthsMm[0] == 321.5f, "accepted platform snapshot mutated with its source");
        }

        static readonly int[] CableOffsets = { 32, 36, 40, 44, 48, 52, 56, 60, 64, 68, 72, 76, 80, 84 };
        static readonly int[] PlatformOffsets = { 28, 32, 36, 40, 44, 48, 56, 60, 64, 68, 72, 76 };

        static void TestConsumerMutations()
        {
            var go = new GameObject("SoftwareFeedbackPayloadConsumerSelfTest") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var target = go.AddComponent<SoftwareDeviceProtocolTwin>();
                for (int kind = 0; kind < 2; kind++)
                {
                    bool cable = kind == 0;
                    byte[] original = cable ? Pack(Cable()) : Pack(Platform());
                    float[] values = cable ? Values(Cable()) : Values(Platform());
                    int[] offsets = cable ? CableOffsets : PlatformOffsets;
                    for (int i = 0; i < offsets.Length; i++)
                    {
                        byte[] changed = (byte[])original.Clone();
                        float[] expected = (float[])values.Clone(); expected[i] += 0.375f;
                        Put(changed, offsets[i], BitConverter.GetBytes(expected[i]));
                        RepairCrc(changed);
                        Require(Consume(target, cable, changed), "changed payload rejected: " + kind + "/" + i);
                        Same(cable ? Values(target.CableFeedback) : Values(target.PlatformFeedback), expected,
                            "consumer did not use received payload");
                    }
                    byte[] changedTime = (byte[])original.Clone();
                    Put(changedTime, 12, BitConverter.GetBytes(23000001UL));
                    RepairCrc(changedTime);
                    Require(Consume(target, cable, changedTime), "changed timestamp rejected");
                    double actualTime = cable ? target.CableFeedback.timeSeconds : target.PlatformFeedback.timeSeconds;
                    Require(actualTime == 23.000001, "consumer timestamp did not come from packet");
                    byte[] changedFlags = (byte[])original.Clone();
                    Put(changedFlags, 20, BitConverter.GetBytes(0u));
                    Put(changedFlags, 24, BitConverter.GetBytes(0u));
                    RepairCrc(changedFlags);
                    Require(Consume(target, cable, changedFlags), "protocol-valid invalid device state rejected");
                    Require(cable ? !target.CableFeedback.valid && !target.CableFeedback.capabilityLimited
                        : !target.PlatformFeedback.valid && !target.PlatformFeedback.capabilityLimited,
                        "consumer validity/capability flags did not come from packet");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        static bool Parses(bool cable, byte[] packet, uint sequence = Sequence)
            => cable ? SoftwareDeviceProtocolTwin.TryParseCableFeedback(packet, sequence, out _, out _)
                : SoftwareDeviceProtocolTwin.TryParsePlatformFeedback(packet, sequence, out _, out _);

        static void TestRejections()
        {
            for (int kind = 0; kind < 2; kind++)
            {
                bool cable = kind == 0;
                byte[] original = cable ? Pack(Cable()) : Pack(Platform());
                byte[] corrupt = (byte[])original.Clone(); corrupt[32] ^= 0x80;
                Require(!Parses(cable, corrupt), "bad CRC accepted");
                Require(!Parses(cable, original, Sequence + 1), "wrong command sequence accepted");
                foreach (int offset in new[] { 0, 4, 8, 20, 24, cable ? 28 : 52 })
                {
                    byte[] changed = (byte[])original.Clone();
                    Put(changed, offset, BitConverter.GetBytes(999u)); RepairCrc(changed);
                    Require(!Parses(cable, changed), "malformed header/count accepted at " + offset);
                }
                foreach (int offset in cable ? CableOffsets : PlatformOffsets)
                {
                    byte[] changed = (byte[])original.Clone();
                    Put(changed, offset, BitConverter.GetBytes(float.NaN)); RepairCrc(changed);
                    Require(!Parses(cable, changed), "nonfinite payload accepted at " + offset);
                }
                for (int length = 0; length < original.Length; length++)
                {
                    var shortPacket = new byte[length]; Array.Copy(original, shortPacket, length);
                    if (length >= 8) RepairCrc(shortPacket);
                    Require(!Parses(cable, shortPacket), "truncated packet accepted: " + length);
                }
            }
        }

        static void TestClearing()
        {
            var go = new GameObject("SoftwareFeedbackClearingSelfTest") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var target = go.AddComponent<SoftwareDeviceProtocolTwin>();
                byte[] cable = Pack(Cable()), platform = Pack(Platform());
                Require(Consume(target, true, cable) && Consume(target, false, platform), "valid feedback setup failed");
                byte[] corruptCable = (byte[])cable.Clone(); corruptCable[40] ^= 1;
                byte[] corruptPlatform = (byte[])platform.Clone(); corruptPlatform[40] ^= 1;
                Require(!Consume(target, true, corruptCable) && !Consume(target, false, corruptPlatform), "bad CRC accepted by consumer");
                Require(!target.CableFeedback.valid && !target.PlatformFeedback.valid
                    && !target.Telemetry.cableFeedbackValid && !target.Telemetry.platformFeedbackValid,
                    "CRC failure retained valid feedback");
                Require(target.CableFeedback.lengthsMm.Length == 0 && target.CableFeedback.forceWorldN == Vector3.zero
                    && target.PlatformFeedback.eulerDegrees == Vector3.zero, "CRC failure retained stale numeric feedback");
                Require(Consume(target, true, cable) && Consume(target, false, platform), "feedback recovery failed");
                target.BeginFrame();
                Require(!target.CableFeedback.valid && !target.PlatformFeedback.valid
                    && target.CableFeedback.status == "NOT_DISPATCHED" && target.PlatformFeedback.status == "NOT_DISPATCHED",
                    "BeginFrame retained feedback from prior frame");
                Require(Consume(target, true, cable) && Consume(target, false, platform), "feedback setup failed");
                Require(!target.DispatchCable(null, default, 0.01f) && !target.DispatchPlatform(null, default, 0.01f),
                    "missing controller dispatch accepted");
                Require(!target.CableFeedback.valid && !target.PlatformFeedback.valid, "controller rejection retained valid feedback");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        static void Put(byte[] data, int offset, byte[] value) => Buffer.BlockCopy(value, 0, data, offset, value.Length);
        static void RepairCrc(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length - 4; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1u) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            Put(data, data.Length - 4, BitConverter.GetBytes(~crc));
        }
    }
}
