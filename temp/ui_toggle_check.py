#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用原生 UIA TogglePattern 勾选「显示未检测到的风盘/分集水器」，并核对分组变化。"""

from __future__ import annotations

import re
import sys
import time

from comtypes.gen import UIAutomationClient as UIA
from pywinauto import Application

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

NAME_PATTERN = re.compile(r"^参数名称 行 (\d+)$")


def groups(window):
    result = []
    for item in window.descendants(control_type="DataItem"):
        if not NAME_PATTERN.match(item.window_text() or ""):
            continue
        try:
            value = str(item.legacy_properties().get("Value") or "").strip()
        except Exception:
            continue
        if "外机-4G 风盘" in value or "外机-4G 分集水器" in value:
            result.append(value)
    return result


def toggle_pattern(element):
    """取原生 TogglePattern（GetCurrentPattern 返回 IUnknown，需要 QueryInterface）。"""
    raw = element.GetCurrentPattern(UIA.UIA_TogglePatternId)
    return raw.QueryInterface(UIA.IUIAutomationTogglePattern)


def toggle_state(element) -> str:
    state = toggle_pattern(element).CurrentToggleState
    return {0: "Off", 1: "On", 2: "Indeterminate"}.get(state, str(state))


def main() -> int:
    app = Application(backend="uia").connect(title_re=".*W_TB_MS.*", timeout=10)
    window = app.window(title_re=".*W_TB_MS.*")
    if window.is_minimized():
        window.restore()
    window.set_focus()
    time.sleep(0.5)

    checkbox = window.child_window(title="显示未检测到的风盘/分集水器", control_type="CheckBox")
    element = checkbox.element_info.element
    print("复选框当前状态:", toggle_state(element))

    before = groups(window)
    print(f"勾选前分组数: {len(before)}")
    for name in before:
        print("   ", name)

    pattern = toggle_pattern(element)
    pattern.Toggle()
    time.sleep(2.5)
    print("\nToggle 后复选框状态:", toggle_state(element))

    after = groups(window)
    print(f"勾选后分组数: {len(after)}")
    for name in after:
        print("   ", name)

    # 还原
    pattern.Toggle()
    time.sleep(1.5)
    print("\n还原后状态:", toggle_state(element), " 分组数:", len(groups(window)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
