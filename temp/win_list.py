#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""列出所有可见的顶层窗口（句柄/进程/标题/位置），排查窗口是否被遮挡或最小化。"""

from __future__ import annotations

import ctypes
import sys
from ctypes import wintypes

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

user32 = ctypes.windll.user32

WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
user32.GetWindowTextLengthW.restype = ctypes.c_int
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]


def main() -> int:
    results = []

    def callback(hwnd, _lparam):
        length = user32.GetWindowTextLengthW(hwnd)
        if length <= 0:
            return True
        buffer = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buffer, length + 1)
        if not user32.IsWindowVisible(hwnd):
            return True
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        rect = wintypes.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
        results.append((buffer.value, int(pid.value), int(hwnd),
                        (rect.left, rect.top, rect.right, rect.bottom)))
        return True

    user32.EnumWindows(WNDENUMPROC(callback), 0)

    keyword = sys.argv[1] if len(sys.argv) > 1 else "W_TB_MS"
    for title, pid, hwnd, rect in results:
        mark = "  <== 目标" if keyword in title else ""
        print(f"pid={pid:<7} hwnd={hwnd:<9} rect={rect}  {title!r}{mark}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
