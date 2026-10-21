#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""查看表格某一行（DataItem）的单元格结构，确认能否读到参数名/当前值。"""

from __future__ import annotations

import sys

from pywinauto import Application

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

TARGET_ROW = 500


def main() -> int:
    app = Application(backend="uia").connect(title_re=".*W_TB_MS.*", timeout=10)
    window = app.window(title_re=".*W_TB_MS.*")
    if window.is_minimized():
        window.restore()
    window.set_focus()

    items = window.descendants(control_type="DataItem")
    print("DataItem 总数:", len(items))
    sample = [item for item in items if item.window_text().endswith(f"行 {TARGET_ROW}")]
    print(f"行 {TARGET_ROW} 的控件数: {len(sample)}")
    for item in sample:
        info = item.element_info
        print(f"  name={item.window_text()!r} ctype={info.control_type} class={info.class_name}")
        try:
            print(f"    legacy_value={item.legacy_properties().get('Value')!r}")
        except Exception as error:
            print("    legacy_properties 失败:", error)
        for child in item.children():
            cinfo = child.element_info
            print(f"      child name={child.window_text()!r} ctype={cinfo.control_type}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
