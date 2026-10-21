#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""纯 Win32 通道的模态对话框操作helper（不依赖 UIA、不依赖鼠标）。

为什么需要它：
  用 UIA 的 InvokePattern.Invoke 去点「操作」列按钮时，调用是**同步跨进程**的 ——
  应用 UI 线程进入 ShowDialog 后不返回，UIA provider 被这次未完成的调用占住，
  于是对该进程的**任何 UIA 查询**都会超时（解析刚弹出的对话框、扫描主窗体全都不行）。
  实测对照：同一个 hwnd，win32 后端 0.00s 解析成功，UIA 后端抛 COMError。

  而 Win32 通道不受影响：对话框的嵌套消息循环照常 pump 消息，
  EnumWindows / EnumChildWindows / GetWindowText / PostMessage 全部正常。

  另一个好处：全程不需要交互桌面。真实鼠标点击（click_input）在会话锁屏时会抛
  `RuntimeError: There is no active desktop required for moving mouse cursor!`，
  而 UIA Invoke + Win32 读文本在锁屏下依然可用。

用法：
    from win32_dlg import top_windows, find_top, children, find_child, post_click, set_text
"""

from __future__ import annotations

import ctypes
import time
from ctypes import wintypes
from typing import List, Optional, Tuple

user32 = ctypes.windll.user32

WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

user32.EnumWindows.argtypes = [WNDENUMPROC, wintypes.LPARAM]
user32.EnumWindows.restype = wintypes.BOOL
user32.EnumChildWindows.argtypes = [wintypes.HWND, WNDENUMPROC, wintypes.LPARAM]
user32.EnumChildWindows.restype = wintypes.BOOL
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.IsWindowVisible.restype = wintypes.BOOL
user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
user32.GetWindowTextLengthW.restype = ctypes.c_int
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.restype = ctypes.c_int
user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetClassNameW.restype = ctypes.c_int
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetWindowThreadProcessId.restype = wintypes.DWORD
user32.IsWindow.argtypes = [wintypes.HWND]
user32.IsWindow.restype = wintypes.BOOL
user32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.PostMessageW.restype = wintypes.BOOL
# 第 4 个参数按 LPARAM(整数) 声明的话，WM_SETTEXT 传字符串指针会抛
# ArgumentError；声明成 c_void_p 才能既传 0 又传指针（实测：声明成 LPARAM 时
# SendMessage 根本发不出去，编辑框文本不会变，而返回值也不报错，很难发现）。
user32.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, ctypes.c_void_p]
user32.SendMessageW.restype = ctypes.c_ssize_t

WM_CLOSE = 0x0010
WM_SETTEXT = 0x000C
WM_GETTEXT = 0x000D
WM_GETTEXTLENGTH = 0x000E
WM_COMMAND = 0x0111
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
BM_CLICK = 0x00F5
VK_RETURN = 0x0D
VK_ESCAPE = 0x1B

# WinForms 控件的类名形如 WindowsForms10.STATIC.app.0.378734a_r3_ad1，
# 第二个点后面那截才是控件类型。
__all__ = [
    "top_windows", "top_windows_of", "find_top", "children", "find_child",
    "child_texts", "post_click", "set_text", "set_text_verified", "get_text",
    "close_window",
    "wait_top", "wait_new_top", "top_titles", "control_kind",
    "desktop_locked", "lock_owner_pid", "image_name",
    "window_rect", "capture_hwnd", "capture_stats",
]


def _text(hwnd: int) -> str:
    n = user32.GetWindowTextLengthW(hwnd)
    if n <= 0:
        return ""
    buf = ctypes.create_unicode_buffer(n + 1)
    user32.GetWindowTextW(hwnd, buf, n + 1)
    return buf.value


def _class(hwnd: int) -> str:
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def _pid(hwnd: int) -> int:
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def top_windows(visible_only: bool = True) -> List[Tuple[int, int, str]]:
    """[(hwnd, pid, title)]，顶层窗口按 Z 序。"""
    out: List[Tuple[int, int, str]] = []

    @WNDENUMPROC
    def cb(hwnd, _lparam):
        if visible_only and not user32.IsWindowVisible(hwnd):
            return True
        title = _text(hwnd)
        if title:
            out.append((hwnd, _pid(hwnd), title))
        return True

    user32.EnumWindows(cb, 0)
    return out


def top_windows_of(pid: int) -> List[Tuple[int, int, str]]:
    return [w for w in top_windows() if w[1] == pid]


def top_titles(pid: Optional[int] = None) -> List[str]:
    return [t for _h, _p, t in (top_windows_of(pid) if pid is not None else top_windows())]


def find_top(pid: int, keyword: str) -> Optional[Tuple[int, str]]:
    """在指定进程里按标题关键字找一个顶层窗口，返回 (hwnd, title)。"""
    for hwnd, p, title in top_windows():
        if p == pid and keyword in title:
            return hwnd, title
    return None


def wait_top(pid: int, keyword: str, timeout: float = 8.0) -> Optional[Tuple[int, str]]:
    deadline = time.time() + timeout
    while time.time() < deadline:
        found = find_top(pid, keyword)
        if found:
            return found
        time.sleep(0.25)
    return None


def wait_new_top(pid: int, before: List[str], timeout: float = 8.0) -> Optional[Tuple[int, str]]:
    """等一个**新增的**顶层窗口（用于「点确定后弹出的下一个对话框」）。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        for hwnd, p, title in top_windows():
            if p == pid and title not in before:
                return hwnd, title
        time.sleep(0.25)
    return None


