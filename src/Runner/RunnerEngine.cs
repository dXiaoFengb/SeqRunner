using System.Diagnostics;
using System.IO;
using System.Text;

namespace Runner;

public sealed class RunnerEngine
{
    readonly Store store;
    readonly Action changed;
    readonly TimeProvider clock;
    public RunnerEngine(Store store, Action changed, TimeProvider? clock = null) { this.store=store; this.changed=changed; this.clock=clock ?? TimeProvider.System; uncertainOwnership=false; if (NeedsRecovery) Status="存在未核实的中断运行，请先检查相关程序"; }
    public bool Running { get; private set; }
    public bool StopRequested { get; private set; }
    public bool ManualConfirmed { get; private set; }
    public bool Waiting { get; private set; }
    public string Status { get; private set; } = "空闲";
    Process? active;
    bool manualMode;
    bool uncertainOwnership;
    bool storageFailed;
    public bool NeedsRecovery => uncertainOwnership || Recovery.HasPending(store);
    readonly object decisionGate = new();
    enum ManualDecision { Confirmed, Skipped, Stopped }
    TaskCompletionSource<bool>? decision;
    TaskCompletionSource<ManualDecision>? manualDecision;
    CancellationTokenSource? stopDelay;
    RunRecord? run;
    ControlledRestartChannel? controlledFixture;
    internal Func<OwnedProcess, RestartCandidateTracker>? CandidateTrackerFactory { get; set; }
    internal async Task StartControlledFixture(IReadOnlyList<TaskSpec> tasks, Action<string>? channelCreated = null)
    {
        if (Running || controlledFixture != null || tasks.Count == 0) return;
        using var channel = new ControlledRestartChannel();
        channelCreated?.Invoke(channel.Name);
        var copy = tasks.Select(t => t.Copy()).ToList();
        copy[0].Arguments += " " + channel.Arguments;
        controlledFixture = channel;
        try { await StartTasks(copy); } finally { controlledFixture = null; }
    }

