#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""展开串口下拉框，列出可选串口（只读探测，不改变最终选择）。"""

from __future__ import annotations

import sys
import time

from pywinauto import Application

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass


def main() -> int:
    app = Application(backend="uia").connect(title_re=".*W_TB_MS.*", timeout=10)
    window = app.window(title_re=".*W_TB_MS.*")
    if window.is_minimized():
        window.restore()
    window.set_focus()

    combos = window.descendants(control_type="ComboBox")
    combo = combos[1]
    print("ComboBox name:", repr(combo.window_text()))
    print("ComboBox children:")
    for child in combo.children():
        print("   ", child.element_info.control_type, repr(child.window_text()))

    combo.expand()
    time.sleep(0.6)
    items = window.descendants(control_type="ListItem")
    print(f"\n展开后 ListItem 共 {len(items)} 个:")
    for item in items:
        print("   ", repr(item.window_text()))
    # 收起下拉，不改动选择
    combo.collapse()
    time.sleep(0.3)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