def children(hwnd: int) -> List[Tuple[int, str, str]]:
    """[(child_hwnd, class_name, text)]，递归枚举（EnumChildWindows 本身即递归）。"""
    out: List[Tuple[int, str, str]] = []

    @WNDENUMPROC
    def cb(h, _lparam):
        out.append((h, _class(h), _text(h)))
        return True

    user32.EnumChildWindows(hwnd, cb, 0)
    return out


def control_kind(class_name: str) -> str:
    """把 WindowsForms10.XXX.app... 归成 EDIT / BUTTON / STATIC / COMBOBOX / LISTBOX。"""
    parts = class_name.split(".")
    return parts[1].upper() if len(parts) > 1 else class_name.upper()


def child_texts(hwnd: int) -> List[str]:
    """对话框里所有非空控件文本（含编辑框里的当前内容）。"""
    return [t for _h, _c, t in children(hwnd) if t]


def find_child(hwnd: int, text: Optional[str] = None, kind: Optional[str] = None,
               exact: bool = False, nth: int = 0) -> Optional[int]:
    """按文本/控件类型找子控件句柄。text 默认做 contains 匹配。"""
    hits = []
    for h, cls, t in children(hwnd):
        if kind is not None and control_kind(cls) != kind.upper():
            continue
        if text is not None:
            if (t == text) if exact else (text in t):
                pass
            else:
                continue
        hits.append(h)
    if not hits:
        return None
    return hits[nth] if nth < len(hits) else None


def post_click(hwnd: int) -> bool:
    """PostMessage(BM_CLICK) —— 不阻塞、不需要真实鼠标、不需要前台焦点。"""
    return bool(user32.PostMessageW(hwnd, BM_CLICK, 0, 0))


def set_text(hwnd: int, text: str) -> bool:
    """WM_SETTEXT 直接给编辑框赋值（同步，靠对话框的嵌套消息循环处理）。

    比 UIA 的 ValuePattern 更可靠：模态框挂着时 UIA provider 被占住，ValuePattern 也会超时。
    """
    buf = ctypes.c_wchar_p(text)
    return user32.SendMessageW(hwnd, WM_SETTEXT, 0, ctypes.cast(buf, ctypes.c_void_p)) != 0


def get_text(hwnd: int) -> str:
    """**跨进程**读控件文本，必须走 WM_GETTEXT。

    踩坑记录：`GetWindowTextW` 对**别的进程**的控件不保证实时 —— 它读的是窗口管理器
    里的缓存标题，跨进程时不会去问目标控件要文本。表现就是：`WM_SETTEXT` 明明写成功
    （返回值非 0、后续确认框里也是新值），紧接着用 `GetWindowTextW` 读回来却还是旧值，
    于是把一次成功的写入误判成失败。
    WM_GETTEXT 是显式让目标控件把文本拷进我们给的缓冲区，跨进程可靠。
    """
    length = user32.SendMessageW(hwnd, WM_GETTEXTLENGTH, 0, None)
    if length <= 0:
        return ""
    buf = ctypes.create_unicode_buffer(length + 1)
    user32.SendMessageW(hwnd, WM_GETTEXT, length + 1,
                        ctypes.cast(buf, ctypes.c_void_p))
    return buf.value


