#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""验证本轮两条改动在真界面上的效果（配合模拟外机 COM14 / 软件连 COM13）。

检查项：
  1. 40501 / 40510 / 40511 / 40519（IMEI、室外机激活、激活 MD5、单机并联模式）提供写入入口
  2. 4G_OTA 40601~40674 与 Flash 持久化 40801/40851/40855 变成只读（没有写入入口）
  3. 41101~43006（风盘设置/风盘产测/地暖控制/MixPad 网络管理）已整段从界面移除
  4. 回归：40401/40402 仍不显示；风盘控制段 41001~41017 与版本信息段仍保留
  5. 40510 的写入对话框列出「可选值：0 = 未激活 / 1 = 允许使用 / 2 = 禁止使用」
  6. 40501 的写入走"整段 0x10 写入"专用对话框，真写一条 IMEI 后当前值刷新

可写性的判据：DataGridView 的可写行「操作」列是 Button（UIA 里存在「操作 行 N」），
只读行的该列被换成文本单元格，UIA 里没有这个 Button。因此
「操作 行 N 存在」 ⟺ 「第 N 行可写」，与代码里 RegisterMap.IsWritableInUi 一一对应。

踩过的坑（已在代码里规避）：
  * 主窗体若留在左侧副屏（负坐标），pywinauto 的 mouse 用主屏尺寸归一化绝对坐标，
    点击会落到别处 —— 先 SetWindowPos 挪回主屏（必须声明 argtypes，否则
    HWND_TOPMOST=-1 当 32 位 int 传，64 位下句柄错乱、SetWindowPos 直接失败）。
  * ShowDialog 出来的模态窗体（ShowInTaskbar=False）不在 pywinauto 的 app.windows() 里，
    必须用 EnumWindows 按 pid 找（复用 ui_write_dialog_shot.wait_window）。
  * DataGridView 行虚拟化：滚动后行元素会重建，旧 UIA 对象一读就抛异常，每轮重新扫描。
  * **模态对话框打开期间，主窗体的 UIA 查询会超时**（进程 UI 线程在 ShowDialog 里），
    所以 Invoke 打开对话框之后绝不能再 scan_cells(主窗体)，否则整轮报废（踩过两次）。
    本机桌面会话还会间歇性不可用（SetCursorPos 报 "no active desktop"），所以交互
    一律优先用 UIA Pattern（Invoke / SetValue），真实鼠标只当兜底。

用法：
    python temp/ui_writable_verify.py --port COM13
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes
import re
import sys
import threading
import time
from pathlib import Path
from typing import Dict, List, Optional

from pywinauto import Application, Desktop, mouse

sys.path.insert(0, str(Path(__file__).resolve().parent))
# 复用上一轮验证过的辅助函数（三重点击降级、EnumWindows 找模态窗、虚拟化滚动定位）
from ui_write_dialog_shot import (  # noqa: E402
    scan_cells,
    set_topmost,
    top_windows,
)
# 对话框一律走纯 Win32 通道：UIA 在模态框挂着时会被 provider 占住而超时（详见 win32_dlg）
import win32_dlg  # noqa: E402

MAIN_TITLE = r"W_TB_MS v\d+\.\d+\.\d+"
NAME_ROW = re.compile(r"^参数名称 行 (\d+)$")
ACTION_ROW = re.compile(r"^操作 行 (\d+)$")

WRITABLE_KEYWORDS = {
    "40501 唯一识别码(4G IMEI)": "唯一识别码",
    "40510 室外机激活": "室外机激活",
    "40511 室外机激活MD5加密段0~7": "激活MD5加密段0~7",
    "40519 热泵主机单机/并联模式": "单机/并联模式",
}

READONLY_KEYWORDS = [
    "是否启动新的升级", "升级类型", "升级软件版本", "升级文件大小",
    "当前升级包序号", "当前升级包长度", "当前升级包数据", "当前升级包校验码",
    "压缩机累计运行时长", "热水杀菌完成时间", "风机滤网清洁时间",
]

