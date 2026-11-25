using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ConnectorPrototype;

internal sealed record WorkerRequest(Guid Run, Guid Session, long Sequence, int Pid, long CreatedTicks,
    DateTimeOffset Deadline, string? SelectedName, string? SelectedId, bool Stall, bool ExitEarly, bool WrongSequence);
internal sealed record WorkerReply(Guid Run, Guid Session, long Sequence, int Pid, long CreatedTicks,
    DateTimeOffset Deadline, string ProcessState, string WindowState, Quality Quality, string Detail,
    ControlObservation[] Controls, bool Truncated, long WindowHandle);

// Prototype only: a new child handles each UIA read. It never owns the observed target.
internal sealed class IsolatedSession : IObservationSession
{
    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    static extern bool TerminateOwnedWorker(SafeProcessHandle handle, uint code);
    readonly Process target;
    readonly DateTime created;
    readonly Guid run = Guid.NewGuid(), session = Guid.NewGuid();
    readonly TimeSpan timeout;
    readonly string? selectedName, selectedId;
    bool stallNext;
    bool exitNext, wrongSequenceNext;
    readonly object gate = new();
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim serial = new(1, 1);
    Snapshot? latest;
    long sequence, latestAt;
    bool disposed;
    bool workerCleanupUnconfirmed;
    bool controlCleanupUnconfirmed;
    readonly SemaphoreSlim controlSerial = new(1, 1);
    int activeWorkerId;
    internal string CleanupStatus { get; private set; } = "not-required";
    internal int ActiveWorkerId { get { lock (gate) return activeWorkerId; } }
    internal Guid Id => session;
    internal bool ControlAvailable { get { lock (gate) return !disposed && !controlCleanupUnconfirmed; } }
    internal void BlockControls() { lock (gate) controlCleanupUnconfirmed = true; }
    internal bool TryEnterControl() => controlSerial.Wait(0);
    internal void ExitControl() => controlSerial.Release();
    internal Snapshot? Current(TimeSpan maxAge)
    {
        lock (gate)
        {
            if (disposed || latest == null) return null;
            return maxAge < TimeSpan.Zero || Stopwatch.GetElapsedTime(latestAt) > maxAge
                ? latest with { Quality = Quality.Stale, Detail = "observation expired", Controls = Array.Empty<ControlObservation>(), WindowHandle = IntPtr.Zero }
                : latest;
        }
    }
    internal IsolatedSession(Process target, TimeSpan timeout, string? selectedName = null, string? selectedId = null, bool stall = false)
    {
        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.target = target; this.timeout = timeout; this.selectedName = selectedName; this.selectedId = selectedId; stallNext = stall;
        created = target.StartTime.ToUniversalTime();
    }
    public Process FixtureProcess => target;
    internal void StallNextForTest() { lock (gate) { if (disposed) throw new ObjectDisposedException(nameof(IsolatedSession)); stallNext = true; } }
    internal void ExitNextForTest() { lock (gate) { if (disposed) throw new ObjectDisposedException(nameof(IsolatedSession)); exitNext = true; } }
    internal void WrongSequenceNextForTest() { lock (gate) { if (disposed) throw new ObjectDisposedException(nameof(IsolatedSession)); wrongSequenceNext = true; } }
    internal void SimulateUnconfirmedCleanupForTest() { lock (gate) { workerCleanupUnconfirmed = true; CleanupStatus = "worker-cleanup-unconfirmed"; latest = null; } }
    public Snapshot Observe() => ObserveAsync().GetAwaiter().GetResult();
    async Task<Snapshot> ObserveAsync()
    {
        await serial.WaitAsync();
        try
        {
            long seq, started = Stopwatch.GetTimestamp(); bool requestStall, requestExit, requestWrongSequence, blocked;
            var observed = DateTimeOffset.UtcNow;
            lock (gate) { seq = ++sequence; latest = null; blocked = workerCleanupUnconfirmed; requestStall = stallNext; requestExit = exitNext; requestWrongSequence = wrongSequenceNext; stallNext = exitNext = wrongSequenceNext = false; if (!blocked) CleanupStatus = "not-required"; }
            Snapshot Unknown(string detail) => new("0.2", run, session, seq, observed, target.Id, created,
                "UNKNOWN", "UNKNOWN", "UNKNOWN", "UNKNOWN", "UNKNOWN", Quality.Unavailable, "isolated UIA", "UNKNOWN", detail);
            lock (gate) if (disposed) return Unknown("session released");
            if (blocked) return Unknown("worker cleanup unconfirmed; observation suspended");
            Snapshot result = Unknown("worker unavailable");
            Process? worker = null; SafeProcessHandle? workerHandle = null; DateTime workerCreated = default;
            try
            {
                target.Refresh();
                if (target.HasExited) return Publish(Unknown("target exited") with { ProcessState = "EXITED" }, started, seq);
                if (target.StartTime.ToUniversalTime() != created) return Publish(Unknown("target identity changed"), started, seq);
                string executable = Environment.ProcessPath ?? throw new InvalidOperationException("executable unavailable");
                if (!string.Equals(Path.GetFileNameWithoutExtension(executable), "ConnectorPrototype", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("apphost required");
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
                info.ArgumentList.Add("observation-worker");
                worker = Process.Start(info) ?? throw new InvalidOperationException("worker start failed");
                workerHandle = worker.SafeHandle;
                workerCreated = worker.StartTime.ToUniversalTime();
                lock (gate) activeWorkerId = worker.Id;
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                limit.CancelAfter(timeout);
                var deadline = DateTimeOffset.UtcNow.Add(timeout);
                var request = new WorkerRequest(run, session, seq, target.Id, created.Ticks, deadline, selectedName, selectedId, requestStall, requestExit, requestWrongSequence);
                await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), limit.Token);
                worker.StandardInput.Close();
                string? line = await worker.StandardOutput.ReadLineAsync(limit.Token);
                await worker.WaitForExitAsync(limit.Token);
                if (line == null || line.Length > 16384 || worker.ExitCode != 0) throw new InvalidDataException("invalid worker output");
                var reply = JsonSerializer.Deserialize<WorkerReply>(line) ?? throw new InvalidDataException("missing worker reply");
                target.Refresh();
                if (reply.Run != run || reply.Session != session || reply.Sequence != seq || reply.Pid != target.Id || reply.CreatedTicks != created.Ticks || reply.Deadline != deadline || DateTimeOffset.UtcNow > deadline || target.HasExited || target.StartTime.ToUniversalTime() != created || reply.Controls.Length > 32)
                    throw new InvalidDataException("late or mismatched reply");
                result = new Snapshot("0.2", run, session, seq, observed, target.Id, created, reply.ProcessState, reply.WindowState,
                    "UNKNOWN", "UNKNOWN", "UNKNOWN", reply.Quality, "isolated UIA", "UNKNOWN", reply.Detail)
                    { Controls = Array.AsReadOnly(reply.Controls), ControlsTruncated = reply.Truncated, WindowHandle = new IntPtr(reply.WindowHandle) };
            }
            catch (OperationCanceledException) { result = Unknown(lifetime.IsCancellationRequested ? "session released" : "observation timed out"); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or ArgumentException)
            { result = Unknown("observation unavailable: " + e.GetType().Name); }
            finally
            {
                if (worker != null)
                {
                    try
                    {
                        worker.Refresh();
                        if (!worker.HasExited)
                        {
                            string? workerPath = worker.MainModule?.FileName;
                            if (workerHandle is { IsClosed: false, IsInvalid: false } && workerCreated != default && worker.StartTime.ToUniversalTime() == workerCreated && string.Equals(workerPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) && TerminateOwnedWorker(workerHandle, 1))
                                CleanupStatus = worker.WaitForExit(2000) ? "owned-worker-stopped" : "owned-worker-exit-unconfirmed";
                            else CleanupStatus = "worker-identity-or-stop-unconfirmed";
                        }
                        else CleanupStatus = "worker-exited";
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { CleanupStatus = "worker-cleanup-unconfirmed"; }
                    worker.Dispose();
                    lock (gate)
                    {
                        activeWorkerId = 0;
                        if (CleanupStatus.EndsWith("unconfirmed", StringComparison.Ordinal)) workerCleanupUnconfirmed = true;
                    }
                }
            }
            if (workerCleanupUnconfirmed) result = Unknown(result.Detail + "; " + CleanupStatus + "; observation suspended");
            return Publish(result, started, seq);
        }
        finally { serial.Release(); }
    }
    Snapshot Publish(Snapshot result, long started, long seq)
    {
        lock (gate)
        {
            if (disposed || seq != sequence) return result with { Quality = Quality.Unavailable, Detail = "session released or request superseded; " + result.Detail, Controls = Array.Empty<ControlObservation>(), WindowHandle = IntPtr.Zero };
            latest = result; latestAt = started; return result;
        }
    }
    public bool Matches(Snapshot snapshot, TimeSpan maxAge)
    {
        lock (gate) return !disposed && ReferenceEquals(snapshot, latest) && snapshot.Run == run && snapshot.Session == session && snapshot.Sequence == sequence && snapshot.Quality == Quality.Partial && !snapshot.ControlsTruncated && maxAge >= TimeSpan.Zero && Stopwatch.GetElapsedTime(latestAt) <= maxAge;
    }
    internal bool Authorize(Snapshot snapshot, TimeSpan maxAge, Func<bool> dispatch)
    {
        lock (gate) return Matches(snapshot, maxAge) && dispatch();
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; sequence++; latest = null; lifetime.Cancel(); } }

