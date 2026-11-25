namespace ConnectorPrototype;

internal enum MonitorConnection { Created, Running, Degraded, Blocked, TargetExited, IdentityUnknown, Faulted, Stopping, Stopped, StopUnconfirmed }
internal sealed record MonitorMessage(Guid Session, long Sequence, DateTimeOffset RecordedAt, MonitorConnection Connection, Snapshot? Observation);

// This is a test-only host; it does not schedule, control, or own the target process.
internal sealed class BackgroundMonitor(IsolatedSession session, TimeSpan interval) : IDisposable
{
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    readonly Queue<MonitorMessage> history = new();
    Task? worker;
    MonitorConnection state = MonitorConnection.Created;
    long sequence;
    bool released;
    int failNext;
    internal Guid SessionId => session.Id;
    internal CancellationToken Lifetime => stop.Token;
    internal void FailNextForTest() => Interlocked.Exchange(ref failNext, 1);

    void Record(MonitorConnection next, Snapshot? snapshot = null)
    {
        state = next;
        // History is evidence only; it never carries a handle or controls usable for an action.
        var evidence = snapshot == null ? null : snapshot with { Controls = Array.Empty<ControlObservation>(), WindowHandle = IntPtr.Zero };
        history.Enqueue(new(session.Id, ++sequence, DateTimeOffset.UtcNow, next, evidence));
        while (history.Count > 64) history.Dequeue();
    }
    internal IReadOnlyList<MonitorMessage> History() { lock (gate) return history.ToArray(); }
    internal bool Authorize(Snapshot snapshot, Func<bool> dispatch)
    {
        lock (gate)
        {
            return !released && state == MonitorConnection.Running && session.Authorize(snapshot, TimeSpan.FromSeconds(2), dispatch);
        }
    }
    internal MonitorMessage Current(TimeSpan maxAge)
    {
        lock (gate)
        {
            Snapshot? snapshot = state is MonitorConnection.Running or MonitorConnection.Degraded ? session.Current(maxAge) : null;
            return new(session.Id, sequence, DateTimeOffset.UtcNow, state, snapshot);
        }
    }
    internal void Start()
    {
        lock (gate)
        {
            if (released || worker != null || interval < TimeSpan.FromMilliseconds(50) || interval > TimeSpan.FromMinutes(1)) throw new InvalidOperationException("invalid monitor state or interval");
            Record(MonitorConnection.Running);
            worker = Task.Run(Loop);
        }
    }
    async Task Loop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (Interlocked.Exchange(ref failNext, 0) != 0) throw new InvalidOperationException("simulated monitor failure");
                var observation = session.Observe();
                bool terminal;
                lock (gate)
                {
                    if (stop.IsCancellationRequested) break;
                    var next = observation.ProcessState == "EXITED" ? MonitorConnection.TargetExited
                        : observation.Detail.Contains("identity changed", StringComparison.Ordinal) ? MonitorConnection.IdentityUnknown
                        : session.CleanupStatus.EndsWith("unconfirmed", StringComparison.Ordinal) ? MonitorConnection.Blocked
                        : observation.Quality == Quality.Partial ? MonitorConnection.Running : MonitorConnection.Degraded;
                    Record(next, observation);
                    terminal = next is MonitorConnection.TargetExited or MonitorConnection.IdentityUnknown or MonitorConnection.Blocked;
                }
                if (terminal) { stop.Cancel(); session.Dispose(); return; }
                await Task.Delay(interval, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception)
        {
            session.Dispose();
            lock (gate) if (!stop.IsCancellationRequested) Record(MonitorConnection.Faulted);
            stop.Cancel();
        }
    }
    internal async Task<bool> StopAsync()
    {
        Task? pending;
        lock (gate)
        {
            if (released) return state == MonitorConnection.Stopped;
            released = true; Record(MonitorConnection.Stopping); pending = worker;
        }
        stop.Cancel(); session.Dispose();
        try { if (pending != null) await pending.WaitAsync(TimeSpan.FromSeconds(7)).ConfigureAwait(false); }
        catch (TimeoutException) { lock (gate) Record(MonitorConnection.StopUnconfirmed); return false; }
        catch (Exception) { lock (gate) Record(MonitorConnection.Faulted); return false; }
        lock (gate) Record(MonitorConnection.Stopped);
        return true;
    }
    public void Dispose() { StopAsync().GetAwaiter().GetResult(); }
}