REMOVED_KEYWORDS = [
    "【风盘设置段（风盘设备）】", "风盘型号设置", "新风电机最低转速", "新风电机最高转速",
    "新风滤网清洗提醒时间", "风盘电机最低转速", "风盘电机最高转速",
    "重置风盘电机转速限值(41107)",
    "【风盘产测段（风盘设备）】", "拨码开关产测",
    "【地暖控制段（地暖设备 0xA1~0xAF）】", "分水器阀门", "关闭所有分水器阀门",
    "【MixPad 网络管理段（底壳设备）】", "底壳485地址修改通知", "底壳在线离线状态",
    "风盘在线离线状态", "地暖在线离线状态", "热泵在线离线状态", "热泵485地址表",
]

KEPT_KEYWORDS = [
    "【风盘控制段（风盘设备 0x91~0x9F）】", "风盘关机", "重置风盘电机转速限值",
    "【风盘/地暖版本信息（设备本地）】", "风盘硬件版本号", "地暖故障码",
    "【读写信息段：IMEI/激活（40501~40599）· 可写入，写前二次确认】",
    "【4G_OTA 段（40601~40699）· 只读】",
    "【Flash 持久化数据段（40801~40899）· 只读】",
]

CHECKS: List[str] = []


def emit(text: str = "") -> None:
    print(text, flush=True)
    CHECKS.append(text)


def step(text: str) -> None:
    print(f">>> {text}", flush=True)


def check(label: str, ok: bool, detail: str = "") -> bool:
    emit(f"[{'PASS' if ok else 'FAIL'}] {label}{(' — ' + detail) if detail else ''}")
    return ok


def place_window(window, x: int = 30, y: int = 15, width: int = 1250, height: int = 930) -> None:
    """把窗口挪到主屏并置顶（见文件头"踩过的坑"）。"""
    user32 = ctypes.windll.user32
    user32.SetWindowPos.argtypes = [
        ctypes.wintypes.HWND, ctypes.wintypes.HWND,
        ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_uint,
    ]
    user32.SetWindowPos.restype = ctypes.wintypes.BOOL
    user32.SetForegroundWindow.argtypes = [ctypes.wintypes.HWND]
    user32.SetForegroundWindow.restype = ctypes.wintypes.BOOL

    hwnd = window.element_info.handle
    user32.SetWindowPos(hwnd, ctypes.wintypes.HWND(-1), x, y, width, height, 0x0040)
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.8)
    emit(f"窗口位置 {window.rectangle()}")


def connect_port(window, port: str) -> bool:
    """选中串口并点「连接」。

    这里刻意全部走 UIA 模式（SelectionItem / InvokePattern），不用真实鼠标：
    实测机器上桌面会话偶尔不可用（SetCursorPos 报 "no active desktop"），
    一旦走鼠标，click_input 会直接抛异常整轮跑不完。Invoke 与 SetValue 不受影响。
    """
    combos = window.descendants(control_type="ComboBox")
    if len(combos) < 2:
        emit("未找到串口下拉框")
        return False

    if any("已连接" in (c.window_text() or "") for c in window.descendants(control_type="Text")):
        emit("串口已经处于连接状态，跳过连接步骤")
        return True

    combo = combos[1]
    try:
        combo.select(port)
    except Exception as exc:
        # pywinauto 的 select 会先展开下拉框，需要真实鼠标；桌面不可用时抛异常，
        # 但选中动作本身已经完成，后面用 Value 复核即可。
        emit(f"  select({port}) 报错（继续）: {exc}")
    time.sleep(0.8)
    selected = str(combo.legacy_properties().get("Value") or "").strip()
    if selected != port:
        emit(f"串口下拉框当前是 {selected!r}，不是 {port}")
        return False

    invoke_async(window.child_window(title="连接", control_type="Button"))
    for _ in range(50):
        time.sleep(0.5)
        texts = [(c.window_text() or "").strip() for c in window.descendants(control_type="Text")]
        if any("已连接" in text for text in texts):
            emit(f"已连接 {port}")
            return True
    emit("等待连接超时")
    return False


