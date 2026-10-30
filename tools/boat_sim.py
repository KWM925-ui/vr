#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
龙舟六点浮力 + 波浪时域仿真（不依赖 Unity）

目的：在没有 Unity 许可证的情况下，用纯数值积分回答四个问题
  1. 2500 N/m × 6 点 + 283 N·s/m 阻尼，在 50Hz 定步长下会不会数值爆炸？
  2. 稳态吃水是不是设计值 ~0.20 m？
  3. 波浪激励下的垂荡/横摇/纵摇幅值是否在"人不晕"的范围内？
  4. maxForcePerPoint = 60000 N 这个上限够不够（有没有削顶）？

模型（与 BoatBuoyancy.cs 逐项对应）：
  · 刚体 3 自由度：垂荡 y、横摇 roll(绕 z)、纵摇 pitch(绕 x)
  · 6 个采样点，本地坐标 (±0.45, 0, ±5.5)（世界尺度，对应船体 1.2×1×12 m）
  · 每点浮力 = min(depth × stiffness, maxForcePerPoint) − vy_point × damping，下限 0
  · 波浪取 WaveField 默认三波，与 shader / CPU 侧同一公式
  · 半隐式欧拉，dt = 0.02（Unity FixedUpdate 默认 50Hz）
"""
import math
import sys

# ---------------- 船体 ----------------
MASS = 1500.0           # kg（12m 龙舟空船 + 划手总重）
LEN, WID, HGT = 12.0, 1.2, 1.0
I_ROLL = MASS * (WID ** 2 + HGT ** 2) / 12.0     # 绕 z
I_PITCH = MASS * (LEN ** 2 + HGT ** 2) / 12.0    # 绕 x
G = 9.81

# 采样点必须贴在船底（局部 y = -HGT/2）。放船体中心会把"点在水下的深度"
# 误当成吃水，实测会让船沉 60cm 而不是 10cm。
POINTS = [(0.45, -HGT / 2, 5.5), (-0.45, -HGT / 2, 5.5),
          (0.45, -HGT / 2, 0.0), (-0.45, -HGT / 2, 0.0),
          (0.45, -HGT / 2, -5.5), (-0.45, -HGT / 2, -5.5)]

STIFFNESS = 23544.0     # 每点 N/m（= ρg·A_wp/6，见 compute：141264/6）
DAMPING = 3397.0        # 每点 N·s/m（总阻尼 ζ=0.7 均分六点）
MAX_FORCE = 60000.0     # 每点上限
ANGULAR_DAMPING = 1.2   # 与 BoatBuoyancy.angularDamping 一致

# ---------------- 波浪（WaveField 默认三波）----------------
WAVES = [
    # (dirX, dirZ, wavelength, amplitude)
    (1.0, 0.2, 12.0, 0.18),
    (0.7, 1.0, 6.5, 0.10),
    (-0.4, 1.0, 3.2, 0.05),
]
WAVE_SCALE = 0.35       # 演示用缩放（默认参数振幅合计 0.33m，对 300kg 船偏猛，缩放后接近内河缓浪）


def wave_height(x, z, t):
    h = 0.0
    for dx, dz, wl, amp in WAVES:
        n = math.hypot(dx, dz)
        dx, dz = dx / n, dz / n
        k = 2 * math.pi / wl
        c = math.sqrt(G / k)
        h += amp * math.sin(k * (dx * x + dz * z) - k * c * t)
    return h * WAVE_SCALE


def simulate(seconds=40.0, dt=0.02):
    y, roll, pitch = 0.396, 0.0, 0.0   # 0.5 − 吃水 0.104
    vy, w_roll, w_pitch = 0.0, 0.0, 0.0
    t = 0.0
    steps = int(seconds / dt)

    peak_force = 0.0
    clipped = 0
    samples = []

    for _ in range(steps):
        fy = 0.0
        mx = 0.0   # 绕 x（pitch）
        mz = 0.0   # 绕 z（roll）

        cr, sr = math.cos(roll), math.sin(roll)
        cp, sp = math.cos(pitch), math.sin(pitch)

        for px, py, pz in POINTS:
            # 先绕 x 轴 pitch，再绕 z 轴 roll
            y1 = py * cp - pz * sp
            z1 = py * sp + pz * cp
            x2 = px * cr - y1 * sr
            y2 = px * sr + y1 * cr
            z2 = z1

            wy = y + y2          # 世界 y
            wx = x2
            wz = z2

            water = wave_height(wx, wz, t)
            depth = water - wy
            if depth <= 0.0:
                continue

            f = depth * STIFFNESS
            if f > MAX_FORCE:
                f = MAX_FORCE
                clipped += 1

            # 点的垂直速度（刚体运动学：v = v_cm + ω × r）
            # ω = (w_pitch, 0, w_roll)，r = (x2, y2, z2)
            # (ω × r).y = w_roll*x2 - w_pitch*z2
            v_point = vy + w_roll * x2 - w_pitch * z2
            f -= v_point * DAMPING
            if f < 0.0:
                f = 0.0

            peak_force = max(peak_force, f)

            fy += f
            mx += -z2 * f
            mz += x2 * f

        fy -= MASS * G

        ay = fy / MASS
        a_roll = mz / I_ROLL
        a_pitch = mx / I_PITCH

        # 半隐式欧拉（Unity Rigidbody 同类做法）
        vy += ay * dt
        w_roll += a_roll * dt
        w_pitch += a_pitch * dt

        # 与 BoatBuoyancy.FixedUpdate 末尾的角阻尼一致
        w_roll /= (1.0 + ANGULAR_DAMPING * dt)
        w_pitch /= (1.0 + ANGULAR_DAMPING * dt)

        y += vy * dt
        roll += w_roll * dt
        pitch += w_pitch * dt

        t += dt
        if not (math.isfinite(y) and math.isfinite(roll) and math.isfinite(pitch)):
            print(f"[FAIL] t={t:.2f}s 数值发散（NaN/Inf）")
            return None
        if t > seconds * 0.5:      # 后半程才采样，避开启动瞬态
            samples.append((y, math.degrees(roll), math.degrees(pitch)))

    if not samples:
        return None
    ys = [s[0] for s in samples]
    rolls = [s[1] for s in samples]
    pitches = [s[2] for s in samples]

    def rng(v):
        return min(v), max(v)

    return {
        "draft": -(sum(ys) / len(ys)) + HGT / 2,
        "heave": rng(ys),
        "roll": rng(rolls),
        "pitch": rng(pitches),
        "peak_force": peak_force,
        "clipped": clipped,
        "steps": steps,
    }


def main():
    print("=" * 62)
    print("龙舟六点浮力 · 时域仿真（dt=0.02s，50Hz，与 Unity FixedUpdate 一致）")
    print("=" * 62)
    r = simulate()
    if r is None:
        return 1

    print(f"积分步数        : {r['steps']}（40 s，未发散）")
    expected = MASS / (1000.0 * LEN * WID)
    print(f"平均吃水        : {r['draft']*100:.1f} cm  （理论 {expected*100:.1f} cm = 质量 / 水线面）")
    print(f"垂荡 y 范围     : {r['heave'][0]:+.3f} ~ {r['heave'][1]:+.3f} m")
    print(f"横摇 roll 范围  : {r['roll'][0]:+.2f} ~ {r['roll'][1]:+.2f} °")
    print(f"纵摇 pitch 范围 : {r['pitch'][0]:+.2f} ~ {r['pitch'][1]:+.2f} °")
    print(f"单点峰值浮力    : {r['peak_force']:.0f} N（上限 {MAX_FORCE:.0f}）")
    print(f"触顶次数        : {r['clipped']}")

    print("-" * 62)
    ok = True
    expected = MASS / (1000.0 * LEN * WID)
    if abs(r["draft"] - expected) > 0.02:
        print(f"[!] 吃水 {r['draft']*100:.1f}cm 偏离理论值 {expected*100:.1f}cm 超过 2cm"); ok = False
    if r["roll"][1] - r["roll"][0] > 12.0:
        print(f"[!] 横摇幅值 {r['roll'][1]-r['roll'][0]:.1f}° 偏大，晕动风险"); ok = False
    if r["pitch"][1] - r["pitch"][0] > 10.0:
        print(f"[!] 纵摇幅值 {r['pitch'][1]-r['pitch'][0]:.1f}° 偏大，晕动风险"); ok = False
    if r["clipped"] > 0:
        print(f"[!] 浮力触顶 {r['clipped']} 次，峰值被削，建议提高 maxForcePerPoint"); ok = False
    if ok:
        print("[OK] 参数稳定：无发散、吃水达标、姿态幅值在舒适区、浮力未削顶")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
