using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Runner;

// A matching process is only a candidate, not evidence of update or business success.
internal sealed class RestartCandidateTracker : IDisposable
{
    readonly OwnedProcess owned;
    readonly Func<uint[]> getIds;
    readonly Func<int, Process> openProcess;
    readonly Func<SafeProcessHandle, bool> containsProcess;
    readonly Action<string> report;
    internal RestartCandidateTracker(OwnedProcess owned, Func<uint[]>? getIds = null, Func<int, Process>? openProcess = null, Func<SafeProcessHandle, bool>? containsProcess = null, Action<string>? report = null)
    { this.owned = owned; this.getIds = getIds ?? owned.ProcessIds; this.openProcess = openProcess ?? Process.GetProcessById; this.containsProcess = containsProcess ?? owned.ContainsProcess; this.report = report ?? (_ => { }); }
    readonly Dictionary<(int, DateTime), Process> observed = new();
    readonly HashSet<(int, DateTime)> exited = new();
    readonly HashSet<string> failures = new();
    bool limitReported;
    void Failure(string stage, int? pid, Exception? error = null)
    {
        MarkUncertain();
        string reason = error is Win32Exception win ? $"Win32 错误 {win.NativeErrorCode}" : error?.GetType().Name ?? "进程已退出或归属不符";
        string key = $"{stage}|{pid}|{reason}";
        if (failures.Count < 128 && failures.Add(key)) report($"受管进程查询不明：{stage}；PID {(pid?.ToString() ?? "未知")}；{reason}；需人工核实");
    }
    Process? candidate;
    int? candidateId;
    DateTime candidateCreated;
    internal bool Uncertain { get; private set; }
    internal bool Ambiguous { get; private set; }
    internal bool HasCandidate => candidateId.HasValue;
    internal bool CandidateExited { get; private set; }
    internal int? CandidateId => candidateId;
    internal DateTime CandidateCreated => candidateCreated;
    internal void RecordIdentity(int pid, DateTime created)
    {
        if (!candidateId.HasValue) { candidateId = pid; candidateCreated = created; }
        else if (candidateId != pid || candidateCreated != created) Ambiguous = true;
    }
    internal void MarkUncertain() => Uncertain = true;
    internal void Poll()
    {
        try
        {
            foreach (var entry in observed)
            {
                try
                {
                    if (entry.Value.HasExited && exited.Add(entry.Key)) report($"候选进程已退出（PID {entry.Key.Item1}，创建时间 {entry.Key.Item2:O}）；不代表业务成功");
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception or UnauthorizedAccessException) { Failure("检查候选退出", entry.Key.Item1, e); }
            }
            if (candidate != null && candidate.HasExited) CandidateExited = true;
            foreach (uint id in getIds())
            {
                Process? process = null;
                string stage = "打开进程";
                try
                {
                    process = openProcess(checked((int)id));
                    _ = process.SafeHandle; // Keep the original handle while verifying identity.
                    stage = "核实进程归属";
                    if (process.HasExited || !containsProcess(process.SafeHandle)) { Failure(stage, (int)id); continue; }
                    stage = "读取创建时间和程序路径";
                    DateTime created = process.StartTime.ToUniversalTime();
                    string? path = process.MainModule?.FileName;
                    if (path == null) { Failure(stage, (int)id); continue; }
                    if (created <= owned.CreatedUtc || !string.Equals(Path.GetFullPath(path), owned.Executable, StringComparison.OrdinalIgnoreCase)) continue;
                    RecordIdentity(process.Id, created);
                    var identity = (process.Id, created);
                    if (!observed.ContainsKey(identity))
                    {
                        if (observed.Count >= 128) { if (!limitReported) { limitReported = true; Failure("候选记录达到上限", null); } continue; }
                        string relation = observed.Count == 0 ? "首次观察" : observed.Count > exited.Count ? "其他候选尚未记录退出，可能重叠" : "已观察到此前候选退出";
                        report($"受管候选出现（PID {identity.Id}，创建时间 {identity.created:O}；归属已核实；{relation}）；不代表业务成功");
                        observed.Add(identity, process);
                        candidate ??= process;
                        process = null;
                    }
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or IOException or OverflowException) { Failure(stage, id <= int.MaxValue ? (int)id : null, e); }
                finally { process?.Dispose(); }
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException or OverflowException) { Failure("查询进程组", null, e); }
    }
    public void Dispose() { foreach (var process in observed.Values) process.Dispose(); }
}