def grid_snapshot(window):
    """返回 (行号 → 参数名称, 可写行集合)。"""
    names: Dict[int, str] = {}
    for item in window.descendants(control_type="DataItem"):
        match = NAME_ROW.match(item.window_text() or "")
        if not match:
            continue
        try:
            value = str(item.legacy_properties().get("Value") or "").strip()
        except Exception:
            value = ""
        names[int(match.group(1))] = value

    writable = set()
    for button in window.descendants(control_type="Button"):
        match = ACTION_ROW.match(button.window_text() or "")
        if match:
            writable.add(int(match.group(1)))
    return names, writable


def value_of(window, row: int) -> str:
    for item in window.descendants(control_type="DataItem"):
        if (item.window_text() or "") == f"当前值 行 {row}":
            try:
                return str(item.legacy_properties().get("Value") or "").strip()
            except Exception:
                return ""
    return ""


def wait_dialog(pid: int, keyword: str, timeout: float = 8.0):
    """等一个模态对话框出现，返回 (hwnd, title)。

    **不再用 UIA 去解析对话框**：UIA 的 Invoke 是同步跨进程调用，应用 UI 线程进
    ShowDialog 后不返回，UIA provider 被这次未完成的调用占住 —— 此时对该进程的任何
    UIA 查询都会超时。实测对照（同一 hwnd、同一时刻）：
        win32 后端 0.00s 解析成功 / UIA 后端 COMError。
    所以对话框一律走 EnumWindows + win32 通道（见 win32_dlg）。
    """
    deadline = time.time() + timeout
    while time.time() < deadline:
        found = win32_dlg.find_top(pid, keyword)
        if found:
            return found
        time.sleep(0.25)
    return None


def invoke_dialog(window, index: int, pid: int, keyword: str, timeout: float = 10.0):
    """点第 index 行的「操作」按钮（UIA Invoke，后台线程）并等对话框。

    铁律：**Invoke 之后绝不能再对主窗体做 UIA 查询** —— 模态框挂着时必然超时，
    只会白白炸掉一轮（踩过好几次）。这里只碰 EnumWindows。
    """
    try:
        cells = scan_cells(window)
    except Exception as exc:
        emit(f"  主窗体 UIA 不可用，无法点按钮: {type(exc).__name__}: {exc}")
        return None
    action = cells.get(("操作", index))
    if action is None:
        emit(f"  第 {index} 行没有「操作」按钮")
        return None

    before = win32_dlg.top_titles(pid)
    step(f"Invoke「操作 行 {index}」（后台线程，不占 UIA）")
    invoke_async(action)

    deadline = time.time() + timeout
    while time.time() < deadline:
        for hwnd, p, title in win32_dlg.top_windows():
            if p == pid and title not in before:
                return hwnd, title
        time.sleep(0.25)
    emit(f"  顶层窗口: {win32_dlg.top_titles(pid)}")
    return None


def dlg_texts(hwnd: int) -> str:
    """对话框全部控件文本拼在一起（含编辑框内容、静态标签、按钮），用空格分隔。"""
    return " ".join(win32_dlg.child_texts(hwnd))


def dlg_click(hwnd: int, text: str) -> bool:
    """按文本找对话框按钮并 PostMessage(BM_CLICK)。

    不用真实鼠标：锁屏时 SetCursorPos 直接抛 RuntimeError；也不占 UIA。
    """
    button = win32_dlg.find_child(hwnd, text=text, kind="BUTTON")
    if button is None:
        emit(f"！对话框里没找到「{text}」按钮")
        return False
    returns = win32_dlg.post_click(button)
    step(f"点击「{text}」")
    return returns


def wait_next_dialog(pid: int, keyword: str, timeout: float = 10.0):
    """等下一个标题含 keyword 的新对话框（点确定后的确认框/结果框）。"""
    return wait_dialog(pid, keyword, timeout)


def close_stray_dialogs(pid: int) -> None:
    """收尾：把可能残留的模态对话框关掉，避免卡住主窗体（曾把主窗体卡死过一次）。"""
    for hwnd, owner_pid, title in win32_dlg.top_windows():
        if owner_pid != pid:
            continue
        if not any(k in title for k in ("写入", "确认", "执行", "成功", "失败", "无效")):
            continue
        if not dlg_click(hwnd, "取消"):
            win32_dlg.close_window(hwnd)
        time.sleep(0.4)


