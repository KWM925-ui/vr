#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
浮力参数推导器（水线面法）—— 改船体尺寸时用这个重算，别凭感觉调

为什么不用"目标吃水反推刚度"：
    那样做等价于假设船是尖底独木舟（水线面积极小）。12m × 1.2m 的平底龙舟
    水线面有 14.4 m²，物理刚度是 ρg·A = 141 kN/m，比"反推值"大近 10 倍。
    拿反推值跑仿真，船会沉 60cm（实测，见 boat_sim.py）。

用法：
    python physics_check.py                # 用默认 12m 龙舟
    python physics_check.py 14 1.3 1800    # 自定义 长(m) 宽(m) 质量(kg)
"""
import math
import sys

RHO = 1000.0    # kg/m³
G = 9.81
N_POINTS = 6
ZETA = 0.7      # 目标阻尼比


def derive(length, width, mass, n_points=N_POINTS, zeta=ZETA):
    A = length * width
    k_total = RHO * G * A
    k_point = k_total / n_points
    draft = mass / (RHO * A)
    T_heave = 2 * math.pi * math.sqrt(mass / k_total)
    c_total = 2 * zeta * math.sqrt(k_total * mass)
    c_point = c_total / n_points
    # 横摇周期（GM 经验取 0.25×船宽，方箱近似）
    gm = 0.25 * width
    I_roll = mass * (width ** 2 + 1.0 ** 2) / 12.0
    T_roll = 2 * math.pi * math.sqrt(I_roll / (mass * G * gm))
    return dict(A=A, k_total=k_total, k_point=k_point, draft=draft,
                T_heave=T_heave, c_total=c_total, c_point=c_point, T_roll=T_roll)


def main():
    L, W, M = 12.0, 1.2, 1500.0
    if len(sys.argv) >= 4:
        L, W, M = float(sys.argv[1]), float(sys.argv[2]), float(sys.argv[3])

    r = derive(L, W, M)

    print("=" * 60)
    print(f"船体：长 {L:.1f} m × 宽 {W:.1f} m，质量 {M:.0f} kg，{N_POINTS} 点浮力")
    print("=" * 60)
    print(f"水线面面积 A_wp   : {r['A']:.2f} m²")
    print(f"总浮力刚度 k      : {r['k_total']:.0f} N/m   ← ρg·A_wp")
    print(f"静水吃水          : {r['draft']*100:.1f} cm  ← 质量 / (ρ·A_wp)")
    print(f"垂荡固有周期      : {r['T_heave']:.2f} s")
    print(f"横摇固有周期      : {r['T_roll']:.2f} s  （GM 经验取 0.25×船宽）")
    print("-" * 60)
    print("直接抄进 Inspector / 代码的值：")
    print(f"  BuoyancyPoint.stiffness = {r['k_point']:.0f}f;   // 每点刚度")
    print(f"  BuoyancyPoint.damping   = {r['c_point']:.0f}f;   // 每点阻尼（ζ={ZETA}）")
    print(f"  Rigidbody.mass          = {M:.0f}f;")
    print(f"  船体初始 y              = {0.5 - r['draft']:.3f}f;  // 半高 0.5 − 吃水")
    print("-" * 60)
    print("提醒：采样点必须贴在船底（局部 y = −半高），否则吃水会被算多一个半高。")
    print("参数定完跑一遍 python boat_sim.py 验证不发散。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
