# -*- coding: utf-8 -*-
"""三绳锚点布置搜索：找出能最大程度还原水阻的三点几何。

绳子只能受拉（T>=0），三根绳的可合力范围是一个"锥"。若水阻方向落在锥外，
无论怎么分配张力都还原不出这个力 —— 这是物理约束，不是算法问题。
这里穷举几组候选布置，算出各自对"典型水阻方向"的还原率。

纯 Python 实现（3x3 正规方程 + 高斯消元），不依赖 numpy。
"""

V = lambda a, b: (a[0] - b[0], a[1] - b[1], a[2] - b[2])
DOT = lambda a, b: a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
NORM = lambda a: DOT(a, a) ** 0.5


def solve3(M, b, n):
    """高斯-约当解 n×n 方程组，奇异返回 None"""
    a = [list(M[i][:n]) + [b[i]] for i in range(n)]
    for col in range(n):
        piv = max(range(col, n), key=lambda r: abs(a[r][col]))
        if abs(a[piv][col]) < 1e-9:
            return None
        a[col], a[piv] = a[piv], a[col]
        d = a[col][col]
        a[col] = [v / d for v in a[col]]
        for r in range(n):
            if r == col:
                continue
            f = a[r][col]
            if abs(f) < 1e-12:
                continue
            a[r] = [a[r][j] - f * a[col][j] for j in range(n + 1)]
    return [a[i][n] for i in range(n)]


def restore_ratio(dirs, F):
    """三绳非负最小二乘：返回 1 - 相对残差（1.0 表示完美还原）"""
    fnorm = NORM(F)
    if fnorm < 1e-9:
        return 1.0
    best = 1e9
    for mask in range(1, (1 << len(dirs))):
        idx = [i for i in range(len(dirs)) if (mask >> i) & 1]
        n = len(idx)
        M = [[DOT(dirs[idx[i]], dirs[idx[j]]) for j in range(n)] for i in range(n)]
        b = [DOT(dirs[idx[i]], F) for i in range(n)]
        t = solve3(M, b, n)
        if t is None or any(v < -1e-6 for v in t):
            continue
        s = [0.0, 0.0, 0.0]
        for k, i in enumerate(idx):
            for d in range(3):
                s[d] += dirs[i][d] * max(0.0, t[k])
        res = NORM((s[0] - F[0], s[1] - F[1], s[2] - F[2])) / fnorm
        best = min(best, res)
    if best > 1e8:          # 一个可行组合都没有：这个方向三根绳帮不上忙
        return 0.0
    return 1.0 - best


def evaluate(anchors, blade_pts, forces):
    worst, total, n = 1e9, 0.0, 0
    for bp in blade_pts:
        dirs = []
        for a in anchors:
            d = V(a, bp)
            L = NORM(d)
            if L < 1e-6:
                return 0.0, 0.0
            dirs.append((d[0] / L, d[1] / L, d[2] / L))
        for F in forces:
            r = restore_ratio(dirs, F)
            worst = min(worst, r)
            total += r
            n += 1
    return worst, total / n


BLADE = [(1.55, y, z) for y in (-0.55, -0.2, 0.15, 0.45) for z in (-0.25, 0.2, 0.65)]

# 只评估"物理上真会出现"的水阻方向。
# 桨叶沿船身前后划（z），加上入水/出水的上下运动（y），
# 侧向（x）几乎不动 —— 桨叶横向位置是固定在船舷外的。
# 若把 ±x 也纳入评估，等于要求"三根装在船内侧的绳子把桨往外推"，
# 那是绳子做不到的事，会把所有布置的评分都拉成负值，评估就失去意义。
RAW = [(0, 0, 1), (0, 0, -1), (0, 1, 0), (0, -1, 0),
       (0, 0.7, 0.7), (0, 0.7, -0.7), (0, -0.7, 0.7), (0, -0.7, -0.7)]
FORCES = []
for F in RAW:
    L = NORM(F)
    FORCES.append((F[0] / L, F[1] / L, F[2] / L))

CANDS = {
    "A 前上/后上/内下 y=0.50": [(1.40, 0.50, 1.60), (1.40, 0.50, -1.20), (0.60, -1.40, 0.20)],
    "B 前中/后中/内下 y=0.15": [(1.40, 0.15, 1.60), (1.40, 0.15, -1.20), (0.60, -1.40, 0.20)],
    "C 前中/后中/内下 y=0.00": [(1.40, 0.00, 1.60), (1.40, 0.00, -1.20), (0.60, -1.40, 0.20)],
    "D 前平/后平/内下 y=-0.15": [(1.40, -0.15, 1.60), (1.40, -0.15, -1.20), (0.60, -1.40, 0.20)],
    "E 更远更平": [(1.70, 0.00, 2.20), (1.70, 0.00, -1.80), (0.55, -1.55, 0.20)],
    "F 三点立体": [(1.50, 0.35, 1.80), (1.35, -0.35, -1.40), (0.55, -1.45, 0.15)],
    "G 前后下三角": [(1.35, -0.30, 1.70), (1.35, -0.30, -1.30), (1.05, 0.60, 0.20)],
    "H 前后平+外侧": [(1.40, 0.10, 1.70), (1.40, 0.10, -1.30), (2.30, -0.20, 0.20)],
    "I 前后平+外下": [(1.40, 0.10, 1.70), (1.40, 0.10, -1.30), (2.30, -1.20, 0.20)],
    # 非对称三角：三根绳的 (x,y) 比例互不相同，避免"两根绳只差 z 符号"的退化。
    # 对称布置看起来规整，但如果两点的 x、y 相同、只有 z 相反，
    # 它们组合起来只能沿固定方向拉，z 分量互相抵消，等于少了一根绳。
    "J 非对称三角": [(1.70, 0.60, 1.60), (0.90, -0.80, -1.00), (2.20, 0.20, 0.40)],
    "K 非对称三角 大": [(1.90, 0.70, 1.90), (0.85, -1.00, -1.30), (2.40, 0.30, 0.30)],
    "L 前后不对称": [(1.80, 0.75, 1.70), (1.10, -0.75, -1.40), (0.70, -0.30, 0.20)],
    "M 三点包围外": [(1.80, 0.60, 1.70), (1.60, -0.90, -1.30), (2.35, 0.10, 0.30)],
}

print("候选布置还原率（1.000 = 三绳能完美还原任意方向水阻）")
print("-" * 64)
best_name, best_score = None, -1
for name, anchors in CANDS.items():
    worst, avg = evaluate(anchors, BLADE, FORCES)
    print(f"{name:30s} 最差={worst:.3f}  平均={avg:.3f}")
    if avg > best_score:
        best_score, best_name = avg, name

print("-" * 64)
print(f"最佳：{best_name}  平均还原率={best_score:.3f}")
print(f"建议锚点：{CANDS[best_name]}")
