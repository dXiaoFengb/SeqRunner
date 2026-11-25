using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ConnectorPrototype;

// A deterministic test double; it never reads or controls a real window.
internal sealed class SimulatedWindowSession : IObservationSession
{
    readonly Process process = Process.GetCurrentProcess();
    readonly long observedAt;
    Snapshot latest;
    internal int ObserveCalls { get; private set; }
    internal Snapshot Initial { get; }
    internal SimulatedWindowSession(bool expired)
    {
        observedAt = Stopwatch.GetTimestamp() - (expired ? 3 * Stopwatch.Frequency : 0);
        Initial = latest = new Snapshot("test", Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
            process.Id, process.StartTime.ToUniversalTime(), "ALIVE", "VISIBLE", "UNKNOWN", "UNKNOWN", "UNKNOWN", Quality.Partial, "test double", "UNKNOWN", "simulated") { WindowHandle = new IntPtr(1) };
    }
    public Snapshot Observe() { ObserveCalls++; return latest = Initial with { Sequence = 2, WindowHandle = new IntPtr(2) }; }
    public bool Matches(Snapshot snapshot, TimeSpan age) => ReferenceEquals(snapshot, latest) && Stopwatch.GetElapsedTime(observedAt) <= age;
    public Process FixtureProcess => process;
    public void Dispose() => process.Dispose();
}

