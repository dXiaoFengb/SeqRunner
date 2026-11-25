using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ConnectorPrototype;

internal enum Quality { Valid, Partial, Stale, Unavailable, Conflict }
internal sealed record ControlObservation(string Type, bool Enabled, bool Offscreen, string? SelectedName, string? SelectedId);
internal sealed record Snapshot(string Version, Guid Run, Guid Session, long Sequence, DateTimeOffset ObservedAt,
    int Pid, DateTime CreatedUtc, string ProcessState, string WindowState, string TaskState, string UpdateState,
    string ErrorState, Quality Quality, string Source, string BusinessResult, string Detail)
{
    internal IReadOnlyList<ControlObservation> Controls { get; init; } = Array.Empty<ControlObservation>();
    internal bool ControlsTruncated { get; init; }
    internal IntPtr WindowHandle { get; init; }
}
internal sealed record Command(Guid Id, DateTimeOffset Expires, long ObservationSequence, string ControlId);
internal sealed record Result(Guid Id, string Dispatch, string Effect, string Detail);
internal interface IObservationSession : IDisposable
{
    Snapshot Observe();
    bool Matches(Snapshot snapshot, TimeSpan maxAge);
    Process FixtureProcess { get; }
}

internal static class ObservationResolver
{
    internal static (string State, Quality Quality) Resolve(params string[] observations)
    {
        var known = observations.Where(x => !string.IsNullOrWhiteSpace(x) && x != "UNKNOWN").Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return known.Length switch { 0 => ("UNKNOWN", Quality.Unavailable), 1 => (known[0], Quality.Partial), _ => ("UNKNOWN", Quality.Conflict) };
    }
}

// Test-only cooperative observation loop; it does not own or terminate the target process.
internal sealed class FixtureMonitorLoop(Session session) : IDisposable
{
    readonly CancellationTokenSource stop = new();
    Task? worker;
    int samples;
    internal int Samples => Volatile.Read(ref samples);
    internal void Start()
    {
        if (worker != null) throw new InvalidOperationException("already started");
        worker = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                session.Observe(); Interlocked.Increment(ref samples);
                try { await Task.Delay(50, stop.Token); } catch (OperationCanceledException) { break; }
            }
        });
    }
    internal async Task StopAsync() { stop.Cancel(); if (worker != null) await worker.WaitAsync(TimeSpan.FromSeconds(3)); }
    public void Dispose() { stop.Cancel(); }
}

