# -*- coding: utf-8 -*-
"""
把当前 WorkBuddy 对话完整导出到 F:\\vr视觉\\资料汇总：
1. 对话完整原文（可读 md + 原始 jsonl）
2. 对话中上传的所有图片（blob 还原，保留原始文件名）
3. 对话中引用的所有本地文件（PDF/MD 等，复制）

注：导出的 md 不写时间戳（老大要求）；时间戳只保留在原始 jsonl 里。
"""
import json, os, re, shutil, sys

SRC = r"C:\Users\wysxgd\.workbuddy\projects\c-Users-wysxgd-WorkBuddy-2026-10-20-00-33-11\f7109956-e82b-4c75-8583-4b11935d8cd6.jsonl"
OUT = r"F:\vr视觉\资料汇总"
DIR_MD = os.path.join(OUT, "对话完整导出")
DIR_IMG = os.path.join(OUT, "对话上传的图片")
DIR_FILE = os.path.join(OUT, "对话上传的文件")
for d in (DIR_MD, DIR_IMG, DIR_FILE):
    os.makedirs(d, exist_ok=True)

# ---------- 1. 收集图片 blob 与用户消息里的文件路径 ----------
images = {}          # blob_path -> original_filename
ref_files = []       # 用户消息里引用的本地文件路径
PATH_RE = re.compile(r'[A-Za-z]:\\[^"\'<>\n]*?\.(?:pdf|md|docx|xlsx|xls|png|jpg|jpeg|txt|py|zip)')

with open(SRC, encoding="utf-8") as f:
    lines = f.readlines()

def walk_content(content):
    """递归遍历消息 content，收集 image_blob_ref 与文本"""
    texts = []
    if isinstance(content, str):
        texts.append(content)
    elif isinstance(content, list):
        for item in content:
            if not isinstance(item, dict):
                continue
            t = item.get("type")
            if t == "image_blob_ref":
                bp = item.get("blob_path")
                if bp and os.path.exists(bp):
                    images[bp] = item.get("original_filename") or os.path.basename(bp)
            elif t in ("input_text", "text", "output_text"):
                texts.append(item.get("text", ""))
            elif "content" in item:
                texts.extend(walk_content(item["content"]))
    return texts

# 只收真正上传的文件：排除 .workbuddy 内部路径，且目录深度 ≤2（F:\vr视觉\x.pdf / D:\ros2\龙舟\x.md）
def is_upload(p):
    if p.startswith(r"C:\Users\wysxgd\.workbuddy") or "\\.workbuddy\\" in p:
        return False
    parts = p.replace("/", "\\").split("\\")
    return len(parts) <= 4 and parts[0].endswith(":")

for line in lines:
    try:
        o = json.loads(line)
    except Exception:
        continue
    if o.get("type") != "message":
        continue
    texts = walk_content(o.get("content"))
    if o.get("role") == "user":
        for tx in texts:
            for m in PATH_RE.finditer(tx):
                p = m.group(0).strip().rstrip("\\")
                if is_upload(p) and os.path.exists(p):
                    ref_files.append(p)

# ---------- 2. 复制图片 ----------
print("=== 复制图片 ===")
seen = set()
for bp, name in images.items():
    dst = os.path.join(DIR_IMG, name)
    if name in seen:
        base, ext = os.path.splitext(name)
        k = 2
        while os.path.join(DIR_IMG, f"{base}_{k}{ext}") in {os.path.join(DIR_IMG, x) for x in os.listdir(DIR_IMG)}:
            k += 1
        dst = os.path.join(DIR_IMG, f"{base}_{k}{ext}")
    shutil.copy2(bp, dst)
    seen.add(name)
    print("  图片:", name)
print(f"共 {len(seen)} 张")

# ---------- 3. 复制引用的文件 ----------
print("=== 复制文件 ===")
for p in dict.fromkeys(ref_files):
    dst = os.path.join(DIR_FILE, os.path.basename(p))
    if not os.path.exists(dst):
        shutil.copy2(p, dst)
    print("  文件:", os.path.basename(p))

# ---------- 4. 导出可读全文（完整版 + 精简版，均不带时间戳） ----------
print("=== 导出对话全文 ===")
out_md = os.path.join(DIR_MD, "当前对话_完整记录_2026-10-20.md")
out_slim = os.path.join(DIR_MD, "当前对话_完整记录_精简版_2026-10-20.md")
n_user = n_ai = n_tool = 0

