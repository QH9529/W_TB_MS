#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""打印指定行上所有 UIA 元素（不限定控件类型），确认"操作"列暴露成什么。"""

from __future__ import annotations

import argparse
import sys

from pywinauto import Application

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--row", type=int, default=182)
    args = parser.parse_args()

    app = Application(backend="uia").connect(title_re=".*W_TB_MS.*", timeout=10)
    window = app.window(title_re=".*W_TB_MS.*")

    suffix = f"行 {args.row}"
    for child in window.descendants():
        name = child.window_text() or ""
        if suffix not in name:
            continue
        try:
            ctype = child.element_info.control_type
        except Exception:
            ctype = "?"
        try:
            value = child.legacy_properties().get("Value")
        except Exception:
            value = None
        print(f"{ctype:<14} name={name!r:<28} value={value!r}")

    print("\n--- 表格列头元素 ---")
    for child in window.descendants(control_type="HeaderItem"):
        print("HeaderItem", repr(child.window_text()))
    for child in window.descendants(control_type="Button"):
        text = (child.window_text() or "").strip()
        if text in ("操作", "读取", "写入"):
            print("Button", repr(text))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
