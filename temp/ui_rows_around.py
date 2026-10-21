#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""打印指定行号附近的表格行（名称 + 可见性），用来判断目标行是否被折叠分组隐藏。"""

from __future__ import annotations

import argparse
import re
import sys

from pywinauto import Application

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

NAME_PATTERN = re.compile(r"^参数名称 行 (\d+)$")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--center", type=int, default=182)
    parser.add_argument("--span", type=int, default=12)
    args = parser.parse_args()

    app = Application(backend="uia").connect(title_re=".*W_TB_MS.*", timeout=10)
    window = app.window(title_re=".*W_TB_MS.*")
    rows = {}
    hidden = []
    for item in window.descendants(control_type="DataItem"):
        match = NAME_PATTERN.match(item.window_text() or "")
        if not match:
            continue
        index = int(match.group(1))
        try:
            value = str(item.legacy_properties().get("Value") or "").strip()
        except Exception:
            value = ""
        try:
            visible = item.is_visible()
        except Exception:
            visible = None
        rows[index] = (value, visible)
        if visible is False:
            hidden.append(index)

    print("总行数:", len(rows), " 隐藏行数:", len(hidden))
    print("首个隐藏行:", hidden[:5], " 末个:", hidden[-5:] if hidden else None)
    if hidden:
        runs = []
        start = prev = hidden[0]
        for index in hidden[1:]:
            if index == prev + 1:
                prev = index
                continue
            runs.append((start, prev))
            start = prev = index
        runs.append((start, prev))
        print("隐藏行区间:", runs)

    print(f"\n--- 行 {args.center} 附近 ---")
    for index in range(args.center - args.span, args.center + args.span + 1):
        if index in rows:
            value, visible = rows[index]
            flag = "可见" if visible else ("隐藏" if visible is False else "?")
            print(f"  行{index:<5} {flag}  {value}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
