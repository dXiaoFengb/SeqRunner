from __future__ import annotations

import ctypes
import os
import time
from ctypes import wintypes

import psutil

from .processes import OwnedProcess


class JobObservationError(RuntimeError):
    pass


class WindowsJobObserver:
    """Observe a job started by this runner; closing it never terminates its members."""

    def __init__(self, root: OwnedProcess) -> None:
        if os.name != "nt":
            raise JobObservationError("Windows Job 不可用")
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.events: list[tuple[int, int, float]] = []
        k = self.kernel
        k.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        k.CreateJobObjectW.restype = wintypes.HANDLE
        k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        k.OpenProcess.restype = wintypes.HANDLE
        k.CloseHandle.argtypes = [wintypes.HANDLE]
        k.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        k.IsProcessInJob.argtypes = [wintypes.HANDLE, wintypes.HANDLE, ctypes.POINTER(wintypes.BOOL)]
        k.GetProcessTimes.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.FILETIME), ctypes.POINTER(wintypes.FILETIME), ctypes.POINTER(wintypes.FILETIME), ctypes.POINTER(wintypes.FILETIME)]
        k.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
        k.QueryInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
        k.CreateIoCompletionPort.argtypes = [wintypes.HANDLE, wintypes.HANDLE, ctypes.c_size_t, wintypes.DWORD]
        k.CreateIoCompletionPort.restype = wintypes.HANDLE
        k.GetQueuedCompletionStatus.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(ctypes.c_size_t), ctypes.POINTER(ctypes.c_void_p), wintypes.DWORD]
        self.job = k.CreateJobObjectW(None, None)
        self.port = None
        if not self.job:
            raise JobObservationError(f"创建 Job 失败：{ctypes.get_last_error()}")
        try:
            self._allow_breakaway()
            self._attach_port()
            process = self._open_verified(root, 0x0100 | 0x0001 | 0x1000)
            try:
                if not k.AssignProcessToJobObject(self.job, process):
                    raise JobObservationError(f"无法观察目标 Job：{ctypes.get_last_error()}")
            finally:
                k.CloseHandle(process)
        except Exception:
            self.close()
            raise

    def _allow_breakaway(self) -> None:
        class Basic(ctypes.Structure):
            _fields_ = [("PerProcessUserTimeLimit", ctypes.c_int64), ("PerJobUserTimeLimit", ctypes.c_int64),
                        ("LimitFlags", wintypes.DWORD), ("MinimumWorkingSetSize", ctypes.c_size_t),
                        ("MaximumWorkingSetSize", ctypes.c_size_t), ("ActiveProcessLimit", wintypes.DWORD),
                        ("Affinity", ctypes.c_size_t), ("PriorityClass", wintypes.DWORD), ("SchedulingClass", wintypes.DWORD)]

        class IO(ctypes.Structure):
            _fields_ = [(name, ctypes.c_uint64) for name in ("ReadOperationCount", "WriteOperationCount", "OtherOperationCount", "ReadTransferCount", "WriteTransferCount", "OtherTransferCount")]

        class Extended(ctypes.Structure):
            _fields_ = [("BasicLimitInformation", Basic), ("IoInfo", IO), ("ProcessMemoryLimit", ctypes.c_size_t),
                        ("JobMemoryLimit", ctypes.c_size_t), ("PeakProcessMemoryUsed", ctypes.c_size_t), ("PeakJobMemoryUsed", ctypes.c_size_t)]

        limits = Extended()
        limits.BasicLimitInformation.LimitFlags = 0x00000800  # JOB_OBJECT_LIMIT_BREAKAWAY_OK
        if not self.kernel.SetInformationJobObject(self.job, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
            raise JobObservationError(f"无法设置 Job 脱离规则：{ctypes.get_last_error()}")

    def _attach_port(self) -> None:
        class Association(ctypes.Structure):
            _fields_ = [("CompletionKey", ctypes.c_void_p), ("CompletionPort", wintypes.HANDLE)]

        self.port = self.kernel.CreateIoCompletionPort(wintypes.HANDLE(-1), None, 0, 1)
        if not self.port:
            raise JobObservationError(f"无法创建进程通知端口：{ctypes.get_last_error()}")
        association = Association(ctypes.c_void_p(1), self.port)
        if not self.kernel.SetInformationJobObject(self.job, 7, ctypes.byref(association), ctypes.sizeof(association)):
            raise JobObservationError(f"无法关联进程通知端口：{ctypes.get_last_error()}")

    def _open_verified(self, process: OwnedProcess, access: int) -> int:
        handle = self.kernel.OpenProcess(access, False, process.pid)
        if not handle:
            raise JobObservationError(f"无法读取进程身份：{ctypes.get_last_error()}")
        created, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
        if not self.kernel.GetProcessTimes(handle, ctypes.byref(created), ctypes.byref(exited), ctypes.byref(kernel), ctypes.byref(user)):
            self.kernel.CloseHandle(handle)
            raise JobObservationError("无法核对进程创建时间")
        ticks = (created.dwHighDateTime << 32) | created.dwLowDateTime
        timestamp = ticks / 10_000_000 - 11_644_473_600
        if abs(timestamp - process.created_at) > 0.00001:
            self.kernel.CloseHandle(handle)
            raise JobObservationError("进程身份已变化")
        return handle

    def contains(self, process: OwnedProcess) -> bool:
        handle = self._open_verified(process, 0x1000)
        try:
            answer = wintypes.BOOL()
            if not self.kernel.IsProcessInJob(handle, self.job, ctypes.byref(answer)):
                raise JobObservationError("无法核对进程 Job 身份")
            return bool(answer.value)
        finally:
            self.kernel.CloseHandle(handle)

    def members(self, executable: str, since: float) -> list[OwnedProcess]:
        self._drain_notifications()
        capacity = 64
        while capacity <= 8192:
            buffer = ctypes.create_string_buffer(8 + capacity * ctypes.sizeof(ctypes.c_size_t))
            length = wintypes.DWORD()
            if self.kernel.QueryInformationJobObject(self.job, 3, buffer, len(buffer), ctypes.byref(length)):
                break
            if ctypes.get_last_error() != 234:
                raise JobObservationError("无法读取 Job 进程列表")
            capacity *= 2
        else:
            raise JobObservationError("Job 进程列表超出上限")
        count = ctypes.c_uint32.from_buffer(buffer, 4).value
        if count > capacity:
            raise JobObservationError("Job 进程列表不完整")
        pids = (ctypes.c_size_t * count).from_buffer(buffer, 8)
        expected = os.path.normcase(os.path.realpath(executable))
        matches = []
        for pid in pids:
            try:
                target = psutil.Process(pid)
                identity = OwnedProcess(int(pid), target.create_time())
                if identity.created_at >= since and os.path.normcase(os.path.realpath(target.exe())) == expected and self.contains(identity):
                    matches.append(identity)
            except (psutil.NoSuchProcess, psutil.ZombieProcess):
                continue
            except (psutil.Error, OSError) as error:
                raise JobObservationError("无法确认 Job 内进程身份") from error
        return matches

    def _drain_notifications(self) -> None:
        # Notifications are hints only; membership is queried every time.
        for _ in range(128):
            code, key, value = wintypes.DWORD(), ctypes.c_size_t(), ctypes.c_void_p()
            if not self.kernel.GetQueuedCompletionStatus(self.port, ctypes.byref(code), ctypes.byref(key), ctypes.byref(value), 0):
                break
            if code.value in (4, 6, 7, 8):
                self.events.append((code.value, value.value or 0, time.monotonic()))
                self.events = self.events[-128:]

    def close(self) -> None:
        if self.port:
            self.kernel.CloseHandle(self.port)
            self.port = None
        if self.job:
            self.kernel.CloseHandle(self.job)
            self.job = None
