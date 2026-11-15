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
    internal RestartCandidateTracker(OwnedProcess owned, Func<uint[]>? getIds = null, Func<int, Process>? openProcess = null, Func<SafeProcessHandle, bool>? containsProcess = null)
    { this.owned = owned; this.getIds = getIds ?? owned.ProcessIds; this.openProcess = openProcess ?? Process.GetProcessById; this.containsProcess = containsProcess ?? owned.ContainsProcess; }
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
            if (candidate != null && candidate.HasExited) CandidateExited = true;
            foreach (uint id in getIds())
            {
                Process? process = null;
                try
                {
                    process = openProcess(checked((int)id));
                    _ = process.SafeHandle; // Keep the original handle while verifying identity.
                    if (process.HasExited || !containsProcess(process.SafeHandle)) { MarkUncertain(); continue; }
                    DateTime created = process.StartTime.ToUniversalTime();
                    string? path = process.MainModule?.FileName;
                    if (path == null) { MarkUncertain(); continue; }
                    if (created <= owned.CreatedUtc || !string.Equals(Path.GetFullPath(path), owned.Executable, StringComparison.OrdinalIgnoreCase)) continue;
                    RecordIdentity(process.Id, created);
                    if (candidate == null) { candidate = process; process = null; }
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or IOException or OverflowException) { MarkUncertain(); }
                finally { process?.Dispose(); }
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or UnauthorizedAccessException or IOException or OverflowException) { MarkUncertain(); }
    }
    public void Dispose() => candidate?.Dispose();
}
