#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
mock_hardware.py — 龙舟 VR 虚拟硬件模拟器
==========================================

不依赖任何真硬件，验证与 Unity（HardwareUdpBridge.cs）的 UDP 通信闭环：
  - 以 150Hz 向 Unity 发送模拟 PaddleState（正弦划桨轨迹：左右桨交替、插水-拉桨-出水）
  - 接收 Unity 回传的 BoatState（期望阻力 / 六轴姿态 / 船速 / 波高）
  - 实时统计：RTT（用回传包里带的 echo 时间戳）、收包率、丢帧数
  - 可选 --plot 开 matplotlib 实时波形（回传阻力 & 波高）

接口合同（48 字节定长包，小端序，与《开发实施手册.md》§2 逐字节对齐）：
  PaddleState: '<IIQI' + 9f + 4x  (magic 0x44424F41, version 1)
  BoatState:   '<IIQI' + 7f + 8x  (magic 0x44424F42, version 1)

用法：
  python mock_hardware.py                # 纯终端模式
  python mock_hardware.py --plot         # 带 matplotlib 波形
  python mock_hardware.py --hz 200       # 提高发包频率到 200Hz
"""

import argparse
import math
import socket
import struct
import sys
import threading
import time

MAGIC_PADDLE = 0x44424F41  # 'DBOA' 硬件->Unity
MAGIC_BOAT = 0x44424F42   # 'DBOB' Unity->硬件
VERSION = 1
PKT_SIZE = 48

# 布局（与手册 §2 / HardwareUdpBridge.cs 逐字节对齐）：
#   头 20 字节 = magic(4) + version(4) + timestamp(8) + seq(4)
#   载荷 28 字节 = 7 个 float
PADDLE_PACK = struct.Struct("<IIQI7f")    # 20 + 28 = 48 ✓
BOAT_PACK = struct.Struct("<IIQI7f")      # 20 + 28 = 48 ✓
HDR = struct.Struct("<IIQI")              # 20 字节头
FLOATS = struct.Struct("<7f")


class Stats:
    def __init__(self):
        self.lock = threading.Lock()
        self.rx_count = 0
        self.rx_rate = 0.0
        self.rtt_ms_latest = 0.0
        self.rtt_ms_avg = 0.0
        self.drag_latest = 0.0
        self.wave_latest = 0.0
        self.speed_latest = 0.0
        self.roll = 0.0
        self.pitch = 0.0
        # 波形缓存（--plot 用）
        self.t_buf = []
        self.drag_buf = []
        self.wave_buf = []

    def snapshot(self):
        with self.lock:
            return dict(rx=self.rx_count, rate=self.rx_rate, rtt=self.rtt_ms_latest,
                        rtt_avg=self.rtt_ms_avg, drag=self.drag_latest, wave=self.wave_latest,
                        speed=self.speed_latest, roll=self.roll, pitch=self.pitch,
                        t_buf=list(self.t_buf), drag_buf=list(self.drag_buf), wave_buf=list(self.wave_buf))


def rx_loop(sock: socket.socket, stats: Stats, plot_flag: bool):
    """收 Unity 回传的 BoatState，统计 RTT/收包率。
    RTT 原理：Unity 在 BoatState 的保留字段(偏移44)回显最近收到的
    PaddleState 时间戳低 32 位（微秒），本端用当前时刻减它即得往返时延。
    """
    window_start = time.perf_counter()
    window_count = 0
    rtt_accum, rtt_n = 0.0, 0
    while True:
        try:
            data, addr = sock.recvfrom(2048)
        except socket.timeout:
            continue  # 1s 没包：正常（Unity 未启动/暂停），继续等
        except OSError:
            return
        now_us = int(time.perf_counter() * 1_000_000)
        if len(data) != PKT_SIZE:
            continue
        magic, ver, ts_us, seq = HDR.unpack_from(data, 0)
        if magic != MAGIC_BOAT or ver != VERSION:
            continue
        roll, pitch, yaw, drag, speed, wave, _echo_f = FLOATS.unpack_from(data, 20)
        # 保留字段按 uint 位重解释（C# 端写入的是 uint 时间戳位模式）
        echo_u32 = struct.unpack_from("<I", data, 44)[0]

        if echo_u32:
            rtt_us = (now_us & 0xFFFFFFFF) - echo_u32
            rtt_us &= 0xFFFFFFFF
            rtt_ms = rtt_us / 1000.0
            rtt_accum += rtt_ms
            rtt_n += 1

        with stats.lock:
            stats.rx_count += 1
            window_count += 1
            if rtt_n:
                stats.rtt_ms_latest = rtt_ms
                stats.rtt_ms_avg = rtt_accum / rtt_n
            stats.drag_latest = drag
            stats.wave_latest = wave
            stats.speed_latest = speed
            stats.roll = roll
            stats.pitch = pitch
            if plot_flag and len(stats.t_buf) < 600:
                stats.t_buf.append(stats.rx_count / 90.0)
                stats.drag_buf.append(drag)
                stats.wave_buf.append(wave)

        el = time.perf_counter() - window_start
        if el >= 1.0:
            with stats.lock:
                stats.rx_rate = window_count / el
            window_count = 0
            window_start = time.perf_counter()


def make_paddle_frame(seq: int, t: float):
    """正弦划桨轨迹：模拟右桨插水-拉桨-出水的周期动作。
    坐标约定与 C# 端一致：Unity 世界坐标，Y 向上，Z 为船头方向。
    """
    stroke_hz = 0.8  # 划频：每秒 0.8 桨（约 48 桨/分钟，休闲龙舟桨频）
    phase = 2 * math.pi * stroke_hz * t

    # 桨柄中心：前后（Z）+ 上下（Y）走一个立起来的椭圆，X 固定在右舷桨位
    # 上下振幅 0.75：桨叶 = 桨柄 − 1.35m（杆长），水面在船体 −0.12
    #   桨柄 1.60 → 桨叶  0.25，出水；桨柄 0.10 → 桨叶 −1.25，入水 1.13m
    # 振幅给小了（比如 0.25）桨叶会全程泡在水里，力反馈变成"恒定拖拽"，
    # 而不是真实的一桨一顿 —— 这正是力反馈手感最要命的地方。
    x = 0.55
    y = 0.85 + 0.75 * math.cos(phase)
    z = 0.20 + 0.45 * math.sin(phase)

    # 桨的姿态欧拉角：桨杆随划桨俯仰
    rx = 30.0 * math.sin(phase)          # 前倾后仰
    ry = -12.0                            # 略朝外
    rz = 20.0 * math.sin(phase - 0.6)     # 左右倾

    # 握压（grip force）：拉桨段发力
    grip = 40.0 + 35.0 * max(0.0, math.sin(phase))

    return x, y, z, rx, ry, rz, grip


def main():
    ap = argparse.ArgumentParser(description="龙舟 VR 虚拟硬件模拟器")
    ap.add_argument("--hz", type=float, default=150.0, help="发包频率 Hz（默认 150）")
    ap.add_argument("--unity-ip", default="127.0.0.1", help="Unity 主机 IP（默认本机）")
    ap.add_argument("--rx-port", type=int, default=9871, help="收 BoatState 的本地端口（默认 9871）")
    ap.add_argument("--tx-port", type=int, default=9870, help="发 PaddleState 到 Unity 的端口（默认 9870）")
    ap.add_argument("--plot", action="store_true", help="matplotlib 实时波形")
    ap.add_argument("--duration", type=float, default=0.0,
                    help="运行时长（秒），0=一直跑。自动化回归测试用，跑完自动退出")
    args = ap.parse_args()

    # 收包 socket：显式绑定 9871（Unity 发往这个端口）
    rx_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    rx_sock.bind(("0.0.0.0", args.rx_port))
    rx_sock.settimeout(1.0)

    # 发包 socket
    tx_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    unity_addr = (args.unity_ip, args.tx_port)

    stats = Stats()
    threading.Thread(target=rx_loop, args=(rx_sock, stats, args.plot), daemon=True).start()

    interval = 1.0 / args.hz
    seq = 0
    t0 = time.perf_counter()

    # 终端打印节流
    next_report = t0 + 1.0

    print(f"[mock] 发包 -> {unity_addr} @ {args.hz:.0f}Hz；收包 <- :{args.rx_port}")
    print(f"[mock] 请在 Unity 中启动 Play（场景需挂 HardwareUdpBridge.cs，listenPort=9870）")
    if args.plot:
        print("[mock] 波形窗口将随数据到达自动绘制")

    plot_fig = None
    if args.plot:
        try:
            import matplotlib.pyplot as plt
            plt.ion()
            plot_fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(8, 6))
            ax1.set_title("BoatState.paddleDrag (N, Unity 回传)")
            ax2.set_title("BoatState.waveHeight (m, Unity 回传)")
        except ImportError:
            print("[mock] 未安装 matplotlib，退回终端模式：pip install matplotlib")
            args.plot = False

    try:
        while True:
            now = time.perf_counter()
            t = now - t0
            if args.duration > 0 and t >= args.duration:
                break
            seq += 1

            x, y, z, rx, ry, rz, grip = make_paddle_frame(seq, t)
            # 时间戳用绝对 perf_counter 钟（RTT 计算依赖同一时钟基准）
            ts_us = int(time.perf_counter() * 1_000_000)
            pkt = PADDLE_PACK.pack(MAGIC_PADDLE, VERSION, ts_us, seq,
                                   x, y, z, rx, ry, rz, grip)
            assert len(pkt) == PKT_SIZE, len(pkt)
            tx_sock.sendto(pkt, unity_addr)

            # 每秒打印链路状态
            if now >= next_report:
                s = stats.snapshot()
                print(f"[{t:7.1f}s] 发={seq:7d}  收={s['rx']:7d}  收包率={s['rate']:6.1f}Hz  "
                      f"RTT={s['rtt']:6.2f}ms(均{s['rtt_avg']:6.2f})  阻力={s['drag']:7.1f}N  "
                      f"波高={s['wave']:+.3f}m  船速={s['speed']:5.2f}m/s  "
                      f"姿态 R{s['roll']:+6.1f}°/P{s['pitch']:+6.1f}°")
                next_report += 1.0

            # 波形刷新（4Hz 足够）
            if args.plot and plot_fig is not None and int(t * 4) != int((t - interval) * 4):
                s = stats.snapshot()
                if s["t_buf"]:
                    ax1.clear(); ax1.set_title("paddleDrag (N)")
                    ax1.plot(s["t_buf"], s["drag_buf"], "r-")
                    ax2.clear(); ax2.set_title("waveHeight (m)")
                    ax2.plot(s["t_buf"], s["wave_buf"], "b-")
                    plot_fig.canvas.draw_idle()
                    plt.pause(0.001)

            # 精确节拍
            target = t0 + seq * interval
            sleep = target - time.perf_counter()
            if sleep > 0:
                time.sleep(sleep)
    except KeyboardInterrupt:
        print("\n[mock] 退出。")
    finally:
        s = stats.snapshot()
        # 汇总行给自动化脚本抓取：一行就能判断链路是否合格
        print(f"[MOCKSUM] 发={seq} 收={s['rx']} 收包率={s['rate']:.1f}Hz "
              f"RTT均={s['rtt_avg']:.2f}ms 阻力={s['drag']:.1f}N "
              f"船速={s['speed']:.2f}m/s roll={s['roll']:+.1f} pitch={s['pitch']:+.1f}")
        tx_sock.close()
        rx_sock.close()


if __name__ == "__main__":
    main()
