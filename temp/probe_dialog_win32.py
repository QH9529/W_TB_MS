#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探针：UIA Invoke 打开模态对话框后，改用 **win32 后端**读取对话框内容。

为什么：UIA 的 InvokePattern.Invoke 是同步跨进程调用，应用 UI 线程进 ShowDialog 后
不返回，UIA provider 被占住 —— 之后对该进程的**任何 UIA 查询**（解析对话框、扫主窗体）
全部超时。这是桌面可用与否都存在的死结（真实点击兜底还要求桌面解锁，实测不可靠）。

但 Win32 通道不受影响：对话框的嵌套消息循环照常 pump 消息，所以
EnumWindows / EnumChildWindows / GetWindowText / PostMessage 都能正常工作
（已实测 PostMessage(WM_CLOSE) 能把这颗"卡住"的对话框关掉）。

用法：
    W_TB_MS.exe 已启动（本脚本不连串口，只验证弹窗）
    python temp/probe_dialog_win32.py --row 270
"""

from __future__ import annotations

import argparse
import os
import sys
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from pywinauto import Application, Desktop  # noqa: E402

from ui_write_dialog_shot import scan_cells, set_topmost  # noqa: E402
from ui_writable_verify import top_windows_of  # noqa: E402

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

TITLE_RE = r"W_TB_MS v\d+\.\d+\.\d+"


def log(msg: str) -> None:
    print(msg, flush=True)


def invoke_async(element) -> None:
    """UIA Invoke 会一直阻塞到模态对话框关闭，必须丢后台线程。"""
    def run():
        try:
            element.invoke()
        except Exception as exc:  # noqa: BLE001
            log(f"  [invoke 线程] {type(exc).__name__}: {exc}")
    threading.Thread(target=run, daemon=True).start()


def dump_win32(dialog) -> list:
    """用 win32 后端枚举对话框子控件，返回 [(类名, 文本)]。"""
    out = []
    try:
        for child in dialog.descendants():
            try:
                cls = child.class_name()
            except Exception:
                cls = "?"
            try:
                text = (child.window_text() or "").strip()
            except Exception:
                text = ""
            out.append((cls, text))
    except Exception as exc:
        log(f"  win32 descendants 失败: {type(exc).__name__}: {exc}")
    return out


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--row", type=int, default=270)
    parser.add_argument("--keyword", default="写入参数")
    args = parser.parse_args()

    app = Application(backend="uia").connect(title_re=TITLE_RE, timeout=25)
    window = app.window(title_re=TITLE_RE)
    pid = app.process
    if window.is_minimized():
        window.restore()
    set_topmost(window, True)
    log(f"主窗口: {window.window_text()!r} pid={pid}")

    cells = scan_cells(window)
    action = cells.get(("操作", args.row))
    if action is None:
        log(f"[FAIL] 第 {args.row} 行无「操作」按钮")
        return 1
    log(f"[OK] 找到「操作 行 {args.row}」")

    log(">>> UIA Invoke（后台线程）")
    invoke_async(action)

    hwnd = None
    deadline = time.time() + 10.0
    while time.time() < deadline:
        for h, p, title in top_windows_of(pid):
            if args.keyword in title:
                hwnd, dlg_title = h, title
                break
        if hwnd:
            break
        time.sleep(0.3)
    log(f"  顶层窗口: {[t for _h, _p, t in top_windows_of(pid)]}")
    if not hwnd:
        log("[FAIL] 对话框没弹出")
        return 1
    log(f"[OK] 对话框 hwnd={hwnd} title={dlg_title!r}")

    # ---------- 核心验证：win32 后端能不能读到内容 ----------
    log(">>> win32 后端解析对话框")
    t0 = time.time()
    try:
        dialog = Desktop(backend="win32").window(handle=hwnd)
        dialog.wrapper_object()
        log(f"[OK] win32 解析成功（{time.time() - t0:.2f}s）")
    except Exception as exc:
        log(f"[FAIL] win32 解析失败: {type(exc).__name__}: {exc}")
        return 1

    children = dump_win32(dialog)
    log(f"  子控件 {len(children)} 个:")
    for cls, text in children:
        if text:
            log(f"    [{cls}] {text}")

    joined = " ".join(t for _c, t in children)
    for expected in ("未激活", "允许使用", "禁止使用", "0～2"):
        log(f"  {'[OK]' if expected in joined else '[MISS]'} 含 {expected!r}")

    # ---------- 对照：同一时刻 UIA 还能不能用 ----------
    log(">>> 对照实验：同一时刻 UIA 后端")
    try:
        Desktop(backend="uia").window(handle=hwnd).wrapper_object()
        log("[OK] UIA 居然可用")
    except Exception as exc:
        log(f"[EXPECTED-FAIL] UIA 不可用: {type(exc).__name__}")

    # ---------- 关闭：win32 通道 PostMessage ----------
    log(">>> PostMessage(WM_CLOSE) 关闭对话框")
    import ctypes
    ok = ctypes.windll.user32.PostMessageW(hwnd, 0x0010, 0, 0)
    time.sleep(1.5)
    log(f"  PostMessage -> {ok}")
    log(f"  收尾后顶层窗口: {[t for _h, _p, t in top_windows_of(pid)]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