    internal static int RunWorker()
    {
        try
        {
            string? line = Console.ReadLine();
            if (line == null || line.Length > 4096) return 2;
            var req = JsonSerializer.Deserialize<WorkerRequest>(line);
            if (req == null || req.Pid <= 0 || req.Sequence <= 0 || req.Deadline <= DateTimeOffset.UtcNow || req.SelectedName?.Length > 64 || req.SelectedId?.Length > 64) return 2;
            if (req.ExitEarly) return 7;
            if (req.Stall) Thread.Sleep(TimeSpan.FromSeconds(30));
            using var process = Process.GetProcessById(req.Pid);
            if (process.StartTime.ToUniversalTime().Ticks != req.CreatedTicks) return 2;
            using var observer = new Session(process, req.Run, root => Session.ReadControlsBounded(root, 512, 32, req.SelectedName, req.SelectedId));
            var snapshot = observer.Observe();
            var reply = new WorkerReply(req.Run, req.Session, req.WrongSequence ? req.Sequence + 1 : req.Sequence, req.Pid, req.CreatedTicks, req.Deadline,
                snapshot.ProcessState, snapshot.WindowState, snapshot.Quality, snapshot.Detail, snapshot.Controls.ToArray(), snapshot.ControlsTruncated, snapshot.WindowHandle.ToInt64());
            Console.WriteLine(JsonSerializer.Serialize(reply));
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException or JsonException) { return 2; }
    }
}
