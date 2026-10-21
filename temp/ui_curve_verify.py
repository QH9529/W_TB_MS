#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""验证四条新需求在真界面上的效果（配合模拟外机 COM14 / 软件 COM13）。

检查项：
  1. 40401/40402 已从参数表移除（连分组标题一起消失）
  2. "隐藏BIT曲线"按钮与同排按钮同高对齐（比对 UIA 矩形 top/height）
  3. 曲线勾选项出现「风盘（内机）」「地暖（分集水器）」分组（按在线位图生成）
  4. 曲线搜索框存在、输入关键字能把勾选项过滤到只剩命中项，两个搜索框共用关键字

用法：
    python temp/ui_curve_verify.py --port COM13
"""

from __future__ import annotations

import argparse
import ctypes
import sys
import time
from typing import List, Optional

from pywinauto import Application

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

# 精确到带版本号的标题：桌面上还有别的进程的 "W_TB_MS" 窗口，宽松匹配会歧义。
MAIN_TITLE = r"W_TB_MS v\d+\.\d+\.\d+"
FAN_GROUP = "风盘（内机）"
MANIFOLD_GROUP = "地暖（分集水器）"

CHECKS: List[str] = []


def emit(text: str = "") -> None:
    print(text)
    CHECKS.append(text)


def check(label: str, ok: bool, detail: str = "") -> bool:
    emit(f"[{'PASS' if ok else 'FAIL'}] {label}{(' — ' + detail) if detail else ''}")
    return ok


def place_window(window, x: int = 60, y: int = 40, width: int = 1240, height: int = 900) -> None:
    """把窗口挪回屏幕可见区并置顶：坐标可能被上次会话留在副屏（负值），
    那样点选串口下拉的坐标会落到屏幕外。"""
    user32 = ctypes.windll.user32
    hwnd = window.element_info.handle
    HWND_TOPMOST = -1
    SWP_SHOWWINDOW = 0x0040
    user32.SetWindowPos(hwnd, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.8)


def connect_port(window, port: str) -> bool:
    combos = window.descendants(control_type="ComboBox")
    if len(combos) < 2:
        emit("未找到串口下拉框")
        return False
    combo = combos[1]
    combo.expand()
    time.sleep(0.6)
    items = [item for item in window.descendants(control_type="ListItem")
             if (item.window_text() or "").strip() == port]
    if not items:
        emit(f"！下拉框里没有 {port}：" +
             str([(i.window_text() or "").strip() for i in window.descendants(control_type="ListItem")]))
        combo.collapse()
        return False
    items[0].click_input()
    time.sleep(0.6)

    window.child_window(title="连接", control_type="Button").click_input()
    for _ in range(50):
        time.sleep(0.5)
        texts = [(c.window_text() or "").strip() for c in window.descendants(control_type="Text")]
        if any("已连接" in text for text in texts):
            emit(f"已连接 {port}")
            return True
    emit("等待连接超时")
    return False


def node_texts(tree) -> List[str]:
    if tree is None:
        return []
    return [t for t in ((item.window_text() or "").strip()
                        for item in tree.descendants(control_type="TreeItem")) if t]


def leaf_texts(tree) -> List[str]:
    """只取叶子节点：分组节点名不含关键字是正常的（叶子要挂在它下面）。"""
    if tree is None:
        return []
    result: List[str] = []
    for item in tree.descendants(control_type="TreeItem"):
        text = (item.window_text() or "").strip()
        if not text:
            continue
        try:
            if item.children(control_type="TreeItem"):
                continue
        except Exception:
            pass
        result.append(text)
    return result


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", default="COM13")
    parser.add_argument("--shot", default="temp/shot6_curve_selector.png")
    args = parser.parse_args(argv)

    app = Application(backend="uia").connect(title_re=MAIN_TITLE, timeout=15)
    window = app.window(title_re=MAIN_TITLE)
    if window.is_minimized():
        window.restore()
    place_window(window)

    if not connect_port(window, args.port):
        return 1
    # 等在线位图生效：UpdateDevicePresence 变化后才会重建曲线勾选项。
    time.sleep(8)

    # ---------- 需求1：40401/40402 不再显示 ----------
    names = [(item.window_text() or "") for item in window.descendants(control_type="DataItem")
             if (item.window_text() or "").startswith("参数名称 行 ")]
    for keyword in ("是否进入单板产测", "是否进入工厂产测模式", "单板产测密码读写段"):
        hits = [text for text in names if keyword in text]
        check(f"表格中不应出现「{keyword}」", not hits, f"命中 {len(hits)} 处" if hits else "")

    # ---------- 需求2：按钮对齐 ----------
    def rect(title: str):
        try:
            return window.child_window(title=title, control_type="Button").rectangle()
        except Exception as exc:
            emit(f"取按钮「{title}」矩形失败: {exc}")
            return None

    toggle = rect("隐藏BIT曲线")
    if toggle is None:
        check("找到「隐藏BIT曲线」按钮", False)
    else:
        check("找到「隐藏BIT曲线」按钮", True, f"top={toggle.top} height={toggle.height()}")
        peers = [r for r in (rect(n) for n in ("⟲ 回到当前", "导出LOG", "导出Excel", "打开历史", "存档路径"))
                 if r is not None]
        check("与同排按钮垂直对齐", all(abs(toggle.top - r.top) <= 1 for r in peers),
              f"本按钮 top={toggle.top}，同排 tops={sorted({r.top for r in peers})}")
        check("与同排按钮高度一致", all(abs(toggle.height() - r.height()) <= 1 for r in peers),
              f"本按钮 height={toggle.height()}，同排 heights={sorted({r.height() for r in peers})}")

    # ---------- 需求3：曲线勾选项目录 ----------
    trees = window.descendants(control_type="Tree")
    emit(f"\n找到 {len(trees)} 个 Tree 控件")
    for index, tree in enumerate(trees):
        texts = node_texts(tree)
        emit(f"  Tree[{index}] 可见节点={len(texts)} 前 8 项: {texts[:8]}")

    numeric_tree = trees[0] if trees else None
    bit_tree = trees[1] if len(trees) > 1 else None

    numeric_nodes = node_texts(numeric_tree)
    check(f"参数曲线勾选项含「{FAN_GROUP}」分组", any(FAN_GROUP in t for t in numeric_nodes))
    check("参数曲线勾选项含风盘曲线（如 风盘1）", any(t.startswith("风盘1") for t in numeric_nodes),
          next((t for t in numeric_nodes if t.startswith("风盘1")), "未找到"))
    fan_curves = [t for t in leaf_texts(numeric_tree) if t.startswith("风盘")]
    emit(f"风盘曲线叶子 {len(fan_curves)} 条: {fan_curves[:6]}")

    bit_nodes = node_texts(bit_tree)
    check(f"BIT 曲线勾选项含「{MANIFOLD_GROUP}」分组", any(MANIFOLD_GROUP in t for t in bit_nodes))

    edits = window.descendants(control_type="Edit")
    emit(f"\n找到 {len(edits)} 个 Edit 控件")
    if len(edits) < 2:
        check("找到两个曲线搜索框", False)
        window.capture_as_image().save(args.shot)
        return 1
    check("找到两个曲线搜索框", True)

    # 搜索「分集水器」：BIT 树会把命中层展开，从而暴露地暖阀门叶子。
    edits[0].set_edit_text("分集水器")
    time.sleep(1.4)
    manifold_leaves = leaf_texts(bit_tree)
    check("地暖阀门曲线已生成", len(manifold_leaves) > 0,
          f"{len(manifold_leaves)} 条: {manifold_leaves[:4]}")
    check("地暖叶子均为阀门项", manifold_leaves and all("号阀门" in t for t in manifold_leaves))
    check("按「分集水器」过滤后参数树无残留", not leaf_texts(numeric_tree),
          f"残留 {len(leaf_texts(numeric_tree))} 条")

    # 清空后测参数树过滤
    edits[0].set_edit_text("")
    time.sleep(1.4)
    before = leaf_texts(numeric_tree)
    edits[0].set_edit_text("温度")
    time.sleep(1.4)
    after = leaf_texts(numeric_tree)
    emit(f"过滤前叶子={len(before)}，输入「温度」后={len(after)}")
    check("搜索框能过滤勾选项", 0 < len(after) < len(before))
    check("过滤后只剩命中项", all("温度" in t for t in after), f"未命中: {[t for t in after if '温度' not in t][:4]}")

    mirrored = ""
    try:
        mirrored = (edits[1].window_text() or "")
    except Exception:
        pass
    check("两个搜索框共用关键字", mirrored == "温度", f"另一个='{mirrored}'")

    edits[0].set_edit_text("")
    time.sleep(1.4)
    restored = leaf_texts(numeric_tree)
    check("清空后恢复全部勾选项", len(restored) == len(before), f"{len(restored)} vs {len(before)}")

    window.capture_as_image().save(args.shot)
    emit(f"\n已保存截图 {args.shot}")

    passed = sum(1 for line in CHECKS if line.startswith("[PASS]"))
    failed = sum(1 for line in CHECKS if line.startswith("[FAIL]"))
    emit(f"\n===== 汇总: PASS {passed} / FAIL {failed} =====")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
