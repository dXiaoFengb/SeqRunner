using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ConnectorPrototype;

internal static class Program
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    static int failures;
    static void Check(bool value, string name) { Console.WriteLine((value ? "PASS " : "FAIL ") + name); if (!value) failures++; }
    [STAThread] static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "observation-worker") return IsolatedSession.RunWorker();
        if (args.Length == 1 && args[0] == "smoke-isolation") return SmokeIsolation();
        if (args.Length == 1 && args[0] == "verify-isolation") return VerifyIsolation();
        if (args.Length == 1 && args[0] == "fixture") { Fixture(); return 0; }
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
    static void Fixture()
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
        panel.Children.Add(label); panel.Children.Add(button); panel.Children.Add(chinese); panel.Children.Add(duplicate); panel.Children.Add(privateButton); panel.Children.Add(replaceButton); window.Content = panel; app.Run(window);
    }
    static Process StartFixture()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException();
        var p = Process.Start(new ProcessStartInfo(exe, "fixture") { UseShellExecute = false }) ?? throw new InvalidOperationException();
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
            var oldWindow = other.Observe();
            var otherRoot = AutomationElement.FromHandle(b.MainWindowHandle);
            var replace = otherRoot.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "fixture-replace-window"));
            ((InvokePattern)replace.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var waitWindow = Stopwatch.StartNew();
            do { Thread.Sleep(100); b.Refresh(); } while (b.MainWindowHandle == oldWindow.WindowHandle && waitWindow.Elapsed < TimeSpan.FromSeconds(5));
            bool previousObservationStillFresh = other.Matches(oldWindow, TimeSpan.FromSeconds(2));
            var replacedControl = new FixtureController(other, journal + ".replacement");
            var oldWindowAction = replacedControl.Execute(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(10), oldWindow.Sequence, "fixture-button"), oldWindow, true, CancellationToken.None);
            Check(previousObservationStillFresh && oldWindowAction.Dispatch == "REJECTED", "replacement window cannot inherit prior action authorization");
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
