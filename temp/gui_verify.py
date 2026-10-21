#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""让 W_TB_MS 连上「模拟外机」，并从界面读出风盘/分集水器相关行做验证。

前置：
    python temp/sim_odu.py --port COM14 --fans 1,2,5 --manifolds 1,3
    （上位机连虚拟串口对的另一端 COM13）

用法：
    python temp/gui_verify.py --port COM13 --out temp/gui_verify_report.txt
"""

from __future__ import annotations

import argparse
import re
import sys
import time
from typing import Dict, List, Optional

from pywinauto import Application

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

NAME_PATTERN = re.compile(r"^(参数名称|当前值|单位|操作) 行 (\d+)$")


class Grid:
    """把 DataGridView 的 UIA 元素整理成 {行号: {列名: 值}}。"""

    def __init__(self, window):
        self.rows: Dict[int, Dict[str, str]] = {}
        self.visible: Dict[int, bool] = {}
        for item in window.descendants(control_type="DataItem"):
            match = NAME_PATTERN.match(item.window_text() or "")
            if not match:
                continue
            column, index = match.group(1), int(match.group(2))
            try:
                value = item.legacy_properties().get("Value")
            except Exception:
                value = None
            self.rows.setdefault(index, {})[column] = "" if value in (None, "(null)") else str(value).strip()
            if column == "参数名称":
                self.visible[index] = item.is_visible()

        self.name_to_index: Dict[str, List[int]] = {}
        for index, cells in self.rows.items():
            name = cells.get("参数名称", "").strip()
            if name:
                self.name_to_index.setdefault(name, []).append(index)

    def find(self, keyword: str) -> List[int]:
        """按关键字模糊匹配参数名称，返回行号（保持表格顺序）。"""
        return sorted(index for index, cells in self.rows.items()
                      if keyword in cells.get("参数名称", ""))

    def value(self, index: int) -> str:
        return self.rows.get(index, {}).get("当前值", "")

    def name(self, index: int) -> str:
        return self.rows.get(index, {}).get("参数名称", "")

    def is_visible(self, index: int) -> bool:
        return self.visible.get(index, False)


def read_status(window) -> List[str]:
    texts = []
    for child in window.descendants(control_type="Text"):
        text = (child.window_text() or "").strip()
        if not text or len(text) > 80:
            continue
        if any(key in text for key in ("未连接", "已连接", "连接成功", "连接失败", "重连", "故障汇总")):
            texts.append(text)
    return texts


def connect_port(window, port: str) -> bool:
    combos = window.descendants(control_type="ComboBox")
    if len(combos) < 2:
        print("未找到串口下拉框")
        return False
    combo = combos[1]

    # WinForms ComboBox 的选项要展开后才以 ListItem 暴露，直接点选才等同人工操作。
    combo.expand()
    time.sleep(0.6)
    items = [item for item in window.descendants(control_type="ListItem")
             if (item.window_text() or "").strip() == port]
    options = [(item.window_text() or "").strip() for item in window.descendants(control_type="ListItem")]
    print(f"串口下拉框选项: {options}")
    if not items:
        print(f"！下拉框里没有 {port}")
        combo.collapse()
        return False

    items[0].click_input()
    time.sleep(0.6)
    print(f"已选串口: {combo.window_text()!r}")

    window.child_window(title="连接", control_type="Button").click_input()
    for _ in range(40):
        time.sleep(0.5)
        status = read_status(window)
        if any("已连接" in item or "连接成功" in item for item in status):
            print(f"连接状态: {status}")
            return True
    print(f"等待连接超时，当前状态: {read_status(window)}")
    return False


def dump(window, grid: Grid, lines: List[str]) -> None:
    def emit(text: str = "") -> None:
        print(text)
        lines.append(text)

    emit("\n===== 外机-4G 相关行 =====")
    for index in grid.find("外机-4G"):
        emit(f"行{index:<5} vis={grid.is_visible(index)!s:<5} {grid.name(index)}")

    emit("\n===== 在线位图 =====")
    for keyword in ("风盘在线状态", "分集水器在线状态"):
        for index in grid.find(keyword):
            emit(f"行{index:<5} {grid.name(index)} = {grid.value(index)}")

    emit("\n===== 风盘模板（组内各项）=====")
    for fan in (1, 2, 5, 3):
        emit(f"--- 风盘{fan} ---")
        group_rows = grid.find(f"外机-4G 风盘{fan}（")
        if not group_rows:
            emit("  （未找到分组行）")
            continue
        start = group_rows[0]
        for index in range(start, start + 20):
            name = grid.name(index)
            if not name:
                break
            if index != start and "外机-4G 风盘" in name:
                break
            emit(f"  行{index:<5} vis={grid.is_visible(index)!s:<5} {name:<26} = {grid.value(index)}")

    emit("\n===== 分集水器 =====")
    for manifold in (1, 3, 2):
        rows = grid.find(f"外机-4G 分集水器{manifold}·")
        if not rows:
            emit(f"  分集水器{manifold}: 未找到分组行")
            continue
        start = rows[0]
        emit(f"  行{start:<5} vis={grid.is_visible(start)!s:<5} {grid.name(start)}")
        emit(f"        行{start + 1:<5} vis={grid.is_visible(start + 1)!s:<5} "
             f"{grid.name(start + 1)} = {grid.value(start + 1)}")

    emit("\n===== 其它关键值 =====")
    for keyword in ("产品型号", "空调进水温度（快速）", "压缩机频率（快速）", "热水器出水温度",
                    "压缩机累计运行时长", "唯一识别码", "室外机激活", "机内设备运行数量"):
        for index in grid.find(keyword):
            emit(f"行{index:<5} {grid.name(index)} = {grid.value(index)}")


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", default="COM13")
    parser.add_argument("--out", default="temp/gui_verify_report.txt")
    parser.add_argument("--shot", default="temp/gui_verify_grid.png")
    args = parser.parse_args(argv)

    app = Application(backend="uia").connect(title_re=".*W_TB_MS.*", timeout=10)
    window = app.window(title_re=".*W_TB_MS.*")
    window.wait("ready", timeout=10)
    if window.is_minimized():
        window.restore()
    window.set_focus()
    print("窗口:", window.window_text())

    lines: List[str] = []
    if not connect_port(window, args.port):
        return 1

    time.sleep(8)   # 等两三个轮询周期，让在线位图与风盘模板都读上来
    lines.append(f"连接状态: {read_status(window)}")
    print("连接状态:", read_status(window))

    grid = Grid(window)
    dump(window, grid, lines)

    with open(args.out, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines))
    print(f"\n报告已写入 {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