internal static class Program
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    static int failures;
    static void Check(bool value, string name) { Console.WriteLine((value ? "PASS " : "FAIL ") + name); if (!value) failures++; }
    [STAThread] static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "observation-worker") return IsolatedSession.RunWorker();
        if (args.Length == 1 && args[0] == "control-worker") return IsolatedControl.RunWorker();
        if (args.Length == 1 && args[0] == "smoke-window") return SmokeWindow();
        if (args.Length == 1 && args[0] == "verify-control-counterexamples") return VerifyControlCounterexamples();
        if (args.Length == 1 && args[0] == "smoke-link") return SmokeLink();
        if (args.Length == 1 && args[0] == "verify-link") return VerifyLink();
        if (args.Length == 1 && args[0] == "smoke-background") return SmokeBackground();
        if (args.Length == 1 && args[0] == "verify-background") return VerifyBackground();
        if (args.Length == 1 && args[0] == "smoke-isolation") return SmokeIsolation();
        if (args.Length == 1 && args[0] == "verify-isolation") return VerifyIsolation();
        if (args.Length == 1 && args[0] == "fixture") { Fixture(); return 0; }
        if (args.Length == 1 && args[0] == "fixture-duplicate-target") { Fixture(true); return 0; }
        if (args.Length == 2 && args[0] == "observe" && int.TryParse(args[1], out var pid)) return ObserveOnly(pid);
        if (args.Length == 4 && args[0] == "observe" && int.TryParse(args[1], out var selectedPid) && (args[2] == "--name" || args[2] == "--id"))
        {
            string selected = args[3];
            if (selected.Length is < 1 or > 64 || selected.Any(char.IsControl)) { Console.Error.WriteLine("Selected value invalid"); return 1; }
            return ObserveOnly(selectedPid, args[2] == "--name" ? selected : null, args[2] == "--id" ? selected : null);
        }
        if (args.Length != 0) { Console.Error.WriteLine("Invalid arguments"); return 2; }
        return Test();
    }
    static int SmokeWindow()
    {
        using (var changed = new SimulatedWindowSession(false))
        {
            var before = changed.Initial;
            bool fresh = changed.Matches(before, TimeSpan.FromSeconds(2));
            var control = new FixtureController(changed, Path.Combine(Path.GetTempPath(), "v3-window-change-" + Guid.NewGuid().ToString("N")));
            var result = control.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), before.Sequence, "fixture-button"), before, true, CancellationToken.None);
            Console.WriteLine($"DETAIL simulated initialFresh={fresh} oldWindow={before.WindowHandle} newWindow=2 dispatch={result.Dispatch} reason={result.Detail}");
            Check(fresh && changed.ObserveCalls == 1 && result.Dispatch == "REJECTED" && result.Detail == "fresh identity unavailable", "fresh observation rejects changed window (test double)");
        }
        using (var expired = new SimulatedWindowSession(true))
        {
            var before = expired.Initial;
            bool fresh = expired.Matches(before, TimeSpan.FromSeconds(2));
            var control = new FixtureController(expired, Path.Combine(Path.GetTempPath(), "v3-window-expired-" + Guid.NewGuid().ToString("N")));
            var result = control.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), before.Sequence, "fixture-button"), before, true, CancellationToken.None);
            Console.WriteLine($"DETAIL simulated initialFresh={fresh} age=at-least-3s dispatch={result.Dispatch} reason={result.Detail}");
            Check(!fresh && expired.ObserveCalls == 0 && result.Dispatch == "REJECTED" && result.Detail == "authorization, observation, command or cancellation invalid", "expired observation rejects before window access (test double)");
        }
        Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1;
    }
    static int SmokeLink()
    {
        Process? fixture = null;
        try
        {
            fixture = StartFixture();
            using var session = new IsolatedSession(fixture, TimeSpan.FromSeconds(3));
            using var monitor = new BackgroundMonitor(session, TimeSpan.FromSeconds(2));
            monitor.Start();
            Snapshot? current = null;
            bool Ready(long after) => SpinWait.SpinUntil(() => { current = monitor.Current(TimeSpan.FromSeconds(2)).Observation; return current?.Quality == Quality.Partial && current.Sequence > after; }, TimeSpan.FromSeconds(10));
            if (!Ready(0) || current == null) throw new InvalidOperationException("observation unavailable");
            string journal = Path.Combine(Path.GetTempPath(), "v3-link-" + Guid.NewGuid().ToString("N") + ".journal");
            var ctl = new IsolatedControl(session, monitor, journal);
            var first = new Command(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(8), current.Sequence, "fixture-button");
            var result = ctl.Execute(first, current, true, CancellationToken.None);
            long previous = current.Sequence;
            Check(result.Dispatch == "SUBMITTED" && result.Effect == "UNVERIFIED" && Ready(previous) && current?.BusinessResult == "UNKNOWN" && Count(fixture) == "count:1", "background observation, single control and fresh observation");
            if (current == null) throw new InvalidOperationException("fresh observation missing");
            var previousObservation = current;
            session.ExitNextForTest();
            bool failed = SpinWait.SpinUntil(() => monitor.Current(TimeSpan.FromSeconds(2)).Connection == MonitorConnection.Degraded, TimeSpan.FromSeconds(8));
            var rejected = ctl.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(8), previousObservation.Sequence, "fixture-button"), previousObservation, true, CancellationToken.None);
            Check(failed && rejected.Dispatch == "REJECTED" && Count(fixture) == "count:1", "monitor failure invalidates observation and prevents control");
            if (!Ready(previous)) throw new InvalidOperationException("observation missing");
            var slow = new Command(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(8), current!.Sequence, "fixture-button");
            var timed = ctl.Execute(slow, current, true, CancellationToken.None, true);
            previous = current.Sequence;
            bool fresh = Ready(previous);
            var repeat = fresh ? new IsolatedControl(session, monitor, journal).Execute(slow with { ObservationSequence = current!.Sequence, Expires = DateTimeOffset.UtcNow.AddSeconds(8) }, current!, true, CancellationToken.None) : null;
            Check(timed.Dispatch == "UNKNOWN" && repeat?.Dispatch == "UNKNOWN" && Count(fixture) == "count:1", "timed out control is not replayed after controller restart");
        }
        catch (Exception e) { failures++; Console.WriteLine("FAIL link smoke: " + e.GetType().Name + ": " + e.Message); }
        finally { if (fixture != null) { try { fixture.Refresh(); if (!fixture.HasExited) { fixture.CloseMainWindow(); fixture.WaitForExit(5000); } } catch (InvalidOperationException) { } fixture.Dispose(); } }
        Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1;
    }
    static int VerifyLink()
    {
        if (SmokeLink() != 0) return 1;
        Process? a = null, b = null;
        try
        {
            a = StartFixture(); b = StartFixture();
            using var session = new IsolatedSession(a, TimeSpan.FromSeconds(3));
            using var other = new IsolatedSession(b, TimeSpan.FromSeconds(3));
            using var monitor = new BackgroundMonitor(session, TimeSpan.FromSeconds(5));
            monitor.Start(); Snapshot? observation = null;
            bool ready = SpinWait.SpinUntil(() => { observation = monitor.Current(TimeSpan.FromSeconds(3)).Observation; return observation?.Quality == Quality.Partial; }, TimeSpan.FromSeconds(8));
            if (!ready || observation == null) throw new InvalidOperationException("observation unavailable");
            var control = new IsolatedControl(session, monitor, Path.Combine(Path.GetTempPath(), "v3-link-verify-" + Guid.NewGuid().ToString("N")));
            Command New(Snapshot s) => new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), s.Sequence, "fixture-button");
            Check(control.Execute(New(observation), observation, false, CancellationToken.None).Dispatch == "REJECTED", "missing authorization rejects control");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Check(control.Execute(New(observation), observation, true, cancelled.Token).Dispatch == "REJECTED", "stopped request rejects control");
            Check(control.Execute(New(observation) with { Expires = DateTimeOffset.UtcNow.AddSeconds(-1) }, observation, true, CancellationToken.None).Dispatch == "REJECTED", "expired command rejects control");
            var wrong = other.Observe();
            Check(control.Execute(New(wrong), wrong, true, CancellationToken.None).Dispatch == "REJECTED", "other instance rejects control");
            var broken = new IsolatedControl(session, monitor, Path.GetTempPath());
            Check(session.Matches(observation, TimeSpan.FromSeconds(2)) && broken.Execute(New(observation), observation, true, CancellationToken.None).Detail == "target or attempt record unavailable", "attempt storage failure rejects control");
            monitor.StopAsync().GetAwaiter().GetResult(); a.Refresh(); b.Refresh();
            Check(control.Execute(New(observation), observation, true, CancellationToken.None).Dispatch == "REJECTED" && !a.HasExited && !b.HasExited && Count(a) == "count:0" && Count(b) == "count:0", "released session rejects control while fixtures remain alive");
        }
        catch (Exception e) { failures++; Console.WriteLine("FAIL link verification: " + e.GetType().Name + ": " + e.Message); }
        finally { foreach (var p in new[] { a, b }) if (p != null) { try { p.Refresh(); if (!p.HasExited) { p.CloseMainWindow(); p.WaitForExit(5000); } } catch (InvalidOperationException) { } p.Dispose(); } }
        Console.WriteLine("Verification failures: " + failures); return failures == 0 ? 0 : 1;
    }
    static int VerifyControlCounterexamples()
    {
        Process? duplicate = null, fixture = null;
        try
        {
            duplicate = StartFixture(true);
            using (var duplicateSession = new IsolatedSession(duplicate, TimeSpan.FromSeconds(3)))
            using (var duplicateMonitor = new BackgroundMonitor(duplicateSession, TimeSpan.FromSeconds(3)))
            {
                duplicateMonitor.Start(); Snapshot? sample = null;
                bool ready = SpinWait.SpinUntil(() => { sample = duplicateMonitor.Current(TimeSpan.FromSeconds(2)).Observation; return sample?.Quality == Quality.Partial; }, TimeSpan.FromSeconds(10));
                if (!ready || sample == null) throw new InvalidOperationException("duplicate observation unavailable");
                var controller = new IsolatedControl(duplicateSession, duplicateMonitor, Path.Combine(Path.GetTempPath(), "v3-duplicate-" + Guid.NewGuid().ToString("N")));
                var result = controller.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(8), sample.Sequence, "fixture-button"), sample, true, CancellationToken.None);
                Check(sample.Controls.Count(x => x.SelectedId == "fixture-button") == 2 && result.Dispatch == "REJECTED" && Count(duplicate) == "count:0", "isolated controller rejects duplicate target controls before dispatch");
            }
            fixture = StartFixture();
            using var session = new IsolatedSession(fixture, TimeSpan.FromSeconds(3));
            using var monitor = new BackgroundMonitor(session, TimeSpan.FromSeconds(3)); monitor.Start(); Snapshot? observation = null;
            bool Ready(long after) => SpinWait.SpinUntil(() => { observation = monitor.Current(TimeSpan.FromSeconds(2)).Observation; return observation?.Quality == Quality.Partial && observation.Sequence > after; }, TimeSpan.FromSeconds(10));
            if (!Ready(0) || observation == null) throw new InvalidOperationException("fixture observation unavailable");
            string journal = Path.Combine(Path.GetTempPath(), "v3-worker-exit-" + Guid.NewGuid().ToString("N"));
            var control = new IsolatedControl(session, monitor, journal);
            var exited = control.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(8), observation.Sequence, "fixture-button"), observation, true, CancellationToken.None, exitEarly: true);
            Check(exited.Dispatch == "UNKNOWN" && exited.Detail == "worker did not become ready" && Count(fixture) == "count:0", "isolated worker abnormal exit is unknown with no action");
            long previous = observation.Sequence; if (!Ready(previous) || observation == null) throw new InvalidOperationException("new observation unavailable");
            var simulated = control.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(8), observation.Sequence, "fixture-button"), observation, true, CancellationToken.None, exitEarly: true, simulateUnconfirmedCleanup: true);
            previous = observation.Sequence; bool blocked = !session.ControlAvailable;
            if (!Ready(previous) || observation == null) throw new InvalidOperationException("post-cleanup observation unavailable");
            var after = new IsolatedControl(session, monitor, journal).Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(8), observation.Sequence, "fixture-button"), observation, true, CancellationToken.None);
            Check(simulated.Dispatch == "UNKNOWN" && blocked && after.Dispatch == "REJECTED" && Count(fixture) == "count:0", "simulated unconfirmed cleanup blocks a new controller in same session");
        }
        catch (Exception e) { failures++; Console.WriteLine("FAIL control counterexamples: " + e.GetType().Name + ": " + e.Message); }
        finally { foreach (var p in new[] { duplicate, fixture }) if (p != null) { try { p.Refresh(); if (!p.HasExited) { p.CloseMainWindow(); p.WaitForExit(5000); } } catch (InvalidOperationException) { } p.Dispose(); } }
        Console.WriteLine("Verification failures: " + failures); return failures == 0 ? 0 : 1;
    }
    static int SmokeBackground()
    {
        Process? fixture = null;
        try
        {
            fixture = StartFixture();
            using var session = new IsolatedSession(fixture, TimeSpan.FromSeconds(3));
            using var monitor = new BackgroundMonitor(session, TimeSpan.FromMilliseconds(100));
            monitor.Start();
            bool observing = SpinWait.SpinUntil(() => monitor.History().Count(x => x.Observation?.Quality == Quality.Partial) >= 2, TimeSpan.FromSeconds(10));
            Snapshot? old = null;
            bool hadCurrent = SpinWait.SpinUntil(() => { old = session.Current(TimeSpan.FromSeconds(3)); return old?.Quality == Quality.Partial; }, TimeSpan.FromSeconds(3));
            Check(observing && hadCurrent && monitor.Current(TimeSpan.FromSeconds(3)).Session == session.Id, "background observation continues after simulated script ends");
            session.ExitNextForTest();
            bool degraded = SpinWait.SpinUntil(() => monitor.History().Any(x => x.Connection == MonitorConnection.Degraded), TimeSpan.FromSeconds(10));
            Check(degraded && old != null && !session.Matches(old, TimeSpan.FromSeconds(10)), "failed background observation invalidates old snapshot");
            long failedAt = monitor.History().LastOrDefault(x => x.Connection == MonitorConnection.Degraded)?.Sequence ?? long.MaxValue;
            bool recovered = SpinWait.SpinUntil(() => monitor.History().Any(x => x.Sequence > failedAt && x.Connection == MonitorConnection.Running && x.Observation?.Quality == Quality.Partial), TimeSpan.FromSeconds(10));
            Check(recovered, "background observation recovers with a fresh sample after worker exit");
            bool stopped = monitor.StopAsync().GetAwaiter().GetResult(); fixture.Refresh();
            Check(stopped && !fixture.HasExited && monitor.Current(TimeSpan.FromSeconds(3)).Observation == null, "release clears observation and leaves target running");
        }
        catch (Exception e) { failures++; Console.WriteLine("FAIL background smoke: " + e.GetType().Name + ": " + e.Message); }
        finally
        {
            if (fixture != null) { try { fixture.Refresh(); if (!fixture.HasExited) { fixture.CloseMainWindow(); fixture.WaitForExit(5000); } } catch (InvalidOperationException) { } fixture.Dispose(); }
        }
        Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1;
    }
    static int VerifyBackground()
    {
        if (SmokeBackground() != 0) return 1;
        Process? fixture = null;
        try
        {
            fixture = StartFixture();
            using var session = new IsolatedSession(fixture, TimeSpan.FromSeconds(3));
            using var monitor = new BackgroundMonitor(session, TimeSpan.FromMilliseconds(100));
            monitor.Start();
            bool ready = SpinWait.SpinUntil(() => monitor.History().Any(x => x.Observation?.Quality == Quality.Partial), TimeSpan.FromSeconds(10));
            fixture.CloseMainWindow(); bool exited = fixture.WaitForExit(5000);
            bool noted = SpinWait.SpinUntil(() => monitor.Current(TimeSpan.FromSeconds(3)).Connection == MonitorConnection.TargetExited, TimeSpan.FromSeconds(10));
            var messages = monitor.History();
            Check(ready && exited && noted && messages.Zip(messages.Skip(1), (a, b) => b.Sequence > a.Sequence).All(x => x), "target exit stops monitoring and message sequences increase");
            Check(messages.All(x => x.Observation == null || (x.Observation.Controls.Count == 0 && x.Observation.WindowHandle == IntPtr.Zero)), "history does not expose controls or actionable handles");
            Check(monitor.StopAsync().GetAwaiter().GetResult(), "target-exited session releases cleanly");
        }
        catch (Exception e) { failures++; Console.WriteLine("FAIL background verification: " + e.GetType().Name + ": " + e.Message); }
        finally
        {
            if (fixture != null) { try { fixture.Refresh(); if (!fixture.HasExited) { fixture.CloseMainWindow(); fixture.WaitForExit(5000); } } catch (InvalidOperationException) { } fixture.Dispose(); }
        }
        Console.WriteLine("Verification failures: " + failures); return failures == 0 ? 0 : 1;
    }
    static int ObserveOnly(int pid, string? selectedName = null, string? selectedId = null)
    {
        try
        {
            using var process = Process.GetProcessById(pid); using var session = new IsolatedSession(process, TimeSpan.FromSeconds(3), selectedName, selectedId);
            Console.WriteLine("Read-only observation: no click, activation, restore, close or termination; supplied PID is not proof of V3 ownership.");
            for (int i = 0; i < 20; i++)
            {
                var s = session.Observe(); Console.WriteLine($"{s.ObservedAt:O} seq={s.Sequence} process={s.ProcessState} window={s.WindowState} quality={s.Quality} task={s.TaskState} update={s.UpdateState} business={s.BusinessResult} detail={s.Detail}");
                foreach (var control in s.Controls) Console.WriteLine($"  type={control.Type} enabled={control.Enabled} offscreen={control.Offscreen} name={control.SelectedName ?? "[not selected]"} id={control.SelectedId ?? "[not selected]"}");
                if (session.CleanupStatus.EndsWith("unconfirmed", StringComparison.Ordinal)) { Console.WriteLine("Worker cleanup unconfirmed; observation suspended. Do not terminate unidentified processes."); return 1; }
                if (s.ProcessState == "EXITED") break;
                Thread.Sleep(1000);
            }
            return 0;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Console.Error.WriteLine("Observation unavailable: " + e.GetType().Name); return 1; }
    }
    static int SmokeIsolation()
    {
        Process? fixture = null;
        try
        {
            fixture = StartFixture();
            using var session = new IsolatedSession(fixture, TimeSpan.FromSeconds(3));
            var good = session.Observe();
            Check(good.Quality == Quality.Partial && good.BusinessResult == "UNKNOWN" && session.Matches(good, TimeSpan.FromSeconds(3)), "isolated controlled observation returns a fresh unknown-business snapshot");
            session.StallNextForTest(); var watch = Stopwatch.StartNew(); var failed = session.Observe();
            Check(failed.Quality == Quality.Unavailable && failed.Detail == "observation timed out" && watch.Elapsed < TimeSpan.FromSeconds(7), "stalled worker times out and returns unavailable");
            var controller = new FixtureController(session, Path.Combine(Path.GetTempPath(), "v3-isolation-smoke-" + Guid.NewGuid().ToString("N")));
            var attempt = controller.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), good.Sequence, "fixture-button"), good, true, CancellationToken.None);
            Check(!session.Matches(good, TimeSpan.FromSeconds(10)) && attempt.Dispatch == "REJECTED", "old observation cannot authorize after timeout");
            fixture.Refresh(); Check(!fixture.HasExited && session.CleanupStatus == "owned-worker-stopped", "only the owned stalled worker was stopped; target remains alive");
        }
        catch (Exception e) { failures++; Console.WriteLine("FAIL isolation smoke: " + e.GetType().Name + ": " + e.Message); }
        finally
        {
            if (fixture != null) { try { fixture.Refresh(); if (!fixture.HasExited) { fixture.CloseMainWindow(); fixture.WaitForExit(5000); } } catch (InvalidOperationException) { } fixture.Dispose(); }
        }
        Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1;
    }
    static int VerifyIsolation()
    {
        if (SmokeIsolation() != 0) return 1;
        Process? fixture = null;
        try
        {
            fixture = StartFixture();
            using var session = new IsolatedSession(fixture, TimeSpan.FromSeconds(3));
            var beforeExit = session.Observe(); session.ExitNextForTest(); var failed = session.Observe();
            Check(failed.Quality == Quality.Unavailable && !session.Matches(beforeExit, TimeSpan.FromSeconds(10)), "worker abnormal exit invalidates prior observation");
            var recovered = session.Observe(); session.WrongSequenceNextForTest(); var mismatch = session.Observe();
            Check(recovered.Quality == Quality.Partial && mismatch.Quality == Quality.Unavailable && !session.Matches(recovered, TimeSpan.FromSeconds(10)), "wrong sequence is rejected and prior observation invalidated");
            var recoveredAgain = session.Observe();
            Check(recoveredAgain.Quality == Quality.Partial && session.Matches(recoveredAgain, TimeSpan.FromSeconds(3)), "later observation can recover after worker failure");
            using (var blocked = new IsolatedSession(fixture, TimeSpan.FromSeconds(3)))
            {
                blocked.SimulateUnconfirmedCleanupForTest(); var suspended = blocked.Observe(); fixture.Refresh();
                Check(suspended.Quality == Quality.Unavailable && suspended.Detail.Contains("suspended", StringComparison.Ordinal) && blocked.ActiveWorkerId == 0 && !fixture.HasExited, "unconfirmed cleanup suspends observation without launching another worker");
            }
            session.StallNextForTest(); var pending = Task.Run(session.Observe);
            bool started = SpinWait.SpinUntil(() => session.ActiveWorkerId > 0, TimeSpan.FromSeconds(3));
            session.Dispose(); bool returned = pending.Wait(TimeSpan.FromSeconds(7)); fixture.Refresh();
            Check(started && returned && pending.Result.Quality == Quality.Unavailable && !session.Matches(recoveredAgain, TimeSpan.FromSeconds(10)) && !fixture.HasExited, "session release cancels worker and preserves target");
        }
        catch (Exception e) { failures++; Console.WriteLine("FAIL isolation verification: " + e.GetType().Name + ": " + e.Message); }
        finally
        {
            if (fixture != null) { try { fixture.Refresh(); if (!fixture.HasExited) { fixture.CloseMainWindow(); fixture.WaitForExit(5000); } } catch (InvalidOperationException) { } fixture.Dispose(); }
        }
        Console.WriteLine("Verification failures: " + failures); return failures == 0 ? 0 : 1;
    }
    static void Fixture(bool duplicateTarget = false)
    {
        var app = new Application();
        var window = new Window { Title = "Connector controlled fixture", Width = 420, Height = 180, ShowInTaskbar = true };
        var panel = new StackPanel(); var label = new TextBlock { Text = "count:0" }; AutomationProperties.SetAutomationId(label, "fixture-count");
        var button = new Button { Content = "controlled action" }; AutomationProperties.SetAutomationId(button, "fixture-button");
        var chinese = new Button { Content = "开始任务（模拟）" }; AutomationProperties.SetAutomationId(chinese, "fixture-chinese");
        var duplicate = new Button { Content = "开始任务（模拟）" }; AutomationProperties.SetAutomationId(duplicate, "fixture-duplicate");
        var privateButton = new Button { Content = "private-name-98765" }; AutomationProperties.SetAutomationId(privateButton, "private-id-98765");
        var replaceButton = new Button { Content = "replace fixture window" }; AutomationProperties.SetAutomationId(replaceButton, "fixture-replace-window");
        replaceButton.Click += (_, _) =>
        {
            var replacement = new Window { Title = "Connector replacement fixture", Width = 320, Height = 140, Content = new TextBlock { Text = "replacement" } };
            replacement.Show(); app.MainWindow = replacement; window.Close();
        };
        int count = 0; button.Click += (_, _) => label.Text = "count:" + ++count;
        panel.Children.Add(label); panel.Children.Add(button); panel.Children.Add(chinese); panel.Children.Add(duplicate); panel.Children.Add(privateButton); panel.Children.Add(replaceButton);
        if (duplicateTarget) { var second = new Button { Content = "controlled action" }; AutomationProperties.SetAutomationId(second, "fixture-button"); panel.Children.Add(second); }
        window.Content = panel; app.Run(window);
    }
    static Process StartFixture(bool duplicateTarget = false)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException();
        var p = Process.Start(new ProcessStartInfo(exe, duplicateTarget ? "fixture-duplicate-target" : "fixture") { UseShellExecute = false }) ?? throw new InvalidOperationException();
        var until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromSeconds(12)) { p.Refresh(); if (p.HasExited) throw new InvalidOperationException("fixture exited"); if (p.MainWindowHandle != IntPtr.Zero) return p; Thread.Sleep(100); }
        throw new TimeoutException("fixture window unavailable");
    }
    static string Count(Process p)
    {
        p.Refresh(); var window = AutomationElement.FromHandle(p.MainWindowHandle);
        var x = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "fixture-count"));
        return x?.Current.Name ?? "unknown";
    }
    static int Test()
    {
        Process? a = null, b = null;
        try
        {
            Check(ObservationResolver.Resolve("UNKNOWN", "UNKNOWN") == ("UNKNOWN", Quality.Unavailable), "simulated missing observations remain unknown");
            Check(ObservationResolver.Resolve("RUNNING", "COMPLETED") == ("UNKNOWN", Quality.Conflict), "simulated contradictory observations remain conflict");
            a = StartFixture(); b = StartFixture();
            using var s = new Session(a, Guid.NewGuid()); using var other = new Session(b, Guid.NewGuid());
            var foregroundBefore = GetForegroundWindow();
            var first = s.Observe(); var bfirst = other.Observe();
            Check(GetForegroundWindow() == foregroundBefore, "read-only observations do not change foreground in controlled run");
            Check(first.Quality == Quality.Partial && first.Pid == a.Id && first.BusinessResult == "UNKNOWN", "read-only monitor binds controlled process and leaves business unknown");
            Check(first.Controls.Count(x => x.SelectedName == "开始任务（模拟）") == 2 && first.Controls.Any(x => x.SelectedName == "controlled action") && first.Controls.All(x => x.Type == "Button"), "bounded observer reports Chinese English and duplicate buttons");
            var (limitedControls, wasTruncated) = Session.ReadControlsBounded(AutomationElement.FromHandle(a.MainWindowHandle), 512, 2);
            Check(limitedControls.Count == 2 && wasTruncated, "control enumeration reports configured truncation");
            var (selectedControls, _) = Session.ReadControlsBounded(AutomationElement.FromHandle(a.MainWindowHandle), 512, 32, "private-name-98765", "private-id-98765");
            Check(selectedControls.Any(x => x.SelectedName == "[user-selected]" && x.SelectedId == "[user-selected]") && selectedControls.All(x => x.SelectedName != "private-name-98765" && x.SelectedId != "private-id-98765"), "explicit selector is matched without echoing its value");
            using (var truncatedMonitor = new Session(a, Guid.NewGuid(), root => Session.ReadControlsBounded(root, 512, 2)))
            {
                var limited = truncatedMonitor.Observe();
                Check(limited.ControlsTruncated && !truncatedMonitor.Matches(limited, TimeSpan.FromSeconds(2)), "truncated observation cannot authorize control");
            }
            Check(first.Controls.Any(x => x.SelectedName == null && x.SelectedId == null) && first.Controls.All(x => x.SelectedName != "private-name-98765" && x.SelectedId != "private-id-98765"), "unselected control text and id are not exposed");
            Check(s.Current(TimeSpan.Zero)?.Controls.Count == 0, "stale snapshot contains no actionable controls");
            using (var failingMonitor = new Session(a, Guid.NewGuid(), _ => throw new InvalidOperationException("simulated UIA failure")))
            {
                var unavailable = failingMonitor.Observe();
                Check(unavailable.Quality == Quality.Unavailable && unavailable.Controls.Count == 0 && !failingMonitor.Matches(unavailable, TimeSpan.FromSeconds(2)), "monitor failure reports unavailable and prevents authorization");
            }
            Check(s.Connected && s.Current(TimeSpan.FromMinutes(1)) == first && s.Current(TimeSpan.Zero)?.Quality == Quality.Stale, "connection status and expired snapshot are explicit");
            Check(!s.Matches(bfirst, TimeSpan.FromSeconds(2)), "other instance rejected");
            Check(Count(a) == "count:0" && Count(b) == "count:0", "monitor performs no action");
            string journal = Path.Combine(Path.GetTempPath(), "v3-connector-fixture-" + Guid.NewGuid().ToString("N") + ".journal");
            var ctl = new FixtureController(s, journal); var command = new Command(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), first.Sequence, "fixture-button");
            var rejected = ctl.Execute(command, first, false, CancellationToken.None);
            Check(rejected.Dispatch == "REJECTED" && Count(a) == "count:0", "unauthorized control rejected");
            var clone = first with { ObservedAt = DateTimeOffset.UtcNow };
            var cloneResult = ctl.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), clone.Sequence, "fixture-button"), clone, true, CancellationToken.None);
            Check(cloneResult.Dispatch == "REJECTED" && Count(a) == "count:0", "copied or altered observation cannot authorize action");
            var wrong = ctl.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), bfirst.Sequence, "fixture-button"), bfirst, true, CancellationToken.None);
            Check(wrong.Dispatch == "REJECTED" && Count(a) == "count:0" && Count(b) == "count:0", "control rejects observation from another instance");
            var expired = s.Observe();
            var er = ctl.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-1), expired.Sequence, "fixture-button"), expired, true, CancellationToken.None);
            Check(er.Dispatch == "REJECTED" && Count(a) == "count:0", "expired command rejected");
            var stale = s.Observe(); s.Observe();
            var sr = ctl.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), stale.Sequence, "fixture-button"), stale, true, CancellationToken.None);
            Check(sr.Dispatch == "REJECTED" && Count(a) == "count:0", "stale observation rejected");
            using var stopped = new CancellationTokenSource(); stopped.Cancel(); var obs = s.Observe();
            var cancel = ctl.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), obs.Sequence, "fixture-button"), obs, true, stopped.Token);
            Check(cancel.Dispatch == "REJECTED" && Count(a) == "count:0", "cancelled action rejected");
            using (var locked = new FileStream(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var lockedObs = s.Observe();
                var lockedResult = ctl.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), lockedObs.Sequence, "fixture-button"), lockedObs, true, CancellationToken.None);
                Check(lockedResult.Dispatch == "REJECTED" && Count(a) == "count:0", "locked action journal prevents dispatch");
            }
            string brokenJournal = journal + ".partial"; File.WriteAllText(brokenJournal, "partial");
            var corruptController = new FixtureController(s, brokenJournal); var corruptObs = s.Observe();
            Check(corruptController.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), corruptObs.Sequence, "fixture-button"), corruptObs, true, CancellationToken.None).Dispatch == "REJECTED" && Count(a) == "count:0", "partial action journal prevents dispatch");
            var preexistingController = new FixtureController(s, journal);
            obs = s.Observe(); bool chooseFixture = obs.Quality == Quality.Partial && obs.BusinessResult == "UNKNOWN" && obs.Controls.Count(x => x.SelectedId == "fixture-button" && x.Enabled) == 1;
            var run = new Command(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), obs.Sequence, "fixture-button");
            Check(chooseFixture, "simulated script selects the single controlled fixture button");
            var result = ctl.Execute(run, obs, true, CancellationToken.None); var again = ctl.Execute(run, obs, true, CancellationToken.None);
            Check(result.Dispatch == "SUBMITTED" && result.Effect == "UNVERIFIED" && again == result, "controlled invoke submitted once with unverified effect");
            Check(Count(a) == "count:1" && Count(b) == "count:0", "only intended fixture receives one action");
            var afterAction = s.Observe(); Check(afterAction.Sequence > obs.Sequence && afterAction.BusinessResult == "UNKNOWN" && Count(a) == "count:1", "script re-observes without claiming business success");
            var background = other.Observe(); Check(other.Connected && background.Quality == Quality.Partial && background.Pid == b.Id, "separate monitoring session remains available during fixture action");
            using (var backgroundSession = new Session(b, Guid.NewGuid()))
            using (var loop = new FixtureMonitorLoop(backgroundSession))
            {
                loop.Start(); bool started = SpinWait.SpinUntil(() => loop.Samples >= 2, TimeSpan.FromSeconds(3));
                int beforeScriptEnds = loop.Samples;
                Task.CompletedTask.GetAwaiter().GetResult();
                bool continued = SpinWait.SpinUntil(() => loop.Samples > beforeScriptEnds, TimeSpan.FromSeconds(3));
                loop.StopAsync().GetAwaiter().GetResult(); b.Refresh();
                Check(started && continued && !b.HasExited, "test-only monitor continues after simulated script ends and release leaves target alive");
            }
            var freshAgain = s.Observe();
            Check(preexistingController.Execute(run with { ObservationSequence = freshAgain.Sequence }, freshAgain, true, CancellationToken.None).Dispatch == "UNKNOWN" && Count(a) == "count:1", "previously created controller rereads journal before action");
            var restarted = new FixtureController(s, journal);
            var afterRestart = s.Observe();
            Check(restarted.Execute(run with { ObservationSequence = afterRestart.Sequence }, afterRestart, true, CancellationToken.None).Dispatch == "UNKNOWN" && Count(a) == "count:1", "persisted attempt prevents replay after controller restart");
            var badStore = new FixtureController(s, Path.GetTempPath()); var current = s.Observe();
            Check(badStore.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), current.Sequence, "fixture-button"), current, true, CancellationToken.None).Dispatch == "REJECTED" && Count(a) == "count:1", "record failure prevents control");
            var beforeClose = s.Observe(); s.Dispose(); a.Refresh(); Check(!a.HasExited, "disconnect leaves fixture running");
            Check(!s.Matches(beforeClose, TimeSpan.FromSeconds(2)) && s.Observe().Quality == Quality.Unavailable, "released session cannot authorize actions");
            Check(!s.Connected && s.Current(TimeSpan.FromMinutes(1)) == null, "released session exposes no cached current snapshot");
            var observationAge = Stopwatch.StartNew(); var oldWindow = other.Observe();
            var otherRoot = AutomationElement.FromHandle(b.MainWindowHandle);
            var replace = otherRoot.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "fixture-replace-window"));
            ((InvokePattern)replace.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var waitWindow = Stopwatch.StartNew();
            do { Thread.Sleep(100); b.Refresh(); } while (b.MainWindowHandle == oldWindow.WindowHandle && waitWindow.Elapsed < TimeSpan.FromSeconds(5));
            bool previousObservationStillFresh = other.Matches(oldWindow, TimeSpan.FromSeconds(2));
            var replacedControl = new FixtureController(other, journal + ".replacement");
            var oldWindowAction = replacedControl.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), oldWindow.Sequence, "fixture-button"), oldWindow, true, CancellationToken.None);
            Console.WriteLine($"DETAIL real replacement ageMs={observationAge.ElapsedMilliseconds} oldWindow={oldWindow.WindowHandle} currentWindow={b.MainWindowHandle} freshBefore={previousObservationStillFresh} dispatch={oldWindowAction.Dispatch} reason={oldWindowAction.Detail}");
            Console.WriteLine("INCONCLUSIVE real fresh-window replacement: rejection reason may combine freshness and identity; see test double separately");
            Check(oldWindowAction.Dispatch == "REJECTED", "replacement or expired observation cannot authorize action");
            var newWindow = other.Observe();
            Check(newWindow.Quality == Quality.Partial && newWindow.WindowHandle != IntPtr.Zero && newWindow.WindowHandle != oldWindow.WindowHandle && !other.Matches(oldWindow, TimeSpan.FromSeconds(2)), "recreated fixture window receives fresh observation and invalidates old snapshot");
            b.CloseMainWindow(); b.WaitForExit(5000);
            var exited = other.Observe(); Check(exited.ProcessState == "EXITED" && exited.Quality == Quality.Unavailable && exited.Controls.Count == 0, "exited target invalidates controls and reports unavailable");
        }
        catch (Exception e) { failures++; Console.WriteLine("FAIL fixture environment: " + e); }
        finally
        {
            foreach (var p in new[] { a, b }) if (p != null) { try { p.Refresh(); if (!p.HasExited) { p.CloseMainWindow(); p.WaitForExit(5000); } } catch (InvalidOperationException) { } p.Dispose(); }
        }
        Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1;
    }
}
