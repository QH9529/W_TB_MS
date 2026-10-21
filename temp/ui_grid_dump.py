#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""探针：按行号汇总 DataGridView 的单元格内容（参数名称/寄存器/属性/操作）。

DataGridView 在 UIA 里每行每列都是一个 DataItem，名字形如「属性 行 182」，
真实单元格内容要用 legacy_properties()['Value'] 取（window_text() 只有列名+行号）。

用法:
    python temp/ui_grid_dump.py                 # 只汇总
    python temp/ui_grid_dump.py --find 40501    # 只打印含关键字的行
"""

from __future__ import annotations

import argparse
import re
import sys

from pywinauto import Application

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

MAIN_TITLE = r"W_TB_MS v\d+\.\d+\.\d+"
COLUMNS = ("参数名称", "当前值", "单位", "寄存器", "属性", "操作")
PATTERN = re.compile(rf"^({'|'.join(COLUMNS)}) 行 (\d+)$")


def collect(window) -> dict[int, dict[str, str]]:
    rows: dict[int, dict[str, str]] = {}
    for item in window.descendants(control_type="DataItem"):
        match = PATTERN.match(item.window_text() or "")
        if not match:
            continue
        column, index = match.group(1), int(match.group(2))
        try:
            value = str(item.legacy_properties().get("Value") or "").strip()
        except Exception:
            value = ""
        rows.setdefault(index, {})[column] = value
    return rows


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--find", default="")
    parser.add_argument("--from-row", type=int, default=0)
    parser.add_argument("--to-row", type=int, default=10 ** 9)
    args = parser.parse_args()

    app = Application(backend="uia").connect(title_re=MAIN_TITLE, timeout=20)
    window = app.window(title_re=MAIN_TITLE)
    rows = collect(window)
    print(f"UIA 暴露的行数: {len(rows)}（DataGridView 有行虚拟化，只含可视/已渲染行）")
    if rows:
        print(f"行号范围: {min(rows)} ~ {max(rows)}")

    for index in sorted(rows):
        if index < args.from_row or index > args.to_row:
            continue
        cells = rows[index]
        name = cells.get("参数名称", "")
        register = cells.get("寄存器", "")
        blob = " | ".join(cells.get(c, "") for c in COLUMNS)
        if args.find and args.find not in blob and args.find != register:
            continue
        print(f"  行{index:<5} 寄存器={register:<6} 属性={cells.get('属性', ''):<5} "
              f"操作={cells.get('操作', ''):<5} 名称={name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