// A read-only session may inspect a user-supplied PID; that alone does not establish V3 ownership.
internal sealed class Session(Process process, Guid run, Func<AutomationElement, (IReadOnlyList<ControlObservation> Items, bool Truncated)>? readControls = null) : IObservationSession
{
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    readonly DateTime created = process.StartTime.ToUniversalTime();
    readonly Guid id = Guid.NewGuid();
    readonly object stateGate = new();
    Snapshot? latest;
    long latestAt;
    bool disposed;
    long sequence;
    static readonly HashSet<string> SelectedNames = new(StringComparer.Ordinal)
        { "Link Start!", "开始", "开始任务", "停止", "Start", "Stop", "controlled action", "开始任务（模拟）" };
    static readonly HashSet<string> SelectedIds = new(StringComparer.Ordinal) { "fixture-button", "fixture-chinese", "fixture-duplicate" };
    internal static (IReadOnlyList<ControlObservation> Items, bool Truncated) ReadControls(AutomationElement root) => ReadControlsBounded(root, 512, 32);
    internal static (IReadOnlyList<ControlObservation> Items, bool Truncated) ReadControlsBounded(AutomationElement root, int maxNodes, int maxControls, string? requestedName = null, string? requestedId = null)
    {
        if (maxNodes <= 0 || maxControls <= 0) throw new ArgumentOutOfRangeException();
        var found = new List<ControlObservation>(); var walker = TreeWalker.ControlViewWalker;
        int ownerPid = root.Current.ProcessId;
        var pending = new Stack<AutomationElement>();
        var first = walker.GetFirstChild(root); if (first != null) pending.Push(first);
        int visited = 0;
        while (pending.Count > 0 && visited < maxNodes && found.Count < maxControls)
        {
            var element = pending.Pop(); visited++;
            var sibling = walker.GetNextSibling(element); if (sibling != null) pending.Push(sibling);
            var current = element.Current;
            if (current.ProcessId != ownerPid) continue;
            var child = walker.GetFirstChild(element); if (child != null) pending.Push(child);
            if (current.ControlType != ControlType.Button) continue;
            string name = current.Name ?? "", controlId = current.AutomationId ?? "";
            found.Add(new("Button", current.IsEnabled, current.IsOffscreen,
                name.Length <= 64 && SelectedNames.Contains(name) ? name : name.Length <= 64 && requestedName != null && name == requestedName ? "[user-selected]" : null,
                controlId.Length <= 64 && SelectedIds.Contains(controlId) ? controlId : controlId.Length <= 64 && requestedId != null && controlId == requestedId ? "[user-selected]" : null));
        }
        return (found.AsReadOnly(), pending.Count > 0);
    }
    internal Guid Id => id;
    internal string[] Capabilities => ["process.identity", "window.identity", "uia.read", "fixture.invoke"];
    internal bool Connected { get { lock (stateGate) return !disposed; } }
    internal Snapshot? Current(TimeSpan maxAge)
    {
        lock (stateGate)
        {
            if (disposed || latest == null) return null;
            return maxAge < TimeSpan.Zero || Stopwatch.GetElapsedTime(latestAt) > maxAge ? latest with { Quality = Quality.Stale, Detail = "observation expired", Controls = Array.Empty<ControlObservation>(), WindowHandle = IntPtr.Zero } : latest;
        }
    }
    public Snapshot Observe()
    {
        var now = DateTimeOffset.UtcNow;
        long observationStarted = Stopwatch.GetTimestamp();
        Snapshot Make(string ps, string ws, Quality q, string detail, IReadOnlyList<ControlObservation>? controls = null, bool truncated = false, IntPtr window = default)
        {
            lock (stateGate)
            {
                if (disposed) { ps = "UNKNOWN"; ws = "UNKNOWN"; q = Quality.Unavailable; detail = "session released"; }
                if (disposed) { controls = null; truncated = false; window = IntPtr.Zero; }
                var result = new Snapshot("0.1", run, id, ++sequence, now, process.Id, created, ps, ws, "UNKNOWN", "UNKNOWN", "UNKNOWN", q, "Win32/UIA", "UNKNOWN", detail)
                    { Controls = controls ?? Array.Empty<ControlObservation>(), ControlsTruncated = truncated, WindowHandle = window };
                latest = result; latestAt = observationStarted; return result;
            }
        }
        lock (stateGate) if (disposed) return Make("UNKNOWN", "UNKNOWN", Quality.Unavailable, "session released");
        try
        {
            process.Refresh();
            if (process.HasExited) return Make("EXITED", "UNKNOWN", Quality.Unavailable, "process exited");
            if (process.StartTime.ToUniversalTime() != created) return Make("UNKNOWN", "UNKNOWN", Quality.Unavailable, "identity changed");
            var hwnd = process.MainWindowHandle;
            if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid != process.Id)
                return Make("ALIVE", "UNKNOWN", Quality.Unavailable, "window unavailable");
            var element = AutomationElement.FromHandle(hwnd);
            if (element == null || element.Current.ProcessId != process.Id) return Make("ALIVE", "UNKNOWN", Quality.Unavailable, "UIA identity unavailable");
            var (controls, truncated) = (readControls ?? ReadControls)(element);
            process.Refresh();
            if (process.HasExited || process.StartTime.ToUniversalTime() != created || process.MainWindowHandle != hwnd || GetWindowThreadProcessId(hwnd, out pid) == 0 || pid != process.Id)
                return Make("UNKNOWN", "UNKNOWN", Quality.Unavailable, "identity or window changed during observation");
            return Make("ALIVE", element.Current.IsOffscreen ? "OFFSCREEN_REPORTED" : "ONSCREEN_REPORTED", Quality.Partial,
                $"window/UIA available; buttons={controls.Count}; limited={truncated}; state semantics unverified", controls, truncated, hwnd);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ElementNotAvailableException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException or TimeoutException)
        { return Make("UNKNOWN", "UNKNOWN", Quality.Unavailable, e.GetType().Name); }
    }
    public bool Matches(Snapshot s, TimeSpan age)
    {
        lock (stateGate) return !disposed && ReferenceEquals(s, latest) && s.Session == id && s.Run == run && s.Pid == process.Id && s.CreatedUtc == created && s.Sequence == sequence && s.Quality == Quality.Partial && !s.ControlsTruncated && age >= TimeSpan.Zero && Stopwatch.GetElapsedTime(latestAt) <= age;
    }
    public Process FixtureProcess => process;
    public void Dispose() { lock (stateGate) { disposed = true; latest = null; } } // Does not close the target.
}

