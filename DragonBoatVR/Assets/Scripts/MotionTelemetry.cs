using System;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace DragonBoatVR
{
    /// <summary>
    /// 六轴运动平台 6-DOF 遥测输出（虚实同步的核心链路）。
    ///
    /// 设计依据：《尘埃拉力赛 / 极限竞速》驱动六轴平台的通用做法 ——
    /// 引擎把车/船的姿态角、角速度、三轴加速度以固定频率打包成二进制结构体 UDP 广播，
    /// 平台上位机（SimTools / FlyPT Mover 或自研）解析后驱动电动缸。
    ///
    /// 与 HardwareUdpBridge 的分工：
    ///   HardwareUdpBridge  : 收 PaddleState(9870)、回 BoatState(9871) —— 与划桨力反馈上位机对话
    ///   MotionTelemetry    : 发 MotionState(9880)                   —— 与六轴平台上位机对话
    /// 两条链路分开，互不干扰；任一路挂了另一路照常工作。
    ///
    /// 包结构 MotionState（64 字节定长，小端，详见《开发实施手册.md》）：
    ///   偏移  类型   字段
    ///   0     u32    magic = 0x44425436 ('DBT6')
    ///   4     u32    version = 1
    ///   8     u64    timestampUs（发送时刻，单调递增微秒）
    ///   16    u32    seq（帧序号，丢包检测）
    ///   20    u32    flags（bit0=有效, bit1=在飞, bit2=限位触发）
    ///   24    f32    roll   (deg, 右舷下沉为正)
    ///   28    f32    pitch  (deg, 船头抬起为正)
    ///   32    f32    yaw    (deg, −180~180)
    ///   36    f32    rollRate  (deg/s)
    ///   40    f32    pitchRate (deg/s)
    ///   44    f32    yawRate   (deg/s)
    ///   48    f32    accSway  (m/s², +X 右)
    ///   52    f32    accHeave (m/s², +Y 上，**已扣除重力**)
    ///   56    f32    accSurge (m/s², +Z 前)
    ///   60    f32    waveHeight (m, 船位水面抬升)
    /// </summary>
    public class MotionTelemetry : MonoBehaviour
    {
        [HideInInspector] public bool externallyStepped;
        [Header("网络")]
        [Tooltip("平台上位机地址。本机联调填 127.0.0.1，多机联调填平台电脑 IP")]
        public string remoteHost = "127.0.0.1";
        [Tooltip("平台遥测端口，默认 9880")]
        public int remotePort = 9880;
        [Tooltip("广播开关：平台在上位机同网段时开，省得填 IP")]
        public bool broadcast = false;

        [Header("数据来源")]
        [Tooltip("取姿态的船体；留空自动找 BoatBuoyancy")]
        public Transform boatTransform;

        [Header("发送频率")]
        [Tooltip("平台遥测频率。电动缸一般 60~120Hz，太高会给伺服带来无谓抖动")]
        [Range(20f, 240f)] public float sendRate = 90f;

        [Header("运动提示（Motion Cueing）")]
        [Tooltip("角度低通截止频率（Hz）。太高会把水面高频毛刺原样喂给电机，人会被颠到")]
        [Range(0.2f, 12f)] public float angleLowPassHz = 3.0f;

        [Tooltip("加速度低通截止频率（Hz）")]
        [Range(0.5f, 30f)] public float accelLowPassHz = 8f;

        [Tooltip("平台软限位：最大横滚角（deg）。必须小于机械行程，留 10% 余量")]
        public float maxRoll = 12f;
        [Tooltip("平台软限位：最大俯仰角（deg）")]
        public float maxPitch = 10f;
        [Tooltip("平台软限位：最大偏航角（deg）。多数六轴平台 yaw 行程很小，给 8° 保守值")]
        public float maxYaw = 8f;
        [Tooltip("平台软限位：最大角速度（deg/s）")]
        public float maxAngleRate = 25f;
        [Tooltip("平台软限位：最大线加速度（m/s²），默认 0.4g")]
        public float maxAccel = 4f;

        [Tooltip("倾斜洗出：长时间倾斜会缓慢回中位，避免平台顶到行程极限。0=关闭")]
        [Range(0f, 1f)] public float tiltWashout = 0.15f;

        [Header("状态监视（只读）")]
        public int txSeq;
        public float txRateHz;
        public float lastRoll, lastPitch, lastYaw;
        public float lastAccSway, lastAccHeave, lastAccSurge;
        public bool limitClamped;

        public const uint Magic = 0x44425436u;  // 'DBT6'
        public const uint Version = 1u;
        public const int PacketSize = 64;

        UdpClient _tx;
        float _sendTimer;
        float _rateWindow;
        int _rateCount;

        // 滤波状态
        float _fRoll, _fPitch, _fYaw;
        float _fAccSway, _fAccHeave, _fAccSurge;
        Vector3 _lastVel;
        bool _hasLastVel;
        float _washoutRoll, _washoutPitch;

        void Start()
        {
            if (boatTransform == null)
            {
                var b = FindObjectOfType<BoatBuoyancy>(true);
                if (b != null) boatTransform = b.transform;
            }
            if (boatTransform == null)
            {
                Debug.LogWarning("[MotionTelemetry] 场景里找不到船体，遥测不会发送。");
                enabled = false;
                return;
            }

            _tx = new UdpClient();
            if (broadcast)
            {
                try { _tx.EnableBroadcast = true; } catch { /* 部分网卡不支持，退回单播 */ }
            }
            Debug.Log($"[MotionTelemetry] 平台遥测 → {remoteHost}:{remotePort} @ {sendRate}Hz");
        }

        /// <summary>一阶低通系数（与帧率无关）</summary>
        static float LowPassK(float cutoffHz, float dt)
        {
            if (cutoffHz <= 0f) return 1f;
            float rc = 1f / (2f * Mathf.PI * cutoffHz);
            return dt / (rc + dt);
        }

        void LateUpdate()
        {
            if (!externallyStepped) Pump(Time.unscaledDeltaTime);
        }

        public void Pump(float dt)
        {
            if (_tx == null || boatTransform == null) return;
            if (!SimulationValue.Finite(dt) || dt <= 0f) return;

            Collect(dt);

            _rateWindow += dt;
            _sendTimer += dt;
            if (_sendTimer < 1f / Mathf.Max(1f, sendRate)) return;
            float interval = 1f / Mathf.Max(1f, sendRate);
            _sendTimer -= interval;
            if (_sendTimer > interval) _sendTimer = 0f;

            Send();
            _rateCount++;

            if (_rateWindow >= 1f)
            {
                txRateHz = _rateCount / _rateWindow;
                _rateCount = 0; _rateWindow = 0f;
            }
        }

        /// <summary>采样 → 低通 → 洗出 → 限位</summary>
        void Collect(float dt)
        {
            // ---- 姿态角 ----
            Vector3 e = boatTransform.eulerAngles;
            float roll = Wrap(e.z);
            float pitch = Wrap(e.x);
            float yaw = Wrap(e.y);

            // 倾斜洗出：把长期倾角缓慢吃掉，只保留动态变化
            // 物理含义：平台回到中位，人感受到的是"加速度变化"而不是"一直斜着"
            if (tiltWashout > 0f)
            {
                float w = 1f - Mathf.Exp(-tiltWashout * dt);
                _washoutRoll = Mathf.Lerp(_washoutRoll, roll, w);
                _washoutPitch = Mathf.Lerp(_washoutPitch, pitch, w);
                roll -= _washoutRoll;
                pitch -= _washoutPitch;
            }

            float ka = LowPassK(angleLowPassHz, dt);
            _fRoll = Mathf.Lerp(_fRoll, roll, ka);
            _fPitch = Mathf.Lerp(_fPitch, pitch, ka);
            _fYaw = Mathf.Lerp(_fYaw, yaw, ka);

            // ---- 加速度（世界坐标，已扣重力）----
            Vector3 vel = Vector3.zero;
            var rb = boatTransform.GetComponent<Rigidbody>();
            if (rb != null) vel = rb.velocity;

            Vector3 acc = Vector3.zero;
            if (_hasLastVel)
                acc = (vel - _lastVel) / dt;
            _lastVel = vel;
            _hasLastVel = true;

            // 先在固定世界坐标微分，再投影到船体坐标，避免转弯被误当成加速度。
            acc = boatTransform.InverseTransformDirection(acc);
            float kAcc = LowPassK(accelLowPassHz, dt);
            _fAccSway = Mathf.Lerp(_fAccSway, acc.x, kAcc);
            _fAccHeave = Mathf.Lerp(_fAccHeave, acc.y, kAcc);
            _fAccSurge = Mathf.Lerp(_fAccSurge, acc.z, kAcc);

            // ---- 软限位 ----
            limitClamped = false;
            _fRoll = ClampReport(_fRoll, maxRoll);
            _fPitch = ClampReport(_fPitch, maxPitch);
            _fYaw = ClampReport(_fYaw, maxYaw);
            _fAccSway = ClampReport(_fAccSway, maxAccel);
            _fAccHeave = ClampReport(_fAccHeave, maxAccel);
            _fAccSurge = ClampReport(_fAccSurge, maxAccel);

            lastRoll = _fRoll;
            lastPitch = _fPitch;
            lastYaw = _fYaw;
            lastAccSway = _fAccSway;
            lastAccHeave = _fAccHeave;
            lastAccSurge = _fAccSurge;
        }

        float ClampReport(float v, float limit)
        {
            if (limit <= 0f) return v;
            if (v > limit) { limitClamped = true; return limit; }
            if (v < -limit) { limitClamped = true; return -limit; }
            return v;
        }

        void Send()
        {
            var rb = boatTransform.GetComponent<Rigidbody>();
            Vector3 angVel = rb != null ? boatTransform.InverseTransformDirection(rb.angularVelocity) * Mathf.Rad2Deg : Vector3.zero;

            // 角速度限幅（同步做，避免角速度单独爆表）
            float rx = ClampReport(angVel.z, maxAngleRate);   // 角速度不能按姿态角取模
            float ry = ClampReport(angVel.x, maxAngleRate);
            float rz = ClampReport(angVel.y, maxAngleRate);

            float waveH = 0f;
            var buoy = boatTransform.GetComponent<BoatBuoyancy>();
            if (buoy != null) waveH = buoy.waterElevation;

            uint flags = 0;
            flags |= 1u;                                    // bit0 有效
            if (buoy != null && buoy.submergedCount == 0)   // bit1 在飞
                flags |= 2u;
            if (limitClamped)                               // bit2 限位触发
                flags |= 4u;

            byte[] d = new byte[PacketSize];
            BitConverter.GetBytes(Magic).CopyTo(d, 0);
            BitConverter.GetBytes(Version).CopyTo(d, 4);
            BitConverter.GetBytes((ulong)(Time.realtimeSinceStartupAsDouble * 1_000_000d)).CopyTo(d, 8);
            BitConverter.GetBytes((uint)txSeq++).CopyTo(d, 16);
            BitConverter.GetBytes(flags).CopyTo(d, 20);
            BitConverter.GetBytes(_fRoll).CopyTo(d, 24);
            BitConverter.GetBytes(_fPitch).CopyTo(d, 28);
            BitConverter.GetBytes(_fYaw).CopyTo(d, 32);
            BitConverter.GetBytes(rx).CopyTo(d, 36);
            BitConverter.GetBytes(ry).CopyTo(d, 40);
            BitConverter.GetBytes(rz).CopyTo(d, 44);
            BitConverter.GetBytes(_fAccSway).CopyTo(d, 48);
            BitConverter.GetBytes(_fAccHeave).CopyTo(d, 52);
            BitConverter.GetBytes(_fAccSurge).CopyTo(d, 56);
            BitConverter.GetBytes(waveH).CopyTo(d, 60);

            try { _tx.Send(d, d.Length, remoteHost, remotePort); }
            catch (Exception e) { Debug.LogWarning($"[MotionTelemetry] 发送失败: {e.Message}"); }
        }

        static float Wrap(float a)
        {
            a %= 360f;
            if (a > 180f) a -= 360f;
            if (a < -180f) a += 360f;
            return a;
        }

        void OnDestroy()
        {
            try { _tx?.Close(); } catch { }
        }

        void OnGUI()
        {
            if (!Application.isPlaying || RideHud.HasActivePanel) return;
            GUILayout.BeginArea(new Rect(Screen.width - 300, 10, 290, 130), GUIContent.none, GUI.skin.box);
            GUILayout.Label("Motion Platform Telemetry");
            GUILayout.Label($"→ {remoteHost}:{remotePort}  {txRateHz:F0} Hz  seq {txSeq}");
            GUILayout.Label($"Roll {lastRoll,6:F2}°  Pitch {lastPitch,6:F2}°  Yaw {lastYaw,6:F2}°");
            GUILayout.Label($"Acc S{lastAccSway,6:F2} H{lastAccHeave,6:F2} G{lastAccSurge,6:F2}");
            GUILayout.Label(limitClamped ? "⚠ 软限位生效" : "限位正常");
            GUILayout.EndArea();
        }
    }
}