def shot_hwnd(hwnd: int, path: str) -> None:
    """给对话框截图。

    走 win32_dlg.capture_hwnd（PrintWindow）——**锁屏也能抓到真实内容**。
    不要改用 pywinauto 的 capture_as_image：它锁屏时抓回来的是锁屏壁纸，
    尺寸字节数都正常，极易被误当成有效证据。
    """
    if not win32_dlg.capture_hwnd(hwnd, path):
        emit(f"  截图 {path} 失败（hwnd={hwnd}，rect={win32_dlg.window_rect(hwnd)}）")
        return
    _total, colors = win32_dlg.capture_stats(path)
    emit(f"  截图已存 {path}（不同颜色数={colors}）")


def save_shot(control, path: str) -> None:
    """兼容旧调用点：接受 pywinauto 控件或裸 hwnd，统一走 PrintWindow。"""
    hwnd = getattr(control, "handle", control)
    shot_hwnd(int(hwnd), path)


def invoke_async(control, timeout: float = 0.0) -> bool:
    """在后台线程里 Invoke。

    UIA 的 Invoke 是同步调用：被点按钮若弹出模态对话框（ShowDialog），
    Invoke 会一直阻塞到对话框关闭；放在主线程会把整个脚本卡死，所以必须丢到后台线程。
    timeout > 0 时等待指定秒数并返回是否已返回。
    """
    done = {}

    def run():
        try:
            control.invoke()
            done["ok"] = True
        except Exception as exc:  # noqa: BLE001
            done["err"] = str(exc)

    thread = threading.Thread(target=run, daemon=True)
    thread.start()
    if timeout > 0:
        thread.join(timeout)
        return thread.is_alive()
    return True