internal sealed class FixtureController
{
    readonly IObservationSession session;
    readonly string journalPath;
    readonly ConcurrentDictionary<Guid, Result> results = new();
    readonly object gate = new();
    internal FixtureController(IObservationSession session, string journalPath)
    {
        this.session = session; this.journalPath = journalPath;
    }
    internal Result Execute(Command command, Snapshot observed, bool authorized, CancellationToken stop)
    {
        lock (gate)
        {
            if (results.TryGetValue(command.Id, out var prior)) return prior;
            Result result;
            if (!authorized || stop.IsCancellationRequested || command.Expires <= DateTimeOffset.UtcNow || command.ObservationSequence != observed.Sequence || !session.Matches(observed, TimeSpan.FromSeconds(2)) || command.ControlId != "fixture-button")
                result = new(command.Id, "REJECTED", "UNVERIFIED", "authorization, observation, command or cancellation invalid");
            else
            {
                // Persist the attempt before any effect; restart treats it as unknown and never replays it.
                try
                {
                    using var stream = new FileStream(journalPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
                    using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                    string history = reader.ReadToEnd();
                    if (history.Length > 0 && !history.EndsWith('\n')) throw new InvalidDataException("partial journal");
                    foreach (var line in history.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!Guid.TryParseExact(line.TrimEnd('\r'), "N", out var previous)) throw new InvalidDataException("invalid journal");
                        if (previous == command.Id) return new(command.Id, "UNKNOWN", "UNVERIFIED", "command previously attempted; no replay");
                    }
                    stream.Seek(0, SeekOrigin.End);
                    using var writer = new StreamWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                    writer.WriteLine(command.Id.ToString("N")); writer.Flush(); stream.Flush(true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
                { return new(command.Id, "REJECTED", "UNVERIFIED", "attempt record unavailable"); }
                result = new(command.Id, "ATTEMPTING", "UNVERIFIED", "effect may be unknown");
                results[command.Id] = result;
                try
                {
                    var fresh = session.Observe();
                    var p = session.FixtureProcess; p.Refresh();
                    if (stop.IsCancellationRequested || command.Expires <= DateTimeOffset.UtcNow || !session.Matches(fresh, TimeSpan.FromSeconds(2)) || fresh.WindowHandle == IntPtr.Zero || fresh.WindowHandle != observed.WindowHandle)
                        result = new(command.Id, "REJECTED", "UNVERIFIED", "fresh identity unavailable");
                    else
                    {
                        var window = AutomationElement.FromHandle(p.MainWindowHandle);
                        var matches = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, command.ControlId));
                        p.Refresh();
                        if (p.HasExited || p.StartTime.ToUniversalTime() != fresh.CreatedUtc || matches.Count != 1 || matches[0].Current.ProcessId != p.Id || !matches[0].Current.IsEnabled || !matches[0].TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) || p.MainWindowHandle != fresh.WindowHandle || !session.Matches(fresh, TimeSpan.FromSeconds(2)) || stop.IsCancellationRequested || command.Expires <= DateTimeOffset.UtcNow)
                            result = new(command.Id, "REJECTED", "UNVERIFIED", "fixture control not unique or unavailable");
                        else { ((InvokePattern)pattern).Invoke(); result = new(command.Id, "SUBMITTED", "UNVERIFIED", "invoked; effect requires separate observation"); }
                    }
                }
                catch (Exception e) when (e is InvalidOperationException or ElementNotAvailableException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
                { result = new(command.Id, "UNKNOWN", "UNVERIFIED", e.GetType().Name); }
            }
            results[command.Id] = result;
            return result;
        }
    }
}
