using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using Microsoft.Win32.SafeHandles;

namespace ConnectorPrototype;

internal sealed record ControlRequest(int Pid, long CreatedTicks, long Window, int OwnerPid, DateTimeOffset Deadline, bool Stall);

// This controller is restricted to the executable's own controlled fixture.
internal sealed class IsolatedControl(IsolatedSession session, BackgroundMonitor monitor, string journal)
{
    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    static extern bool TerminateWorker(SafeProcessHandle handle, uint code);
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInfo { internal IntPtr Reserved1, Peb, Reserved2a, Reserved2b, ProcessId, ParentId; }
    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, out ProcessBasicInfo info, int length, out int returnLength);
    static bool OwnedBy(Process process, int owner) => NtQueryInformationProcess(process.SafeHandle, 0, out var info, Marshal.SizeOf<ProcessBasicInfo>(), out _) == 0 && info.ParentId.ToInt64() == owner;
    readonly object gate = new();
    bool cleanupUnconfirmed;

    internal Result Execute(Command command, Snapshot observation, bool authorized, CancellationToken stop, bool stall = false)
    {
        if (!session.TryEnterControl()) return new(command.Id, "REJECTED", "UNVERIFIED", "another control is in flight");
        try { return ExecuteCore(command, observation, authorized, stop, stall); }
        finally { session.ExitControl(); }
    }

    Result ExecuteCore(Command command, Snapshot observation, bool authorized, CancellationToken stop, bool stall)
    {
        lock (gate)
        {
            Result Reject(string reason) => new(command.Id, "REJECTED", "UNVERIFIED", reason);
            Result Unknown(string reason) => new(command.Id, "UNKNOWN", "UNVERIFIED", reason);
            if (cleanupUnconfirmed || !session.ControlAvailable || !authorized || stop.IsCancellationRequested || command.Expires <= DateTimeOffset.UtcNow || command.ControlId != "fixture-button" || command.ObservationSequence != observation.Sequence || !session.Matches(observation, TimeSpan.FromSeconds(2)) || observation.Controls.Count(x => x.SelectedId == "fixture-button" && x.Enabled) != 1)
                return Reject("authorization or observation invalid");
            Process target = session.FixtureProcess;
            try
            {
                target.Refresh();
                if (target.HasExited || !OwnedBy(target, Environment.ProcessId) || target.StartTime.ToUniversalTime() != observation.CreatedUtc || target.MainWindowHandle != observation.WindowHandle || !string.Equals(target.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) || target.MainWindowTitle != "Connector controlled fixture")
                    return Reject("target is not the controlled fixture");
                using var file = new FileStream(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
                using var reader = new StreamReader(file, System.Text.Encoding.ASCII, leaveOpen: true);
                string history = reader.ReadToEnd();
                if (history.Length > 0 && !history.EndsWith('\n')) return Reject("partial journal");
                foreach (string line in history.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!Guid.TryParseExact(line.TrimEnd('\r'), "N", out Guid id)) return Reject("invalid journal");
                    if (id == command.Id) return Unknown("previously attempted; no replay");
                }
                file.Seek(0, SeekOrigin.End);
                using var writer = new StreamWriter(file, System.Text.Encoding.ASCII, leaveOpen: true);
                writer.WriteLine(command.Id.ToString("N")); writer.Flush(); file.Flush(true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { return Reject("target or attempt record unavailable"); }

            Process? worker = null; SafeProcessHandle? handle = null; DateTime workerCreated = default;
            try
            {
                string executable = Environment.ProcessPath ?? throw new InvalidOperationException();
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
                info.ArgumentList.Add("control-worker");
                worker = Process.Start(info) ?? throw new InvalidOperationException();
                handle = worker.SafeHandle; workerCreated = worker.StartTime.ToUniversalTime();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop, monitor.Lifetime);
                limit.CancelAfter(TimeSpan.FromSeconds(3));
                var request = new ControlRequest(target.Id, observation.CreatedUtc.Ticks, observation.WindowHandle.ToInt64(), Environment.ProcessId, command.Expires, stall);
                worker.StandardInput.WriteLine(JsonSerializer.Serialize(request)); worker.StandardInput.Flush();
                string? ready = worker.StandardOutput.ReadLineAsync(limit.Token).AsTask().GetAwaiter().GetResult();
                if (ready != "READY") return Unknown("worker did not become ready");
                bool sent = monitor.Authorize(observation, () =>
                {
                    if (stop.IsCancellationRequested || command.Expires <= DateTimeOffset.UtcNow) return false;
                    worker.StandardInput.WriteLine("GO"); worker.StandardInput.Flush(); return true;
                });
                if (!sent) return Reject("observation expired or session stopped before dispatch");
                string? reply = worker.StandardOutput.ReadLineAsync(limit.Token).AsTask().GetAwaiter().GetResult();
                worker.WaitForExitAsync(limit.Token).GetAwaiter().GetResult();
                return reply == "SUBMITTED" && worker.ExitCode == 0
                    ? new(command.Id, "SUBMITTED", "UNVERIFIED", "effect requires separate observation")
                    : Unknown("worker outcome unavailable");
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { return Unknown("worker timed out, stopped or failed"); }
            finally
            {
                if (worker != null)
                {
                    try
                    {
                        worker.Refresh();
                        if (!worker.HasExited && !(handle is { IsClosed: false, IsInvalid: false } && workerCreated != default && worker.StartTime.ToUniversalTime() == workerCreated && string.Equals(worker.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) && TerminateWorker(handle, 1) && worker.WaitForExit(2000))) cleanupUnconfirmed = true;
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { cleanupUnconfirmed = true; }
                    if (cleanupUnconfirmed) session.BlockControls();
                    worker.Dispose();
                }
            }
        }
    }

    internal static int RunWorker()
    {
        try
        {
            string? line = Console.ReadLine();
            if (line == null || line.Length > 4096) return 2;
            var request = JsonSerializer.Deserialize<ControlRequest>(line);
            if (request == null || request.Pid <= 0 || request.Deadline <= DateTimeOffset.UtcNow) return 2;
            using var target = Process.GetProcessById(request.Pid);
            target.Refresh();
            using var self = Process.GetCurrentProcess();
            if (target.HasExited || !OwnedBy(self, request.OwnerPid) || !OwnedBy(target, request.OwnerPid) || target.StartTime.ToUniversalTime().Ticks != request.CreatedTicks || target.MainWindowHandle.ToInt64() != request.Window || target.MainWindowTitle != "Connector controlled fixture" || !string.Equals(target.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 2;
            var window = AutomationElement.FromHandle(target.MainWindowHandle);
            var matches = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "fixture-button"));
            if (matches.Count != 1 || matches[0].Current.ProcessId != target.Id || !matches[0].Current.IsEnabled || !matches[0].TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) return 2;
            Console.WriteLine("READY"); Console.Out.Flush();
            if (Console.ReadLine() != "GO") return 2;
            if (request.Stall) Thread.Sleep(TimeSpan.FromSeconds(30));
            target.Refresh();
            if (DateTimeOffset.UtcNow >= request.Deadline || target.HasExited || target.StartTime.ToUniversalTime().Ticks != request.CreatedTicks || target.MainWindowHandle.ToInt64() != request.Window) return 2;
            ((InvokePattern)pattern).Invoke();
            Console.WriteLine("SUBMITTED"); Console.Out.Flush(); return 0;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException or JsonException or ElementNotAvailableException or COMException) { return 2; }
    }
}
