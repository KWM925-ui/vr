using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// UDP 链路自检（C# 侧）：加载场景 → 手动启动 HardwareUdpBridge →
    /// 用本机 loopback 灌入 100 个真实格式的 PaddleState 包 →
    /// 校验 rxCount / rxLost / 解析出的位置与姿态是否与发出的一致。
    ///
    /// 这一步验证的是 Unity 端的二进制解析和收包线程，不依赖 python mock。
    /// </summary>
    public static class LinkSelfTest
    {
        const string ScenePath = "Assets/Scenes/Demo.unity";
        const uint MagicPaddle = 0x44424F41;   // 'DBOA'
        const uint Version = 1;
        const int PacketSize = 48;
        const int SendCount = 100;

        // 故意用非零、非对称的值，避免"全 0 也能过"
        static readonly float Px = 1.5f, Py = 0.8f, Pz = -2.25f;
        static readonly float Rx = 12f, Ry = -35f, Rz = 47.5f;
        const float Grip = 23.5f;

        [MenuItem("DragonBoatVR/③ UDP 链路自检（收包+解析）", priority = 2)]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);
            if (!scene.IsValid())
            {
                Debug.LogError("[LINKTEST] FAIL 无法打开场景");
                return;
            }

            var bridge = UnityEngine.Object.FindObjectOfType<HardwareUdpBridge>();
            if (bridge == null)
            {
                Debug.LogError("[LINKTEST] FAIL 场景里没有 HardwareUdpBridge");
                return;
            }

            Call(bridge, "Start");

            int sent;
            try
            {
                sent = SendFakePaddlePackets(bridge.listenPort, out string sendErr);
                if (sent < 0)
                {
                    Debug.LogError("[LINKTEST] FAIL 发送端异常: " + sendErr);
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[LINKTEST] FAIL 发送异常: " + e.GetType().Name + " " + e.Message);
                return;
            }

            // 给收包线程时间处理（100 包 @ 200Hz ≈ 0.5s）
            Thread.Sleep(900);

            // 非 Play 模式下 MonoBehaviour 生命周期不会自动跑，而 LatestPaddle 正是在
            // LateUpdate 里从私有 _latest 搬运过去的（第 158 行），故手动驱动一次。
            Call(bridge, "LateUpdate");

            int rxCount = GetField<int>(bridge, "rxCount");
            int rxLost = GetField<int>(bridge, "rxLost");
            int rxSeq = GetField<int>(bridge, "rxSeq");
            var latest = bridge.LatestPaddle;

            float dPos = Vector3.Distance(latest.position, new Vector3(Px, Py, Pz));
            float dEuler = Mathf.Max(
                Mathf.Abs(Mathf.DeltaAngle(latest.eulerDeg.x, Rx)),
                Mathf.Max(Mathf.Abs(Mathf.DeltaAngle(latest.eulerDeg.y, Ry)),
                          Mathf.Abs(Mathf.DeltaAngle(latest.eulerDeg.z, Rz))));
            float dGrip = Mathf.Abs(latest.gripForce - Grip);

            bool encoderOk = TestPaddleEncoderPipeline(out string encoderEvidence);
            bool protocolOk = TestSoftwareDeviceProtocol(out string protocolEvidence);

            bool okCount = rxCount >= SendCount * 0.9f;     // 允许 loopback 少量丢
            bool okLost = rxLost == 0;
            bool okPos = dPos < 0.01f;
            bool okEuler = dEuler < 0.05f;
            bool okGrip = dGrip < 0.05f;
            bool pass = okCount && okLost && okPos && okEuler && okGrip && encoderOk && protocolOk;

            Debug.Log($"[LINKTEST] 发出={sent} 收到={rxCount} 丢包={rxLost} 末序号={rxSeq}");
            Debug.Log($"[LINKTEST] 解析 position={latest.position} (误差 {dPos:F4}m)");
            Debug.Log($"[LINKTEST] 解析 eulerDeg={latest.eulerDeg} (最大误差 {dEuler:F3}°)  grip={latest.gripForce:F2}N");
            Debug.Log($"[LINKTEST] 判定 count={okCount} lost={okLost} pos={okPos} euler={okEuler} grip={okGrip}");
            Debug.Log("[LINKTEST] ENCODER " + encoderEvidence);
            Debug.Log("[LINKTEST] DEVICE_PROTOCOL " + protocolEvidence);
            Debug.Log(pass
                ? "[LINKTEST] RESULT=PASS C# 侧二进制解析与收包线程正常"
                : "[LINKTEST] RESULT=FAIL 通信链路有问题（见上）");
        }

        static bool TestSoftwareDeviceProtocol(out string evidence)
        {
            evidence = "not run";
            var boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
            var hydro = UnityEngine.Object.FindObjectOfType<PaddleHydrodynamics>();
            if (boat == null || hydro == null) { evidence = "missing boat or hydrodynamics"; return false; }
            var go = new GameObject("SoftwareDeviceProtocolLinkSelfTest");
            try
            {
                var cable = go.AddComponent<CableDriveTwin>();
                cable.externallyStepped = true; cable.boat = boat; cable.paddle = hydro;
                cable.showCables = false; cable.showMotorUnits = false; cable.sendUdp = false;
                var platform = go.AddComponent<StewartPlatformTwin>();
                platform.externallyStepped = true; platform.sourceBoat = boat;
                platform.autoBuildModel = false; platform.loadRuntimeConfiguration = false;
                var protocol = go.AddComponent<SoftwareDeviceProtocolTwin>();
                protocol.BeginFrame();
                var force = new ForceFeedbackRequest
                {
                    commandSequence = 123, inputSequence = 77, timeSeconds = 1.25,
                    forceWorldN = new Vector3(3.25f, 11.5f, -4.75f),
                    torqueAtGripWorldNm = new Vector3(0.55f, -0.8f, 1.1f),
                    gripWorldPosition = hydro.BladePosition + hydro.transform.up * 1.0f, valid = true
                };
                byte[] cablePacket = SoftwareDeviceProtocolTwin.PackCableCommand(force);
                bool cableDecode = SoftwareDeviceProtocolTwin.TryParseCableCommand(cablePacket,
                    out ForceFeedbackRequest cableDecoded, out _, out bool cableDecodeCrc);
                bool cableFields = cableDecode && cableDecodeCrc && cableDecoded.valid == force.valid
                    && cableDecoded.commandSequence == force.commandSequence && cableDecoded.inputSequence == force.inputSequence
                    && Math.Abs(cableDecoded.timeSeconds - force.timeSeconds) < 1e-6
                    && (cableDecoded.forceWorldN - force.forceWorldN).magnitude < 1e-5f
                    && (cableDecoded.torqueAtGripWorldNm - force.torqueAtGripWorldNm).magnitude < 1e-5f
                    && (cableDecoded.gripWorldPosition - force.gripWorldPosition).magnitude < 1e-5f;
                bool cableOk = protocol.DispatchCable(cable, force, 0.01f);
                var platformRequest = new PlatformMotionRequest
                {
                    commandSequence = 456, timeSeconds = 1.25,
                    rawEulerDegrees = new Vector3(2.3f, -1.1f, 3.7f),
                    rawTranslationM = new Vector3(0.02f, -0.03f, 0.01f),
                    eulerDegrees = new Vector3(2.3f, -1.1f, 3.7f),
                    translationM = new Vector3(0.02f, -0.03f, 0.01f), valid = true
                };
                byte[] platformPacket = SoftwareDeviceProtocolTwin.PackPlatformCommand(platformRequest);
                bool platformDecode = SoftwareDeviceProtocolTwin.TryParsePlatformCommand(platformPacket,
                    out PlatformMotionRequest platformDecoded, out _, out bool platformDecodeCrc);
                bool platformFields = platformDecode && platformDecodeCrc && platformDecoded.valid == platformRequest.valid
                    && platformDecoded.clamped == platformRequest.clamped
                    && platformDecoded.commandSequence == platformRequest.commandSequence
                    && Math.Abs(platformDecoded.timeSeconds - platformRequest.timeSeconds) < 1e-6
                    && (platformDecoded.rawEulerDegrees - platformRequest.rawEulerDegrees).magnitude < 1e-5f
                    && (platformDecoded.rawTranslationM - platformRequest.rawTranslationM).magnitude < 1e-5f
                    && (platformDecoded.eulerDegrees - platformRequest.eulerDegrees).magnitude < 1e-5f
                    && (platformDecoded.translationM - platformRequest.translationM).magnitude < 1e-5f;
                bool platformOk = protocol.DispatchPlatform(platform, platformRequest, 0.01f);
                var t = protocol.Telemetry;
                bool sizes = t.cableCommandBytes == SoftwareDeviceProtocolTwin.CableCommandBytes
                    && t.cableFeedbackBytes == 92 && t.platformCommandBytes == SoftwareDeviceProtocolTwin.PlatformCommandBytes
                    && t.platformFeedbackBytes == 84;
                bool stages = t.cableCommandPacked && t.cableCommandParsed && t.cableExecuted && t.cableFeedbackPacked && t.cableFeedbackParsed
                    && t.platformCommandPacked && t.platformCommandParsed && t.platformExecuted && t.platformFeedbackPacked && t.platformFeedbackParsed;
                bool crc = t.cableCommandCrcValid && t.cableFeedbackCrcValid && t.platformCommandCrcValid && t.platformFeedbackCrcValid;
                bool cableResponse = cable.Feedback != null && cable.Feedback.valid && cable.Feedback.simulated
                    && cable.Feedback.commandSequence == force.commandSequence
                    && cable.Feedback.lengthsMm != null && cable.Feedback.lengthsMm.Length == 3
                    && cable.Feedback.tensionsN != null && cable.Feedback.tensionsN.Length == 3;
                if (cableResponse)
                    for (int i = 0; i < 3; i++) cableResponse &= SimulationValue.Finite(cable.Feedback.lengthsMm[i])
                        && cable.Feedback.lengthsMm[i] > 0f && SimulationValue.Finite(cable.Feedback.tensionsN[i])
                        && cable.Feedback.tensionsN[i] >= 0f && cable.Feedback.tensionsN[i] <= 300f;
                bool platformResponse = platform.Feedback != null && platform.Feedback.valid && platform.Feedback.simulated
                    && platform.Feedback.commandSequence == platformRequest.commandSequence
                    && platform.Feedback.actuatorLengthsMm != null && platform.Feedback.actuatorLengthsMm.Length == 6;
                if (platformResponse)
                    for (int i = 0; i < 6; i++) platformResponse &= SimulationValue.Finite(platform.Feedback.actuatorLengthsMm[i])
                        && platform.Feedback.actuatorLengthsMm[i] >= 300f && platform.Feedback.actuatorLengthsMm[i] <= 1100f;
                bool ids = cableResponse && platformResponse;
                bool acceptedRequest = t.cableCommandValid && t.platformCommandValid
                    && t.cableFeedbackValid && t.platformFeedbackValid;
                var command = SoftwareDeviceProtocolTwin.PackCableCommand(force);
                command[32] ^= 0x20;
                bool corruptRejected = !SoftwareDeviceProtocolTwin.TryParseCableCommand(command,
                    out _, out _, out bool corruptCrc) && !corruptCrc;
                bool duplicateRejected = !protocol.DispatchCable(cable, force, 0.01f)
                    && protocol.Telemetry.cableStatus == "CABLE_COMMAND_SEQUENCE_REJECTED"
                    && cable.Feedback != null && !cable.Feedback.valid;
                evidence = $"cable={cableOk}/{cableResponse} platform={platformOk}/{platformResponse} " +
                    $"decode={cableDecode}/{cableDecodeCrc}/fields:{cableFields}; platformFields={platformFields} " +
                    $"stages={stages} crc={crc} sizes={sizes} accepted={acceptedRequest} responseSequence={ids} " +
                    $"duplicateRejected={duplicateRejected} corruptRejected={corruptRejected} " +
                    $"bytes={t.cableCommandBytes}/{t.cableFeedbackBytes}/{t.platformCommandBytes}/{t.platformFeedbackBytes} " +
                    $"status={t.cableStatus}/{t.platformStatus}";
                return cableOk && platformOk && cableResponse && platformResponse && cableFields && platformFields
                    && acceptedRequest && stages && crc && sizes && ids && duplicateRejected && corruptRejected;
            }
            catch (Exception error)
            {
                evidence = error.GetType().Name + ": " + error.Message;
                return false;
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        static bool TestPaddleEncoderPipeline(out string evidence)
        {
            evidence = "not run";
            BoatBuoyancy boat = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
            if (boat == null) { evidence = "missing boat"; return false; }
            var go = new GameObject("PaddleEncoderLinkSelfTest");
            try
            {
                var driver = go.AddComponent<PaddleDriver>();
                var pipeline = go.AddComponent<PaddleEncoderPipeline>();
                pipeline.Configure(driver, boat);
                Vector3 localPosition = new Vector3(0.2534f, -0.4178f, 1.6923f);
                Vector3 localEuler = new Vector3(13.27f, -47.63f, 82.19f);
                var source = new PaddleSample
                {
                    sequence = 77,
                    timeSeconds = 1.0,
                    deviceTimeUs = 1234567,
                    bladePosition = boat.transform.TransformPoint(localPosition),
                    bladeRotation = boat.transform.rotation * Quaternion.Euler(localEuler),
                    bladeVelocity = new Vector3(0.31f, -0.22f, 0.47f),
                    bladeAngularVelocity = new Vector3(0.11f, -0.23f, 0.41f),
                    gripForceN = 23.57f,
                    valid = true,
                    source = PaddleInputKind.Synthetic,
                    originalSource = PaddleInputKind.Synthetic
                };
                PaddleSample decoded = pipeline.Process(source, 1.0, 0.01f);
                byte[] acceptedPacket = pipeline.CopyLastPacket();
                float posError = Vector3.Distance(boat.transform.InverseTransformPoint(decoded.bladePosition), localPosition);
                float rotError = Quaternion.Angle(Quaternion.Euler(localEuler),
                    Quaternion.Inverse(boat.transform.rotation) * decoded.bladeRotation);
                float gripError = Mathf.Abs(decoded.gripForceN - source.gripForceN);
                bool accepted = decoded.valid && pipeline.Telemetry.packetAccepted && pipeline.Telemetry.crcValid
                    && decoded.sequence == source.sequence && pipeline.Telemetry.sourceSequence == source.sequence
                    && pipeline.Telemetry.packetBytes == PaddleEncoderPipeline.PacketSize
                    && acceptedPacket != null && acceptedPacket.Length == PaddleEncoderPipeline.PacketSize
                    && pipeline.Telemetry.rawCounts != null && pipeline.Telemetry.rawCounts.Length == PaddleEncoderPipeline.ChannelCount
                    && decoded.source == PaddleInputKind.Synthetic && posError < 0.0002f && rotError < 0.03f && gripError < 1e-6f;

                PaddleSample duplicate = pipeline.DecodePacket(acceptedPacket, 1.01, 0.01f);
                bool duplicateRejected = !duplicate.valid && pipeline.Telemetry.status == "SEQUENCE_REJECTED";

                byte[] corrupt = (byte[])acceptedPacket.Clone();
                corrupt[24] ^= 0x40;
                PaddleSample badCrc = pipeline.DecodePacket(corrupt, 1.02, 0.01f);
                bool crcRejected = !badCrc.valid && !pipeline.Telemetry.crcValid && pipeline.Telemetry.status == "CRC_REJECTED";

                evidence = $"accepted={accepted} duplicateRejected={duplicateRejected} crcRejected={crcRejected} " +
                    $"packetBytes={acceptedPacket?.Length ?? 0} positionErrorM={posError:F6} rotationErrorDeg={rotError:F4} gripErrorN={gripError:F4}";
                return accepted && duplicateRejected && crcRejected;
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        /// <summary>按手册 §2 的 48 字节布局灌包：magic(4) ver(4) ts(8) seq(4) + 7×float(28)</summary>
        static int SendFakePaddlePackets(int port, out string err)
        {
            err = null;
            using (var tx = new UdpClient())
            {
                var ep = new IPEndPoint(IPAddress.Loopback, port);
                for (int i = 0; i < SendCount; i++)
                {
                    var d = new byte[PacketSize];
                    BitConverter.GetBytes(MagicPaddle).CopyTo(d, 0);
                    BitConverter.GetBytes(Version).CopyTo(d, 4);
                    BitConverter.GetBytes((ulong)(DateTime.UtcNow.Ticks / 10)).CopyTo(d, 8);
                    BitConverter.GetBytes((uint)i).CopyTo(d, 16);
                    BitConverter.GetBytes(Px).CopyTo(d, 20);
                    BitConverter.GetBytes(Py).CopyTo(d, 24);
                    BitConverter.GetBytes(Pz).CopyTo(d, 28);
                    BitConverter.GetBytes(Rx).CopyTo(d, 32);
                    BitConverter.GetBytes(Ry).CopyTo(d, 36);
                    BitConverter.GetBytes(Rz).CopyTo(d, 40);
                    BitConverter.GetBytes(Grip).CopyTo(d, 44);
                    tx.Send(d, d.Length, ep);
                    Thread.Sleep(5);   // 200Hz
                }
            }
            return SendCount;
        }

        static T GetField<T>(object target, string name)
        {
            var f = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) return default;
            var v = f.GetValue(target);
            return v is T t ? t : default;
        }

        static void Call(object target, string method)
        {
            var m = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                Debug.LogWarning($"[LINKTEST] 找不到方法 {target.GetType().Name}.{method}");
                return;
            }
            m.Invoke(target, null);
        }
    }
}
