#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探测 W_TB_MS 主窗口关键控件（只读）。"""

from __future__ import annotations

import re
import sys

from pywinauto import Application

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

ROW_ACTION = re.compile(r"^操作 行 \d+$")


def main() -> int:
    app = Application(backend="uia").connect(title_re=".*W_TB_MS.*", timeout=10)
    window = app.window(title_re=".*W_TB_MS.*")
    window.wait("ready", timeout=10)
    if window.is_minimized():
        window.restore()
    window.set_focus()
    print("窗口:", window.window_text(), window.rectangle())

    print("\n--- 下拉框 ---")
    for index, combo in enumerate(window.descendants(control_type="ComboBox")):
        try:
            items = combo.item_texts()
        except Exception as error:
            items = [f"<{error}>"]
        print(f"[{index}] 当前={combo.window_text()!r} 可用?={combo.is_enabled()} 选项={items}")

    print("\n--- 顶层按钮 / 复选框 ---")
    for child in window.descendants(control_type="Button"):
        text = (child.window_text() or "").strip()
        if ROW_ACTION.match(text):
            continue
        print(f"Button     | vis={child.is_visible()} | {text!r}")
    for child in window.descendants(control_type="CheckBox"):
        print(f"CheckBox   | vis={child.is_visible()} | {(child.window_text() or '').strip()!r}")

    print("\n--- 文本标签（含状态） ---")
    for child in window.descendants(control_type="Text"):
        text = (child.window_text() or "").strip()
        if not text or len(text) > 70:
            continue
        if any(key in text for key in ("状态", "连接", "故障", "模式", "COM", "已")):
            print(f"Text       | vis={child.is_visible()} | {text!r}")

    print("\n--- 表格结构 ---")
    tables = window.descendants(control_type="Table")
    for table in tables:
        print(f"Table      | {table.window_text()!r}")
    rows = window.descendants(control_type="DataItem")
    print(f"DataItem 行数: {len(rows)}")
    for row in rows[:6]:
        print("  ", repr(row.window_text())[:150])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
