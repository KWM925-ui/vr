using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DragonBoatVR
{
    /// <summary>
    /// 硬件 UDP 桥：与硬件组上位机（或 mock_hardware.py）双向二进制通信。
    ///
    /// 接口合同（48 字节定长包，小端序，详见《开发实施手册.md》第 2 节）：
    ///   PaddleState 硬件→Unity  端口 9870  100~200Hz（magic 0x44424F41）
    ///   BoatState   Unity→硬件  端口 9871   90~120Hz（magic 0x44424F42）
    ///
    /// 线程模型：收包在后台线程，只做"最新帧覆盖"（UDP 旧帧排队比丢帧更害人）；
    /// 主线程 LateUpdate 里取最新帧驱动场景，并回传 BoatState。
    /// </summary>
    public class HardwareUdpBridge : MonoBehaviour
    {
        [Header("网络")]
        public int listenPort = 9870;                 // 收 PaddleState
        public string remoteHost = "127.0.0.1";       // 发往对端（硬件上位机/mock）
        public int remotePort = 9871;                 // 发 BoatState

        [Header("回传内容提供者（自动查找）")]
        [Tooltip("取回传姿态的船体；为空则自动找带 Rigidbody 的船根")]
        public Transform boatTransform;
        [Tooltip("取桨阻力的桨；为空则自动找 PaddleHydrodynamics")]
        public PaddleHydrodynamics paddle;

        [Header("回传频率")]
        [Range(30f, 240f)] public float sendRate = 90f;

        [Header("状态监视（只读）")]
        public int rxSeq;
        public int rxCount;
        public int rxLost;          // 序号跳变累计
        public float rxRateHz;      // 最近 1s 平均收包率
        public bool linkAlive;      // 1s 内有包为 true
        public int rxRejected;
        public float maximumInputAgeSeconds = 0.10f;
        [HideInInspector] public bool externallyStepped;
        public bool showDebugOverlay = true;
        public PaddleState LatestPaddle = PaddleState.Zero;

        [Header("自检用")]
        [Tooltip("时间步覆盖（秒）。>0 时用它代替 Time.unscaledDeltaTime，供 batchmode 精确驱动")]
        public float dtOverride = 0f;

        // ---- 包结构（与手册 §2 逐字节对齐，勿单边改动）----
        public const uint MagicPaddleState = 0x44424F41u; // 'DBOA'
        public const uint MagicBoatState   = 0x44424F42u; // 'DBOB'
        public const uint ProtoVersion = 1u;
        public const int PacketSize = 48;

        public struct PaddleState
        {
            public uint seq;
            public ulong timestampUs;
            public Vector3 position;   // 桨柄中心；v1帧坐标由PaddleDriver配置解释（默认船体局部米）
            public Vector3 eulerDeg;   // 桨姿态欧拉角
            public float gripForce;    // N
            public static PaddleState Zero => new PaddleState();
        }

        UdpClient _rx;
        UdpClient _tx;
        Thread _rxThread;
        volatile bool _running;
        ulong _lastPaddleTsUs;   // 用于 BoatState 保留字段回显（RTT 测量）

        readonly object _latestLock = new object();
        PaddleState _latest;
        bool _hasNew;
        float _sendTimer;
        float _rateWindow;
        int _rateCount;
        long _receivedTicks;
        bool _hasAccepted;
        uint _acceptedSequence;

        void Start()
        {
            // 自动找依赖
            if (paddle == null) paddle = FindObjectOfType<PaddleHydrodynamics>(true);
            if (boatTransform == null)
            {
                var b = FindObjectOfType<BoatBuoyancy>(true);
                if (b != null) boatTransform = b.transform;
            }

            _rx = new UdpClient(listenPort);
            _tx = new UdpClient();
            _running = true;
            _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "UdpRx" };
            _rxThread.Start();
            Debug.Log($"[HardwareUdpBridge] listening :{listenPort}, sending {remoteHost}:{remotePort}");
        }

        // ---------------- 收包线程 ----------------
        void RxLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try { data = _rx.Receive(ref remote); }
                catch (SocketException) { continue; }   // 关闭时抛异常退出
                catch (ObjectDisposedException) { break; }
                if (data == null || data.Length != PacketSize) continue;

                var s = ParsePaddleState(data);
                if (s == null) continue; // magic/version 不符

                AcceptPaddle(s.Value, Stopwatch.GetTimestamp());
            }
        }

        public static PaddleState? ParsePaddleState(byte[] d)
        {
            if (d == null || d.Length != PacketSize || !BitConverter.IsLittleEndian) return null;
            // 小端解析。布局：<IIQI 7f>
            if (BitConverter.ToUInt32(d, 0) != MagicPaddleState) return null;
            if (BitConverter.ToUInt32(d, 4) != ProtoVersion) return null;
            var s = new PaddleState
            {
                timestampUs = BitConverter.ToUInt64(d, 8),
                seq = BitConverter.ToUInt32(d, 16),
                position = new Vector3(
                    BitConverter.ToSingle(d, 20), BitConverter.ToSingle(d, 24), BitConverter.ToSingle(d, 28)),
                eulerDeg = new Vector3(
                    BitConverter.ToSingle(d, 32), BitConverter.ToSingle(d, 36), BitConverter.ToSingle(d, 40)),
                gripForce = BitConverter.ToSingle(d, 44),
            };
            return SimulationValue.Finite(s.position) && SimulationValue.Finite(s.eulerDeg)
                && SimulationValue.Finite(s.gripForce) ? s : (PaddleState?)null;
        }

        public bool AcceptPaddle(PaddleState state, long receivedTicks)
        {
            if (!SimulationValue.Finite(state.position) || !SimulationValue.Finite(state.eulerDeg)
                || !SimulationValue.Finite(state.gripForce)) { Interlocked.Increment(ref rxRejected); return false; }
            lock (_latestLock)
            {
                // Half-range sequence comparison supports uint wrap, rejects duplicates/old packets.
                if (_hasAccepted && unchecked((int)(state.seq - _acceptedSequence)) <= 0)
                { Interlocked.Increment(ref rxRejected); return false; }
                if (_hasAccepted) rxLost += (int)Math.Min((uint)int.MaxValue, state.seq - _acceptedSequence - 1);
                _acceptedSequence = state.seq;
                rxSeq = unchecked((int)state.seq);
                _lastPaddleTsUs = state.timestampUs;
                _receivedTicks = receivedTicks;
                _hasAccepted = true;
                Interlocked.Increment(ref rxCount);
                _rateCount++;
                _latest = state;
                _hasNew = true;
                return true;
            }
        }

        public bool TryGetPaddle(float maxAgeSeconds, out PaddleState state)
            => TryGetPaddleAt(ObservationClockTicks(), maxAgeSeconds, out state);
        public bool TryGetPaddle(float maxAgeSeconds, out PaddleState state, out long receivedTicks)
            => TryGetPaddleAt(ObservationClockTicks(), maxAgeSeconds, out state, out receivedTicks);

        // Production uses the monotonic wall clock. Deterministic simulation-only
        // fixtures may provide a clock in the same Stopwatch tick units.
        protected virtual long ObservationClockTicks() => Stopwatch.GetTimestamp();

        public bool TryGetPaddleAt(long nowTicks, float maxAgeSeconds, out PaddleState state)
            => TryGetPaddleAt(nowTicks, maxAgeSeconds, out state, out _);

        public bool TryGetPaddleAt(long nowTicks, float maxAgeSeconds, out PaddleState state, out long receivedTicks)
        {
            lock (_latestLock)
            {
                state = _latest;
                receivedTicks = _receivedTicks;
                double age = (nowTicks - _receivedTicks) / (double)Stopwatch.Frequency;
                bool valid = _hasAccepted && SimulationValue.Finite(maxAgeSeconds) && maxAgeSeconds > 0
                    && age >= 0 && age <= maxAgeSeconds;
                if (valid) LatestPaddle = state;
                return valid;
            }
        }

        // ---------------- 主线程：消费 + 回传 ----------------
        void LateUpdate()
        {
            if (!externallyStepped) Pump(dtOverride > 0f ? dtOverride : Time.unscaledDeltaTime);
        }

        /// <summary>
        /// 单步泵：消费最新一帧 + 按频率回传 BoatState。
        /// 抽出来的原因和 PaddleDriver.Tick / PaddleHydrodynamics.Step 一样 ——
        /// batchmode 非 Play 模式下 Time.unscaledDeltaTime 不推进，
        /// 不注入 dt 就永远累不到回传间隔，一个包也发不出去（实测收包数恒为 0）。
        /// </summary>
        public void Pump(float dt)
        {
            // 统计窗口
            _rateWindow += dt;
            if (_rateWindow >= 1f)
            {
                rxRateHz = _rateCount / _rateWindow;
                lock (_latestLock) { _rateCount = 0; }
                _rateWindow = 0f;
            }

            // 取最新一帧（覆盖式，不排队）
            PaddleState frame;
            bool fresh;
            lock (_latestLock)
            {
                frame = _latest;
                fresh = _hasNew;
                _hasNew = false;
            }
            if (fresh) LatestPaddle = frame;
            linkAlive = TryGetPaddle(maximumInputAgeSeconds, out _);

            // 按固定频率回传 BoatState（与渲染帧率解耦）
            _sendTimer += dt;
            float interval = 1f / Mathf.Max(1f, sendRate);
            if (_sendTimer < interval) return;
            // 减去一个间隔而不是清零：清零会把余数吞掉，实际频率被量化到 dt 的整数倍。
            // 物理 100Hz 要发 90Hz 时，清零的写法实际只有 50Hz（实测 300 包/6 秒），
            // 少发一半 —— 平台姿态更新的密度直接减半，动着就"木"了。
            _sendTimer -= interval;
            if (_sendTimer > interval) _sendTimer = 0f;   // 防止长时间卡顿后连发
            SendBoatState();
        }

        void SendBoatState()
        {
            if (_tx == null || boatTransform == null) return;

            var rot = boatTransform.eulerAngles; // 0~360，转成 ±180 语义更直观
            float Wrap(float a) { while (a > 180f) a -= 360f; while (a < -180f) a += 360f; return a; }

            float drag = paddle != null ? paddle.CurrentDrag : 0f;
            float waveH = 0f;
            var wf = WaveField.Instance;
            if (wf != null && paddle != null)
            {
                var p = paddle.transform.position;
                waveH = wf.GetHeight(p.x, p.z) - boatTransform.position.y;
            }
            float speed = 0f;
            var rb = boatTransform.GetComponent<Rigidbody>();
            if (rb != null) speed = rb.velocity.magnitude;

            byte[] d = new byte[PacketSize];
            BitConverter.GetBytes(MagicBoatState).CopyTo(d, 0);
            BitConverter.GetBytes(ProtoVersion).CopyTo(d, 4);
            BitConverter.GetBytes((ulong)(Time.realtimeSinceStartupAsDouble * 1_000_000d)).CopyTo(d, 8);
            BitConverter.GetBytes((uint)rxCount).CopyTo(d, 16); // 复用计数作序号足够
            BitConverter.GetBytes(Wrap(rot.z)).CopyTo(d, 20);   // roll
            BitConverter.GetBytes(Wrap(rot.x)).CopyTo(d, 24);   // pitch
            BitConverter.GetBytes(Wrap(rot.y)).CopyTo(d, 28);   // yaw
            BitConverter.GetBytes(drag).CopyTo(d, 32);
            BitConverter.GetBytes(speed).CopyTo(d, 36);
            BitConverter.GetBytes(waveH).CopyTo(d, 40);
            // 44~47：回显最近收到的 PaddleState 时间戳低 32 位（微秒），
            // 供硬件端/mock 计算真实 RTT。无回显需求时可作保留位。
            BitConverter.GetBytes((uint)(_lastPaddleTsUs & 0xFFFFFFFFu)).CopyTo(d, 44);

            try { _tx.Send(d, d.Length, remoteHost, remotePort); }
            catch (Exception e) { Debug.LogWarning($"[HardwareUdpBridge] 发送失败: {e.Message}"); }
        }

        void OnDestroy()
        {
            LifecycleTrace.Mark("hardware bridge OnDestroy begin");
            _running = false;
            LifecycleTrace.Mark("hardware bridge rx close begin");
            try { _rx?.Close(); } catch { }
            LifecycleTrace.Mark("hardware bridge rx close end");
            try { _tx?.Close(); } catch { }
            LifecycleTrace.Mark("hardware bridge rx join begin");
            _rxThread?.Join(500);
            LifecycleTrace.Mark("hardware bridge OnDestroy end");
        }

        void OnGUI()
        {
            if (!showDebugOverlay || !Application.isPlaying || RideHud.HasActivePanel) return;
            // 简易链路监视器（打包后也能看，F1 之外零依赖）
            GUILayout.BeginArea(new Rect(10, 10, 320, 140), GUIContent.none, GUI.skin.box);
            GUILayout.Label("UDP Link");
            GUILayout.Label($"Alive: {linkAlive}   Rate: {rxRateHz:F0} Hz");
            GUILayout.Label($"Rx: {rxCount}   Lost: {rxLost}");
            GUILayout.Label($"Paddle pos: {LatestPaddle.position}");
            GUILayout.Label($"Drag(grip): {LatestPaddle.gripForce:F1} N");
            GUILayout.EndArea();
        }
    }
}
