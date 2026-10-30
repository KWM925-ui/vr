#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
六轴平台遥测接收器 / 同步率在线监视器

监听 Unity 的 MotionTelemetry 输出（默认 UDP 9880，64 字节定长包），
实时打印姿态与加速度，并可把数据落成 CSV 供 sync_analyzer.py 做互相关分析。

包结构（小端，与 MotionTelemetry.cs 逐字节对齐）：
    0   u32  magic        0x44425436 'DBT6'
    4   u32  version
    8   u64  timestampUs
    16  u32  seq
    20  u32  flags        bit0 有效 / bit1 在飞 / bit2 限位触发
    24  f32  roll   deg
    28  f32  pitch  deg
    32  f32  yaw    deg
    36  f32  rollRate   deg/s
    40  f32  pitchRate  deg/s
    44  f32  yawRate    deg/s
    48  f32  accSway  m/s^2
    52  f32  accHeave m/s^2
    56  f32  accSurge m/s^2
    60  f32  waveHeight m

用法：
    python motion_listener.py                      # 实时打印
    python motion_listener.py --csv out.csv        # 同时落 CSV
    python motion_listener.py --port 9880 --quiet   # 只统计，不刷屏
"""

import argparse
import csv
import socket
import struct
import sys
import time

MAGIC = 0x44425436
VERSION = 1
PKT = struct.Struct("<IIQII7f")   # 4+4+8+4+4+28 = 52 → 补到 64
# 更精确：按字段逐个解，避免对齐歧义
HEAD = struct.Struct("<IIQII")     # magic, version, timestampUs, seq, flags
BODY = struct.Struct("<7f")        # roll, pitch, yaw, rollRate, pitchRate, yawRate, ... 不够

FULL = struct.Struct("<IIQIIfffffffff")   # 5 个整型 + 9 个 float = 4*5 + 4*9 = 56
# 实际字段：roll,pitch,yaw (3) + 角速度(3) + 加速度(3) + waveHeight(1) = 10 个 float
FULL = struct.Struct("<IIQII" + "f" * 10)  # 4*5 + 4*10 = 60，还差 4 字节

assert FULL.size == 60, FULL.size


def parse(data):
    """返回 dict；magic/version 不符返回 None"""
    if len(data) < 64:
        return None
    magic, version, ts, seq, flags = HEAD.unpack_from(data, 0)
    if magic != MAGIC or version != VERSION:
        return None
    vals = struct.unpack_from("<10f", data, 24)
    return {
        "ts_us": ts,
        "seq": seq,
        "flags": flags,
        "roll": vals[0], "pitch": vals[1], "yaw": vals[2],
        "roll_rate": vals[3], "pitch_rate": vals[4], "yaw_rate": vals[5],
        "acc_sway": vals[6], "acc_heave": vals[7], "acc_surge": vals[8],
        "wave_h": vals[9],
    }


def flag_text(f):
    parts = []
    if f & 1: parts.append("有效")
    if f & 2: parts.append("在飞")
    if f & 4: parts.append("限位")
    return "/".join(parts) if parts else "无效"


def main():
    ap = argparse.ArgumentParser(description="六轴平台遥测接收与同步率在线监视")
    ap.add_argument("--host", default="0.0.0.0", help="监听地址")
    ap.add_argument("--port", type=int, default=9880, help="监听端口")
    ap.add_argument("--csv", default=None, help="落盘 CSV 路径")
    ap.add_argument("--quiet", action="store_true", help="不逐帧刷屏，只打统计")
    ap.add_argument("--duration", type=float, default=0, help="运行秒数，0=一直跑")
    args = ap.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    # 大接收缓冲：90Hz × 64B 很小，但防止系统默认 64KB 在高频下溢出
    try:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1 << 20)
    except OSError:
        pass
    sock.bind((args.host, args.port))
    sock.settimeout(0.5)

    print(f"[motion] 监听 UDP {args.host}:{args.port} … (Ctrl+C 结束)")

    fh = None
    writer = None
    if args.csv:
        fh = open(args.csv, "w", newline="", encoding="utf-8")
        writer = csv.writer(fh)
        writer.writerow([
            "wall_time", "ts_us", "seq", "flags", "roll", "pitch", "yaw",
            "roll_rate", "pitch_rate", "yaw_rate",
            "acc_sway", "acc_heave", "acc_surge", "wave_h",
        ])

    count = 0
    lost = 0
    last_seq = None
    t0 = time.time()
    last_print = 0.0
    t_seq = None          # (seq, t_wall) 用于实测到达频率
    rate = 0.0
    lat_sum = 0.0
    lat_n = 0
    clamped = 0

    try:
        while True:
            if args.duration and (time.time() - t0) >= args.duration:
                break
            try:
                data, addr = sock.recvfrom(2048)
            except socket.timeout:
                if count == 0:
                    print("[motion] 还没收到包。检查 Unity 是否在 Play、MotionTelemetry 是否启用、防火墙是否放行 UDP。")
                continue

            pkt = parse(data)
            if pkt is None:
                continue

            now = time.time()
            count += 1

            if last_seq is not None:
                d = (pkt["seq"] - last_seq) & 0xFFFFFFFF
                if d > 1:
                    lost += d - 1
            last_seq = pkt["seq"]

            # 单向延迟估计：本机时钟与 Unity realtimeSinceStartup 不同源，
            # 这里只统计"相邻包到达间隔"，不做绝对延迟（绝对延迟要靠环回时间戳法）
            if t_seq is not None:
                dt = now - t_seq[1]
                if dt > 0:
                    inst = (pkt["seq"] - t_seq[0]) / dt if pkt["seq"] != t_seq[0] else 0
                    if 0 < inst < 500:
                        rate = inst if rate == 0 else rate * 0.9 + inst * 0.1
            t_seq = (pkt["seq"], now)

            if pkt["flags"] & 4:
                clamped += 1

            if writer:
                writer.writerow([
                    f"{now:.6f}", pkt["ts_us"], pkt["seq"], pkt["flags"],
                    f"{pkt['roll']:.4f}", f"{pkt['pitch']:.4f}", f"{pkt['yaw']:.4f}",
                    f"{pkt['roll_rate']:.4f}", f"{pkt['pitch_rate']:.4f}", f"{pkt['yaw_rate']:.4f}",
                    f"{pkt['acc_sway']:.4f}", f"{pkt['acc_heave']:.4f}", f"{pkt['acc_surge']:.4f}",
                    f"{pkt['wave_h']:.4f}",
                ])

            if not args.quiet and (now - last_print) >= 0.2:
                last_print = now
                lat = (now * 1e6 - pkt["ts_us"]) / 1000.0
                if -1000 < lat < 10000:
                    lat_sum += lat
                    lat_n += 1
                sys.stdout.write(
                    f"\r[{count:6d}] {rate:5.1f}Hz 丢{lost:4d} | "
                    f"R{pkt['roll']:+6.2f} P{pkt['pitch']:+6.2f} Y{pkt['yaw']:+6.2f}° | "
                    f"a {pkt['acc_surge']:+5.2f}/{pkt['acc_sway']:+5.2f}/{pkt['acc_heave']:+5.2f} | "
                    f"h{pkt['wave_h'] * 100:+6.1f}cm {flag_text(pkt['flags'])}   "
                )
                sys.stdout.flush()

    except KeyboardInterrupt:
        print("\n[motion] 用户中断")
    finally:
        try:
            sock.close()
        except OSError:
            pass
        if fh:
            fh.close()

    elapsed = max(1e-6, time.time() - t0)
    print("\n" + "=" * 58)
    print(f"  收包      : {count}")
    print(f"  丢包      : {lost}")
    print(f"  平均频率  : {count / elapsed:.1f} Hz")
    print(f"  限位触发  : {clamped} 帧")
    if lat_n:
        print(f"  到达延迟  : 平均 {lat_sum / lat_n:.1f} ms（跨进程近似，仅供参考）")
    print(f"  丢包率    : {100.0 * lost / max(1, count + lost):.3f} %")
    if args.csv:
        print(f"  CSV       : {args.csv}")
    print("=" * 58)
    return 0 if count > 0 else 2


if __name__ == "__main__":
    sys.exit(main())
