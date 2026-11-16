using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Runner;

// Job completion-port packets are hints; Windows does not guarantee delivery.
internal sealed class JobEventObserver : IDisposable
{
    const uint NewProcess = 6, ExitProcess = 7, AbnormalExit = 8, WaitTimeout = 258;
    const int MaxMembers = 4096, MaxReported = 32;
    readonly SafeFileHandle port;
    readonly Dictionary<uint, Process?> members = new();
    readonly HashSet<uint> exits = new();
    readonly List<string> reasons = new();
    Process? borrowedRoot;
    int reported;
    internal int NewCount { get; private set; }
    internal int ExitCount { get; private set; }
    internal bool Uncertain { get; private set; }
    internal string Reason => string.Join("；", reasons);
    internal bool Backlog { get; private set; }

    [StructLayout(LayoutKind.Sequential)] struct Association { public IntPtr CompletionKey, CompletionPort; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeFileHandle CreateIoCompletionPort(IntPtr file, IntPtr existing, UIntPtr key, uint threads);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref Association association, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetQueuedCompletionStatus(SafeFileHandle port, out uint message, out UIntPtr key, out IntPtr value, uint timeout);

    internal JobEventObserver(SafeFileHandle port) => this.port = port;
    internal static JobEventObserver? TryAttach(SafeFileHandle job, out string reason)
    {
        reason = "";
        var port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
        if (port.IsInvalid) { reason = "无法创建 Job 事件通道（错误 " + Marshal.GetLastWin32Error() + "）"; port.Dispose(); return null; }
        var association = new Association { CompletionKey = new IntPtr(1), CompletionPort = port.DangerousGetHandle() };
        if (!SetInformationJobObject(job, 7, ref association, (uint)Marshal.SizeOf<Association>()))
        { reason = "无法关联 Job 事件通道（错误 " + Marshal.GetLastWin32Error() + "）"; port.Dispose(); return null; }
        return new JobEventObserver(port);
    }

    internal void MarkUncertain(string reason)
    { Uncertain = true; if (reasons.Count < 8 && !reasons.Contains(reason)) reasons.Add(reason); }

    internal void Record(uint message, uint pid, Func<uint, Process?> open, Action<string> report)
    {
        if (message == NewProcess)
        {
            NewCount++;
            if (pid == 0 || members.ContainsKey(pid)) { MarkUncertain("进程事件重复或 PID 复用"); return; }
            if (members.Count >= MaxMembers) { MarkUncertain("进程事件超过记录上限"); return; }
            Process? process = null; DateTime? created = null;
            try { process = open(pid); if (process == null) MarkUncertain("进程事件对应句柄或归属无法核实"); else created = process.StartTime.ToUniversalTime(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or IOException or OverflowException)
            { if (process != null && !ReferenceEquals(process, borrowedRoot)) process.Dispose(); process = null; MarkUncertain("进程事件对应句柄或归属无法核实"); }
            members.Add(pid, process);
            if (reported++ < MaxReported) report($"Job 进程加入通知：PID {pid}；" + (created == null ? "身份未确认" : $"句柄已保留，创建时间 {created:O}；用途未确认"));
            else if (reported == MaxReported + 1) report("Job 事件较多，仅保留前 32 条明细；统计继续，身份仍需核实");
        }
        else if (message is ExitProcess or AbnormalExit)
        {
            ExitCount++;
            if (pid == 0 || !members.ContainsKey(pid) || !exits.Add(pid)) MarkUncertain("退出事件缺少加入事件或存在重复");
            if (reported++ < MaxReported) report($"Job 进程退出通知：PID {pid}；通知不代表业务成功");
            else if (reported == MaxReported + 1) report("Job 事件较多，仅保留前 32 条明细；统计继续，身份仍需核实");
        }
    }

    internal void Poll(OwnedProcess owned, Action<string> report)
    {
        borrowedRoot = owned.Root;
        Backlog = false;
        for (int i = 0; i < 256; i++)
        {
            if (!GetQueuedCompletionStatus(port, out uint message, out UIntPtr key, out IntPtr value, 0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != WaitTimeout) MarkUncertain("读取 Job 事件失败（错误 " + error + "）");
                return;
            }
            if (key != (UIntPtr)1 || value.ToInt64() < 0 || (ulong)value.ToInt64() > uint.MaxValue) { MarkUncertain("Job 事件内容无法核实"); continue; }
            uint pid = (uint)value.ToInt64();
            Record(message, pid, id =>
            {
                if (id == (uint)owned.Root.Id)
                {
                    if (owned.Root.SafeHandle.IsClosed || owned.Root.SafeHandle.IsInvalid || owned.Root.StartTime.ToUniversalTime() != owned.CreatedUtc) return null;
                    return owned.Root;
                }
                var candidate = Process.GetProcessById(checked((int)id));
                try
                {
                    _ = candidate.SafeHandle;
                    if (candidate.HasExited || !owned.ContainsProcess(candidate.SafeHandle) || candidate.StartTime.ToUniversalTime() < owned.CreatedUtc) { candidate.Dispose(); return null; }
                    MarkUncertain("不能仅凭 PID 证明通知与当前句柄属于同一身份");
                    return candidate;
                }
                catch { candidate.Dispose(); throw; }
            }, report);
        }
        Backlog = true;
    }

    internal string Summary(uint total)
    {
        if (Backlog) MarkUncertain("Job 事件积压，未完成核对");
        if (NewCount != total || ExitCount != total) MarkUncertain("Job 事件数量与累计进程数不一致或通知尚未送达");
        foreach (var process in members.Values)
        {
            if (process == null) continue;
            try { if (!process.HasExited) MarkUncertain("已记录进程的退出状态无法确认"); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or UnauthorizedAccessException) { MarkUncertain("已记录进程的退出状态无法确认"); }
        }
        return $"Job 事件核对：累计进程 {total}；加入通知 {NewCount}；退出通知 {ExitCount}；" + (Uncertain ? "不明：" + Reason : "数量已核对；进程用途及通知完整性仍需核实");
    }

    public void Dispose() { foreach (var process in members.Values) if (process != null && !ReferenceEquals(process, borrowedRoot)) process.Dispose(); port.Dispose(); }
}
