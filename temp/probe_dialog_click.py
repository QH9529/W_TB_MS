#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探针：验证「真实点击打开模态对话框 → 再用 UIA 读对话框」这条路是否成立。

背景（踩过的坑）：
  UIA 的 InvokePattern.Invoke 是**同步**跨进程调用，应用 UI 线程在 ShowDialog 里
  不返回，UIA provider 被这次调用占住 —— 之后任何对该进程的 UIA 查询（包括解析
  刚弹出的对话框、扫描主窗体）都会超时。日志里表现为
  「顶层窗口: [... '写入参数']」明明看得见窗口，wait_dialog 却解析不出来。

思路：改用真实鼠标点击（click_input / mouse.click），点击只是把消息投递出去、
不占 UIA provider；对话框弹出后 UIA 就能正常工作。

用法：
    python temp/sim_odu.py --port COM14 --fans 1,2,5 --manifolds 1,3 --quiet
    W_TB_MS.exe 已启动
    python temp/probe_dialog_click.py --port COM13 --row 270
"""

from __future__ import annotations

import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from pywinauto import Application, Desktop, mouse  # noqa: E402

from ui_write_dialog_shot import (  # noqa: E402
    MAIN_TITLE, scan_cells, set_topmost, visible_rows,
)
from ui_writable_verify import top_windows_of, close_stray_dialogs  # noqa: E402

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass


def log(msg: str) -> None:
    print(msg, flush=True)


def scroll_row_into_view(window, row: int) -> str:
    """把指定行滚进视口，返回用掉的手段名。

    DataGridView 的行在 UIA 里是虚拟化的：不在视口内的行元素存在、但矩形是
    (0,0,0,0)，click_input 会点到 (0,0) 上。这里有三级手段，从最"正规"往下退。
    """
    # (a) 行元素自己的 ScrollItemPattern —— DataGridView 会把它滚进视口
    try:
        item = None
        for it in window.descendants(control_type="DataItem"):
            if (it.window_text() or "") == f"参数名称 行 {row}":
                item = it
                break
        if item is not None:
            item.iface_scroll_item.ScrollIntoView()
            time.sleep(0.5)
            return "ScrollItemPattern"
    except Exception as exc:
        log(f"  ScrollItemPattern 不可用: {type(exc).__name__}: {exc}")

    # (b) 表格本体的 ScrollPattern，按行占比折算滚动百分比
    try:
        for el in window.descendants():
            if el.element_info.control_type not in ("Table", "DataGrid", "List"):
                continue
            try:
                scroll = el.iface_scroll
                total = 371.0
                pct = max(0.0, min(100.0, (row - 6) / max(1.0, total - 12) * 100.0))
                scroll.SetScrollPercent(-1.0, pct)
                time.sleep(0.5)
                return f"ScrollPattern({pct:.1f}%)"
            except Exception:
                continue
    except Exception as exc:
        log(f"  ScrollPattern 不可用: {type(exc).__name__}: {exc}")

    # (c) 真滚轮兜底：锚点必须落在表格本体上，否则滚的是曲线图
    try:
        rect = window.rectangle()
        anchor = (rect.left + 400, rect.top + 620)
        for _ in range(max(1, int(row / 90))):
            mouse.scroll(coords=anchor, wheel_dist=-3)
            time.sleep(0.15)
        return "mouse.wheel"
    except Exception as exc:
        return f"失败({type(exc).__name__}: {exc})"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", default="COM13")
    parser.add_argument("--row", type=int, default=270)
    parser.add_argument("--keyword", default="写入参数")
    args = parser.parse_args()

    # 机器上可能同时开着**另一个**旧版 W_TB_MS 实例（标题不带版本号），
    # 用 ".*W_TB_MS.*" 会 ElementAmbiguousError，这里只认带版本号的本构建。
    title_re = r"W_TB_MS v\d+\.\d+\.\d+"
    app = Application(backend="uia").connect(title_re=title_re, timeout=25)
    window = app.window(title_re=title_re)
    pid = app.process
    if window.is_minimized():
        window.restore()
    set_topmost(window, True)
    time.sleep(1.0)
    log(f"主窗口: {window.window_text()!r} pid={pid}")

    close_stray_dialogs(pid)

    cells = scan_cells(window)
    action = cells.get(("操作", args.row))
    if action is None:
        log(f"[FAIL] 第 {args.row} 行没有「操作」按钮")
        return 1
    log(f"[OK] 找到「操作 行 {args.row}」: {action.window_text()!r}")

    # --- 关键：真实点击前先把目标滚进视口 ---
    how = scroll_row_into_view(window, args.row)
    log(f"  滚动手段: {how}")
    time.sleep(0.5)

    cells = scan_cells(window)
    action = cells.get(("操作", args.row))
    if action is None:
        log(f"[FAIL] 滚动后第 {args.row} 行的「操作」按钮消失")
        return 1

    rect = action.rectangle()
    log(f"  按钮矩形: {rect}  可见={action.is_visible()}")
    set_topmost(window, True)
    time.sleep(0.4)

    log(">>> 真实点击 click_input")
    try:
        action.click_input()
    except Exception as exc:
        log(f"  click_input 报错: {exc}")
        log(f"  改用 mouse.click(mid_point={rect.mid_point()})")
        mouse.click(coords=rect.mid_point())

    # --- 等对话框（纯 EnumWindows，不碰 UIA） ---
    hwnd = None
    deadline = time.time() + 8.0
    while time.time() < deadline:
        for h, p, title in top_windows_of(pid):
            if args.keyword in title:
                hwnd, dlg_title = h, title
                break
        if hwnd:
            break
        time.sleep(0.3)
    log(f"顶层窗口: {[t for _h, _p, t in top_windows_of(pid)]}")
    if not hwnd:
        log("[FAIL] 对话框未弹出")
        close_stray_dialogs(pid)
        return 1
    log(f"[OK] 对话框已弹出: hwnd={hwnd} title={dlg_title!r}")

    # --- 关键验证：对话框还开着的时候，UIA 能不能读到它 ---
    log(">>> UIA 解析对话框（对话框仍打开）")
    try:
        dialog = Desktop(backend="uia").window(handle=hwnd)
        dialog.wrapper_object()
        log("[OK] UIA 解析成功")
    except Exception as exc:
        log(f"[FAIL] UIA 解析失败: {type(exc).__name__}: {exc}")
        close_stray_dialogs(pid)
        return 1

    texts = []
    for c in dialog.descendants():
        t = (c.window_text() or "").strip()
        if t:
            texts.append(t)
    log(f"  对话框文本 {len(texts)} 条:")
    for t in texts[:25]:
        log(f"    - {t}")

    for expected in ("未激活", "允许使用", "禁止使用", "0～2"):
        log(f"  {'[OK]' if any(expected in t for t in texts) else '[MISS]'} 含 {expected!r}")

    # --- 顺带验证：对话框开着时主窗体 UIA 是否可用（Invoke 路线的老毛病） ---
    log(">>> 对话框开着时扫描主窗体（Invoke 路线就是在这里炸的）")
    try:
        again = scan_cells(window)
        log(f"[OK] 主窗体 UIA 可用，扫到 {len(again)} 个单元格")
    except Exception as exc:
        log(f"[FAIL] 主窗体 UIA 不可用: {type(exc).__name__}: {exc}")

    log(">>> 关闭对话框")
    for button in ("取消", "关闭"):
        try:
            dialog.child_window(title=button, control_type="Button").click_input()
            log(f"  已点「{button}」")
            break
        except Exception as exc:
            log(f"  「{button}」不可用: {exc}")
    time.sleep(1.0)
    log(f"收尾后顶层窗口: {[t for _h, _p, t in top_windows_of(pid)]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
