#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""滚到风盘分组并截图存证（滚轮定位 + 原生 TogglePattern 切换开关）。

要点：UIA 全量枚举一次约 1~2 秒，滚动循环里只查目标元素，避免每轮重枚举。
"""

from __future__ import annotations

import re
import sys
import time
from typing import Dict, List, Optional, Tuple

from comtypes.gen import UIAutomationClient as UIA
from pywinauto import Application, mouse

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

NAME_PATTERN = re.compile(r"^参数名称 行 (\d+)$")


def scan_rows(window) -> List[Tuple[int, str, object]]:
    result = []
    for item in window.descendants(control_type="DataItem"):
        match = NAME_PATTERN.match(item.window_text() or "")
        if not match:
            continue
        try:
            value = str(item.legacy_properties().get("Value") or "").strip()
        except Exception:
            continue
        result.append((int(match.group(1)), value, item))
    return result


def toggle(element) -> None:
    raw = element.GetCurrentPattern(UIA.UIA_TogglePatternId)
    raw.QueryInterface(UIA.IUIAutomationTogglePattern).Toggle()


def toggle_state(element) -> str:
    raw = element.GetCurrentPattern(UIA.UIA_TogglePatternId)
    state = raw.QueryInterface(UIA.IUIAutomationTogglePattern).CurrentToggleState
    return {0: "Off", 1: "On", 2: "Indeterminate"}.get(state, str(state))


def scroll_to(window, item, anchor, max_steps: int = 400) -> bool:
    """滚轮滚动，直到该行进入窗口可视区域。只查询目标元素，速度可控。"""
    window_rect = window.rectangle()
    for _ in range(max_steps):
        try:
            if item.is_visible():
                rect = item.rectangle()
                if window_rect.top <= rect.top and rect.bottom <= window_rect.bottom:
                    return True
        except Exception:
            pass
        mouse.scroll(coords=anchor, wheel_dist=-3)
        time.sleep(0.03)
    return False


def main() -> int:
    app = Application(backend="uia").connect(title_re=".*W_TB_MS.*", timeout=10)
    window = app.window(title_re=".*W_TB_MS.*")
    if window.is_minimized():
        window.restore()
    window.set_focus()
    time.sleep(0.6)

    checkbox = window.child_window(title="显示未检测到的风盘/分集水器", control_type="CheckBox")
    checkbox_element = checkbox.element_info.element
    print("复选框状态:", toggle_state(checkbox_element))

    anchor = window.rectangle().mid_point()
    rows = scan_rows(window)
    for row, name, item in rows:
        if item.is_visible():
            anchor = item.rectangle().mid_point()
            break

    def shoot(keyword: str, path: str, label: str) -> None:
        target: Optional[Tuple[int, str, object]] = next((r for r in rows if keyword in r[1]), None)
        if target is not None:
            scroll_to(window, target[2], anchor)
            time.sleep(0.9)
        window.capture_as_image().save(path)
        print(f"已保存 {path}（{label}，行 {target[0] if target else '?'}）")

    shoot("外机-4G 风盘1（", "temp/shot1_online_groups.png", "仅在线设备")

    toggle(checkbox_element)
    time.sleep(2.0)
    print("勾选后状态:", toggle_state(checkbox_element))
    rows = scan_rows(window)
    shoot("外机-4G 风盘1（", "temp/shot2_all_groups.png", "勾选后 30 组")
    shoot("风盘在线状态", "temp/shot3_online_bitmap.png", "在线位图行")

    toggle(checkbox_element)
    time.sleep(1.2)
    print("还原后状态:", toggle_state(checkbox_element))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
