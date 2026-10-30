#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
C# 交叉引用静态检查（不依赖 Unity，不需要许可证）

做的事：
1. 扫描 Assets 下所有 .cs，按大括号深度正确归属类的 public 字段/属性/方法/嵌套类型
2. 在 SetupWizard.cs 里解析变量的实际类型（AddComponent<T>() 等）
3. 检查 `变量.成员 = ...` / `变量.成员(...)` 的成员是否真实存在
4. 检查 new X.Y { field = ... } 的字段名是否存在
5. 检查 Enum.Member 引用是否合法（只对真正的 enum）

这类"名字对不上"是 Unity 里最常见的编译错误，靠这个能提前抓出来。
"""
import io
import os
import re
import sys

ROOT = os.environ.get("XREF_ROOT") or os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "DragonBoatVR", "Assets")

INHERITED = {
    "transform", "gameObject", "name", "tag", "enabled", "hideFlags",
    "GetComponent", "AddComponent", "StartCoroutine", "StopCoroutine",
    "Invoke", "CancelInvoke", "SendMessage", "BroadcastMessage",
    "isActiveAndEnabled", "useGUILayout", "runInEditMode",
    "print", "Equals", "GetHashCode", "ToString", "GetType", "CompareTo",
}

BUILTIN = {
    "GameObject", "Camera", "Light", "Rigidbody", "MeshRenderer", "Material",
    "Shader", "AudioListener", "Transform", "Vector3", "Vector4", "Vector2",
    "Color", "Quaternion", "Mathf", "Debug", "AssetDatabase", "EditorUtility",
    "PlayerSettings", "GraphicsSettings", "QualitySettings", "EditorSceneManager",
    "Object", "ScriptableObject", "System",
}

CLASS_RE = re.compile(r"\b(?:public|internal|private|protected)\s+(?:static\s+)?(?:sealed\s+)?(?:abstract\s+)?(?:partial\s+)?(?:class|struct)\s+(\w+)")
ENUM_DECL_RE = re.compile(r"\b(?:public|internal)\s+enum\s+(\w+)\s*\{([^}]*)\}")
FIELD_RE = re.compile(r"^\s*public\s+(?!class\b|struct\b|enum\b)([\w\.<>\[\],\s]+?)\s+(\w+)\s*(?:=|;)")
PROP_RE = re.compile(r"^\s*public\s+([\w\.<>\[\],\s]+?)\s+(\w+)\s*\{\s*get")
METHOD_RE = re.compile(r"^\s*public\s+(?:static\s+)?(?:virtual\s+|override\s+)?([\w\.<>\[\],\s]+?)\s+(\w+)\s*\(")


def read(p):
    return io.open(p, encoding="utf-8").read()


def parse_members(text):
    """按大括号深度正确归属成员到类。返回 (classes, enums)"""
    classes = {}
    enums = {}
    stack = []          # [[name, entry_depth, entered]]
    depth = 0

    for line in text.splitlines():
        em = ENUM_DECL_RE.search(line)
        if em:
            enums[em.group(1)] = {
                x.split("=")[0].strip()
                for x in em.group(2).split(",") if x.strip()
            }

        cm = CLASS_RE.search(line)
        if cm:
            name = cm.group(1)
            classes.setdefault(name, set())
            # 记录进入时的深度；C# 常把 "{" 放下一行，故不能在此判断是否已闭合
            stack.append([name, depth, False])
        elif stack:
            cur = stack[-1][0]
            for rx in (FIELD_RE, PROP_RE, METHOD_RE):
                mm = rx.match(line)
                if mm:
                    classes[cur].add(mm.group(2))
                    break

        depth += line.count("{") - line.count("}")
        # 只有"曾经进入过类体"(depth 超过 entry_depth) 后又回落到 entry_depth，才算离开
        for item in stack:
            if depth > item[1]:
                item[2] = True
        while stack and stack[-1][2] and depth <= stack[-1][1]:
            stack.pop()

    return classes, enums


def collect():
    classes, enums, files = {}, {}, []
    for dirpath, _, names in os.walk(ROOT):
        for n in names:
            if n.endswith(".cs"):
                files.append(os.path.join(dirpath, n))
    for f in files:
        c, e = parse_members(read(f))
        for k, v in c.items():
            classes.setdefault(k, set()).update(v)
        enums.update(e)
    return classes, enums, files


def resolve_types(src):
    var2type = {}
    for m in re.finditer(r"var\s+(\w+)\s*=\s*\w+\.(?:AddComponent|GetComponent)<(\w+)>\(\)", src):
        var2type[m.group(1)] = m.group(2)
    for m in re.finditer(r"var\s+(\w+)\s*=\s*(?:GameObject\.CreatePrimitive|new\s+GameObject)", src):
        var2type[m.group(1)] = "GameObject"
    return var2type


def main():
    classes, enums, files = collect()
    print(f"扫描 {len(files)} 个 .cs | 类型 {len(classes)} 个 | 枚举 {len(enums)} 个")
    print("类型清单：" + ", ".join(sorted(classes)))
    print("-" * 68)

    problems = []

    # 1) SetupWizard 跨组件成员检查
    wiz = os.path.join(ROOT, "Editor", "SetupWizard.cs")
    src = read(wiz)
    var2type = resolve_types(src)
    print("变量 → 类型：")
    for k, v in sorted(var2type.items()):
        if v not in BUILTIN:
            print(f"  {k:16s} : {v}")

    for m in re.finditer(r"\b(\w+)\.(\w+)\s*(?:=|\()", src):
        var, member = m.group(1), m.group(2)
        t = var2type.get(var)
        if t is None or t in BUILTIN or t not in classes:
            continue
        if member in INHERITED or member in classes[t]:
            continue
        line_no = src[:m.start()].count("\n") + 1
        problems.append(f"SetupWizard.cs:{line_no}  {var}({t}).{member} —— {t} 无此成员")

    # 2) 嵌套类型对象初始化字段检查
    for f in files:
        s = read(f)
        for m in re.finditer(r"new\s+(\w+)\.(\w+)\s*\{([^}]*)\}", s, re.S):
            outer, inner, body = m.group(1), m.group(2), m.group(3)
            if inner not in classes:
                continue
            body = re.sub(r"//[^\n]*", "", body)          # 去掉行注释，避免把 // ζ=0.7 当字段
            for fm in re.finditer(r"(\w+)\s*=(?!=)", body):
                field = fm.group(1)
                if field not in classes[inner] and field not in INHERITED:
                    line_no = s[:m.start()].count("\n") + 1
                    problems.append(
                        f"{os.path.basename(f)}:{line_no}  new {outer}.{inner}{{...}} 字段 {field} 不存在")

    # 3) 枚举引用检查（只查真正的 enum）
    for f in files:
        s = read(f)
        for m in re.finditer(r"\b(\w+)\.(\w+)\.(\w+)\b", s):
            outer, enum_t, member = m.group(1), m.group(2), m.group(3)
            if enum_t in enums and member not in enums[enum_t]:
                line_no = s[:m.start()].count("\n") + 1
                problems.append(
                    f"{os.path.basename(f)}:{line_no}  枚举 {enum_t}.{member} 不存在（可用: {sorted(enums[enum_t])}）")

    print("-" * 68)
    if problems:
        print(f"发现 {len(problems)} 个可疑点：")
        for p in problems:
            print("  [!]", p)
        return 1
    print("通过：未发现成员/字段/枚举引用错误。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