def top_windows_of(pid: int):
    """属于被测进程的可见顶层窗口（含 ShowDialog 的模态窗体）。"""
    return [(hwnd, p, title) for hwnd, p, title in top_windows() if p == pid]


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", default="COM13")
    parser.add_argument("--imei", default="861187654321098")
    parser.add_argument("--shot-prefix", default="temp/shot7_writable")
    args = parser.parse_args(argv)

    step("连接主窗口")
    app = Application(backend="uia").connect(title_re=MAIN_TITLE, timeout=25)
    window = app.window(title_re=MAIN_TITLE)
    pid = app.process
    if window.is_minimized():
        window.restore()

    # 先收尾上一次跑崩留下的模态框：模态框挂着时主窗体的 UIA 查询会超时，
    # 不清理的话本脚本会在第一步就炸（踩过）。这样脚本才是幂等的、可反复重跑。
    step("清理残留模态对话框")
    stray = [t for t in win32_dlg.top_titles(pid) if t != window.window_text()]
    if stray:
        emit(f"发现残留: {stray}，逐个关闭")
        close_stray_dialogs(pid)
    else:
        emit("无残留")

    set_topmost(window, True)
    place_window(window)

    step("连接串口")
    if not connect_port(window, args.port):
        return 1
    time.sleep(6)

    step("读取表格快照")
    names, writable = grid_snapshot(window)
    emit(f"表格共 {len(names)} 行，可写行 {len(writable)} 行")
    emit()

    # ---------- 需求1：405xx 可写 ----------
    step("检查需求1：405xx 可写")
    emit("--- 需求1：405xx 读写信息段提供写入入口 ---")
    rows: Dict[str, int] = {}
    for label, keyword in WRITABLE_KEYWORDS.items():
        row = next((i for i, n in sorted(names.items()) if keyword in n), None)
        if row is None:
            check(f"{label} 行存在", False, "未找到该参数名")
            continue
        rows[keyword] = row
        check(f"{label} 行存在", True, f"行{row}")
        check(f"{label} 可写（有「操作」按钮）", row in writable,
              f"操作按钮{'存在' if row in writable else '缺失'}")
    emit()

    # ---------- 需求2：OTA / Flash 只读 ----------
    step("检查需求2：OTA/Flash 只读")
    emit("--- 需求2：4G_OTA 与 Flash 持久化段改为只读 ---")
    for keyword in READONLY_KEYWORDS:
        row = next((i for i, n in sorted(names.items()) if keyword in n), None)
        if row is None:
            check(f"「{keyword}」行存在", False, "未找到该参数名")
            continue
        check(f"「{keyword}」只读（无「操作」按钮）", row not in writable,
              f"行{row}{'，但仍有操作按钮' if row in writable else ''}")
    emit()

    # ---------- 需求3：41101~43006 整段移除 ----------
    step("检查需求3：设备段已移除")
    emit("--- 需求3：41101~43006 从机设备段整段移除 ---")
    for keyword in REMOVED_KEYWORDS:
        hits = [name for name in names.values() if keyword in name]
        check(f"表格中不应出现「{keyword}」", not hits, f"命中 {len(hits)} 处" if hits else "")
    emit()

    # ---------- 回归 ----------
    step("检查回归：保留项")
    emit("--- 回归：保留项仍在 ---")
    for keyword in KEPT_KEYWORDS:
        hits = [name for name in names.values() if keyword in name]
        check(f"「{keyword}」仍显示", bool(hits), f"命中 {len(hits)} 处")
    for keyword in ("是否进入单板产测", "是否进入工厂产测模式", "单板产测密码读写段"):
        hits = [name for name in names.values() if keyword in name]
        check(f"「{keyword}」仍不显示", not hits, f"命中 {len(hits)} 处" if hits else "")
    emit()

    # ---------- 需求5：40510 写入对话框的可选值 ----------
    step("需求5：40510 写入对话框")
    emit("--- 需求5：40510「室外机激活」写入对话框列出可选值 ---")
    row_activate = rows.get("室外机激活")
    if row_activate is None:
        check("找到 40510 行", False)
    else:
        check("40510 行可写按钮在 UIA 中可用",
              scan_cells(window).get(("操作", row_activate)) is not None)
        step("点击 40510 的写入按钮")
        found = invoke_dialog(window, row_activate, pid, "写入参数", timeout=10.0)
        check("弹出「写入参数」对话框", found is not None)
        if found is not None:
            hwnd, title = found
            texts = win32_dlg.child_texts(hwnd)
            joined = " ".join(texts)
            emit(f"对话框 {title!r}（win32 读到 {len(texts)} 条）:")
            for t in texts:
                emit(f"    - {t}")
            for expected in ("未激活", "允许使用", "禁止使用"):
                check(f"对话框列出「{expected}」", expected in joined)
            check("对话框列出允许范围 0～2", "0～2" in joined)
            check("对话框标出寄存器号 [40510]", "[40510]" in joined)
            check("对话框给出当前值 0x0001", "0x0001" in joined)
            shot_hwnd(hwnd, f"{args.shot_prefix}_40510_dialog.png")
            step("关闭 写入参数 对话框")
            dlg_click(hwnd, "取消")
            time.sleep(1.5)
            check("对话框已关闭", win32_dlg.find_top(pid, "写入参数") is None)
    emit()

    # ---------- 需求6：40501 整段写入 IMEI（全流程） ----------
    step("需求6：40501 整段写入")
    emit("--- 需求6：40501 IMEI 整段 0x10 写入（录入→确认→写入→回读） ---")
    row_imei = rows.get("唯一识别码")
    target = args.imei  # 实际写入值，可能因"预填值冲突"被自动换掉（见下）
    if row_imei is None:
        check("找到 40501 行", False)
    else:
        check("40501 行可写按钮在 UIA 中可用",
              scan_cells(window).get(("操作", row_imei)) is not None)
        step("点击 40501 的写入按钮")
        found = invoke_dialog(window, row_imei, pid, "写入 唯一识别码", timeout=10.0)
        check("弹出「写入 唯一识别码」整段写入对话框", found is not None)
        if found is not None:
            hwnd, title = found
            texts = win32_dlg.child_texts(hwnd)
            joined = " ".join(texts)
            emit(f"对话框 {title!r}（win32 读到 {len(texts)} 条）:")
            for t in texts:
                emit(f"    - {t.replace(chr(10), ' / ')}")
            check("提示含 9 个寄存器", "9 个寄存器" in joined)
            check("提示含 18 字节", "18 字节" in joined)
            check("提示 0x10 整段写入", "0x10" in joined)

            step(f"填入 IMEI {args.imei}")
            edit = win32_dlg.find_child(hwnd, kind="EDIT")
            check("对话框有输入框", edit is not None)
            prefilled = win32_dlg.get_text(edit).strip() if edit is not None else ""
            emit(f"  输入框预填值 = {prefilled!r}")
            # 预填值就是设备当前值。若它恰好等于待写入值，本次"写入"就观测不到任何变化，
            # 验证会退化成同义反复 —— 所以自动换一个末位不同的合法 IMEI。
            target = args.imei
            if prefilled == target:
                target = target[:-1] + ("8" if target[-1] != "8" else "7")
                emit(f"  预填值恰等于待写入值，自动改用 {target} 以保证写入有可观测变化")
            check(f"写入前后取值不同（{prefilled!r} → {target!r}）", prefilled != target)
            if edit is not None:
                # 写-读-校验循环：WinForms 对话框可能在 Shown/Load 里再设一次默认值，
                # 与我们的写入抢时序；且跨进程读文本必须走 WM_GETTEXT（见 win32_dlg 注释）。
                filled = win32_dlg.set_text_verified(edit, target).strip()
                check(f"输入框已填入 {target}", filled == target, f"实际={filled!r}")

            shot_hwnd(hwnd, f"{args.shot_prefix}_40501_dialog.png")

            step("提交整段写入对话框")
            dlg_click(hwnd, "确定")
            time.sleep(1.0)
            check("写入对话框已关闭", win32_dlg.find_top(pid, "写入 唯一识别码") is None)

            step("等待高危二次确认框")
            confirm = wait_dialog(pid, "确认写入", timeout=10.0)
            check("弹出高危二次确认", confirm is not None)
            if confirm is not None:
                hwnd2, title2 = confirm
                ctext = dlg_texts(hwnd2)
                emit(f"确认框 {title2!r}: " + ctext.replace("\n", " / ")[:400])
                check("确认框给出寄存器区间 40501~40509", "40501~40509" in ctext)
                check("确认框含「高危段」警示", "高危段" in ctext)
                check("确认框注明 9 个寄存器", "9 个寄存器" in ctext)
                check(f"确认框回显 IMEI {target}", target in ctext)
                shot_hwnd(hwnd2, f"{args.shot_prefix}_40501_confirm.png")

                step("确认写入（真写 0x10）")
                dlg_click(hwnd2, "确定")
                time.sleep(2.5)

                step("等待写入成功提示")
                success = wait_dialog(pid, "写入成功", timeout=15.0)
                check("弹出「写入成功」并给出回读值", success is not None)
                if success is not None:
                    hwnd3, title3 = success
                    otext = dlg_texts(hwnd3)
                    emit(f"成功框 {title3!r}: " + otext.replace("\n", " / ")[:300])
                    check(f"回读值含写入的 IMEI {target}", target in otext)
                    shot_hwnd(hwnd3, f"{args.shot_prefix}_40501_success.png")
                    dlg_click(hwnd3, "确定")
                    time.sleep(4.0)

                step("复核表格当前值")
                # 对话框全部关掉后 UIA 才恢复可用，这时才扫主窗体
                check("所有模态框已关闭",
                      not [t for t in win32_dlg.top_titles(pid) if "写入" in t or "确认" in t])
                current = value_of(window, row_imei)
                check(f"表格当前值已刷新为新 IMEI {target}", current == target,
                      f"当前值={current!r}")
    emit()

    step("整表截图")
    save_shot(window, f"{args.shot_prefix}_grid.png")
    close_stray_dialogs(pid)

    failed = [line for line in CHECKS if line.startswith("[FAIL]")]
    total = len([c for c in CHECKS if c.startswith("[PASS]") or c.startswith("[FAIL]")])
    emit()
    emit(f"合计 {total} 项，失败 {len(failed)} 项")
    locked = win32_dlg.desktop_locked()
    emit(f"桌面会话: {'已锁屏' if locked else '可用'}"
         f"（LockApp/LogonUI pid={win32_dlg.lock_owner_pid()}）"
         f"；截图走 PrintWindow，锁屏不影响取证")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
