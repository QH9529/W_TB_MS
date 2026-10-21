#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""逐步滚动并打印表格「已渲染行区间」，用来定位滚动步长与目标行为何不可见。"""

from __future__ import annotations

import sys
import time

from pywinauto import Application, mouse

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

COLUMNS = ("参数名称", "当前值", "单位", "操作")


def scan(window):
    rows = {}
    for item in window.descendants(control_type="DataItem"):
        name = item.window_text() or ""
        for column in COLUMNS:
            prefix = f"{column} 行 "
            if name.startswith(prefix) and name[len(prefix):].isdigit():
                index = int(name[len(prefix):])
                if column == "参数名称":
                    try:
                        value = str(item.legacy_properties().get("Value") or "").strip()
                    except Exception:
                        value = ""
                    rows[index] = (item, value)
                break
    return rows


def main() -> int:
    app = Application(backend="uia").connect(title_re=".*W_TB_MS.*", timeout=10)
    window = app.window(title_re=".*W_TB_MS.*")
    if window.is_minimized():
        window.restore()
    window.set_focus()
    time.sleep(0.5)

    rows = scan(window)
    anchor = None
    for index in sorted(rows):
        try:
            if rows[index][0].is_visible():
                anchor = rows[index][0].rectangle().mid_point()
                break
        except Exception:
            continue
    print("锚点:", anchor)

    for step in range(0, 21):
        rows = scan(window)
        visible = []
        for index, (element, _text) in rows.items():
            try:
                if element.is_visible():
                    visible.append(index)
            except Exception:
                pass
        total = max(rows) if rows else 0
        print(f"step{step:>3}  已渲染行数={len(rows):<5} 可见 {min(visible) if visible else '?'}~{max(visible) if visible else '?'}")
        mouse.scroll(coords=anchor, wheel_dist=-3)
        time.sleep(0.15)

    rows = scan(window)
    for index in (180, 181, 182):
        if index in rows:
            element, text = rows[index]
            try:
                vis = element.is_visible()
            except Exception as exc:
                vis = f"异常 {exc}"
            print(f"行{index}: {text}  visible={vis}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
