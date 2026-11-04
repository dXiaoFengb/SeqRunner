"""Two temporary native windows used solely by the UI Automation integration test."""
import ctypes
import sys
from ctypes import wintypes
from pathlib import Path

user = ctypes.WinDLL("user32", use_last_error=True)
kernel = ctypes.WinDLL("kernel32", use_last_error=True)
proc_type = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)


class WindowClass(ctypes.Structure):
    _fields_ = [("style", wintypes.UINT), ("wndproc", proc_type), ("extra_class", ctypes.c_int),
                ("extra_window", ctypes.c_int), ("instance", wintypes.HINSTANCE), ("icon", wintypes.HICON),
                ("cursor", wintypes.HANDLE), ("background", wintypes.HBRUSH), ("menu", wintypes.LPCWSTR),
                ("name", wintypes.LPCWSTR)]


user.DefWindowProcW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user.DefWindowProcW.restype = ctypes.c_ssize_t
user.CreateWindowExW.argtypes = [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
                                ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.HWND,
                                wintypes.HMENU, wintypes.HINSTANCE, ctypes.c_void_p]
user.CreateWindowExW.restype = wintypes.HWND
user.RegisterClassW.argtypes = [ctypes.POINTER(WindowClass)]
kernel.GetModuleHandleW.restype = wintypes.HINSTANCE
instance = kernel.GetModuleHandleW(None)
output = Path(sys.argv[1])


@proc_type
def wndproc(hwnd, message, wparam, lparam):
    if message == 0x0111:  # WM_COMMAND
        control_id = wparam & 0xffff
        if control_id in (101, 102):
            with output.open("a", encoding="utf-8") as stream:
                stream.write("closed\n" if control_id == 101 else "started\n")
            if control_id == 101:
                user.DestroyWindow(hwnd)
            return 0
    return user.DefWindowProcW(hwnd, message, wparam, lparam)


klass = WindowClass(0, wndproc, 0, 0, instance, None, None, None, None, "RunnerUIAFixture")
if not user.RegisterClassW(ctypes.byref(klass)):
    raise ctypes.WinError(ctypes.get_last_error())
windows = []
for title, label, control_id, x in [("Runner Startup Fixture", "Start Fixture", 102, 50),
                                    ("Runner Announcement Fixture", "Close Notice", 101, 450)]:
    parent = user.CreateWindowExW(0, klass.name, title, 0x10CF0000, x, 100, 350, 180, None, None, instance, None)
    if not parent:
        raise ctypes.WinError(ctypes.get_last_error())
    button = user.CreateWindowExW(0, "BUTTON", label, 0x50010000, 30, 40, 200, 40,
                                 parent, control_id, instance, None)
    if not button:
        raise ctypes.WinError(ctypes.get_last_error())
    if (control_id == 102 and len(sys.argv) > 2 and sys.argv[2] == "duplicate" and
            not user.CreateWindowExW(0, "BUTTON", label, 0x50010000, 30, 90, 200, 40,
                                     parent, 103, instance, None)):
        raise ctypes.WinError(ctypes.get_last_error())
    windows.append(parent)
msg = wintypes.MSG()
while user.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
    user.TranslateMessage(ctypes.byref(msg))
    user.DispatchMessageW(ctypes.byref(msg))