def set_text_verified(hwnd: int, text: str, attempts: int = 5,
                      delay: float = 0.25) -> str:
    """写入并按 WM_GETTEXT 回读校验，写入+回读都返回最终值。

    WinForms 对话框在 `Shown`/`Load` 里可能还会再设一次默认值，与我们的写入抢时序；
    所以这里写成"写-读-不一致就重写"的幂等循环，而不是写完就信。
    """
    for _ in range(max(1, attempts)):
        set_text(hwnd, text)
        time.sleep(delay)
        current = get_text(hwnd)
        if current == text:
            return current
    return get_text(hwnd)


def close_window(hwnd: int) -> bool:
    return bool(user32.PostMessageW(hwnd, WM_CLOSE, 0, 0))


# ---------------------------------------------------------------------------
# 桌面会话状态：锁屏时窗口**不渲染**，PrintWindow/capture_as_image 抓回来的其实是
# 锁屏壁纸 —— 尺寸、字节数都正常，看着像"截到了窗口"，实际全是废图
# （踩过：一度把锁屏壁纸当成对话框截图存证）。
#
# 判据用"LockApp.exe / LogonUI.exe 是否在跑"：
#   * 不能用"本线程桌面名 vs 输入桌面名"比对 —— 现代 Windows 的锁屏（LockApp）
#     就跑在 Default 桌面上，两边名字都是 Default，比对永远返回"没锁"（踩过）。
#   * 也不该用 SetCursorPos 是否失败来探 —— 那是副作用式探测，还会真移鼠标。
# ---------------------------------------------------------------------------

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
kernel32 = ctypes.windll.kernel32
kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.QueryFullProcessImageNameW.argtypes = [
    wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD),
]
kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CloseHandle.restype = wintypes.BOOL

LOCK_OWNER_IMAGES = ("lockapp.exe", "logonui.exe")


def image_name(pid: int) -> str:
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return ""
    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = wintypes.DWORD(len(buf))
        if kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
            return buf.value
        return ""
    finally:
        kernel32.CloseHandle(handle)


def _window_pids(visible_only: bool = False) -> set:
    """所有顶层窗口（含无标题）的进程 id 集合。"""
    out = set()

    @WNDENUMPROC
    def cb(hwnd, _lparam):
        if not visible_only or user32.IsWindowVisible(hwnd):
            out.add(_pid(hwnd))
        return True

    user32.EnumWindows(cb, 0)
    out.discard(0)
    return out


def lock_owner_pid() -> int:
    """返回 LockApp/LogonUI 的 pid（0 表示没锁屏），便于日志里说明原因。

    注意：锁屏窗口（LockApp 的 `Windows.UI.Core.CoreWindow`）**不在 EnumWindows 的
    枚举结果里**（它属于输入桌面），所以除了枚举本桌面的顶层窗口，还必须单独看
    GetForegroundWindow —— 锁屏时它就是 LockApp 的窗口（实测踩过：只枚举窗口会漏判，
    结果把锁屏壁纸当成了对话框截图）。
    """
    pids = set(_window_pids())
    pids.add(_pid(user32.GetForegroundWindow()))
    pids.discard(0)
    for pid in pids:
        if image_name(pid).lower().endswith(LOCK_OWNER_IMAGES):
            return pid
    return 0


def desktop_locked() -> bool:
    """会话是否锁屏。

    锁屏的影响**仅限于真实鼠标**（SetCursorPos / click_input 会失败）。
    UIA 的 Invoke、Win32 的消息、以及下面基于 PrintWindow 的截图都照常可用。
    """
    return lock_owner_pid() != 0


# ---------------------------------------------------------------------------
# 窗口截图：PrintWindow 让**目标窗口自己**往我们提供的 DC 重绘。
#
# 实测结论（与直觉相反，值得记牢）：
#   * 锁屏时 PrintWindow **依然能拿到真实 UI 内容** —— 它走窗口自身的绘制路径，
#     不读屏幕像素。实测：会话锁屏、LockApp 是前台窗口时，抓 WorkBuddy 窗口
#     拿到了完整的浅色主题正文（10009 种颜色、白色为主），不是壁纸。
#   * 反过来，pywinauto 的 `capture_as_image()` 在锁屏下抓回来的是**锁屏壁纸**
#     —— 尺寸、字节数都正常，极具迷惑性（一度把 Bing 壁纸当成对话框截图存证）。
#   所以：截图一律走这里，不要再用 capture_as_image。
# ---------------------------------------------------------------------------

gdi32 = ctypes.windll.gdi32

PW_RENDERFULLCONTENT = 0x00000002
SRCCOPY = 0x00CC0020
DIB_RGB_COLORS = 0

