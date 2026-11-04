"""Isolated Windows UI Automation worker, using only the operating system COM API."""
from __future__ import annotations

import ctypes
import json
import sys
import uuid
from contextlib import ExitStack
from ctypes import wintypes

import psutil


class GUID(ctypes.Structure):
    _fields_ = [("data1", wintypes.DWORD), ("data2", wintypes.WORD), ("data3", wintypes.WORD),
                ("data4", ctypes.c_ubyte * 8)]

    @classmethod
    def parse(cls, value: str) -> GUID:
        return cls.from_buffer_copy(uuid.UUID(value).bytes_le)


def main() -> None:
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    request = json.load(sys.stdin)
    owner_pid = int(request["pid"])
    created_at = float(request["created_at"])
    ole = ctypes.OleDLL("ole32")
    ole.CoInitializeEx.argtypes = [ctypes.c_void_p, wintypes.DWORD]
    ole.CoCreateInstance.argtypes = [ctypes.POINTER(GUID), ctypes.c_void_p, wintypes.DWORD,
                                     ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p)]
    ole.CoUninitialize.argtypes = []
    ole.CoInitializeEx(None, 0)
    user = ctypes.WinDLL("user32", use_last_error=True)
    user.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user.GetWindowTextLengthW.argtypes = [wintypes.HWND]
    user.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    user.IsWindow.argtypes = [wintypes.HWND]
    user.EnumWindows.argtypes = [ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM), wintypes.LPARAM]
    oleaut = ctypes.WinDLL("oleaut32")
    oleaut.SysFreeString.argtypes = [ctypes.c_void_p]

    def identity() -> None:
        if psutil.Process(owner_pid).create_time() != created_at:
            raise RuntimeError("目标进程身份已变化")

    def method(obj, index, *argtypes):
        table = ctypes.cast(obj, ctypes.POINTER(ctypes.POINTER(ctypes.c_void_p))).contents
        return ctypes.WINFUNCTYPE(ctypes.HRESULT, ctypes.c_void_p, *argtypes)(table[index])

    def call(obj, index, argtypes, *args):
        result = method(obj, index, *argtypes)(obj, *args)
        if result < 0:
            raise OSError(result, "UI Automation 调用失败")

    def release(obj):
        if obj and obj.value:
            method(obj, 2)(obj)

    def number(element, index):
        value = ctypes.c_int()
        call(element, index, [ctypes.POINTER(ctypes.c_int)], ctypes.byref(value))
        return value.value

    def string(element, index):
        value = ctypes.c_void_p()
        call(element, index, [ctypes.POINTER(ctypes.c_void_p)], ctypes.byref(value))
        try:
            return ctypes.wstring_at(value.value) if value.value else ""
        finally:
            oleaut.SysFreeString(value)

    try:
        identity()
        windows = []

        @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        def visit(hwnd, _):
            pid = wintypes.DWORD()
            user.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            if pid.value == owner_pid:
                text = ctypes.create_unicode_buffer(user.GetWindowTextLengthW(hwnd) + 1)
                user.GetWindowTextW(hwnd, text, len(text))
                if text.value == request["window_title"]:
                    windows.append(hwnd)
            return True

        if not user.EnumWindows(visit, 0):
            raise ctypes.WinError(ctypes.get_last_error())
        if not windows and not request["invoke"]:
            print(json.dumps({"window_found": False, "controls": []}))
            return
        if len(windows) != 1:
            raise RuntimeError("目标窗口不存在或不唯一")
        clsid = GUID.parse("ff48dba4-60ef-4201-aa87-54103eef594e")
        iid = GUID.parse("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")
        automation = ctypes.c_void_p()
        ole.CoCreateInstance(ctypes.byref(clsid), None, 1, ctypes.byref(iid), ctypes.byref(automation))
        with ExitStack() as stack:
            stack.callback(release, automation)
            root, condition, array = ctypes.c_void_p(), ctypes.c_void_p(), ctypes.c_void_p()
            call(automation, 6, [wintypes.HWND, ctypes.POINTER(ctypes.c_void_p)], windows[0], ctypes.byref(root))
            stack.callback(release, root)
            call(automation, 21, [ctypes.POINTER(ctypes.c_void_p)], ctypes.byref(condition))
            stack.callback(release, condition)
            call(root, 6, [ctypes.c_int, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)], 4, condition, ctypes.byref(array))
            stack.callback(release, array)
            count = ctypes.c_int()
            call(array, 3, [ctypes.POINTER(ctypes.c_int)], ctypes.byref(count))
            controls = []
            matches = []
            for index in range(count.value):
                element = ctypes.c_void_p()
                call(array, 4, [ctypes.c_int, ctypes.POINTER(ctypes.c_void_p)], index, ctypes.byref(element))
                stack.callback(release, element)
                if number(element, 20) != owner_pid or number(element, 21) != 50000:
                    continue
                name, aid = string(element, 23), string(element, 29)
                if not name and not aid:
                    continue
                pattern = ctypes.c_void_p()
                call(element, 16, [ctypes.c_int, ctypes.POINTER(ctypes.c_void_p)], 10000, ctypes.byref(pattern))
                stack.callback(release, pattern)
                selectable = bool(number(element, 28) and not number(element, 38) and pattern.value)
                controls.append({"window_title": request["window_title"], "automation_id": aid,
                                 "control_name": name, "selectable": selectable})
                if ((not request["automation_id"] or aid == request["automation_id"]) and
                        (not request["control_name"] or name == request["control_name"])):
                    matches.append((element, pattern, selectable))
            identity()
            if request["invoke"]:
                if len(matches) != 1 or not matches[0][2]:
                    raise RuntimeError("目标控件不存在、不唯一或不可调用")
                element, pattern, _ = matches[0]
                if number(element, 20) != owner_pid or not number(element, 28) or number(element, 38):
                    raise RuntimeError("目标控件身份或状态已变化")
                current_name, current_id = string(element, 23), string(element, 29)
                if ((request["control_name"] and request["control_name"] != current_name) or
                        (request["automation_id"] and request["automation_id"] != current_id)):
                    raise RuntimeError("目标控件标识已变化")
                window_pid = wintypes.DWORD()
                title = ctypes.create_unicode_buffer(user.GetWindowTextLengthW(windows[0]) + 1)
                user.GetWindowTextW(windows[0], title, len(title))
                user.GetWindowThreadProcessId(windows[0], ctypes.byref(window_pid))
                if not user.IsWindow(windows[0]) or window_pid.value != owner_pid or title.value != request["window_title"]:
                    raise RuntimeError("目标窗口身份已变化")
                identity()
                call(pattern, 3, [])
                print(json.dumps({"invoked": True}))
            else:
                print(json.dumps({"window_found": True, "controls": controls}, ensure_ascii=False))
    finally:
        ole.CoUninitialize()


if __name__ == "__main__":
    main()
