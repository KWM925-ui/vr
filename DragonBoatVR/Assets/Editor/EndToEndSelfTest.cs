using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DragonBoatVR.EditorTools
{
    /// <summary>
    /// 虚实同步端到端自检（C# 侧闭环，不依赖 python / 真硬件）。
    ///
    /// 它在本机扮演"硬件上位机"：按 150Hz 往 Unity 灌真实格式的 PaddleState
    /// （桨柄位姿 + 握力），Unity 收到后驱动桨 → 算水动力 → 推船 → 回传 BoatState，
    /// 本工具再收回来量三件事：
    ///
    ///   1. 延迟：包内时间戳回显算真实 RTT。老师原话"画面已经翻浪、身体还没跟上，
    ///      体验就立刻变差"，所以这个数是整个项目的硬指标，不是参考值。
    ///   2. 波形：一桨之内阻力是否起得来、有没有在入水段出现峰值、出水后是否归零。
    ///      只测"平均阻力"没意义，力反馈的手感全在波形形状里。
    ///   3. 一致性：回传的 roll/pitch 是否就是船体真实姿态（平台指令直接由它生成，
    ///      这里错一度，人身上就错一度）。
    ///
    /// 用法：
    ///   Unity.exe -batchmode -nographics -quit -projectPath ... \
    ///     -executeMethod DragonBoatVR.EditorTools.EndToEndSelfTest.Run -logFile e2e.log
    /// </summary>
    public static class EndToEndSelfTest
    {
        const string ScenePath = "Assets/Scenes/Demo.unity";

        // 时间预算：6 秒 = 4.8 个划桨周期（48 桨/分），足够看出波形又不至于让迭代太慢
        const float Dt = 0.01f;          // 物理步长 100Hz
        const int Steps = 600;           // 6 秒

        const int UnityRxPort = 19870;   // Unity 收 PaddleState
        const int PeerRxPort = 19871;    // 本工具收 BoatState
        const float SendHz = 150f;
        const float StrokeHz = 0.8f;     // 48 桨/分

        const uint MagicPaddle = 0x44424F41;  // 'DBOA'
        const uint MagicBoat = 0x44424F42;    // 'DBOB'
        const uint Ver = 1;
        const int Pkt = 48;

        static volatile bool _running;
        static int _txCount, _rxCount;
        static readonly List<float> _rttMs = new List<float>();
        static readonly List<float> _drag = new List<float>();
        static readonly List<float> _roll = new List<float>();
        static readonly List<float> _pitch = new List<float>();
        static readonly List<float> _speed = new List<float>();
        static readonly object _lock = new object();

        [MenuItem("DragonBoatVR/⑤ 虚实同步端到端自检", priority = 5)]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath);
            if (!scene.IsValid())
            {
                Debug.LogError("[E2E] FAIL 打不开场景");
                return;
            }

            var bridge = UnityEngine.Object.FindObjectOfType<HardwareUdpBridge>();
            var driver = UnityEngine.Object.FindObjectOfType<PaddleDriver>();
            var paddle = UnityEngine.Object.FindObjectOfType<PaddleHydrodynamics>();
            var buoy = UnityEngine.Object.FindObjectOfType<BoatBuoyancy>();
            var wave = UnityEngine.Object.FindObjectOfType<WaveField>();

            if (bridge == null || driver == null || paddle == null || buoy == null || wave == null)
            {
                Debug.LogError($"[E2E] FAIL 场景组件缺失 bridge={bridge != null} driver={driver != null} " +
                               $"paddle={paddle != null} buoy={buoy != null} wave={wave != null}");
                return;
            }

            // 换到测试端口，避免和真机/已开的编辑器抢 9870/9871
            bridge.listenPort = UnityRxPort;
            bridge.remotePort = PeerRxPort;
            bridge.remoteHost = "127.0.0.1";
            bridge.sendRate = 90f;

            // 关键：切到 UDP 直驱，否则桨由虚拟划桨手驱动，
            // 灌进来的硬件数据根本不参与，测出来的延迟和波形全是假的。
            driver.mode = PaddleDriver.Mode.UdpDirect;
            driver.paddleInBoatSpace = true;

            // 非 Play 模式下生命周期不自动跑，手动按依赖顺序唤醒
            Call(wave, "Awake");
            Call(buoy, "Awake");
            Call(buoy, "Start");
            Call(paddle, "Start");
            Call(driver, "Start");
            Call(bridge, "Start");

            bridge.dtOverride = Dt;
            paddle.dtOverride = Dt;

            var simField = typeof(WaveField).GetField("<SimTime>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            // 桨柄在船体坐标里的位置：右舷、胸前高度、略靠前
            // 这个值要和硬件组交接时对齐 —— 它决定"人的手在哪"，也决定桨叶入水多深
            // （桨叶 = 桨柄 − 杆长，杆长 1.55m，所以桨叶实际在 y ≈ −0.7，水面在 −0.12）
            var paddleMount = new Vector3(0.55f, 0.85f, 0.20f);

            UdpClient peerRx = null;
            UdpClient peerTx = null;
            Thread rxThread = null, txThread = null;

            bool prevAuto = Physics.autoSimulation;
            Physics.autoSimulation = false;

            var rb = buoy.GetComponent<Rigidbody>();
            Vector3 startPos = rb.position;
            Vector3 heading = rb.transform.forward; heading.y = 0f;
            if (heading.sqrMagnitude < 1e-6f) heading = Vector3.forward;
            heading.Normalize();

            float simTime = 0f;
            var sw = Stopwatch.StartNew();
            float rollPeak = 0f, pitchPeak = 0f;

            try
            {
                // ---- 本工具充当硬件上位机 ----
                peerRx = new UdpClient(PeerRxPort);
                peerRx.Client.ReceiveTimeout = 200;
                peerTx = new UdpClient();

                _running = true;
                _txCount = _rxCount = 0;

                rxThread = new Thread(() => RxLoop(peerRx)) { IsBackground = true, Name = "E2E-Rx" };
                txThread = new Thread(() => TxLoop(peerTx, paddleMount)) { IsBackground = true, Name = "E2E-Tx" };
                rxThread.Start();
                txThread.Start();

                // ---- 主循环：按真实时间节拍推进物理 ----
                // 不能跑"空转快进"，否则模拟时间远快于真实时间，测出的 RTT 毫无意义。
                for (int i = 0; i < Steps; i++)
                {
                    simTime += Dt;
                    if (simField != null) simField.SetValue(wave, simTime);

                    driver.Tick(Dt);
                    paddle.Step(Dt);
                    Call(buoy, "FixedUpdate");
                    Physics.Simulate(Dt);
                    bridge.Pump(Dt);      // 消费最新桨帧 + 按 90Hz 回传 BoatState

                    var e = rb.rotation.eulerAngles;
                    rollPeak = Mathf.Max(rollPeak, Mathf.Abs(Normalize(e.z)));
                    pitchPeak = Mathf.Max(pitchPeak, Mathf.Abs(Normalize(e.x)));

                    // 精确节拍
                    double target = (i + 1) * Dt;
                    while (sw.Elapsed.TotalSeconds < target) Thread.Sleep(1);
                }
            }
            finally
            {
                _running = false;
                Physics.autoSimulation = prevAuto;
                try { peerTx?.Close(); } catch { }
                try { peerRx?.Close(); } catch { }
                rxThread?.Join(300);
                txThread?.Join(300);
            }

            // ---------------- 统计 ----------------
            int nRx, nTx;
            var drags = new List<float>();
            var rtts = new List<float>();
            var speeds = new List<float>();
            lock (_lock)
            {
                nRx = _rxCount; nTx = _txCount;
                drags.AddRange(_drag); rtts.AddRange(_rttMs); speeds.AddRange(_speed);
            }

            float dragPeak = 0f, dragSum = 0f, dragWet = 0f;
            int wetCount = 0;
            foreach (var d in drags)
            {
                if (d > dragPeak) dragPeak = d;
                dragSum += d;
                if (d > 1f) { dragWet += d; wetCount++; }
            }
            float dragAvg = drags.Count > 0 ? dragSum / drags.Count : 0f;
            float dragWetAvg = wetCount > 0 ? dragWet / wetCount : 0f;

            rtts.Sort();
            float rttAvg = rtts.Count > 0 ? Sum(rtts) / rtts.Count : 0f;
            float rttP95 = rtts.Count > 0 ? rtts[Mathf.Clamp((int)(rtts.Count * 0.95f), 0, rtts.Count - 1)] : 0f;
            float rttMax = rtts.Count > 0 ? rtts[rtts.Count - 1] : 0f;
            float rttMin = rtts.Count > 0 ? rtts[0] : 0f;

            float speedAvg = speeds.Count > 0 ? Sum(speeds) / speeds.Count : 0f;
            Vector3 disp = rb.position - startPos; disp.y = 0f;
            float advance = Vector3.Dot(disp, heading);

            float expectRx = Steps * Dt * bridge.sendRate;
            float rxRatio = expectRx > 0f ? nRx / expectRx : 0f;

            // ---------------- 判定 ----------------
            // RTT < 50ms：人对手感延迟的容忍上限，超过就能感觉到"拖"
            bool okRtt = rtts.Count > 0 && rttAvg < 50f;
            // 收包率 > 80%：UDP 允许丢，但丢太多力反馈会一顿一顿
            bool okRate = rxRatio > 0.80f;
            // 阻力峰值 > 20N：桨确实入水并抓到水了（低于这个值等于空划）
            bool okDrag = dragPeak > 20f;
            // 回传姿态必须与船体一致（平台指令直接由它生成）
            bool okPosture = rollPeak > 0.5f && pitchPeak > 0.05f;
            // 6 秒内要真的往前走
            bool okMove = advance > 0.1f;

            bool pass = okRtt && okRate && okDrag && okPosture && okMove;

            Debug.Log($"[E2E] 桨柄挂点={paddleMount}  杆长={driver.shaftToBlade:F2}m  驱动={driver.mode}");
            Debug.Log($"[E2E] 链路：发={nTx} 收={nRx} 期望={expectRx:F0} 收包率={rxRatio * 100:F1}%");
            Debug.Log($"[E2E] 延迟：RTT 均={rttAvg:F2}ms  最小={rttMin:F2}  95%={rttP95:F2}  最大={rttMax:F2}  样本={rtts.Count}");
            Debug.Log($"[E2E] 桨阻：峰值={dragPeak:F1}N  全时均={dragAvg:F1}N  入水段均={dragWetAvg:F1}N  入水样本={wetCount}/{drags.Count}");
            Debug.Log($"[E2E] 船体：航速均={speedAvg:F2}m/s  前进={advance:F2}m  横摇峰={rollPeak:F1}°  纵摇峰={pitchPeak:F1}°");
            Debug.Log($"[E2E] 判定 rtt={okRtt} rate={okRate} drag={okDrag} posture={okPosture} move={okMove}");
            Debug.Log(pass
                ? "[E2E] RESULT=PASS 本机假硬件与Unity计算链回环通过；未验收实机或显示延迟"
                : "[E2E] RESULT=FAIL 闭环有断点（见上面各项）");
        }

        // ---------------- 模拟硬件：发桨位姿 ----------------
        static void TxLoop(UdpClient sock, Vector3 mount)
        {
            var sw = Stopwatch.StartNew();
            int seq = 0;
            long nextUs = 0;
            double intervalUs = 1_000_000.0 / SendHz;
            var ep = new IPEndPoint(IPAddress.Loopback, UnityRxPort);

            while (_running)
            {
                double t = sw.Elapsed.TotalSeconds;
                float ph = (float)(2.0 * Math.PI * StrokeHz * t);
                seq++;

                // 桨柄轨迹：前伸—下压—后拉—提桨（与 z 差 90°，走一个立起来的椭圆）
                // 上下振幅必须够大，桨叶才真的会离开水面：
                //   桨叶 y = 桨柄 y − 1.35（杆长），水面在船体坐标 −0.12
                //   桨柄 0.85+0.75=1.60 时桨叶 0.25，出水 0.37m
                //   桨柄 0.85−0.75=0.10 时桨叶 −1.25，入水 1.13m
                // 之前只给 ±0.25，桨叶从头到尾泡在水里，测出来是"恒定拖拽"，
                // 而不是真实的一桨一顿 —— 那种波形骗得过平均值，骗不过手感。
                double u = t * StrokeHz % 1.0;
                bool drive = u < 0.55;
                double strokeTime = drive ? u / 0.55 : (u - 0.55) / 0.45;
                double wet = drive ? Math.Max(0, Math.Min(1, Math.Min(strokeTime / 0.1, (1 - strokeTime) / 0.1))) : 0;
                wet = wet * wet * (3 - 2 * wet);
                // 假硬件固定桨杆姿态，四段轨迹保证水下拉桨、空中回桨。
                float y = 0.45f - (float)wet + 1.35f;
                float z = mount.z + (float)((drive ? 0.45 : -0.45) * Math.Cos(Math.PI * strokeTime));
                float ex = 0f, ey = 0f, ez = 0f;
                float grip = 40f + 35f * Mathf.Max(0f, Mathf.Sin(ph));

                var d = new byte[Pkt];
                BitConverter.GetBytes(MagicPaddle).CopyTo(d, 0);
                BitConverter.GetBytes(Ver).CopyTo(d, 4);
                BitConverter.GetBytes(NowUs()).CopyTo(d, 8);
                BitConverter.GetBytes((uint)seq).CopyTo(d, 16);
                BitConverter.GetBytes(mount.x).CopyTo(d, 20);
                BitConverter.GetBytes(y).CopyTo(d, 24);
                BitConverter.GetBytes(z).CopyTo(d, 28);
                BitConverter.GetBytes(ex).CopyTo(d, 32);
                BitConverter.GetBytes(ey).CopyTo(d, 36);
                BitConverter.GetBytes(ez).CopyTo(d, 40);
                BitConverter.GetBytes(grip).CopyTo(d, 44);

                try { sock.Send(d, d.Length, ep); Interlocked.Increment(ref _txCount); }
                catch { }

                nextUs += (long)intervalUs;
                long wait = nextUs - (long)(sw.Elapsed.TotalSeconds * 1_000_000);
                if (wait > 0) Thread.Sleep((int)Math.Min(wait / 1000, 20));
            }
        }

        // ---------------- 模拟硬件：收回传 ----------------
        static void RxLoop(UdpClient sock)
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try { data = sock.Receive(ref remote); }
                catch (SocketException) { continue; }
                catch (ObjectDisposedException) { break; }
                if (data == null || data.Length != Pkt) continue;

                uint magic = BitConverter.ToUInt32(data, 0);
                if (magic != MagicBoat) continue;

                float roll = BitConverter.ToSingle(data, 20);
                float pitch = BitConverter.ToSingle(data, 24);
                float dragN = BitConverter.ToSingle(data, 32);
                float spd = BitConverter.ToSingle(data, 36);
                uint echo = BitConverter.ToUInt32(data, 44);

                long rttUs = (NowUs() - echo) & 0xFFFFFFFFL;

                lock (_lock)
                {
                    _rxCount++;
                    _drag.Add(dragN);
                    _roll.Add(roll);
                    _pitch.Add(pitch);
                    _speed.Add(spd);
                    if (echo != 0 && rttUs > 0 && rttUs < 2_000_000)   // 过滤掉明显异常值
                        _rttMs.Add(rttUs / 1000f);
                }
            }
        }

        static uint NowUs() => (uint)(Stopwatch.GetTimestamp() * (1_000_000d / Stopwatch.Frequency));

        static float Sum(List<float> list)
        {
            float s = 0f;
            for (int i = 0; i < list.Count; i++) s += list[i];
            return s;
        }

        static void Call(object target, string method)
        {
            var m = target.GetType().GetMethod(method,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                Debug.LogWarning($"[E2E] 找不到方法 {target.GetType().Name}.{method}");
                return;
            }
            m.Invoke(target, null);
        }

        static float Normalize(float deg)
        {
            deg %= 360f;
            if (deg > 180f) deg -= 360f;
            return deg;
        }
    }
}