user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.GetWindowRect.restype = wintypes.BOOL
user32.GetWindowDC.argtypes = [wintypes.HWND]
user32.GetWindowDC.restype = wintypes.HDC
user32.ReleaseDC.argtypes = [wintypes.HWND, wintypes.HDC]
user32.ReleaseDC.restype = ctypes.c_int
user32.PrintWindow.argtypes = [wintypes.HWND, wintypes.HDC, wintypes.UINT]
user32.PrintWindow.restype = wintypes.BOOL

gdi32.CreateCompatibleDC.argtypes = [wintypes.HDC]
gdi32.CreateCompatibleDC.restype = wintypes.HDC
gdi32.CreateCompatibleBitmap.argtypes = [wintypes.HDC, ctypes.c_int, ctypes.c_int]
gdi32.CreateCompatibleBitmap.restype = wintypes.HBITMAP
gdi32.SelectObject.argtypes = [wintypes.HDC, wintypes.HGDIOBJ]
gdi32.SelectObject.restype = wintypes.HGDIOBJ
# 显式声明成 c_void_p：位图 bits 缓冲区与 BITMAPINFO 都是指针，
# 不声明会在 64 位下被当 32 位 int 截断（同 SetWindowPos 的坑）。
gdi32.GetDIBits.argtypes = [
    wintypes.HDC, wintypes.HBITMAP, wintypes.UINT, wintypes.UINT,
    ctypes.c_void_p, ctypes.c_void_p, wintypes.UINT,
]
gdi32.GetDIBits.restype = ctypes.c_int
gdi32.DeleteObject.argtypes = [wintypes.HGDIOBJ]
gdi32.DeleteObject.restype = wintypes.BOOL
gdi32.DeleteDC.argtypes = [wintypes.HDC]
gdi32.DeleteDC.restype = wintypes.BOOL


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG), ("biHeight", wintypes.LONG),
        ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD), ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", wintypes.LONG), ("biYPelsPerMeter", wintypes.LONG),
        ("biClrUsed", wintypes.DWORD), ("biClrImportant", wintypes.DWORD),
    ]


def window_rect(hwnd: int) -> Tuple[int, int, int, int]:
    """(left, top, right, bottom)，**屏幕坐标**（可能为负，窗口在副屏或已移出可视区）。"""
    r = wintypes.RECT()
    if not user32.GetWindowRect(hwnd, ctypes.byref(r)):
        return 0, 0, 0, 0
    return r.left, r.top, r.right, r.bottom


def capture_hwnd(hwnd: int, path: str) -> bool:
    """把窗口内容存成 PNG，成功返回 True。锁屏下同样可用（见上方说明）。"""
    left, top, right, bottom = window_rect(hwnd)
    width, height = right - left, bottom - top
    if width <= 0 or height <= 0:
        return False

    src_dc = user32.GetWindowDC(hwnd)
    mem_dc = gdi32.CreateCompatibleDC(src_dc)
    bmp = gdi32.CreateCompatibleBitmap(src_dc, width, height)
    old = gdi32.SelectObject(mem_dc, bmp)
    try:
        # PrintWindow 返回值在部分 DWM 场景下不可靠，所以不拿它当成功判据，
        # 抓完直接看像素缓冲区。
        user32.PrintWindow(hwnd, mem_dc, PW_RENDERFULLCONTENT)

        header = BITMAPINFOHEADER()
        header.biSize = ctypes.sizeof(BITMAPINFOHEADER)
        header.biWidth = width
        header.biHeight = -height  # 负高度 = 自上而下，省掉一次翻转
        header.biPlanes = 1
        header.biBitCount = 32
        header.biCompression = 0

        buf = ctypes.create_string_buffer(width * height * 4)
        lines = gdi32.GetDIBits(mem_dc, bmp, 0, height, buf,
                                ctypes.byref(header), DIB_RGB_COLORS)
        if lines != height:
            return False

        from PIL import Image  # 延迟导入：只有真要截图时才需要 Pillow

        image = Image.frombuffer("RGBA", (width, height), buf, "raw", "BGRA", 0, 1)
        image.convert("RGB").save(path)
        return True
    finally:
        gdi32.SelectObject(mem_dc, old)
        gdi32.DeleteObject(bmp)
        gdi32.DeleteDC(mem_dc)
        user32.ReleaseDC(hwnd, src_dc)


def capture_stats(path: str) -> Tuple[int, int]:
    """(总像素数, 不同颜色数)，用于判断截图是不是"废图"（纯色/壁纸）。"""
    from PIL import Image

    image = Image.open(path).convert("RGB")
    colors = image.getcolors(maxcolors=image.width * image.height + 1) or []
    return image.width * image.height, len(colors)
