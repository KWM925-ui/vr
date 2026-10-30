using System;
using UnityEngine;

namespace DragonBoatVR
{
    public enum PaddleInputKind { Synthetic, Udp, Mouse, Replay, External, Encoder }

    /// <summary>
    /// 一帧桨编码器链路的可审计状态。值来自编码器协议包，而不是直接从桨 Transform 读取。
    /// 当前是未实测的桌面样机参数；拿到真实编码器后只替换协议适配器和量程配置。
    /// </summary>
    [Serializable]
    public class PaddleEncoderTelemetry
    {
        public string provenance = "ASSUMED_SOFTWARE_ENCODER_FIXTURE: replace counts-per-unit and protocol with measured hardware";
        public bool measured;
        public uint sequence;
        public uint sourceSequence;
        public ulong deviceTimeUs;
        public bool packetBuilt;
        public bool packetAccepted;
        public bool crcValid;
        public string status = "NOT_INITIALIZED";
        public int packetBytes;
        public int[] rawCounts;
        public Vector3 decodedLocalPosition;
        public Vector3 decodedLocalEulerDegrees;
        public float gripForceN;
        public string gripForceSource = "UPSTREAM_SENSOR_SAMPLE";
    }

    [Serializable]
    public class DeviceProtocolTelemetry
    {
        public string provenance = "ASSUMED_SOFTWARE_CONTROLLER_PROTOCOL_V1: not a measured hardware protocol";
        public bool measured;
        public uint sequence;
        public bool cableCommandPacked, cableCommandParsed, cableCommandValid, cableExecuted, cableFeedbackPacked, cableFeedbackParsed, cableFeedbackValid;
        public bool platformCommandPacked, platformCommandParsed, platformCommandValid, platformExecuted, platformFeedbackPacked, platformFeedbackParsed, platformFeedbackValid;
        public bool cableCommandCrcValid, cableFeedbackCrcValid, platformCommandCrcValid, platformFeedbackCrcValid;
        public string cableStatus = "NOT_DISPATCHED", platformStatus = "NOT_DISPATCHED";
        public int cableCommandBytes, cableFeedbackBytes, platformCommandBytes, platformFeedbackBytes;
    }

    /// <summary>Unified input: blade centre in Unity world metres, rotation +Y=shaft, +Z=blade normal.
    /// timeSeconds is the local simulation timeline; deviceTimeUs is provenance, never compared across clocks.</summary>
    [Serializable]
    public struct PaddleSample
    {
        public uint sequence;
        public double timeSeconds;
        public ulong deviceTimeUs;
        public Vector3 bladePosition;
        public Quaternion bladeRotation;
        public Vector3 bladeVelocity;
        // World radians/second. Pose-only sources are differentiated by PaddleDriver.
        public Vector3 bladeAngularVelocity;
        public bool hasAngularVelocity;
        public float gripForceN;
        public bool valid;
        public PaddleInputKind source;
        // Active replay is always tagged Replay; preserve the recorded live source separately.
        public PaddleInputKind originalSource;
    }

    [Serializable]
    public struct ForceFeedbackRequest
    {
        public uint commandSequence;
        public uint inputSequence;
        public double timeSeconds;
        public Vector3 forceWorldN;
        public Vector3 torqueAtGripWorldNm;
        public Vector3 gripWorldPosition;
        // Positive power dissipated relative to the water, after feedback scaling.
        // Includes distributed flow work; it is not minus world-space grip power.
        public float dissipativePowerW;
        public bool valid;
    }

    [Serializable]
    public class CableDeviceFeedback
    {
        public uint commandSequence;
        public double timeSeconds;
        public bool simulated = true;
        public bool valid;
        public bool capabilityLimited;
        public string status;
        public float[] lengthsMm;
        public float[] tensionsN;
        public Vector3 forceWorldN;
        public Vector3 torqueAtGripWorldNm;
        public float forceResidualN;
        public float torqueResidualNm;
    }

    [Serializable]
    public struct PlatformMotionRequest
    {
        public uint commandSequence;
        public double timeSeconds;
        public Vector3 rawEulerDegrees;
        public Vector3 rawTranslationM;
        public Vector3 eulerDegrees;
        public Vector3 translationM;
        public bool valid;
        public bool clamped;
    }

    [Serializable]
    public class PlatformDeviceFeedback
    {
        public uint commandSequence;
        public double timeSeconds;
        public bool simulated = true;
        public bool valid;
        public bool capabilityLimited;
        public Vector3 eulerDegrees;
        public Vector3 translationM;
        public float[] actuatorLengthsMm;
        public string status;
    }

    public interface IForceFeedbackDevice
    {
        CableDeviceFeedback Feedback { get; }
        void Receive(ForceFeedbackRequest request, float dt);
    }

    public interface IPlatformMotionDevice
    {
        PlatformDeviceFeedback Feedback { get; }
        void Receive(PlatformMotionRequest request, float dt);
    }

    public static class SimulationValue
    {
        public static Vector3 AngularVelocity(Quaternion previous, Quaternion current, float dt)
        {
            if (dt <= 0f) return Vector3.zero;
            Quaternion delta = current * Quaternion.Inverse(previous);
            if (delta.w < 0f) delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
            Vector3 vector = new Vector3(delta.x, delta.y, delta.z);
            float sine = vector.magnitude;
            // ToAngleAxis can round w to 1 and erase a real 0.01 degree
            // encoder increment. atan2 retains the vector part at small angles.
            return Finite(delta) && sine > 0f
                ? vector * (2f * Mathf.Atan2(sine, delta.w) / (sine * dt)) : Vector3.zero;
        }
        public static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        public static bool Finite(Vector3 x) => Finite(x.x) && Finite(x.y) && Finite(x.z);
        public static bool Finite(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w)
            && q.x*q.x + q.y*q.y + q.z*q.z + q.w*q.w > 1e-8f;
    }
}
