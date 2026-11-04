from __future__ import annotations

import os
import time
from dataclasses import dataclass

import psutil

from .processes import OwnedProcess


@dataclass(frozen=True)
class ProcessSnapshot:
    pid: int
    created_at: float
    parent_pid: int
    executable: str | None

    @property
    def identity(self) -> OwnedProcess:
        return OwnedProcess(self.pid, self.created_at)


def snapshot_processes(roots: set[int] | None = None) -> list[ProcessSnapshot]:
    """只读取可验证的进程身份；无法读取的进程不视为归属证据。"""
    if os.name == "nt" and roots is not None:
        return _windows_descendants(roots)
    result: list[ProcessSnapshot] = []
    for process in psutil.process_iter(["pid", "ppid", "create_time", "exe"]):
        try:
            info = process.info
            if info["pid"] is None or info["ppid"] is None or info["create_time"] is None:
                continue
            result.append(ProcessSnapshot(int(info["pid"]), float(info["create_time"]), int(info["ppid"]), info["exe"]))
        except (psutil.Error, OSError, TypeError, ValueError):
            continue
    return result


def _windows_descendants(roots: set[int]) -> list[ProcessSnapshot]:
    """Take one fast PID/parent snapshot, then query only the relevant process chain."""
    import ctypes
    from ctypes import wintypes

    class ProcessEntry(ctypes.Structure):
        _fields_ = [("dwSize", wintypes.DWORD), ("cntUsage", wintypes.DWORD),
                    ("th32ProcessID", wintypes.DWORD), ("th32DefaultHeapID", ctypes.c_size_t),
                    ("th32ModuleID", wintypes.DWORD), ("cntThreads", wintypes.DWORD),
                    ("th32ParentProcessID", wintypes.DWORD), ("pcPriClassBase", wintypes.LONG),
                    ("dwFlags", wintypes.DWORD), ("szExeFile", wintypes.WCHAR * 260)]

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)]
    kernel.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    snapshot_started = time.time()
    handle = kernel.CreateToolhelp32Snapshot(2, 0)
    if handle == wintypes.HANDLE(-1).value:
        raise OSError(ctypes.get_last_error(), "无法读取 Windows 进程快照")
    parents: dict[int, int] = {}
    try:
        entry = ProcessEntry()
        entry.dwSize = ctypes.sizeof(entry)
        ok = kernel.Process32FirstW(handle, ctypes.byref(entry))
        if not ok:
            raise OSError(ctypes.get_last_error(), "无法读取 Windows 进程快照首项")
        while ok:
            parents[int(entry.th32ProcessID)] = int(entry.th32ParentProcessID)
            ok = kernel.Process32NextW(handle, ctypes.byref(entry))
        error = ctypes.get_last_error()
        if error != 18:  # ERROR_NO_MORE_FILES
            raise OSError(error, "Windows 进程快照读取未完成")
    finally:
        kernel.CloseHandle(handle)
    relevant = set(roots)
    while True:
        children = {pid for pid, parent in parents.items() if parent in relevant} - relevant
        if not children:
            break
        relevant.update(children)
    result = []
    for pid in relevant & parents.keys():
        try:
            process = psutil.Process(pid)
            created = process.create_time()
            if created > snapshot_started or process.ppid() != parents[pid]:
                continue
            result.append(ProcessSnapshot(pid, created, parents[pid], process.exe()))
        except (psutil.Error, OSError):
            continue
    return result


class UpdateLineage:
    """Track observed parent-child identities without treating names as ownership."""

    def __init__(self, root: OwnedProcess, executable: str) -> None:
        self.root = root
        self.executable = os.path.normcase(os.path.realpath(executable))
        self._known: set[OwnedProcess] = {root}

    def verified_live_parents(self) -> dict[OwnedProcess, float]:
        """Record the time before each exact parent identity was observed alive."""
        verified: dict[OwnedProcess, float] = {}
        for parent in self._known:
            before_check = time.time()
            try:
                process = psutil.Process(parent.pid)
                if process.create_time() == parent.created_at and process.is_running():
                    verified[parent] = before_check
            except (psutil.Error, OSError):
                continue
        return verified

    def known_pids(self) -> set[int]:
        return {process.pid for process in self._known}

    def observed_executables(self, processes: list[ProcessSnapshot], executable: str, since: float) -> list[OwnedProcess]:
        """Return only matching, currently observed members of the verified chain."""
        expected = os.path.normcase(os.path.realpath(executable))
        return [item.identity for item in processes
                if item.identity in self._known and item.created_at >= since and item.executable
                and os.path.normcase(os.path.realpath(item.executable)) == expected]

    def observe(self, processes: list[ProcessSnapshot], verified_parents: dict[OwnedProcess, float] | None = None) -> list[OwnedProcess]:
        # Only a parent whose exact PID and creation time are visible in this
        # snapshot can establish a new child. A vanished or reused PID proves nothing.
        by_pid = {item.pid: item for item in processes}
        while True:
            found: set[OwnedProcess] = set()
            for child in processes:
                parent = by_pid.get(child.parent_pid)
                parent_identity = parent.identity if parent else None
                if parent_identity not in self._known:
                    parent_identity = next((identity for identity, checked_at in (verified_parents or {}).items()
                                            if identity.pid == child.parent_pid and child.created_at <= checked_at), None)
                if parent_identity not in self._known:
                    continue
                if child.created_at < parent_identity.created_at or child.identity in self._known:
                    continue
                found.add(child.identity)
            if not found:
                break
            self._known.update(found)

        matches: list[OwnedProcess] = []
        for process in processes:
            if process.identity == self.root or process.identity not in self._known or not process.executable:
                continue
            if os.path.normcase(os.path.realpath(process.executable)) == self.executable:
                matches.append(process.identity)
        return matches