def fmt_args(a, limit=600):
    s = a if isinstance(a, str) else json.dumps(a, ensure_ascii=False)
    return s if len(s) <= limit else s[:limit] + f" …[截断,原长{len(s)}]"

def clean_user_text(tx):
    """剥掉聊天窗口里看不到的注入内容，只留用户实际打的内容"""
    import re as _re
    # 系统注入块（身份文件/记忆/技能提醒/环境信息等）——UI 不可见
    tx = _re.sub(r"<system-reminder[^>]*>.*?</system-reminder>", "", tx, flags=_re.S)
    # 上下文摘要注入块（非用户输入，原文已在本文件中）
    tx = _re.sub(r"<cb_summary>.*?</cb_summary>", "", tx, flags=_re.S)
    # 后台任务通知 → 一行标记
    def _tn(m):
        tid = _re.search(r"<task-id>(.*?)</task-id>", m.group(0))
        return f"[系统通知：后台任务 {tid.group(1) if tid else '?'} 完成]"
    tx = _re.sub(r"<task-notification>.*?</task-notification>", _tn, tx, flags=_re.S)
    # 图片本地路径 → 计数后移除
    n_img_path = len(_re.findall(r"<image_local_path>", tx))
    tx = _re.sub(r"<image_local_path>.*?</image_local_path>", "", tx, flags=_re.S)
    # user_query 包裹
    tx = _re.sub(r"</?user_query>", "", tx)
    # 压缩多余空行
    tx = _re.sub(r"\n{3,}", "\n\n", tx).strip()
    return tx, n_img_path


with open(out_md, "w", encoding="utf-8") as w, open(out_slim, "w", encoding="utf-8") as ws:
    HEAD = ("# 龙舟 VR 项目对话完整记录（2026-10-20，Codex 接手用）\n\n"
            "> 本文件由工具从 WorkBuddy 会话记录自动导出，保留全部用户消息、助手消息、"
            "工具调用与返回。图片以 [图片: 文件名] 标注，实际文件在 `../对话上传的图片/`。\n\n---\n\n")
    w.write(HEAD)
    ws.write("# 龙舟 VR 项目对话记录 · 精简版（2026-10-20，Codex 接手用）\n\n"
             "> 只保留聊天窗口可见内容：用户消息 + 助手回复正文。无思考过程、无工具调用。\n"
             "> 图片以 [图片: 文件名] 标注，文件在 `../对话上传的图片/`。\n\n---\n\n")
    for line in lines:
        try:
            o = json.loads(line)
        except Exception:
            continue
        t = o.get("type")
        if t == "message":
            role = o.get("role")
            texts = walk_content(o.get("content"))
            body = "\n".join(x for x in texts if x.strip())
            if not body.strip():
                continue
            if role == "user":
                n_user += 1
                w.write(f"\n## 【用户】\n\n{body}\n")
                # 精简版：先剥掉系统注入，再标注图片
                slim_body, n_path = clean_user_text(body)
                if not slim_body.strip():
                    continue
                n_img = max(len(re.findall(r"@image#\d+", slim_body)), n_path)
                if n_img:
                    slim_body += f"\n\n（本消息含 {n_img} 张图片，见 对话上传的图片 目录）"
                ws.write(f"\n## 【用户】\n\n{slim_body}\n")
            else:
                n_ai += 1
                w.write(f"\n## 【助手】\n\n{body}\n")
                ws.write(f"\n## 【助手】\n\n{body}\n")
        elif t == "function_call":
            n_tool += 1
            w.write(f"\n<details><summary>工具调用 {n_tool}｜{o.get('name')}</summary>\n\n```\n{fmt_args(o.get('arguments'))}\n```\n</details>\n")
        elif t == "function_call_result":
            s = json.dumps(o.get("output"), ensure_ascii=False)
            if len(s) > 900:
                s = s[:900] + f" …[截断,原长{len(s)}]"
            w.write(f"\n<details><summary>工具返回｜{o.get('name')}</summary>\n\n```\n{s}\n```\n</details>\n")

    w.write(f"\n\n---\n统计：用户消息 {n_user} 条，助手消息 {n_ai} 条，工具调用 {n_tool} 次。\n")
    ws.write(f"\n\n---\n统计：用户消息 {n_user} 条，助手消息 {n_ai} 条。\n")
print(f"  用户 {n_user} / 助手 {n_ai} / 工具 {n_tool}")

# ---------- 5. 复制原始 jsonl ----------
shutil.copy2(SRC, os.path.join(DIR_MD, "当前对话_原始记录_2026-10-20.jsonl"))
print("原始 jsonl 已复制")
print("完成")
