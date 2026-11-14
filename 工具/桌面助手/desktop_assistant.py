"""Local, explicit-window desktop helper; never accesses guest credentials."""
from __future__ import annotations

import argparse
import ctypes
import os
from pathlib import Path
import sys

BASE = Path(__file__).resolve().parent
VENDOR = BASE / "vendor"
_DLL_DIRS = []
for directory in (VENDOR, VENDOR / "win32", VENDOR / "win32" / "lib", VENDOR / "pythonwin", VENDOR / "pywin32_system32"):
    if directory.is_dir():
        sys.path.insert(0, str(directory))
        if hasattr(os, "add_dll_directory"):
            _DLL_DIRS.append(os.add_dll_directory(str(directory)))


def deps():
    try:
        from pywinauto import Desktop, keyboard
        import pywinauto
    except ImportError as exc:
        raise RuntimeError("本地依赖不可用；请参阅 README，不要全局安装。") from exc
    return Desktop, keyboard, pywinauto.__version__


def check_window(pid: int, handle: int):
    from ctypes import wintypes
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    user32.IsWindow.argtypes = [wintypes.HWND]
    user32.IsWindowVisible.argtypes = [wintypes.HWND]
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    actual = wintypes.DWORD()
    if not user32.IsWindow(handle) or not user32.IsWindowVisible(handle):
        raise RuntimeError("窗口不存在或不可见。")
    if not user32.GetWindowThreadProcessId(handle, ctypes.byref(actual)) or actual.value != pid:
        raise RuntimeError("窗口所属进程与指定 PID 不一致。")
    return user32


def main():
    parser = argparse.ArgumentParser(description="本地桌面助手：必须明确指定进程和窗口。")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("status", help="检查本地依赖")
    listing = sub.add_parser("windows", help="列出指定进程的可见窗口")
    listing.add_argument("--pid", type=int, required=True)
    for name in ("inspect", "invoke", "keys", "click"):
        part = sub.add_parser(name)
        part.add_argument("--pid", type=int, required=True)
        part.add_argument("--handle", type=lambda v: int(v, 0), required=True)
        if name == "inspect":
            part.add_argument("--limit", type=int, default=100)
        if name == "invoke":
            selector = part.add_mutually_exclusive_group(required=True)
            selector.add_argument("--automation-id")
            selector.add_argument("--name")
            part.add_argument("--execute", action="store_true")
        if name == "keys":
            part.add_argument("--keys", required=True, help="pywinauto 键盘语法；不要传入密码")
            part.add_argument("--scan-codes", action="store_true", help="使用按键扫描码；可能受输入法影响")
            part.add_argument("--execute", action="store_true")
        if name == "click":
            part.add_argument("--x", type=int, required=True, help="距离窗口左侧的像素")
            part.add_argument("--y", type=int, required=True, help="距离窗口顶部的像素")
            part.add_argument("--execute", action="store_true")
    args = parser.parse_args()
    Desktop, keyboard, version = deps()
    if args.command == "status":
        print(f"pywinauto {version}；本地依赖 {VENDOR}")
        return 0
    if args.command == "windows":
        windows = Desktop(backend="win32").windows(process=args.pid, visible_only=True)
        for item in windows:
            print(f"pid={args.pid} handle={item.handle} title={item.window_text()}")
        print(f"可见窗口数：{len(windows)}")
        return 0
    user32 = check_window(args.pid, args.handle)
    root = Desktop(backend="uia").window(handle=args.handle)
    if args.command == "inspect":
        if not 1 <= args.limit <= 500:
            raise ValueError("limit 必须在 1 到 500 之间。")
        descendants = root.descendants()
        for item in descendants[:args.limit]:
            info = item.element_info
            print(f"type={info.control_type} id={info.automation_id!r} name={info.name!r}")
        print(f"控件数：{len(descendants)}；最多显示 {args.limit} 项")
        return 0
    if not args.execute:
        raise RuntimeError("操作未执行；必须明确指定 --execute。")
    check_window(args.pid, args.handle)
    if args.command == "invoke":
        items = [x for x in root.descendants() if (x.element_info.automation_id == args.automation_id if args.automation_id is not None else x.element_info.name == args.name)]
        if len(items) != 1:
            raise RuntimeError(f"控件匹配数为 {len(items)}，无法安全操作。")
        items[0].invoke()
        print("已请求控件执行；请检查实际结果。")
        return 0
    user32.GetForegroundWindow.restype = ctypes.c_void_p
    if user32.GetForegroundWindow() != args.handle:
        raise RuntimeError("目标窗口不是前台窗口；没有发送输入。")
    if args.command == "click":
        from ctypes import wintypes
        from pywinauto import mouse
        rectangle = wintypes.RECT()
        point = wintypes.POINT()
        user32.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
        user32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
        user32.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
        if not user32.GetWindowRect(args.handle, ctypes.byref(rectangle)):
            raise RuntimeError("无法获取窗口坐标。")
        width, height = rectangle.right - rectangle.left, rectangle.bottom - rectangle.top
        if not (0 <= args.x < width and 0 <= args.y < height):
            raise ValueError("点击坐标不在目标窗口内。")
        x, y = rectangle.left + args.x, rectangle.top + args.y
        if not user32.SetCursorPos(x, y) or not user32.GetCursorPos(ctypes.byref(point)) or (point.x, point.y) != (x, y):
            raise RuntimeError("鼠标未到达指定位置；没有点击。")
        if user32.GetForegroundWindow() != args.handle:
            raise RuntimeError("前台窗口发生变化；没有点击。")
        user32.WindowFromPoint.argtypes = [wintypes.POINT]
        user32.WindowFromPoint.restype = wintypes.HWND
        user32.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]
        user32.GetAncestor.restype = wintypes.HWND
        under_pointer = user32.WindowFromPoint(point)
        if not under_pointer or user32.GetAncestor(under_pointer, 2) != args.handle:
            raise RuntimeError("点击位置被其他窗口覆盖；没有点击。")
        mouse.click(coords=(x, y))
        print("已发送鼠标点击；请检查实际结果。")
        return 0
    keyboard.send_keys(args.keys, with_spaces=True, pause=0.02, vk_packet=not args.scan_codes)
    print("已发送按键；是否被目标接收须另行验证。")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"桌面助手未完成：{exc}", file=sys.stderr)
        raise SystemExit(1)