    void Event(string text)
    {
        Status = text;
        if (run != null) { run.Events.Add($"{DateTimeOffset.Now:O} {text}"); try { store.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { storageFailed = true; Status = "记录保存失败，停止启动后续任务：" + e.Message; } }
        changed();
    }
    public void Confirm()
    {
        lock (decisionGate)
        {
            if (!Running || !manualMode || StopRequested || storageFailed || ManualConfirmed || manualDecision?.Task.IsCompleted == true) return;
            ManualConfirmed = true;
            Event(active == null ? "已确认当前任务业务完成" : "已确认当前任务业务完成，仍需等待程序退出");
            manualDecision?.TrySetResult(ManualDecision.Confirmed);
        }
    }
    public void Continue()
    {
        lock (decisionGate)
        {
            if (!Waiting || StopRequested || storageFailed) return;
            if (manualDecision != null)
            {
                if (manualDecision.Task.IsCompleted || ManualConfirmed) return;
                Event("用户决定跳过当前未成功项并继续；该项未判定成功");
                manualDecision.TrySetResult(ManualDecision.Skipped);
            }
            else decision?.TrySetResult(true);
        }
    }
    public void AcknowledgeRecovery(RunRecord item)
    {
        if (Running) throw new InvalidOperationException("任务正在运行，不能核实中断。");
        Recovery.Acknowledge(store, item);
        uncertainOwnership = false;
        Status = NeedsRecovery ? "仍有待核实事项" : "待核实事项已处理";
        changed();
    }
    public void Stop()
    {
        lock (decisionGate)
        {
            if (!Running) return;
            StopRequested = true;
            stopDelay?.Cancel();
            decision?.TrySetResult(false);
            manualDecision?.TrySetResult(ManualDecision.Stopped);
        }
        Event("已请求停止，不再启动后续任务；尝试请求当前程序关闭");
        try { if (active is { HasExited: false }) active.CloseMainWindow(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { Event("无法请求关闭当前程序：" + e.Message); }
    }
    async Task<bool> WaitForUser(string reason)
    {
        Task<bool> pending;
        lock (decisionGate)
        {
            if (StopRequested || storageFailed) return false;
            Waiting = true; decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Event(reason + "；等待用户继续或停止");
            if (storageFailed) decision.TrySetResult(false);
            pending = decision.Task;
        }
        bool result = await pending;
        lock (decisionGate)
        {
            Waiting = false; decision = null;
            if (result && !StopRequested && !storageFailed) Event("用户决定跳过当前未成功项并继续；该项未判定成功");
            else changed();
            return result && !StopRequested && !storageFailed;
        }
    }
    public Task Start() => StartTasks(store.Tasks.Select(t => t.Copy()).ToList());
    public async Task StartTasks(IReadOnlyList<TaskSpec> tasks, RunRecord? reserved = null, IReadOnlyList<int>? intervals = null)
    {
        if (Running || NeedsRecovery || tasks.Count == 0) return;
        if (intervals != null && (intervals.Count != tasks.Count || intervals.Any(i => i < 0 || i > 86400))) throw new ArgumentException("任务间隔无效");
        var snapshot = tasks.Select(t => t.Copy()).ToList();
        stopDelay = new CancellationTokenSource();
        bool allSucceeded = true;
        Running = true; StopRequested = false; storageFailed = false; run = reserved ?? new RunRecord(); if (reserved == null) store.History.Add(run); Event("队列开始");
        try
        {
            for (int index=0; index<snapshot.Count; index++)
            {
                var task=snapshot[index];
                if (StopRequested || storageFailed || uncertainOwnership) break;
                string result = await RunOne(task, index == 0 ? controlledFixture : null);
                if (StopRequested || storageFailed || uncertainOwnership) { allSucceeded=false; break; }
                if (result != "成功") allSucceeded=false;
                if (result == "失败" && task.ContinueOnFailure) Event("任务失败，按配置继续：" + task.Name);
                else if (result != "成功" && result != "已跳过" && !await WaitForUser($"{task.Name}：{result}")) break;
                int seconds=intervals == null ? 0 : intervals[index];
                if (index+1 < snapshot.Count && seconds>0 && !StopRequested && !storageFailed)
                {
                    Event($"{task.Name} 已结束，等待 {seconds} 秒后进入下一项");
                    var timer=Stopwatch.StartNew();
                    try { while (!storageFailed && timer.Elapsed < TimeSpan.FromSeconds(seconds)) { int remaining=(int)Math.Ceiling(seconds-timer.Elapsed.TotalSeconds); Status=$"等待下一项，剩余 {remaining} 秒"; changed(); await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000,Math.Max(1,(seconds-timer.Elapsed.TotalSeconds)*1000))),stopDelay.Token); } }
                    catch (OperationCanceledException) when (StopRequested) { break; }
                    if (!StopRequested && !storageFailed) Event("任务间隔结束");
                }
            }
            run.State = uncertainOwnership || storageFailed ? "需人工核实" : StopRequested ? "已停止" : "已结束";
            run.AllSucceeded = allSucceeded && !StopRequested && !storageFailed && !uncertainOwnership;
            Event("队列" + run.State);
            if (storageFailed) { run.AllSucceeded=false; run.State="需人工核实"; }
        }
        catch (Exception ex) { run.AllSucceeded=false; run.State = "需人工核实"; uncertainOwnership=true; Event("运行异常，请检查相关程序：" + ex.Message); }
        finally { lock (decisionGate) { active = null; Running = false; Waiting = false; decision = null; manualDecision = null; manualMode = false; } stopDelay?.Dispose(); stopDelay=null; changed(); run = null; }
    }
    async Task<string> RunOne(TaskSpec task, ControlledRestartChannel? controlled)
    {
        ManualConfirmed = false;
        manualMode = task.Completion == "人工确认";
        if (!File.Exists(task.Program)) { Event("程序不存在：" + task.Name); return "失败"; }
        if (task.Completion == "日志信号" && string.IsNullOrWhiteSpace(task.SuccessText)) { Event("未配置成功信号"); return "失败"; }
        if (!UpdateMarkers.TryParse(task.UpdateMarkers, out var configuredMarkers)) { Event("更新或重启线索无效，最多 32 条、每条 256 个字符"); return "结果不明：线索配置无效"; }
        var markers = UpdateMarkers.BuiltIn.Concat(configuredMarkers).ToArray();
        if (task.TimeoutMinutes < 0 || task.TimeoutMinutes > 525600) { Event("最长运行时间无效"); return "失败"; }
        if (task.SuccessText.Length > 4096) { Event("成功信号过长"); return "失败"; }
        if (task.Completion is not ("进程退出" or "日志信号" or "人工确认") || task.LogSource is not ("程序输出" or "日志文件")) { Event("完成方式或日志来源无效"); return "失败"; }
        FileStream? file = null;
        OwnedProcess? owned = null;
        RestartCandidateTracker? candidateTracker = null;
        try
        {
            if (task.Completion == "日志信号" && task.LogSource == "日志文件")
            {
                file = new FileStream(task.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                file.Seek(0, SeekOrigin.End);
            }
            owned = OwnedProcess.Start(task); var process = owned.Root; active = process; Event($"已启动 {task.Name}（PID {process.Id}，创建时间 {owned.CreatedUtc:O}，路径 {owned.Executable}），已建立进程组归属");
            candidateTracker = task.TrackUpdateRestart ? (CandidateTrackerFactory?.Invoke(owned) ?? new RestartCandidateTracker(owned, report: Event)) : null;
            bool found = false, updateSignal = false, timedOut = false, updateTrackingTimedOut = false; long? updateStarted = null; long started = clock.GetTimestamp(); string tail = ""; Decoder decoder = Encoding.UTF8.GetDecoder();
            void CheckUpdate(string text, string source)
            {
                string? marker = UpdateMarkers.Find(text, markers);
                if (marker == null) return;
                lock (decisionGate)
                {
                    if (!updateSignal) { updateSignal = true; updateStarted = clock.GetTimestamp(); Event($"检测到更新或重启线索（{source}：{marker}），程序及受管子进程退出后需要人工核实"); }
                }
            }
            if (controlled != null) controlled.Start(owned, () => updateSignal, task.SuccessText, Event);
            int markerOverlap = markers.Max(x => x.Length) - 1;
            var fileEvidence = new LineEvidence(markerOverlap);
            var buffered = new List<string>(); int dropped = 0, retained = 0; bool dropReported = false;
            void FlushOutput()
            {
                if (buffered.Count == 0 && dropped == 0) return;
                bool changedOutput = buffered.Count > 0;
                foreach (var item in buffered) run!.Events.Add(item);
                buffered.Clear();
                if (dropped > 0 && !dropReported) { run!.Events.Add($"{DateTimeOffset.Now:O} 输出过多，部分输出已省略"); dropReported = true; changedOutput = true; }
                dropped = 0;
                if (!changedOutput) return;
                try { store.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { storageFailed = true; Status = "记录保存失败，停止启动后续任务：" + e.Message; }
                changed();
            }
            void ObserveEvidence(string text, bool isStandardOutput)
            {
                CheckUpdate(text, isStandardOutput ? "标准输出" : "标准错误");
                if (isStandardOutput && task.Completion == "日志信号" && task.LogSource == "程序输出" && text.Contains(task.SuccessText, StringComparison.Ordinal)) found = true;
            }
            void RecordLine(string line)
            {
                if (buffered.Count < 512 && retained < 10000) { buffered.Add($"{DateTimeOffset.Now:O} {task.Name} 输出：{line}"); retained++; } else dropped++;
            }
            int overlap = Math.Max(task.Completion == "日志信号" && task.LogSource == "程序输出" ? task.SuccessText.Length - 1 : 0, markerOverlap);
            var output = BoundedOutputReader.ReadAsync(owned.Output, text => ObserveEvidence(text, true), RecordLine, overlap);
            var errors = BoundedOutputReader.ReadAsync(owned.Error, text => ObserveEvidence(text, false), RecordLine, overlap);
            void CheckTimeout() { if (!timedOut && task.TimeoutMinutes > 0 && clock.GetElapsedTime(started) >= TimeSpan.FromMinutes(task.TimeoutMinutes)) { timedOut = true; Event("任务超过最长运行时间；不会启动下一项，请手动处理并等待受管程序退出"); } }
            DateTimeOffset? stopAt = null;
            bool reportedChildren = false, reportedUpdateTracking = false, reportedCandidate = false, reportedCandidateExit = false, reportedAmbiguous = false, reportedIdentityError = false; uint maxGroupTotal = 0;
            while (true)
            {
                FlushOutput();
                CheckTimeout();
                var accounting = owned.Accounting();
                maxGroupTotal = Math.Max(maxGroupTotal, accounting.Total);
                if (process.HasExited && candidateTracker != null)
                {
                    candidateTracker.Poll();
                    if (candidateTracker.HasCandidate && !reportedCandidate)
                    {
                        reportedCandidate = true;
                        Event($"发现本轮受管组内可能的重启程序（PID {candidateTracker.CandidateId}，创建时间 {candidateTracker.CandidateCreated:O}）；继续等待，不代表业务成功");
                    }
                    if (candidateTracker.CandidateExited && !reportedCandidateExit) { reportedCandidateExit = true; Event("已记录的重启候选进程已退出；仍以受管组及业务完成证据为准"); }
                    if (candidateTracker.Ambiguous && !reportedAmbiguous) { reportedAmbiguous = true; Event("发现多个可能的重启程序，无法安全确认；退出后需人工核实"); }
                    if (candidateTracker.Uncertain && !reportedIdentityError) { reportedIdentityError = true; Event("受管进程身份或归属无法核实；退出后需人工核实"); }
                }
                if (process.HasExited && accounting.Active == 0) break;
                if (process.HasExited && !reportedChildren) { reportedChildren = true; Event("主程序已退出，仍有受管子进程，等待其退出"); }
                if (task.TrackUpdateRestart && updateSignal && !reportedUpdateTracking && process.HasExited && accounting.Active > 0)
                {
                    reportedUpdateTracking = true;
                    Event($"更新后原程序已退出；进程组仍有 {accounting.Active} 个受管进程，继续等待，无法仅凭数量确认重启程序身份");
                }
                if (task.TrackUpdateRestart && updateStarted is long updateAt && !updateTrackingTimedOut && clock.GetElapsedTime(updateAt) >= TimeSpan.FromMinutes(5))
                {
                    updateTrackingTimedOut = true;
                    Event("更新跟踪已等待 5 分钟，结果不明；请人工检查，仍等待受管进程退出，不启动下一项");
                }
                if (file != null)
                {
                    try
                    {
                        if (!LogFileIdentity.IsSame(file, task.LogPath)) return await ObserveUnknownProcess(owned, "日志文件已替换", () => { FlushOutput(); CheckTimeout(); });
                        if (file.Length < file.Position) return await ObserveUnknownProcess(owned, "日志文件被截断", () => { FlushOutput(); CheckTimeout(); });
                        byte[] bytes = new byte[Math.Min(4096, (int)Math.Min(int.MaxValue, file.Length - file.Position))];
                        if (bytes.Length > 0) { int count = await file.ReadAsync(bytes); char[] chars = new char[Encoding.UTF8.GetMaxCharCount(count)]; int charCount = decoder.GetChars(bytes, 0, count, chars, 0); string chunk = new(chars, 0, charCount); fileEvidence.Feed(chunk, text => CheckUpdate(text, "日志文件")); tail += chunk; if (tail.Contains(task.SuccessText, StringComparison.Ordinal)) found = true; if (tail.Length > 8192) tail = tail[^8192..]; }
                    }
                    catch (IOException e) { return await ObserveUnknownProcess(owned, "读取日志失败：" + e.Message, () => { FlushOutput(); CheckTimeout(); }); }
                }
                if (StopRequested) { stopAt ??= DateTimeOffset.Now; if ((DateTimeOffset.Now - stopAt.Value).TotalSeconds >= 10) { Event("停止请求已等待 10 秒；程序仍在运行，请手动处理后等待退出"); stopAt = DateTimeOffset.Now.AddYears(10); } }
                await Task.Delay(200);
            }
            var drains = Task.WhenAll(output, errors);
            if (await Task.WhenAny(drains, Task.Delay(1000)) != drains) { uncertainOwnership = true; Event("输出管道仍被未知进程占用；停止队列，请人工核实"); return "结果不明"; }
            await drains; FlushOutput();
            if (file != null)
            {
                if (!LogFileIdentity.IsSame(file, task.LogPath)) return "结果不明：日志文件已替换";
                if (file.Length < file.Position) return "结果不明：日志截断";
                byte[] remaining = new byte[4096]; int count;
                while ((count = await file.ReadAsync(remaining)) > 0) { char[] chars = new char[Encoding.UTF8.GetMaxCharCount(count)]; int charCount = decoder.GetChars(remaining, 0, count, chars, 0); string chunk = new(chars, 0, charCount); fileEvidence.Feed(chunk, text => CheckUpdate(text, "日志文件")); tail += chunk; if (tail.Contains(task.SuccessText, StringComparison.Ordinal)) found = true; if (tail.Length > 8192) tail = tail[^8192..]; }
            }
            int code = process.ExitCode; active = null;
            if (StopRequested) return "已停止";
            bool evidence = task.Completion == "人工确认" ? ManualConfirmed : task.Completion == "日志信号" && found;
            if (code != 0 && evidence) { Event($"{task.Name} 退出码 {code}，与成功证据冲突"); return "证据冲突"; }
            if (updateSignal)
            {
                bool controlledSuccess = false;
                if (controlled?.Result is Task<bool> proof && await Task.WhenAny(proof, Task.Delay(1000)) == proof) controlledSuccess = await proof;
                if (controlledSuccess && candidateTracker != null && (candidateTracker.Uncertain || candidateTracker.Ambiguous || !candidateTracker.HasCandidate || candidateTracker.CandidateId != controlled!.VerifiedClientId || candidateTracker.CandidateCreated != controlled.VerifiedClientCreated))
                {
                    controlledSuccess = false;
                    Event("受控证据与重启候选身份不一致或不明确，需人工核实");
                }
                if (controlledSuccess && !StopRequested && !storageFailed && !timedOut && !updateTrackingTimedOut && code == 0 && task.Completion == "人工确认")
                {
                    Task<ManualDecision> pending;
                    lock (decisionGate)
                    {
                        ManualConfirmed = false; Waiting = true; manualDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        Event("受控重启进程已退出，请人工确认业务完成或跳过");
                        if (storageFailed || StopRequested) manualDecision.TrySetResult(ManualDecision.Stopped);
                        pending = manualDecision.Task;
                    }
                    var choice = await pending;
                    lock (decisionGate) { Waiting = false; manualDecision = null; }
                    if (StopRequested || storageFailed || choice == ManualDecision.Stopped) return "已停止";
                    if (choice == ManualDecision.Skipped) return "已跳过";
                    Event(task.Name + " 成功，退出码 0；依据：人工确认");
                    return storageFailed ? "结果不明：记录保存失败" : "成功";
                }
                if (controlledSuccess && !StopRequested && !storageFailed && !timedOut && !updateTrackingTimedOut && code == 0 && task.Completion == "日志信号" && task.LogSource == "程序输出" && task.TrustCompletion)
                {
                    Event(task.Name + " 成功，退出码 0；依据：受控测试的本次业务成功证据");
                    return storageFailed ? "结果不明：记录保存失败" : "成功";
                }
                if (controlled != null) Event("受控测试未确认：" + controlled.Failure);
                if (task.TrackUpdateRestart) Event(reportedUpdateTracking ? "受管进程组已退出；更新后程序身份和业务完成情况仍需人工核实" : "未确认更新后程序归属；需人工核实");
                return updateTrackingTimedOut ? "结果不明：更新跟踪超时，需人工核实" : "结果不明：检测到更新或重启，需人工核实";
            }
            if (candidateTracker != null && (candidateTracker.HasCandidate || candidateTracker.Ambiguous || candidateTracker.Uncertain || maxGroupTotal > 1))
                return "结果不明：出现可能的重启程序或子进程，无法确认更新前的完成证据仍有效；请人工核实";
            if (timedOut) return "结果不明：任务运行超时，请人工核实";
            if (task.Completion == "日志信号" && task.LogSource == "日志文件" && task.SharedLog) return "结果不明：日志可能由多个实例共享，无法确认本次来源";
            if (code != 0) { Event($"{task.Name} 退出码 {code}"); return "失败"; }
            if (task.Completion == "人工确认")
            {
                Task<ManualDecision>? pending = null;
                lock (decisionGate)
                {
                    if (!ManualConfirmed && !StopRequested && !storageFailed)
                    {
                        Waiting = true; manualDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        Event("程序已退出，等待人工确认或跳过");
                        if (storageFailed) manualDecision.TrySetResult(ManualDecision.Stopped);
                        pending = manualDecision.Task;
                    }
                }
                if (pending != null)
                {
                    var choice = await pending;
                    lock (decisionGate) { Waiting = false; manualDecision = null; if (choice != ManualDecision.Confirmed) manualMode = false; }
                    if (StopRequested || storageFailed || choice == ManualDecision.Stopped) return "已停止";
                    if (choice == ManualDecision.Skipped) return "已跳过";
                }
                if (StopRequested || storageFailed) return "已停止";
            }
            if (task.Completion == "日志信号" && !found) return "结果不明：未发现本次成功信号";
            if (task.Completion != "人工确认" && !task.TrustCompletion) return "结果不明：尚未确认完成信号能覆盖外部进程和更新，请人工核实";
            Event(task.Name + " 成功，退出码 0；依据：" + task.Completion); return "成功";
        }
        catch (ProcessLaunchUncertainException e)
        {
            uncertainOwnership = true; Event(e.Message); return "结果不明";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        {
            Event(task.Name + " 无法执行或观察：" + e.Message);
            if (owned != null) return await ObserveUnknownProcess(owned, "运行中观察失败");
            return "失败";
        }
        finally
        {
            controlled?.Dispose();
            if (controlled?.Result is Task<bool> pendingProof) { try { await pendingProof; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException) { } }
            manualMode = false; active = null; candidateTracker?.Dispose(); owned?.Dispose(); file?.Dispose();
        }
    }
    async Task<string> ObserveUnknownProcess(OwnedProcess owned, string reason, Action? flushOutput = null)
    {
        Event(reason + "；等待受管进程组退出，期间不会启动下一项");
        try { DateTimeOffset? stopAt = null; bool reported = false; while (!owned.Root.HasExited || owned.Accounting().Active != 0) { flushOutput?.Invoke(); if (StopRequested) { stopAt ??= DateTimeOffset.Now; if (!reported && (DateTimeOffset.Now - stopAt.Value).TotalSeconds >= 10) { Event("停止请求已等待 10 秒；程序仍在运行，请手动处理后等待退出"); reported = true; } } await Task.Delay(200); } flushOutput?.Invoke(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { uncertainOwnership = true; Event("进程组状态无法核实，必须先人工检查再重启软件：" + e.Message); }
        active = null; return "结果不明：" + reason;
    }
}
