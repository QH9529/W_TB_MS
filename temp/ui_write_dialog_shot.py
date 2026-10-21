#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""验证"写入前二次确认弹窗"里的值含义描述，并截图存证。

流程：连上模拟外机 → 找到「工作模式设置」(40201) 一行（必要时先展开所属分组）→
点该行"操作"列 → 在写入参数对话框里填 2 → 点确定 → 抓住"确认写入"弹窗，
dump 文本 + 截图 → 点取消（不真的写入）。

前置：
    python temp/sim_odu.py --port COM14 --fans 1,2,5 --manifolds 1,3 --quiet
    W_TB_MS.exe 已启动（本脚本会自动连 COM13）

用法：
    python temp/ui_write_dialog_shot.py --target 工作模式设置 --value 2

注意（踩过的坑）：
  * DataGridView 在 UIA 里是虚拟化的：只有进入视口的行 is_visible()=True，
    滚动后行元素会重建，**旧元素对象会失效抛异常**，所以每轮都要重新扫描。
  * 分组标题行上的 ▶/▼ 表示折叠状态，折叠时成员行 Visible=False、永远滚不到，
    必须先点开分组标题行。
  * 滚轮锚点必须落在表格本体上，否则滚的是曲线图（缩放）。
"""

from __future__ import annotations

import argparse
import ctypes
import sys
import time
from ctypes import wintypes
from typing import Dict, List, Optional, Tuple

from pywinauto import Application, Desktop, mouse

from gui_verify import Grid, connect_port, read_status

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

MAIN_TITLE = ".*W_TB_MS.*"
COLUMNS = ("参数名称", "当前值", "单位", "操作")
ROWS_PER_WHEEL3 = 9          # 实测：wheel_dist=±3 一次约滚动 9 行

Cells = Dict[Tuple[str, int], object]

HWND_TOPMOST = -1
HWND_NOTOPMOST = -2
SWP_NOMOVE_NOSIZE = 0x0001 | 0x0002


def set_topmost(window, topmost: bool = True) -> bool:
    """把窗口顶到最前。

    桌面上一旦有别的窗口盖住被测窗口，click_input/mouse.click 的坐标就会打到别的程序上
    （实测踩过：点击落到浏览器/Excel 上，表格单元格永远不会收到 Click）。
    """
    try:
        hwnd = int(window.handle)
    except Exception:
        return False
    user32 = ctypes.windll.user32
    user32.SetWindowPos(hwnd, HWND_TOPMOST if topmost else HWND_NOTOPMOST,
                        0, 0, 0, 0, SWP_NOMOVE_NOSIZE)
    try:
        user32.SetForegroundWindow(hwnd)
    except Exception:
        pass
    return True


def scan_cells(window) -> Cells:
    """一次扫描，返回 {(列名, 行号): 元素}。UIA 全量枚举很慢，尽量少调。

    注意："操作"列是 DataGridViewButtonColumn，暴露为 **Button**（不是 DataItem），
    名称形如 "操作 行 182"、Value 是该行属性（"写入"/"只读"）。
    """
    cells: Cells = {}
    for item in window.descendants():
        name = item.window_text() or ""
        if " 行 " not in name:
            continue
        for column in COLUMNS:
            prefix = f"{column} 行 "
            tail = name[len(prefix):]
            if name.startswith(prefix) and tail.isdigit():
                cells[(column, int(tail))] = item
                break
    return cells


def cell_text(element) -> str:
    if element is None:
        return ""
    try:
        value = element.legacy_properties().get("Value")
    except Exception:
        value = None
    return "" if value in (None, "(null)") else str(value).strip()


def visible_rows(cells: Cells) -> List[int]:
    rows = []
    for (column, row), element in cells.items():
        if column != "参数名称":
            continue
        try:
            if element.is_visible():
                rows.append(row)
        except Exception:
            # 行元素已失效（滚动后重建）→ 视为不在视口内
            pass
    return sorted(rows)


def grid_anchor(window, cells: Cells):
    rows = visible_rows(cells)
    if not rows:
        return window.rectangle().mid_point()
    try:
        return cells[("参数名称", rows[0])].rectangle().mid_point()
    except Exception:
        return window.rectangle().mid_point()


def find_row(cells: Cells, keyword: str) -> Optional[int]:
    hits = [row for (column, row) in cells
            if column == "参数名称" and cell_text(cells[(column, row)]) == keyword]
    if hits:
        return min(hits)
    hits = [row for (column, row) in cells
            if column == "参数名称" and keyword in cell_text(cells[(column, row)])]
    return min(hits) if hits else None


def scroll_row_into_view(window, index: int, anchor, rounds: int = 10) -> Optional[Cells]:
    """把目标行滚进视口，返回最新的扫描结果（失败返回 None）。

    每轮重新扫描：滚动后 DataGridView 会重建行元素，旧对象读 is_visible 会抛异常。
    """
    for _ in range(rounds):
        cells = scan_cells(window)
        rows = visible_rows(cells)
        if not rows:
            mouse.scroll(coords=anchor, wheel_dist=-3)
            time.sleep(0.2)
            continue
        if index in rows:
            return cells
        delta = (min(rows) - index) if index < min(rows) else (index - max(rows))
        wheel = 3 if index < min(rows) else -3
        notches = max(1, min(80, round(delta / ROWS_PER_WHEEL3)))
        for _ in range(notches):
            mouse.scroll(coords=anchor, wheel_dist=wheel)
        time.sleep(0.25)
    return None


def expand_owning_group(window, index: int, anchor, rounds: int = 4) -> Optional[Cells]:
    """折叠分组里的成员行不会被渲染，先把所属分组标题行点开。"""
    cells = scan_cells(window)
    for _ in range(rounds):
        group_row = None
        for row in range(index - 1, -1, -1):
            text = cell_text(cells.get(("参数名称", row)))
            if text.startswith("▶ ") or text.startswith("▼ "):
                group_row = row
                break
        if group_row is None:
            print("  未找到所属分组行，按已展开处理")
            return cells

        found = scroll_row_into_view(window, group_row, anchor)
        if found is None:
            print(f"  ！滚不到分组行 {group_row}")
            return cells
        cells = found
        text = cell_text(cells.get(("参数名称", group_row)))
        if text.startswith("▼ "):
            print(f"  分组已展开: 行{group_row} {text}")
            return cells
        print(f"  展开分组: 行{group_row} {text}")
        cells[("参数名称", group_row)].click_input()
        time.sleep(0.9)
        cells = scan_cells(window)
    return cells


def dump_window(dlg, label: str) -> List[str]:
    lines: List[str] = []
    for child in dlg.descendants():
        text = (child.window_text() or "").strip()
        if not text:
            continue
        try:
            ctype = child.element_info.control_type
        except Exception:
            ctype = "?"
        line = f"[{label}] {ctype:<12} {text}"
        print(line)
        lines.append(line)
    return lines


_WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


def top_windows(keyword: str = "") -> List[Tuple[int, int, str]]:
    """枚举可见顶层窗口 → [(hwnd, pid, title)]。

    不用 app.windows()：ShowDialog 出来的模态窗体（ShowInTaskbar=False）在 pywinauto 的
    windows() 里看不到，但它确实是顶层窗口（实测踩过：弹窗明明开了，脚本却报"未出现"）。
    """
    found: List[Tuple[int, int, str]] = []
    user32 = ctypes.windll.user32

    def callback(hwnd, _lparam):
        length = user32.GetWindowTextLengthW(hwnd)
        if length <= 0 or not user32.IsWindowVisible(hwnd):
            return True
        buffer = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buffer, length + 1)
        title = buffer.value
        if keyword and keyword not in title:
            return True
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        found.append((int(hwnd), int(pid.value), title))
        return True

    user32.EnumWindows(_WNDENUMPROC(callback), 0)
    return found


def wait_window(pid: int, keyword: str, timeout: float = 8.0):
    """等待出现指定标题的顶层窗口，返回 (hwnd, pywinauto 包装)。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        for hwnd, owner_pid, title in top_windows(keyword):
            if owner_pid == pid:
                return hwnd, Desktop(backend="uia").window(handle=hwnd)
        time.sleep(0.3)
    return None


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", default="工作模式设置")
    parser.add_argument("--value", default="2")
    parser.add_argument("--port", default="COM13")
    parser.add_argument("--shot", default="temp/shot4_write_confirm.png")
    args = parser.parse_args(argv)

    app = Application(backend="uia").connect(title_re=MAIN_TITLE, timeout=10)
    window = app.window(title_re=MAIN_TITLE)
    pid = app.process
    print("主程序 pid =", pid)
    if window.is_minimized():
        window.restore()
    window.set_focus()
    set_topmost(window, True)
    time.sleep(0.8)

    status = read_status(window)
    if not any("已连接" in item for item in status):
        print("尚未连接，尝试连接", args.port)
        if not connect_port(window, args.port):
            return 1
        time.sleep(6.0)

    cells = scan_cells(window)
    index = find_row(cells, args.target)
    if index is None:
        print(f"！表格里找不到「{args.target}」")
        return 1
    print(f"目标行 {index}: {cell_text(cells[('参数名称', index)])}  "
          f"当前值={cell_text(cells.get(('当前值', index)))}")

    anchor = grid_anchor(window, cells)
    cells = expand_owning_group(window, index, anchor) or cells

    found = scroll_row_into_view(window, index, anchor)
    if found is None:
        print("！滚动定位失败")
        return 1
    cells = found
    print(f"已定位: 行{index} {cell_text(cells[('参数名称', index)])}，"
          f"当前值={cell_text(cells.get(('当前值', index)))}")
    time.sleep(0.4)

    print(f"操作列属性: {cell_text(cells.get(('操作', index)))!r}")

    def open_dialog(title_keywords):
        for keyword in title_keywords:
            found = wait_window(pid, keyword, timeout=3.0)
            if found is not None:
                return found
        return None

    dialog_result = None
    for strategy in ("click", "mouse-at-rect", "select+click"):
        print(f"尝试打开写入对话框: {strategy}")
        # 每轮都重新扫描：滚动/重绘后 UIA 元素会失效，旧对象一读就抛 COMError。
        cells = scan_cells(window)
        action = cells.get(("操作", index))
        if action is None:
            print("  操作列单元格不可用")
            continue
        try:
            if strategy == "click":
                action.click_input()
            elif strategy == "select+click":
                name_cell = cells.get(("参数名称", index))
                if name_cell is not None:
                    name_cell.click_input()
                    time.sleep(0.3)
                cells = scan_cells(window)
                action = cells.get(("操作", index))
                if action is not None:
                    action.click_input()
            else:
                rect = action.rectangle()
                print(f"  操作列矩形: {rect}")
                mouse.click(coords=rect.mid_point())
        except Exception as exc:
            print(f"  {strategy} 报错: {exc}")
        dialog_result = open_dialog(("写入参数", "执行寄存器命令"))
        if dialog_result is not None:
            break
        time.sleep(0.5)

    if dialog_result is None:
        window.capture_as_image().save("temp/debug_after_click.png")
        print("已保存 temp/debug_after_click.png")
        print("！未出现写入参数对话框，当前顶层窗口:",
              [(t, p) for _h, p, t in top_windows() if p == pid])
        return 1

    dialog_hwnd, dialog = dialog_result
    print(f"=== 写入参数对话框 (hwnd={dialog_hwnd}) ===")
    dump_window(dialog, "dialog")

    edit = dialog.child_window(control_type="Edit")
    edit.set_focus()
    edit.set_edit_text(args.value)
    time.sleep(0.4)
    dialog.capture_as_image().save("temp/shot4a_write_dialog.png")
    print("已保存 temp/shot4a_write_dialog.png")

    dialog.child_window(title="确定", control_type="Button").click_input()

    confirm_result = open_dialog(("确认写入", "确认执行"))
    if confirm_result is None:
        print("！未出现二次确认弹窗，当前顶层窗口:",
              [(t, p) for _h, p, t in top_windows() if p == pid])
        return 1
    confirm_hwnd, confirm = confirm_result
    time.sleep(0.6)
    print(f"=== 二次确认弹窗 (hwnd={confirm_hwnd}) ===")
    lines = dump_window(confirm, "confirm")
    confirm.capture_as_image().save(args.shot)
    print(f"已保存 {args.shot}")

    with open("temp/write_confirm_dump.txt", "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines))

    # 点取消，确保不会真的下发写命令（默认按钮是"取消"，也可以直接按 Esc）。
    try:
        confirm.child_window(title="取消", control_type="Button").click_input()
    except Exception as exc:
        print("点取消失败，改按 Esc:", exc)
        try:
            confirm.type_keys("{ESC}")
        except Exception as inner:
            print("Esc 也失败:", inner)
    time.sleep(0.6)
    set_topmost(window, False)

    joined = " ".join(lines)
    ok = "值含义" in joined and "可选值" in joined
    print("弹窗包含值含义/可选值:", "PASS" if ok else "FAIL")
    return 0 if ok else 2


if __name__ == "__main__":
    raise SystemExit(main())
