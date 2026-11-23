using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Runner;

class Program
{
    static int failures;
    static readonly string root = Path.Combine(Environment.CurrentDirectory, ".build", "checks-" + Guid.NewGuid().ToString("N"));
    static readonly string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    static void Check(bool ok, string name) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
    static Store NewStore(string name) => new(Path.Combine(root, name));
    static TaskSpec Spec(string name, string args) => new() { Name=name, Program=cmd, Arguments="/d /c " + args, WorkingDirectory=Environment.SystemDirectory, TrustCompletion=true };
    static async Task Tests(bool skipControlled=false)
    {
        SelfUpdatePackageTests();
        await SelfUpdateReleaseTests();
        await SelfUpdateCoordinatorTests();
        await SelfUpdateInterlockTests();
        SelfUpdateTransactionTests();
        await SelfUpdateFullFixtureTests();
        await SelfUpdateSimulationTests();
        await QqNotificationTests();
        await MobileNotificationTests();
        VisualEvidenceTests();
        await TaskTrialTests();
        await GenericStartTests();
        await VisualStartTests();
        await StartupErrorTests();
        await NotificationTests();
        await JobEventTests();
        await MaaResumeTests();
        var a = NewStore("sequence"); a.Tasks.Add(Spec("first", "echo first")); a.Tasks.Add(Spec("second", "echo second"));
        a.Save(); var b = NewStore("sequence"); b.Load(); Check(b.Tasks.Count == 2 && b.Tasks[0].Name == "first", "configuration round trip");
        Check(!new TaskSpec().TrustCompletion, "new task defaults to manual review");
        var legacy=NewStore("legacy-choice"); string legacyPath=Path.Combine(root,"legacy-choice","data.json");
        await File.WriteAllTextAsync(legacyPath,"{\"Format\":\"SequentialRunnerV3/1\",\"Tasks\":[{\"Name\":\"enabled\",\"TrustCompletion\":true},{\"Name\":\"disabled\",\"TrustCompletion\":false},{\"Name\":\"missing\"}],\"History\":[]}");
        legacy.Load(); Check(legacy.Tasks.Count==3 && legacy.Tasks[0].TrustCompletion && !legacy.Tasks[1].TrustCompletion && !legacy.Tasks[2].TrustCompletion, "existing true false and missing choices retain safe meaning");
        var protectedStore=NewStore("foreign"); string foreign=Path.Combine(root,"foreign","data.json"); await File.WriteAllTextAsync(foreign,"{\"owner\":\"other\"}"); bool refused=false; try { protectedStore.Load(); } catch(InvalidDataException) { refused=true; } Check(refused && (await File.ReadAllTextAsync(foreign)).Contains("other"), "unknown data format is not overwritten");
        var changedStore=NewStore("external-change"); changedStore.Save(); string changedFile=Path.Combine(root,"external-change","data.json"); await File.WriteAllTextAsync(changedFile,"external change"); refused=false; try { changedStore.Save(); } catch(IOException) { refused=true; } Check(refused && (await File.ReadAllTextAsync(changedFile))=="external change", "externally changed data is not overwritten");
        var engine = new RunnerEngine(b, () => { }); await engine.Start();
        var events = b.History.Single().Events; int one=events.FindIndex(x=>x.Contains("first 成功")), two=events.FindIndex(x=>x.Contains("已启动 second"));
        Check(one >= 0 && two > one && b.History.Single().State == "已结束", "serial execution");
        var c = NewStore("stdout"); var log=Spec("log", "echo READY"); log.Completion="日志信号"; log.SuccessText="READY"; c.Tasks.Add(log); await new RunnerEngine(c,()=>{}).Start();
        Check(c.History.Single().Events.Any(x=>x.Contains("log 成功")), "stdout success evidence");
        var d = NewStore("conflict"); var conflict=Spec("conflict", "echo READY & exit /b 3"); conflict.Completion="日志信号"; conflict.SuccessText="READY"; conflict.ContinueOnFailure=true; d.Tasks.Add(conflict); d.Tasks.Add(Spec("must-not-start", "echo bad"));
        var e = new RunnerEngine(d,()=>{}); var pending=e.Start();
        for (int i=0; i<100 && !e.Waiting; i++) await Task.Delay(50);
        Check(e.Waiting && !d.History.Single().Events.Any(x=>x.Contains("已启动 must-not-start")), "conflicting evidence pauses despite continue policy"); e.Stop(); await pending;
        var f=NewStore("manual"); var manual=Spec("manual", "echo done"); manual.Completion="人工确认"; f.Tasks.Add(manual); var m=new RunnerEngine(f,()=>{}); var mp=m.Start();
        for (int i=0; i<100 && !m.Status.Contains("等待人工确认"); i++) await Task.Delay(50);
        Check(!mp.IsCompleted, "manual completion waits"); m.Confirm(); await mp; Check(f.History.Single().Events.Any(x=>x.Contains("manual 成功")), "manual confirmation completes");
        var g=NewStore("old-log"); Directory.CreateDirectory(root); string path=Path.Combine(root,"old.log"); await File.WriteAllTextAsync(path,"READY\n"); var old=Spec("old", "echo nothing"); old.Completion="日志信号"; old.LogSource="日志文件"; old.LogPath=path; old.SuccessText="READY"; g.Tasks.Add(old); var ge=new RunnerEngine(g,()=>{}); var gp=ge.Start();
        for(int i=0;i<100 && !ge.Waiting;i++) await Task.Delay(50);
        Check(ge.Waiting && ge.Status.Contains("未发现"), "old log content ignored"); ge.Stop(); await gp;
        var h=NewStore("interrupted"); h.History.Add(new RunRecord()); h.Save(); var h2=NewStore("interrupted"); h2.Load(); Check(h2.History.Single().State=="中断", "interrupted run is not replayed");
        h2.Tasks.Add(Spec("recovery", "echo resumed")); var he = new RunnerEngine(h2,()=>{}); await he.Start(); Check(he.NeedsRecovery && h2.History.Count==1, "unresolved interruption prevents a new run"); he.AcknowledgeRecovery(Recovery.Pending(h2).Single()); await he.Start(); Check(!he.NeedsRecovery && h2.History.Last().Events.Any(x=>x.Contains("recovery 成功")), "explicit recovery confirmation unlocks execution");
        var j=NewStore("continue"); var bad=Spec("failure", "exit /b 9"); bad.ContinueOnFailure=true; j.Tasks.Add(bad); j.Tasks.Add(Spec("after-failure", "echo next")); await new RunnerEngine(j,()=>{}).Start();
        Check(j.History.Single().Events.Any(x=>x.Contains("after-failure 成功")), "configured failure continues");
        var q=NewStore("untrusted-completion"); var cautious=Spec("cautious", "echo done"); cautious.TrustCompletion=false; q.Tasks.Add(cautious); q.Tasks.Add(Spec("after-confirmation", "echo next")); var qe=new RunnerEngine(q,()=>{}); var qp=qe.Start();
        for(int i=0;i<100 && !qe.Waiting;i++) await Task.Delay(50); Check(qe.Waiting && !q.History.Single().Events.Any(x=>x.Contains("已启动 after-confirmation")), "unverified completion requires human review"); qe.Continue(); await qp; Check(q.History.Single().Events.Any(x=>x.Contains("未判定成功")) && q.History.Single().Events.Any(x=>x.Contains("after-confirmation 成功")), "manual skip is recorded before continuing");
        var k=NewStore("stop"); k.Tasks.Add(new TaskSpec { Name="wait", Program=Environment.ProcessPath!, Arguments="fixture-wait", WorkingDirectory=AppContext.BaseDirectory }); k.Tasks.Add(Spec("not-started", "echo bad"));
        var ke=new RunnerEngine(k,()=>{}); var kp=ke.Start(); for(int i=0;i<100 && !ke.Status.Contains("已启动 wait");i++) await Task.Delay(30); ke.Stop(); await kp;
        Check(k.History.Single().State=="已停止" && !k.History.Single().Events.Any(x=>x.Contains("已启动 not-started")), "stop waits and prevents next task");
        var z=NewStore("child"); string pidFile=Path.Combine(root,"child.pid"); z.Tasks.Add(new TaskSpec { Name="parent", Program=Environment.ProcessPath!, Arguments="fixture-parent \"" + pidFile + "\"", WorkingDirectory=AppContext.BaseDirectory, TrustCompletion=true }); z.Tasks.Add(Spec("after-parent", "echo second"));
        await new RunnerEngine(z,()=>{}).Start();
        bool childExited;
        try { using var child=Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile))); childExited=child.HasExited; await child.WaitForExitAsync(); }
        catch (ArgumentException) { childExited=true; }
        Check(childExited && z.History.Single().Events.Any(x=>x.Contains("仍有受管子进程")), "job waits for child even after parent closes output pipes");
        var l=NewStore("appended-log"); string lp=Path.Combine(root,"append.log"); await File.WriteAllTextAsync(lp,"old\n");
        l.Tasks.Add(new TaskSpec { Name="append", Program=Environment.ProcessPath!, Arguments="fixture-append \""+lp+"\"", WorkingDirectory=AppContext.BaseDirectory, Completion="日志信号", LogSource="日志文件", LogPath=lp, SuccessText="完成", TrustCompletion=true });
        await new RunnerEngine(l,()=>{}).Start(); Check(l.History.Single().Events.Any(x=>x.Contains("append 成功")), "new split UTF-8 file evidence");
        var r=NewStore("rotated-log"); string rp=Path.Combine(root,"rotate.log"); await File.WriteAllTextAsync(rp,"old\n");
        r.Tasks.Add(new TaskSpec { Name="rotate", Program=Environment.ProcessPath!, Arguments="fixture-rotate \""+rp+"\"", WorkingDirectory=AppContext.BaseDirectory, Completion="日志信号", LogSource="日志文件", LogPath=rp, SuccessText="READY", ContinueOnFailure=true }); r.Tasks.Add(Spec("should-not-run", "echo bad"));
        var re=new RunnerEngine(r,()=>{}); var running=re.Start(); for(int i=0;i<100 && !re.Waiting;i++) await Task.Delay(50);
        Check(re.Waiting && r.History.Single().Events.Any(x=>x.Contains("日志文件已替换")) && !r.History.Single().Events.Any(x=>x.Contains("已启动 should-not-run")), "replaced log pauses despite failure-continue policy"); re.Stop(); await running;
        var u=NewStore("updating"); var update=Spec("update", "echo updating & exit /b 3"); update.ContinueOnFailure=true; u.Tasks.Add(update); u.Tasks.Add(Spec("next", "echo bad")); var ue=new RunnerEngine(u,()=>{}); var ur=ue.Start(); for(int i=0;i<100 && !ue.Waiting;i++) await Task.Delay(50);
        Check(ue.Waiting && u.History.Single().Events.Any(x=>x.Contains("检测到更新或重启")) && !u.History.Single().Events.Any(x=>x.Contains("已启动 next")), "update evidence pauses despite failure-continue policy"); ue.Stop(); await ur;

        Check(!legacy.Tasks[0].SharedLog && legacy.Tasks[0].TimeoutMinutes == 0 && !legacy.NoticeSeen, "legacy fields keep safe defaults");
        var shared=NewStore("shared-log"); string sharedPath=Path.Combine(root,"shared.log"); await File.WriteAllTextAsync(sharedPath,"old\n");
        shared.Tasks.Add(new TaskSpec { Name="shared", Program=Environment.ProcessPath!, Arguments="fixture-append \""+sharedPath+"\"", WorkingDirectory=AppContext.BaseDirectory, Completion="日志信号", LogSource="日志文件", LogPath=sharedPath, SuccessText="完成", TrustCompletion=true, SharedLog=true }); shared.Tasks.Add(Spec("shared-next", "echo bad"));
        var se=new RunnerEngine(shared,()=>{}); var sp=se.Start(); for(int i=0;i<100 && !se.Waiting;i++) await Task.Delay(50);
        Check(se.Waiting && se.Status.Contains("共享") && !shared.History.Single().Events.Any(x=>x.Contains("已启动 shared-next")), "declared shared log pauses even with success text"); se.Stop(); await sp;
        var sharedFail=NewStore("shared-failure"); var sf=Spec("shared-failure", "exit /b 2"); sf.Completion="日志信号"; sf.LogSource="日志文件"; sf.LogPath=sharedPath; sf.SuccessText="never"; sf.SharedLog=true; sf.ContinueOnFailure=true; sharedFail.Tasks.Add(sf); sharedFail.Tasks.Add(Spec("shared-failure-next", "echo bad"));
        var sfe=new RunnerEngine(sharedFail,()=>{}); var sfp=sfe.Start(); for(int i=0;i<100 && !sfe.Waiting;i++) await Task.Delay(50); Check(sfe.Waiting && !sharedFail.History.Single().Events.Any(x=>x.Contains("已启动 shared-failure-next")), "shared log pauses despite nonzero exit and continue policy"); sfe.Stop(); await sfp;
        var timed=NewStore("timeout"); timed.Tasks.Add(new TaskSpec { Name="timed", Program=Environment.ProcessPath!, Arguments="fixture-wait", WorkingDirectory=AppContext.BaseDirectory, TrustCompletion=true, TimeoutMinutes=1 }); timed.Tasks.Add(Spec("timeout-next", "echo bad"));
        var fake=new FakeClock(); var te=new RunnerEngine(timed,()=>{},fake); var tp=te.Start(); fake.Seconds=61; for(int i=0;i<100 && !te.Waiting;i++) await Task.Delay(50);
        Check(te.Waiting && timed.History.Single().Events.Any(x=>x.Contains("超过最长运行时间")) && !timed.History.Single().Events.Any(x=>x.Contains("已启动 timeout-next")), "timeout waits for process then pauses queue"); te.Stop(); await tp;
        var flood=NewStore("flood"); flood.Tasks.Add(new TaskSpec { Name="flood", Program=Environment.ProcessPath!, Arguments="fixture-flood", WorkingDirectory=AppContext.BaseDirectory, TrustCompletion=true }); int updates=0; await new RunnerEngine(flood,()=>updates++).Start();
        Check(flood.History.Single().Events.Any(x=>x.Contains("flood 成功")) && flood.History.Single().Events.Any(x=>x.Contains("省略")) && updates < 500, "high output bounded and batched while completing");
        var unknown=NewStore("unknown-stop"); string up=Path.Combine(root,"unknown.log"); await File.WriteAllTextAsync(up,"old\n"); unknown.Tasks.Add(new TaskSpec { Name="unknown", Program=Environment.ProcessPath!, Arguments="fixture-rotate-wait \""+up+"\"", WorkingDirectory=AppContext.BaseDirectory, Completion="日志信号", LogSource="日志文件", LogPath=up, SuccessText="READY" });
        var un=new RunnerEngine(unknown,()=>{}); var ut=un.Start(); for(int i=0;i<100 && !unknown.History.Single().Events.Any(x=>x.Contains("日志文件已替换"));i++) await Task.Delay(50); un.Stop(); await ut;
        Check(unknown.History.Single().Events.Any(x=>x.Contains("等待 10 秒")), "stop warning survives log observation failure");
        var noticeStore=NewStore("notice"); noticeStore.NoticeSeen=true; noticeStore.Save(); var noticeReload=NewStore("notice"); noticeReload.Load(); Check(noticeReload.NoticeSeen, "notice acknowledgement persists across launches");
        string race=Path.Combine(root,"race-reproduction.json"), candidate=Path.Combine(root,"race-candidate.json"); await File.WriteAllTextAsync(race,"initial"); byte[] expected=System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(race)); await File.WriteAllTextAsync(candidate,"our-save");
        byte[] observed=await File.ReadAllBytesAsync(race); bool unchanged=System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected,System.Security.Cryptography.SHA256.HashData(observed)); await File.WriteAllTextAsync(race,"external-save"); if(unchanged) File.Move(candidate,race,true);
        Check((await File.ReadAllTextAsync(race))=="our-save", "controlled old check-then-replace ordering can overwrite an intervening writer");
        if (skipControlled) Console.WriteLine("SKIP controlled resume: run probe-controlled-resume separately with local pipe access"); else await ControlledResumeTests();
        await UpdateRestartOwnershipProbe();
        await RestartCandidateTests();
        await UpdateRestartTests();
        await UpdateMarkerTests();
        await ManualSkipTests();
        await ScheduleTests();
        await StartupAndIntervalTests();
        await CompletionTests();
        await RecoveryTests();
        await BoundedOutputTests();
        await HistoryStorageTests();
        var x=NewStore("concurrent-store"); x.Save(); var y=NewStore("concurrent-store"); y.Load(); x.Tasks.Add(Spec("writer-one", "echo 1")); y.Tasks.Add(Spec("writer-two", "echo 2"));
        var writes=await Task.WhenAll(Task.Run(()=>TrySave(x)), Task.Run(()=>TrySave(y))); var verify=NewStore("concurrent-store"); verify.Load(); Check(writes.Count(v=>v)==1 && verify.Tasks.Count==1, "cooperating stores reject conflicting simultaneous save");
    }
    static async Task JobEventTests()
    {
        JobEventObserver Empty() => new(new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero,false));
        using(var missing=Empty()) { missing.Record(6,101,_=>Process.GetCurrentProcess(),_=>{});missing.Record(7,101,_=>null,_=>{});string summary=missing.Summary(2);Check(missing.Uncertain && summary.Contains("数量与累计进程数不一致"),"job events detect missing notification or count mismatch"); }
        using(var handle=Empty()) {handle.Record(6,102,_=>null,_=>{});handle.Record(7,102,_=>null,_=>{});handle.Summary(1);Check(handle.Uncertain && handle.Reason.Contains("句柄或归属"),"job events preserve unknown identity when handle unavailable");}
        using(var reused=Empty()) {reused.Record(6,103,_=>Process.GetCurrentProcess(),_=>{});reused.Record(7,103,_=>null,_=>{});reused.Record(6,103,_=>null,_=>{});Check(reused.Uncertain && reused.Reason.Contains("PID 复用"),"job events reject duplicate or reused PID");}
        using(var gap=Empty()) {gap.Record(7,104,_=>null,_=>{});Check(gap.Uncertain && gap.Reason.Contains("缺少加入"),"job events detect exit without observed start");}
        using(var invalid=new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(-1),false)) {using var unavailable=JobEventObserver.TryAttach(invalid,out string reason);Check(unavailable==null && reason.Contains("无法关联"),"job event association failure remains explicit");}
        string gapDir=Path.Combine(root,"job-gap-engine");Directory.CreateDirectory(gapDir);string gapLog=Path.Combine(gapDir,"gui.log");await File.WriteAllTextAsync(gapLog,"old\n");var gapStore=new Store(gapDir);gapStore.Tasks.Add(new TaskSpec{Name="maa",Program=Environment.ProcessPath!,Arguments="fixture-maa-success \""+gapLog+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="MAA日志",LogPath=gapLog,MaaExclusiveLog=true,TrustCompletion=true});gapStore.Tasks.Add(Spec("after-gap","echo wrong"));var gapEngine=new RunnerEngine(gapStore,()=>{}){MaaVersionSupported=_=>true,JobEventsTestHook=events=>events.MarkUncertain("测试：通知缺失")};var gapRun=gapEngine.Start();for(int i=0;i<180 && !gapEngine.Waiting;i++)await Task.Delay(30);bool blocked=gapEngine.Waiting && gapStore.History.Single().Events.Any(x=>x.Contains("测试：通知缺失")) && !gapStore.History.Single().Events.Any(x=>x.Contains("已启动 after-gap"));gapEngine.Stop();await gapRun;Check(blocked,"missing job evidence blocks MAA continuation");
        using var outsider=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-identity-held"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true})!;await outsider.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));var task=new TaskSpec{Program=Environment.ProcessPath!,Arguments="fixture-job-parent",WorkingDirectory=AppContext.BaseDirectory,Completion="MAA日志"};using var owned=OwnedProcess.Start(task);bool outsiderInGroup=owned.ContainsProcess(outsider.SafeHandle);var lines=new List<string>();var output=owned.Output.ReadToEndAsync();var errors=owned.Error.ReadToEndAsync();for(int i=0;i<500 && (!owned.Root.HasExited || owned.Accounting().Active>0);i++){owned.JobEvents?.Poll(owned,lines.Add);await Task.Delay(20);}await owned.Root.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));await Task.Delay(100);owned.JobEvents?.Poll(owned,lines.Add);await output;await errors;var events=owned.JobEvents;uint total=owned.Accounting().Total;string result=events?.Summary(total)??owned.JobEventsUnavailable;Check(events!=null && total>=7 && events.NewCount>=1 && events.ExitCount>=1 && lines.Any(x=>x.Contains("加入通知")) && lines.Any(x=>x.Contains("退出通知")) && result.Contains("累计进程") && ((events.NewCount==total && events.ExitCount==total) || events.Uncertain),"job notifications observe concurrent short-lived children without assuming delivery");bool outsiderSeen=lines.Any(x=>x.Contains("PID "+outsider.Id+"；"));await outsider.StandardInput.WriteLineAsync("release");await outsider.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Check(!outsiderInGroup && !outsiderSeen,"job observer excludes group-outside process");
    }
    static async Task TaskTrialTests()
    {
        var missing=TaskConfigurationReview.Check(new TaskSpec());Check(!missing.CanRun && missing.Errors.Any(x=>x.Contains("程序路径")) && missing.Warnings.Any(x=>x.Contains("业务")),"task review rejects missing program and explains limits");
        var invalid=Spec("bad","echo x");invalid.WorkingDirectory=Path.Combine(root,"not-a-directory");invalid.Completion="日志信号";invalid.LogSource="日志文件";invalid.LogPath=Path.Combine(root,"missing.log");invalid.SuccessText="";
        var checkedBad=TaskConfigurationReview.Check(invalid);Check(!checkedBad.CanRun && checkedBad.Errors.Count>=3,"task review reports directory log and success signal problems");
        var share=Spec("shared","echo READY");share.Completion="日志信号";share.SuccessText="READY";share.SharedLog=true;Check(TaskConfigurationReview.Check(share).Warnings.Any(x=>x.Contains("共享")),"task review flags shared log uncertainty");
        var closeInvalid=Spec("invalid-close","echo x");closeInvalid.CloseAfterManualConfirmation=true;Check(!TaskConfigurationReview.Check(closeInvalid).CanRun && !new TaskSpec().CloseAfterManualConfirmation,"manual close defaults off and requires manual confirmation");
        var logCloseInvalid=Spec("invalid-log-close","echo x");logCloseInvalid.CloseAfterLogSignal=true;Check(!TaskConfigurationReview.Check(logCloseInvalid).CanRun && !new TaskSpec().CloseAfterLogSignal,"log close defaults off and requires trusted unshared file evidence");
        logCloseInvalid.Completion="日志信号";logCloseInvalid.LogSource="日志文件";logCloseInvalid.SuccessText="READY";logCloseInvalid.TrustCompletion=true;Check(TaskConfigurationReview.Check(logCloseInvalid).Errors.Any(x=>x.Contains("确认日志不共享")) && !new TaskSpec().CloseLogExclusiveConfirmed,"log close requires explicit exclusive-log confirmation");
        var closeStore=NewStore("trial-manual-close-failed");closeStore.Tasks.Add(new TaskSpec{Name="manual-close",Program=Environment.ProcessPath!,Arguments="fixture-wait",WorkingDirectory=AppContext.BaseDirectory,Completion="人工确认",CloseAfterManualConfirmation=true});closeStore.Tasks.Add(Spec("after-close","echo next"));int closeCount=0;var closeEngine=new RunnerEngine(closeStore,()=>{}){RequestOwnedWindowClose=_=>{closeCount++;return false;}};var closeRun=closeEngine.Start();for(int i=0;i<100 && !closeStore.History.Single().Events.Any(x=>x.Contains("已启动 manual-close"));i++)await Task.Delay(20);closeEngine.Confirm();for(int i=0;i<200 && !closeEngine.Waiting;i++)await Task.Delay(20);bool closeBlocked=closeCount==1 && closeEngine.Waiting && closeStore.History.Single().Events.Any(x=>x.Contains("关闭请求未确认")) && !closeStore.History.Single().Events.Any(x=>x.Contains("已启动 after-close"));closeEngine.Stop();await closeRun;Check(closeBlocked,"failed manual close blocks next task without repeated requests");
        string closeDir=Path.Combine(root,"trial-manual-close-window");Directory.CreateDirectory(closeDir);string closeLog=Path.Combine(closeDir,"fixture.log");await File.WriteAllTextAsync(closeLog,"old\n");var closeWindowStore=new Store(closeDir);closeWindowStore.Tasks.Add(new TaskSpec{Name="manual-window",Program=Environment.ProcessPath!,Arguments="fixture-maa-window \""+closeLog+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="人工确认",CloseAfterManualConfirmation=true});closeWindowStore.Tasks.Add(Spec("after-window-close","echo next"));var closeWindowEngine=new RunnerEngine(closeWindowStore,()=>{});var closeWindowRun=closeWindowEngine.Start();for(int i=0;i<500 && (!File.Exists(closeLog) || !File.ReadAllText(closeLog).Contains("任务已全部完成"));i++)await Task.Delay(20);closeWindowEngine.Confirm();for(int i=0;i<400 && !closeWindowRun.IsCompleted && !closeWindowEngine.Waiting;i++)await Task.Delay(20);bool closeWindowOk=closeWindowRun.IsCompleted && closeWindowStore.History.Single().Events.Any(x=>x.Contains("已请求正常关闭本轮程序")) && closeWindowStore.History.Single().Events.Any(x=>x.Contains("已启动 after-window-close"));if(!closeWindowRun.IsCompleted)closeWindowEngine.Stop();await closeWindowRun;Check(closeWindowOk,"manual confirmation requests owned window close and waits before advancing");
        string logCloseDir=Path.Combine(root,"trial-log-close-window");Directory.CreateDirectory(logCloseDir);string logCloseFile=Path.Combine(logCloseDir,"fixture.log");await File.WriteAllTextAsync(logCloseFile,"old\n");var logCloseStore=new Store(logCloseDir);logCloseStore.Tasks.Add(new TaskSpec{Name="log-window",Program=Environment.ProcessPath!,Arguments="fixture-maa-window \""+logCloseFile+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",LogSource="日志文件",LogPath=logCloseFile,SuccessText="任务已全部完成",TrustCompletion=true,CloseAfterLogSignal=true,CloseLogExclusiveConfirmed=true});logCloseStore.Tasks.Add(Spec("after-log-close","echo next"));var logCloseEngine=new RunnerEngine(logCloseStore,()=>{});var logCloseRun=logCloseEngine.Start();for(int i=0;i<600 && !logCloseRun.IsCompleted && !logCloseEngine.Waiting;i++)await Task.Delay(20);bool logClosed=logCloseRun.IsCompleted && logCloseStore.History.Single().Events.Any(x=>x.Contains("已请求正常关闭本轮程序")) && logCloseStore.History.Single().Events.Any(x=>x.Contains("已启动 after-log-close"));if(!logCloseRun.IsCompleted)logCloseEngine.Stop();await logCloseRun;Check(logClosed,"trusted log signal requests owned window close before advancing");
        string updateDir=Path.Combine(root,"trial-log-close-update");Directory.CreateDirectory(updateDir);string updateFile=Path.Combine(updateDir,"fixture.log");await File.WriteAllTextAsync(updateFile,"old\n");var updateStore=new Store(updateDir);updateStore.Tasks.Add(new TaskSpec{Name="update-window",Program=Environment.ProcessPath!,Arguments="fixture-maa-window-update \""+updateFile+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",LogSource="日志文件",LogPath=updateFile,SuccessText="任务已全部完成",TrustCompletion=true,CloseAfterLogSignal=true,CloseLogExclusiveConfirmed=true});updateStore.Tasks.Add(Spec("after-update-window","echo wrong"));int updateCloseCalls=0;var updateEngine=new RunnerEngine(updateStore,()=>{}){RequestOwnedWindowClose=_=>{updateCloseCalls++;return false;}};var updateRun=updateEngine.Start();for(int i=0;i<350 && !updateStore.History.Single().Events.Any(x=>x.Contains("检测到更新或重启线索"));i++)await Task.Delay(20);await Task.Delay(2500);bool updateBlocked=updateCloseCalls==0 && updateStore.History.Single().Events.Any(x=>x.Contains("检测到更新或重启线索")) && !updateStore.History.Single().Events.Any(x=>x.Contains("已启动 after-update-window"));updateEngine.Stop();await updateRun;Check(updateBlocked,"update signal prevents log-triggered close and subsequent task");
        var st=NewStore("trial-basic");var first=Spec("first","echo first");var second=Spec("second","echo second");st.Tasks.Add(first);st.Tasks.Add(second);st.Save();var engine=new RunnerEngine(st,()=>{});int cancels=0;var attempt=TaskTrial.Start(st,engine,second.Id,()=>false,()=>{cancels++;return true;});if(attempt.Running!=null)await attempt.Running;
        var history=st.History.Single();Check(attempt.Error==null && cancels==1 && history.Trigger=="手动试运行" && history.PlanName=="second" && history.AllSucceeded==true && history.Events.Any(x=>x.Contains("已启动 second")) && !history.Events.Any(x=>x.Contains("已启动 first")) && !st.History.Any(x=>x.Trigger=="完成后操作"),"single task trial persists intent and never starts other tasks or completion action");
        var reloaded=NewStore("trial-basic");reloaded.Load();Check(reloaded.History.Single().Trigger=="手动试运行" && reloaded.History.Single().Events.Any(x=>x.Contains("试运行意图")),"trial history remains readable after restart");
        var log=NewStore("trial-log");var logTask=Spec("log","echo READY");logTask.Completion="日志信号";logTask.SuccessText="READY";log.Tasks.Add(logTask);log.Save();var le=new RunnerEngine(log,()=>{});var lr=TaskTrial.Start(log,le,logTask.Id,()=>false,()=>true);if(lr.Running!=null)await lr.Running;Check(lr.Error==null && log.History.Single().AllSucceeded==true && log.History.Single().Events.Any(x=>x.Contains("日志信号")),"trial respects log success evidence");
        var manual=NewStore("trial-manual");var mt=Spec("manual","echo done");mt.Completion="人工确认";manual.Tasks.Add(mt);manual.Save();var me=new RunnerEngine(manual,()=>{});var mr=TaskTrial.Start(manual,me,mt.Id,()=>false,()=>true);for(int i=0;i<150 && !me.Waiting;i++)await Task.Delay(20);bool pending=me.Waiting && manual.History.Single().State=="运行中";me.Confirm();if(mr.Running!=null)await mr.Running;Check(pending && manual.History.Single().AllSucceeded==true && manual.History.Single().Events.Any(x=>x.Contains("人工确认")),"trial requires manual confirmation after process exit");
        var uncertain=NewStore("trial-uncertain");var ut=Spec("untrusted","echo done");ut.TrustCompletion=false;uncertain.Tasks.Add(ut);uncertain.Save();var ue=new RunnerEngine(uncertain,()=>{});var ur=TaskTrial.Start(uncertain,ue,ut.Id,()=>false,()=>true);for(int i=0;i<150 && !ue.Waiting;i++)await Task.Delay(20);bool blocked=ue.Waiting;ue.Continue();if(ur.Running!=null)await ur.Running;Check(blocked && uncertain.History.Single().AllSucceeded==false && !uncertain.Tasks.Single().TrustCompletion,"trial preserves untrusted pause and saved task setting");
        var idle=TaskTrial.Start(st,new RunnerEngine(st,()=>{}),"absent",()=>false,()=>true);var busy=TaskTrial.Start(st,new RunnerEngine(st,()=>{}),first.Id,()=>true,()=>true);var denied=TaskTrial.Start(st,new RunnerEngine(st,()=>{}),first.Id,()=>false,()=>false);Check(idle.Running==null && busy.Running==null && denied.Running==null && st.History.Count==1,"trial rejects unsaved busy and uncancellable completion");
        var recovery=NewStore("trial-recovery");recovery.Tasks.Add(first.Copy());recovery.History.Add(new RunRecord{State="中断"});recovery.Save();Check(TaskTrial.Start(recovery,new RunnerEngine(recovery,()=>{}),first.Id,()=>false,()=>true).Running==null,"trial blocks unverified interruption");
        string fdir=Path.Combine(root,"trial-fail-save");var failure=new Store(fdir,p=>new FaultWriteStream(p));failure.Tasks.Add(first.Copy());var fe=new RunnerEngine(failure,()=>{});var fr=TaskTrial.Start(failure,fe,first.Id,()=>false,()=>true);Check(fr.Running==null && fr.Error?.Contains("保存失败")==true && failure.History.Count==0 && !fe.Running,"trial save failure prevents process launch");
        string edir=Path.Combine(root,"trial-unexpected-save");var exceptional=new Store(edir,_=>throw new InvalidOperationException("fixture save failure"));exceptional.Tasks.Add(first.Copy());var ee=new RunnerEngine(exceptional,()=>{});var er=TaskTrial.Start(exceptional,ee,first.Id,()=>false,()=>true);Check(er.Running==null && er.Error?.Contains("保存失败")==true && exceptional.History.Count==0 && !ee.Running,"trial unexpected save failure leaves no pending record");
        var active=NewStore("trial-active");var wait=new TaskSpec{Name="wait",Program=Environment.ProcessPath!,Arguments="fixture-wait",WorkingDirectory=AppContext.BaseDirectory,TrustCompletion=true};active.Tasks.Add(wait);active.Tasks.Add(first.Copy());active.Save();var ae=new RunnerEngine(active,()=>{});var ar=TaskTrial.Start(active,ae,wait.Id,()=>false,()=>true);var other=TaskTrial.Start(active,ae,first.Id,()=>false,()=>true);ae.Stop();if(ar.Running!=null)await ar.Running;Check(other.Running==null && active.History.Single().State=="已停止" && !active.History.Single().Events.Any(x=>x.Contains("已启动 first")),"trial enforces serial execution and stop prevents other tasks");
    }
    static async Task GenericStartTests()
    {
        Check(!new TaskSpec().StartWithSelectedButton, "generic selected-button start defaults off");
        var invalid=Spec("generic-invalid","echo x");invalid.StartWithSelectedButton=true;Check(!TaskConfigurationReview.Check(invalid).CanRun,"generic start requires selected button");
        invalid.StartButtonName="Link Start!";Check(TaskConfigurationReview.Check(invalid).Errors.Any(x=>x.Contains("不会自行开始")) && !new TaskSpec().StartManualLaunchConfirmed,"generic start requires explicit manual-launch confirmation");
        async Task Run(string mode, bool expectedClick)
        {
            string dir=Path.Combine(root,"generic-start-"+mode);Directory.CreateDirectory(dir);string log=Path.Combine(dir,"gui.log"),counter=Path.Combine(dir,"clicked.txt");await File.WriteAllTextAsync(log,"old\n");var store=new Store(dir);store.Tasks.Add(new TaskSpec{Name="generic",Program=Environment.ProcessPath!,Arguments=MaaStartController.SkipArgument+" fixture-maa-start generic-"+mode+" \""+log+"\" \""+counter+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",LogSource="日志文件",LogPath=log,SuccessText="任务已全部完成",TrustCompletion=true,CloseAfterLogSignal=true,CloseLogExclusiveConfirmed=true,StartWithSelectedButton=true,StartManualLaunchConfirmed=true,StartButtonName="Link Start!"});store.Tasks.Add(Spec("generic-next","echo next"));var engine=new RunnerEngine(store,()=>{});var running=engine.Start();for(int i=0;i<1000 && !running.IsCompleted && !engine.Waiting;i++)await Task.Delay(20);var events=store.History.Single().Events;int clicks=File.Exists(counter)?File.ReadAllLines(counter).Length:0;bool ok=expectedClick ? clicks==1 && running.IsCompleted && events.Any(x=>x.Contains("已启动 generic-next")) : clicks==0 && engine.Waiting && !events.Any(x=>x.Contains("已启动 generic-next"));if(!running.IsCompleted)engine.Stop();await running;Check(ok,"generic start "+mode);
        }
        await Run("single",true);await Run("duplicate",false);await Run("disabled",false);await Run("already",false);await Run("update",false);
        string stopDir=Path.Combine(root,"generic-start-stop");Directory.CreateDirectory(stopDir);string stopLog=Path.Combine(stopDir,"gui.log"),stopCounter=Path.Combine(stopDir,"clicked.txt");await File.WriteAllTextAsync(stopLog,"old\n");var stopStore=new Store(stopDir);stopStore.Tasks.Add(new TaskSpec{Name="generic-stop",Program=Environment.ProcessPath!,Arguments=MaaStartController.SkipArgument+" fixture-maa-start generic-missing \""+stopLog+"\" \""+stopCounter+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",LogSource="日志文件",LogPath=stopLog,SuccessText="任务已全部完成",TrustCompletion=true,StartWithSelectedButton=true,StartManualLaunchConfirmed=true,StartButtonName="Link Start!"});stopStore.Tasks.Add(Spec("after-generic-stop","echo wrong"));var stopEngine=new RunnerEngine(stopStore,()=>{});var stopRun=stopEngine.Start();for(int i=0;i<150 && !stopStore.History.Single().Events.Any(x=>x.Contains("已启动 generic-stop"));i++)await Task.Delay(20);stopEngine.Stop();await stopRun;Check(!File.Exists(stopCounter) && stopStore.History.Single().State=="已停止" && !stopStore.History.Single().Events.Any(x=>x.Contains("已启动 after-generic-stop")),"generic start stop blocks invocation and next task");
        string failDir=Path.Combine(root,"generic-start-save-failure");Directory.CreateDirectory(failDir);string failLog=Path.Combine(failDir,"gui.log"),failCounter=Path.Combine(failDir,"clicked.txt");await File.WriteAllTextAsync(failLog,"old\n");bool inject=false;var failStore=new Store(failDir,p=>inject?new FaultWriteStream(p):new FileStream(p,FileMode.CreateNew,FileAccess.Write,FileShare.None));failStore.Tasks.Add(new TaskSpec{Name="generic-save",Program=Environment.ProcessPath!,Arguments=MaaStartController.SkipArgument+" fixture-maa-start generic-single \""+failLog+"\" \""+failCounter+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",LogSource="日志文件",LogPath=failLog,SuccessText="任务已全部完成",TrustCompletion=true,StartWithSelectedButton=true,StartManualLaunchConfirmed=true,StartButtonName="Link Start!"});failStore.Tasks.Add(Spec("after-generic-save","echo wrong"));RunnerEngine? failEngine=null;failEngine=new RunnerEngine(failStore,()=>{if(failEngine?.Status.Contains("已启动 generic-save")==true)inject=true;});await failEngine.Start();Check(inject && failEngine.NeedsRecovery && !File.Exists(failCounter) && !failStore.History.Single().Events.Any(x=>x.Contains("已启动 after-generic-save")),"generic start save failure blocks invocation and next task");
    }
    static async Task OcrHelperTests()
    {
        string helper=Path.Combine(Environment.CurrentDirectory,"工具","视觉组件","bin","Debug","net10.0-windows","win-x64","OcrHelper.exe");
        string dir=Path.Combine(root,"ocr-helper");Directory.CreateDirectory(dir);string image=Path.Combine(dir,"synthetic.png");
        var visual=new System.Windows.Media.DrawingVisual();using(var canvas=visual.RenderOpen()) { canvas.DrawRectangle(System.Windows.Media.Brushes.White,null,new System.Windows.Rect(0,0,600,160));var text=new System.Windows.Media.FormattedText("开始运行 Start",System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),System.Windows.FlowDirection.LeftToRight,new System.Windows.Media.Typeface("Microsoft YaHei"),40,System.Windows.Media.Brushes.Black,1.0);canvas.DrawText(text,new System.Windows.Point(50,45)); }
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(600,160,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using(var stream=File.Create(image))encoder.Save(stream);
        var lines=await OcrHelperClient.RecognizeAsync(image,helper,TimeSpan.FromSeconds(30));Check(lines.Any(x=>x.Text.Contains("开始运行") && x.Points.Count==4 && x.Confidence is >=0 and <=1),"offline OCR helper returns Chinese text and coordinates");
        var inMemory=await OcrHelperClient.RecognizeBytesAsync(await File.ReadAllBytesAsync(image),helper,TimeSpan.FromSeconds(30));Check(inMemory.Any(x=>x.Text.Contains("开始运行") && x.Points.Count==4),"OCR helper accepts in-memory image without screenshot file");
        bool missing=false;try {await OcrHelperClient.RecognizeAsync(image,Path.Combine(dir,"missing.exe"),TimeSpan.FromSeconds(3));}catch(FileNotFoundException){missing=true;}Check(missing,"OCR helper missing fails without fallback");
    }
    static void VisualEvidenceTests()
    {
        var area=new System.Drawing.Rectangle(0,0,200,100);
        OcrHelperClient.Line Line(string text,double confidence,double x=10,double y=10) => new(text,confidence,new[]{(x,y),(x+100,y),(x+100,y+40),(x,y+40)});
        var target=Line("开始运行",0.99);var rendered=Line("开始运行",0.86,12,11);
        bool Agrees(OcrHelperClient.Line[] first,OcrHelperClient.Line[] second) => VisualStartController.AgreeOnText(first,second,"开始运行",area)!=null;
        Check(Agrees([target],[rendered]),"dual OCR accepts independently recognized same target with rendering variation");
        Check(!Agrees([target],[Line("取消",0.99)]),"dual OCR rejects different screen target");
        Check(!Agrees([target],[rendered,rendered]),"dual OCR rejects duplicate screen text");
        Check(!Agrees([target],[Line("开始运行",0.99,25,10)]),"dual OCR rejects target at different position");
        Check(!Agrees([target],[Line("开始运行",0.79)]),"dual OCR rejects low confidence");
        Check(!Agrees([target],[Line("开始运行",0.99,double.NaN)]),"dual OCR rejects invalid coordinates");
        Check(VisualStartController.AgreeOnText([target],[rendered],"开始运行",new System.Drawing.Rectangle(20,20,120,60))==null,"dual OCR rejects text cut by configured region");
    }
    static void RenderedImageEvidenceTests(string scenePath,string templatePath)
    {
        byte[] scene=File.ReadAllBytes(scenePath),template=File.ReadAllBytes(templatePath);var region=new System.Drawing.Rectangle(0,0,600,300);
        var reference=new TaskSpec { VisualWindowRegionHash=VisualStartController.RegionHash(scene,region),VisualScreenRegionHash=VisualStartController.RegionHash(scene,region),VisualTemplateReferenceHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(template)) };
        Check(VisualStartController.ImageReferencesMatch(reference,scene,scene,template,region),"image reference accepts unchanged window screen and template");
        var changedTemplate=(byte[])template.Clone();changedTemplate[^1]^=1;
        Check(!VisualStartController.ImageReferencesMatch(reference,scene,scene,changedTemplate,region),"image reference rejects any template byte change");
        using(var subtle=new System.Drawing.Bitmap(templatePath))using(var output=new MemoryStream())
        { var pixel=subtle.GetPixel(8,12);subtle.SetPixel(8,12,System.Drawing.Color.FromArgb(pixel.A,Math.Min(255,pixel.R+1),pixel.G,pixel.B));subtle.Save(output,System.Drawing.Imaging.ImageFormat.Png);Check(!VisualStartController.ImageReferencesMatch(reference,scene,scene,output.ToArray(),region),"image reference rejects valid subtly altered template"); }
        using(var imageCopy=new System.Drawing.Bitmap(scenePath))using(var output=new MemoryStream())
        { imageCopy.SetPixel(10,10,System.Drawing.Color.Magenta);imageCopy.Save(output,System.Drawing.Imaging.ImageFormat.Png);Check(VisualStartController.ImageReferencesMatch(reference,scene,output.ToArray(),template,region),"image reference permits changed screen region across instances");Check(VisualStartController.ImageReferencesMatch(reference,output.ToArray(),scene,template,region),"image reference permits changed window region across instances"); }
        Check(!VisualStartController.ImageReferencesMatch(new TaskSpec(),scene,scene,template,region),"legacy image task without reference remains blocked");
        var legacy=new TaskSpec { StartWithSelectedButton=true,VisualMode="图片",VisualTemplatePath=templatePath };
        Check(TaskConfigurationReview.Check(legacy).Errors.Any(x=>x.Contains("模板指纹")),"legacy image task requires selecting its region again");
        var templateOnly=new TaskSpec { VisualTemplateReferenceHash=reference.VisualTemplateReferenceHash };
        Check(VisualStartController.ImageReferencesMatch(templateOnly,scene,scene,template,region),"image reference accepts template fingerprint without old region fingerprints");
        var point=VisualStartController.FindRenderedImage(scene,template,region);
        Check(point!=null,"OpenCV identifies rendered fixture template");
        using(var changedInstance=new System.Drawing.Bitmap(scenePath))using(var changedBytes=new MemoryStream())
        {
            changedInstance.SetPixel(500,200,System.Drawing.Color.Magenta);changedInstance.Save(changedBytes,System.Drawing.Imaging.ImageFormat.Png);
            byte[] other=changedBytes.ToArray();var nextPoint=VisualStartController.FindRenderedImage(other,template,region);
            Check(VisualStartController.RegionHash(scene,region)!=VisualStartController.RegionHash(other,region) && VisualStartController.ImageReferencesMatch(reference,other,other,template,region) && point!=null && nextPoint==point,"changed instance region keeps unique unchanged target");
        }
        Check(VisualStartController.FindRenderedImage(scene,template,new System.Drawing.Rectangle(300,150,100,100))==null,"OpenCV rejects candidate outside selected region");
        using var image=new System.Drawing.Bitmap(templatePath);int rejected=0;
        using(var blank=new System.Drawing.Bitmap(image.Width,image.Height))using(var graphics=System.Drawing.Graphics.FromImage(blank))using(var bytes=new MemoryStream())
        { graphics.Clear(System.Drawing.Color.White);blank.Save(bytes,System.Drawing.Imaging.ImageFormat.Png);Check(VisualStartController.FindRenderedImage(scene,bytes.ToArray(),region)==null,"OpenCV rejects uniform templates"); }
        foreach(var (x,y) in new[]{(3,3),(4,4),(8,12),(0,0),(20,5),(35,13),(22,18),(25,22),(39,31)})
        {
            using var variant=new System.Drawing.Bitmap(image);variant.SetPixel(x,y,System.Drawing.Color.Yellow);using var bytes=new MemoryStream();variant.Save(bytes,System.Drawing.Imaging.ImageFormat.Png);
            if(VisualStartController.FindRenderedImage(scene,bytes.ToArray(),region)==null)rejected++;
        }
        Check(rejected==9,"OpenCV rejects nine altered fixture templates");
        if(point!=null)
        {
            using var bitmap=new System.Drawing.Bitmap(scenePath);var source=new System.Drawing.Rectangle(point.Value.X-image.Width/2,point.Value.Y-image.Height/2,image.Width,image.Height);
            using(var crop=bitmap.Clone(source,bitmap.PixelFormat))using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.DrawImageUnscaled(crop,150,100);
            using var bytes=new MemoryStream();bitmap.Save(bytes,System.Drawing.Imaging.ImageFormat.Png);
            Check(VisualStartController.FindRenderedImage(bytes.ToArray(),template,region)==null,"OpenCV rejects two rendered candidates");
        }
        else Check(false,"OpenCV rejects two rendered candidates");
        using(var stripes=new System.Drawing.Bitmap(80,40))using(var pattern=new System.Drawing.Bitmap(20,20))
        {
            using(var g=System.Drawing.Graphics.FromImage(stripes)){g.Clear(System.Drawing.Color.White);for(int x=0;x<80;x+=4)g.FillRectangle(System.Drawing.Brushes.Black,x,0,2,40);}
            using(var g=System.Drawing.Graphics.FromImage(pattern)){g.Clear(System.Drawing.Color.White);for(int x=0;x<20;x+=4)g.FillRectangle(System.Drawing.Brushes.Black,x,0,2,20);}
            using var s=new MemoryStream();using var p=new MemoryStream();stripes.Save(s,System.Drawing.Imaging.ImageFormat.Png);pattern.Save(p,System.Drawing.Imaging.ImageFormat.Png);
            Check(VisualStartController.FindRenderedImage(s.ToArray(),p.ToArray(),new System.Drawing.Rectangle(0,0,80,40))==null,"OpenCV rejects nearby periodic candidates");
        }
    }
    static async Task VisualStartTests(bool quick=false,bool actualScreen=false,bool iconOnly=false)
    {
        string dir=Path.Combine(root,"visual-start");Directory.CreateDirectory(dir);string template=Path.Combine(dir,"icon.png");VisualStartController.DiagnosticTestHook=s=>Console.WriteLine("DETAIL visual check "+s);
        var drawing=new System.Windows.Media.DrawingVisual();using(var dc=drawing.RenderOpen()){dc.DrawRectangle(System.Windows.Media.Brushes.White,null,new System.Windows.Rect(0,0,40,32));dc.DrawRectangle(System.Windows.Media.Brushes.DarkBlue,null,new System.Windows.Rect(3,3,12,24));dc.DrawRectangle(System.Windows.Media.Brushes.DarkRed,null,new System.Windows.Rect(20,5,16,9));dc.DrawRectangle(System.Windows.Media.Brushes.DarkGreen,null,new System.Windows.Rect(22,18,12,10));}var bmp=new System.Windows.Media.Imaging.RenderTargetBitmap(40,32,96,96,System.Windows.Media.PixelFormats.Pbgra32);bmp.Render(drawing);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));using(var stream=File.Create(template))encoder.Save(stream);
        using(var image=new System.Drawing.Bitmap(template))using(var surface=new System.Drawing.Bitmap(100,80))
        {
            using(var graphics=System.Drawing.Graphics.FromImage(surface)){graphics.Clear(System.Drawing.Color.White);graphics.DrawImageUnscaled(image,10,10);}
            byte[] Encode(){using var stream=new MemoryStream();surface.Save(stream,System.Drawing.Imaging.ImageFormat.Png);return stream.ToArray();}
            var expected=new System.Drawing.Rectangle(0,0,100,80);byte[] pattern=await File.ReadAllBytesAsync(template);
            bool exact=VisualStartController.FindRenderedImage(Encode(),pattern,expected)!=null;surface.SetPixel(13,13,System.Drawing.Color.Magenta);
            Check(exact && VisualStartController.FindRenderedImage(Encode(),pattern,expected)==null,"visual template rejects similar nonidentical image");
            using(var graphics=System.Drawing.Graphics.FromImage(surface)){graphics.Clear(System.Drawing.Color.White);graphics.DrawImageUnscaled(image,10,10);}
            var edge=surface.GetPixel(13,13);surface.SetPixel(13,13,System.Drawing.Color.FromArgb(Math.Min(255,edge.R+8),edge.G,edge.B));
            bool rendering=VisualStartController.FindRenderedImage(Encode(),pattern,expected)!=null;
            surface.SetPixel(16,16,System.Drawing.Color.Magenta);
            Check(rendering && VisualStartController.FindRenderedImage(Encode(),pattern,expected)==null,"visual screen tolerates small rendering difference but rejects interior alteration");
        }
        async Task Run(string mode,bool shouldAdvance,bool wrongSize=false)
        {
            string local=Path.Combine(dir,mode+(wrongSize?"-size":""));Directory.CreateDirectory(local);string log=Path.Combine(local,"run.log"),counter=Path.Combine(local,"clicks.txt");await File.WriteAllTextAsync(log,"old\n");
            var task=new TaskSpec{Name="visual",Program=Environment.ProcessPath!,Arguments="fixture-visual-start "+mode+" \""+log+"\" \""+counter+"\" \""+template+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",LogSource="日志文件",LogPath=log,SuccessText="DONE",TrustCompletion=true,CloseAfterLogSignal=true,CloseLogExclusiveConfirmed=true,StartWithSelectedButton=true,StartManualLaunchConfirmed=true,VisualMode=mode.StartsWith("text")?"文字":"图片",VisualText="开始运行",VisualTemplatePath=template,VisualX=0,VisualY=0,VisualWidth=600,VisualHeight=300,VisualWindowWidth=wrongSize?601:600,VisualWindowHeight=300};
            int captures=0;VisualStartController.ScreenTestHook=actualScreen?null:(window,area)=>{var copy=window.Clone(area,System.Drawing.Imaging.PixelFormat.Format32bppArgb);if(mode=="text-changed" && ++captures>1)copy.SetPixel(1,1,System.Drawing.Color.Magenta);return copy;};
            if(task.VisualMode=="图片")
            {
                using var setup=Process.Start(new ProcessStartInfo(task.Program,task.Arguments){UseShellExecute=false,WorkingDirectory=task.WorkingDirectory}) ?? throw new IOException("无法启动配置夹具");
                VisualStartController.Selection? selection=null;
                for(int n=0;n<80 && !setup.HasExited && selection==null;n++)
                {
                    setup.Refresh();if(setup.MainWindowHandle!=IntPtr.Zero)
                    {
                        var rectangle=System.Windows.Automation.AutomationElement.FromHandle(setup.MainWindowHandle).Current.BoundingRectangle;
                        selection=VisualStartController.Select(new System.Drawing.Point((int)rectangle.Left+7,(int)rectangle.Top+7),new System.Drawing.Point((int)rectangle.Right-7,(int)rectangle.Bottom-7),task.Program,template);
                    }
                    if(selection==null)await Task.Delay(100);
                }
                if(!setup.HasExited)setup.CloseMainWindow();await setup.WaitForExitAsync();
                if(selection==null){VisualStartController.ScreenTestHook=null;Check(false,"visual start "+(actualScreen?"real-screen ":"simulated ")+mode+" setup");return;}
                task.VisualX=selection.X;task.VisualY=selection.Y;task.VisualWidth=selection.Width;task.VisualHeight=selection.Height;task.VisualWindowWidth=selection.WindowWidth;task.VisualWindowHeight=selection.WindowHeight;task.VisualWindowRegionHash=selection.WindowHash;task.VisualScreenRegionHash=selection.ScreenHash;task.VisualTemplateReferenceHash=selection.TemplateHash;
            }
            var store=new Store(local);store.Tasks.Add(task);store.Tasks.Add(Spec("visual-next","echo next"));
            bool compared=false;VisualStartController.FrameTestHook=task.VisualMode=="图片"?frame=>{if(iconOnly)File.WriteAllBytes(Path.Combine(local,"frame.png"),frame);if(!compared){compared=true;var region=new System.Drawing.Rectangle(task.VisualX,task.VisualY,task.VisualWidth,task.VisualHeight);Console.WriteLine("DETAIL independent setup/runtime window region hashes equal="+(task.VisualWindowRegionHash==VisualStartController.RegionHash(frame,region)));}}:null;
            var engine=new RunnerEngine(store,()=>{});var run=engine.Start();for(int i=0;i<1200 && !run.IsCompleted && !engine.Waiting;i++)await Task.Delay(25);var events=store.History.Single().Events;int count=File.Exists(counter)?File.ReadAllLines(counter).Length:0;bool ok=shouldAdvance?count==1 && run.IsCompleted && events.Any(x=>x.Contains("已启动 visual-next")):count==0 && engine.Waiting && !events.Any(x=>x.Contains("已启动 visual-next"));if(!ok)Console.WriteLine("DETAIL visual "+mode+" clicks="+count+" completed="+run.IsCompleted+" waiting="+engine.Waiting+" "+engine.Status+" "+string.Join(" | ",events));if(!run.IsCompleted)engine.Stop();await run;VisualStartController.FrameTestHook=null;VisualStartController.ScreenTestHook=null;Check(ok,"visual start "+(actualScreen?"real-screen ":"simulated ")+mode+(wrongSize?" wrong-size":""));
        }
        if(iconOnly){await Run("icon",true);return;}await Run("text",true);if(actualScreen){await Run("icon",true);return;}if(quick)return;await Run("text-changed",false);await Run("text-duplicate",false);await Run("icon",true);await Run("icon-duplicate",false);await Run("text",false,true);
    }
    static async Task StartupErrorTests()
    {
        string runner=Path.Combine(Environment.CurrentDirectory,"src","Runner","bin","Debug","net10.0-windows","Runner.exe");
        Process Launch(string dir)
        {
            var info=new ProcessStartInfo(runner){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true};
            info.Environment["RUNNER_V3_DATA_DIR"]=dir;info.Environment["RUNNER_V3_SMOKE"]="1";info.Environment["RUNNER_V3_SMOKE_SECONDS"]="2";
            return Process.Start(info) ?? throw new IOException("无法启动隔离预览程序");
        }
        async Task<(bool shown,int code,string error)> RunFailure(string dir)
        {
            using var proc=Launch(dir); var err=proc.StandardError.ReadToEndAsync();var output=proc.StandardOutput.ReadToEndAsync();bool shown=false;
            for(int i=0;i<250 && !proc.HasExited;i++)
            {
                EnumWindows((hwnd,_)=>{GetWindowThreadProcessId(hwnd,out uint pid);if(pid!=(uint)proc.Id)return true;bool matched=false;
                    EnumChildWindows(hwnd,(child,_)=>{var text=new StringBuilder(1024);GetWindowText(child,text,text.Capacity);if(text.ToString().Contains("数据读取失败，软件不会覆盖该文件"))matched=true;return true;},IntPtr.Zero);
                    if(matched){shown=true;PostMessage(hwnd,0x0010,IntPtr.Zero,IntPtr.Zero);}return true;},IntPtr.Zero);
                await Task.Delay(40);
            }
            if(!proc.HasExited){proc.Kill();await proc.WaitForExitAsync();return (shown,-999,"测试进程超时");}
            await proc.WaitForExitAsync();await output;return (shown,proc.ExitCode,await err);
        }
        string invalid=Path.Combine(root,"startup-invalid");Directory.CreateDirectory(invalid);string invalidFile=Path.Combine(invalid,"data.json");byte[] bad=Encoding.UTF8.GetBytes("{\"Format\":\"foreign\"}");await File.WriteAllBytesAsync(invalidFile,bad);
        var invalidRun=await RunFailure(invalid);
        Check(invalidRun.shown && invalidRun.code==1 && !invalidRun.error.Contains("XamlParseException") && (await File.ReadAllBytesAsync(invalidFile)).SequenceEqual(bad),"startup invalid data shows error exits and preserves bytes");
        string locked=Path.Combine(root,"startup-locked");Directory.CreateDirectory(locked);string lockedFile=Path.Combine(locked,"data.json");byte[] valid=Encoding.UTF8.GetBytes("{\"Format\":\"SequentialRunnerV3/1\"}");await File.WriteAllBytesAsync(lockedFile,valid);
        (bool shown,int code,string error) lockedRun;
        using(var held=new FileStream(lockedFile,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) lockedRun=await RunFailure(locked);
        Check(lockedRun.shown && lockedRun.code==1 && !lockedRun.error.Contains("XamlParseException") && (await File.ReadAllBytesAsync(lockedFile)).SequenceEqual(valid),"startup locked data shows error exits and preserves bytes");
        using var retry=Launch(locked);await retry.WaitForExitAsync();Check(retry.ExitCode==0,"startup succeeds after lock released and single instance resources reclaimed");
    }
    sealed class FakeQqHandler : System.Net.Http.HttpMessageHandler
    {
        public byte[] Key=[]; public int PollStatus=1; public bool Deny; public bool Tamper; public bool InvalidUser; public int Sends; public TaskCompletionSource<bool>? SendEntered, SendRelease;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(Deny)return new(System.Net.HttpStatusCode.Forbidden){Content=new System.Net.Http.StringContent("private-response")};
            string path=request.RequestUri!.AbsolutePath;string body=await request.Content!.ReadAsStringAsync(ct);string result;
            if(path.EndsWith("create_bind_task")){using var doc=System.Text.Json.JsonDocument.Parse(body);Key=Convert.FromBase64String(doc.RootElement.GetProperty("key").GetString()!);result="{\"retcode\":0,\"data\":{\"task_id\":\"mock-task\"}}";}
            else if(path.EndsWith("poll_bind_result"))
            {
                if(PollStatus!=2)result="{\"retcode\":0,\"data\":{\"status\":"+PollStatus+"}}";
                else{byte[] iv=new byte[12],plain=Encoding.UTF8.GetBytes("fake-secret"),cipher=new byte[plain.Length],tag=new byte[16];using(var aes=new System.Security.Cryptography.AesGcm(Key,16))aes.Encrypt(iv,plain,cipher,tag);if(Tamper)tag[0]^=1;string packed=Convert.ToBase64String(iv.Concat(cipher).Concat(tag).ToArray());result="{\"retcode\":0,\"data\":{\"status\":2,\"bot_appid\":\"123\",\"user_openid\":\""+(InvalidUser?"invalid/path":"mock_user")+"\",\"bot_encrypt_secret\":\""+packed+"\"}}";}
            }
            else if(path.EndsWith("getAppAccessToken"))result="{\"access_token\":\"fake-token\"}";
            else {Sends++;SendEntered?.TrySetResult(true);if(SendRelease!=null)await SendRelease.Task.WaitAsync(ct);result="{\"id\":\"mock-id\"}";}
            return new(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StringContent(result)};
        }
    }
    sealed class FakeReleaseHandler(string json, System.Net.HttpStatusCode status=System.Net.HttpStatusCode.OK) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken cancellationToken)
        {
            if(request.RequestUri?.AbsoluteUri!="https://api.github.com/repos/dXiaoFengb/SeqRunner/releases/latest")throw new InvalidOperationException("unexpected URL");
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(status){Content=new System.Net.Http.StringContent(json)});
        }
    }
    sealed class FakeUpdateDownloadHandler(bool badRedirect=false) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(request.RequestUri!.Host=="github.com")
            {
                var redirect=new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Redirect);redirect.Headers.Location=new Uri(badRedirect?"https://example.com/file":"https://release-assets.githubusercontent.com/test?token=fixture");return Task.FromResult(redirect);
            }
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.ByteArrayContent(Encoding.UTF8.GetBytes("fixture"))});
        }
    }
    sealed class FakeCompleteUpdateHandler(byte[] package) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(request.RequestUri!.Host=="api.github.com")return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StringContent("{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v1.20.0\",\"assets\":[{\"name\":\"SequentialRunnerV3-v1.20.0.zip\",\"browser_download_url\":\"https://github.com/dXiaoFengb/SeqRunner/releases/download/v1.20.0/SequentialRunnerV3-v1.20.0.zip\"}]}")});
            if(request.RequestUri.Host=="github.com")return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.ByteArrayContent(package)});
            throw new InvalidOperationException("unexpected fixture request");
        }
    }
    static async Task SelfUpdateSimulationTests()
    {
        string dir=Path.Combine(root,"simulation-data");Directory.CreateDirectory(dir);var gate=new SelfUpdateInterlock(dir);var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var progress=new List<string>();bool idle=false;
        Task run=SelfUpdateSimulation.RunAsync(dir,gate,()=>idle,fixture=>gate.TryReserve(()=>idle,fixture),s=>{lock(progress)progress.Add(s);if(s.Contains("等待任务空闲"))entered.TrySetResult(true);},CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));Check(!gate.Blocked && !run.IsCompleted,"interactive update simulation waits without reserving while busy");idle=true;await run.WaitAsync(TimeSpan.FromSeconds(30));
        lock(progress)Check(!gate.Blocked && progress.Any(s=>s.Contains("检查模拟版本")) && progress.Any(s=>s.Contains("下载模拟包")) && progress.Any(s=>s.Contains("验证签名")) && progress.Any(s=>s.Contains("独立进程安装")) && progress.Last().Contains("正式程序没有更新"),"interactive update simulation reports stages and completes without official update");
        string fixture=Directory.EnumerateDirectories(dir,"update-fixture-*").Single();using(var status=System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture,"status.json"))))Check(status.RootElement.GetProperty("Status").GetString()=="隔离测试：完成；正式程序没有更新","interactive update simulation persists final test status");
        Check(!SelfUpdateSimulation.Allowed(Path.GetTempPath()),"interactive update simulation rejects non-workspace directories");
    }
    static async Task SelfUpdateFullFixtureTests()
    {
        string dir=Path.Combine(root,"update-complete-chain");Directory.CreateDirectory(dir);using var key=System.Security.Cryptography.RSA.Create(2048);
        byte[] payload=Encoding.UTF8.GetBytes("new");var manifest=new SelfUpdatePackage.Manifest("1.20.0",[new("Runner.dll",payload.Length,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)))]);
        byte[] manifestBytes=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest);byte[] signature=key.SignData(manifestBytes,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pss);
        using var memory=new MemoryStream();using(var zip=new System.IO.Compression.ZipArchive(memory,System.IO.Compression.ZipArchiveMode.Create,true)){void Add(string name,byte[] bytes){using var output=zip.CreateEntry(name).Open();output.Write(bytes);}Add(".v3-update/manifest.json",manifestBytes);Add(".v3-update/manifest.sig",signature);Add("Runner.dll",payload);}
        using var client=new SelfUpdateClient(new FakeCompleteUpdateHandler(memory.ToArray()));var release=await client.CheckAsync(new Version(1,19,0),CancellationToken.None);string package=await client.DownloadAsync(release!,dir,CancellationToken.None);
        File.WriteAllText(Path.Combine(dir,".runner-update-test-root"),"fixture");Directory.CreateDirectory(Path.Combine(dir,"current"));File.WriteAllText(Path.Combine(dir,"current","Runner.dll"),"old");string publicKey=Path.Combine(dir,"public-key.der");File.WriteAllBytes(publicKey,key.ExportSubjectPublicKeyInfo());
        string data=Path.Combine(dir,"data");var store=new Store(data);var gate=new SelfUpdateInterlock(data);var engine=new RunnerEngine(store,()=>{}){UpdateHandoffPending=()=>gate.Blocked};bool reserved=engine.TryReserveUpdate(gate,()=>true);
        var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};info.ArgumentList.Add("fixture-update-verified-install");info.ArgumentList.Add(dir);info.ArgumentList.Add(package);info.ArgumentList.Add(publicKey);using var process=Process.Start(info)!;
        bool exited=process.WaitForExit(15000);bool blocked=engine.NeedsRecovery;bool acknowledged=exited && process.ExitCode==0 && gate.TryAcknowledge(()=>SelfUpdateTestTransaction.Recover(dir)=="完成");
        Check(reserved && blocked && acknowledged && !engine.NeedsRecovery && File.ReadAllText(Path.Combine(dir,"current","Runner.dll"))=="new" && File.ReadAllText(Path.Combine(dir,"previous","Runner.dll"))=="old","update fixture chain checks downloads verifies stages and installs in independent process");
        string bad=Path.Combine(root,"update-bad-helper");Directory.CreateDirectory(bad);File.WriteAllText(Path.Combine(bad,".runner-update-test-root"),"fixture");Directory.CreateDirectory(Path.Combine(bad,"current"));File.WriteAllText(Path.Combine(bad,"current","Runner.dll"),"old");string badPackage=Path.Combine(bad,"package.zip"),badKey=Path.Combine(bad,"public-key.der");File.WriteAllBytes(badPackage,memory.ToArray());using(var wrong=System.Security.Cryptography.RSA.Create(2048))File.WriteAllBytes(badKey,wrong.ExportSubjectPublicKeyInfo());
        var invalidInfo=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};invalidInfo.ArgumentList.Add("fixture-update-verified-install");invalidInfo.ArgumentList.Add(bad);invalidInfo.ArgumentList.Add(badPackage);invalidInfo.ArgumentList.Add(badKey);using var invalid=Process.Start(invalidInfo)!;bool invalidExited=invalid.WaitForExit(15000);
        Check(invalidExited && invalid.ExitCode==5 && File.ReadAllText(Path.Combine(bad,"current","Runner.dll"))=="old" && !Directory.Exists(Path.Combine(bad,"previous")),"independent update helper rejects untrusted package before touching current version");
        string appRoot=Path.Combine(root,"update-runner-helper");Directory.CreateDirectory(appRoot);File.WriteAllText(Path.Combine(appRoot,".runner-update-test-root"),"fixture");Directory.CreateDirectory(Path.Combine(appRoot,"current"));File.WriteAllText(Path.Combine(appRoot,"current","Runner.dll"),"old");string appPackage=Path.Combine(appRoot,"package.zip"),appKey=Path.Combine(appRoot,"public-key.der");File.WriteAllBytes(appPackage,memory.ToArray());File.WriteAllBytes(appKey,key.ExportSubjectPublicKeyInfo());
        string runner=Path.Combine(AppContext.BaseDirectory,"Runner.exe");var appInfo=new ProcessStartInfo(runner){UseShellExecute=false,CreateNoWindow=true};appInfo.ArgumentList.Add("--isolated-update-helper");appInfo.ArgumentList.Add(appRoot);appInfo.ArgumentList.Add(appPackage);appInfo.ArgumentList.Add(appKey);using var app=Process.Start(appInfo)!;bool appExited=app.WaitForExit(15000);
        Check(appExited && app.ExitCode==0 && SelfUpdateTestTransaction.Recover(appRoot)=="完成" && File.ReadAllText(Path.Combine(appRoot,"current","Runner.dll"))=="new","actual Runner helper entry installs only verified isolated fixture");
    }
    static async Task SelfUpdateReleaseTests()
    {
        string Json(string url,bool pre=false)=>System.Text.Json.JsonSerializer.Serialize(new{draft=false,prerelease=pre,tag_name="v1.20.0",assets=new[]{new{name="SequentialRunnerV3-v1.20.0.zip",browser_download_url=url}}});
        const string valid="https://github.com/dXiaoFengb/SeqRunner/releases/download/v1.20.0/SequentialRunnerV3-v1.20.0.zip";
        using(var client=new SelfUpdateClient(new FakeReleaseHandler(Json(valid)))) { var result=await client.CheckAsync(new Version(1,19,0),CancellationToken.None);Check(result?.Version==new Version(1,20,0),"self update selects official newer release");Check(await client.CheckAsync(new Version(1,20,0,0),CancellationToken.None)==null,"self update reports no newer release"); }
        using(var client=new SelfUpdateClient(new FakeReleaseHandler("{}",System.Net.HttpStatusCode.NotFound)))Check(await client.CheckAsync(new Version(1,19,0),CancellationToken.None)==null,"self update handles missing release");
        foreach(var (name,json) in new[]{("prerelease",Json(valid,true)),("foreign source",Json("https://example.com/SequentialRunnerV3-v1.20.0.zip")),("nonstandard port",Json("https://github.com:444/dXiaoFengb/SeqRunner/releases/download/v1.20.0/SequentialRunnerV3-v1.20.0.zip"))})
        {using var client=new SelfUpdateClient(new FakeReleaseHandler(json));bool rejected=false;try{await client.CheckAsync(new Version(1,19,0),CancellationToken.None);}catch(InvalidDataException){rejected=true;}Check(rejected,"self update rejects "+name);}
        string dir=Path.Combine(root,"self-update-download");Directory.CreateDirectory(dir);var release=new SelfUpdateRelease(new Version(1,20,0),"v1.20.0",new Uri(valid));
        using(var client=new SelfUpdateClient(new FakeUpdateDownloadHandler())){string file=await client.DownloadAsync(release,dir,CancellationToken.None);Check(file.EndsWith(".unverified") && await File.ReadAllTextAsync(file)=="fixture","self update downloads only into unverified isolated file");}
        using(var client=new SelfUpdateClient(new FakeUpdateDownloadHandler(true))){bool rejected=false;try{await client.DownloadAsync(release,dir,CancellationToken.None);}catch(InvalidDataException){rejected=true;}Check(rejected,"self update rejects download redirect outside trusted hosts");}
        using(var client=new SelfUpdateClient(new FakeUpdateDownloadHandler()))using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool cancelled=false;try{await client.DownloadAsync(release,dir,cancel.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"self update interrupted download remains unverified");}
    }
    static async Task SelfUpdateCoordinatorTests()
    {
        var store=NewStore("update-coordinator");int requests=0;
        var disabled=new SelfUpdateCoordinator(store,()=>{},()=>false,_=>{requests++;return Task.FromResult<SelfUpdateRelease?>(null);});
        Check(!store.UpdateCheckOnStartup && !store.UpdateCheckScheduled,"self update automatic checks default off");
        await disabled.CheckAsync();Check(requests==0 && disabled.Status.Contains("未配置可信发布公钥"),"self update missing trust key blocks network");
        store.UpdateCheckOnStartup=true;await disabled.OnStartupAsync();Check(requests==0,"self update startup without trust key does not call network");
        store.UpdateCheckScheduled=true;store.UpdateCheckTime="09:00";var now=new DateTimeOffset(2026,11,22,9,0,0,TimeSpan.Zero);
        var enabled=new SelfUpdateCoordinator(store,()=>{},()=>true,_=>{requests++;return Task.FromResult<SelfUpdateRelease?>(null);});
        await enabled.TickAsync(now,TimeZoneInfo.Utc);await enabled.TickAsync(now,TimeZoneInfo.Utc);
        Check(requests==1 && store.UpdateLastCheckDate=="2026-11-22","self update schedule checks once per day after saving intent");
        var loaded=NewStore("update-coordinator");loaded.Load();Check(loaded.UpdateCheckOnStartup && loaded.UpdateCheckScheduled && loaded.UpdateCheckTime=="09:00" && loaded.UpdateLastCheckDate=="2026-11-22","self update settings and schedule marker persist");
        await enabled.TickAsync(now.AddDays(-1),TimeZoneInfo.Utc);Check(requests==1,"self update date rollback does not repeat checks");
        var blocked=new Store(Path.Combine(root,"update-save-failure"),_=>throw new IOException("fixture")){UpdateCheckScheduled=true,UpdateCheckTime="09:00"};int blockedCalls=0;
        var failedSave=new SelfUpdateCoordinator(blocked,()=>{},()=>true,_=>{blockedCalls++;return Task.FromResult<SelfUpdateRelease?>(null);});await failedSave.TickAsync(now,TimeZoneInfo.Utc);Check(blockedCalls==0 && blocked.UpdateLastCheckDate=="" && failedSave.Status.Contains("保存失败"),"self update schedule save failure prevents network check");
        var offline=new SelfUpdateCoordinator(store,()=>{},()=>true,_=>throw new System.Net.Http.HttpRequestException("fixture"));await offline.CheckAsync();Check(offline.Status.Contains("检查失败"),"self update offline check failure is isolated");
        var legacy=NewStore("update-legacy");Directory.CreateDirectory(Path.Combine(root,"update-legacy"));File.WriteAllText(Path.Combine(root,"update-legacy","data.json"),"{\"Format\":\"SequentialRunnerV3/1\"}");legacy.Load();Check(!legacy.UpdateCheckOnStartup && !legacy.UpdateCheckScheduled && legacy.UpdateCheckTime=="09:00","self update legacy data defaults remain disabled");
        var window=new SelfUpdateWindow(store,disabled);IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject parent){foreach(var child in System.Windows.LogicalTreeHelper.GetChildren(parent).OfType<System.Windows.DependencyObject>()){yield return child;foreach(var descendant in Descendants(child))yield return descendant;}}
        var nodes=Descendants(window).ToArray();Check(nodes.OfType<System.Windows.Controls.Button>().Any(x=>x.Name=="UpdateCheckInput") && nodes.OfType<System.Windows.Controls.CheckBox>().Count(x=>x.Name is "UpdateStartupInput" or "UpdateScheduledInput")==2,"self update window exposes check and settings without display");
        var time=nodes.OfType<System.Windows.Controls.TextBox>().Single(x=>x.Name=="UpdateTimeInput");var save=nodes.OfType<System.Windows.Controls.Button>().Single(x=>x.Name=="UpdateSaveInput");var viewStatus=nodes.OfType<System.Windows.Controls.TextBlock>().Single(x=>x.Name=="UpdateStatus");
        time.Text="30:99";save.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Check(store.UpdateCheckTime=="09:00" && viewStatus.Text.Contains("请输入"),"self update window rejects invalid schedule");
        time.Text="10:30";save.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));var reopened=NewStore("update-coordinator");reopened.Load();Check(reopened.UpdateCheckTime=="10:30" && viewStatus.Text.Contains("已保存"),"self update window saves settings independently");window.Close();
        var simulateWindow=new SelfUpdateWindow(store,disabled,(report,_)=>{report("隔离测试：完成；正式程序没有更新");return Task.CompletedTask;});var simulationNodes=Descendants(simulateWindow).ToArray();var simulationButton=simulationNodes.OfType<System.Windows.Controls.Button>().Single(x=>x.Name=="UpdateSimulationInput");simulationButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Check(simulationNodes.OfType<System.Windows.Controls.TextBlock>().Single(x=>x.Name=="UpdateStatus").Text=="隔离测试：完成；正式程序没有更新","update window reports isolated simulation without claiming official update");simulateWindow.Close();
    }
    static async Task SelfUpdateInterlockTests()
    {
        string activeDir=Path.Combine(root,"update-active");var activeStore=new Store(activeDir);activeStore.Tasks.Add(new TaskSpec{Name="wait",Program=Environment.ProcessPath!,Arguments="fixture-wait",WorkingDirectory=AppContext.BaseDirectory,TrustCompletion=true});var activeEngine=new RunnerEngine(activeStore,()=>{});var activeGate=new SelfUpdateInterlock(activeDir);Task activeRun=activeEngine.Start();
        Check(activeEngine.Running && !activeEngine.TryReserveUpdate(activeGate,()=>true) && !activeGate.Blocked,"update reservation waits while an owned task runs");await activeRun;
        string waitingDir=Path.Combine(root,"update-manual-wait");var waitingStore=new Store(waitingDir);waitingStore.Tasks.Add(new TaskSpec{Name="manual",Program=Environment.ProcessPath!,Arguments="fixture-wait",WorkingDirectory=AppContext.BaseDirectory,Completion="人工确认"});var waitingEngine=new RunnerEngine(waitingStore,()=>{});var waitingGate=new SelfUpdateInterlock(waitingDir);Task waitingRun=waitingEngine.Start();
        for(int i=0;i<200 && !waitingEngine.Waiting;i++)await Task.Delay(20);Check(waitingEngine.Waiting && !waitingEngine.TryReserveUpdate(waitingGate,()=>true) && !waitingGate.Blocked,"update reservation waits for manual decision");waitingEngine.Stop();await waitingRun;
        string dir=Path.Combine(root,"update-interlock");var store=new Store(dir);var task=Spec("blocked","echo should-not-start");store.Tasks.Add(task);store.Save();
        var gate=new SelfUpdateInterlock(dir);Check(!gate.TryReserve(()=>false) && !gate.Blocked,"update interlock refuses busy or uncertain reservation");
        Check(gate.TryReserve(()=>true) && gate.Blocked && !gate.TryReserve(()=>true),"update interlock persists one exclusive intent");
        var engine=new RunnerEngine(store,()=>{}){UpdateHandoffPending=()=>new SelfUpdateInterlock(dir).Blocked};await engine.Start();
        Check(engine.NeedsRecovery && !engine.Running && store.History.Count==0,"persisted update intent blocks manual queue after recreation");
        var trial=TaskTrial.Start(store,engine,task.Id,()=>false,()=>true);Check(trial.Error!=null && !engine.Running,"persisted update intent blocks task trial");
        var now=new DateTimeOffset(2026,11,22,9,0,0,TimeSpan.Zero);store.Plans.Add(new SchedulePlan{Name="blocked",Enabled=true,StartupMode="always",EffectiveFrom=now.AddDays(-1),Items=[new ScheduleItem{TaskId=task.Id,Days=[now.DayOfWeek]}],Slots=[new ScheduleSlot{Time="09:00",Days=[now.DayOfWeek]}]});
        var startup=new StartupCoordinator(store,engine,()=>{});await startup.StartAsync(now,TimeZoneInfo.Utc);Check(store.History.Any(x=>x.State=="已跳过" && x.Events.Any(y=>y.Contains("启动计划未运行"))),"persisted update intent blocks startup plans");
        var scheduler=new Scheduler(store,engine,()=>{});scheduler.Tick(now.AddMinutes(-1),TimeZoneInfo.Utc);scheduler.Tick(now,TimeZoneInfo.Utc);
        Check(store.History.Any(x=>x.Trigger=="定时" && x.State=="已跳过" && x.Events.Any(y=>y.Contains("存在待核实事项"))) && !engine.Running,"persisted update intent skips due schedules");
        Check(!gate.TryAcknowledge(()=>false) && gate.Blocked,"update interlock remains blocked without verified recovery");
        Check(gate.TryAcknowledge(()=>true) && !gate.Blocked && !engine.NeedsRecovery,"verified recovery clears update interlock");
        string corruptDir=Path.Combine(root,"update-interlock-corrupt");Directory.CreateDirectory(corruptDir);File.WriteAllText(Path.Combine(corruptDir,"self-update-intent.json"),"invalid");var corrupt=new SelfUpdateInterlock(corruptDir);Check(corrupt.Blocked && !corrupt.TryAcknowledge(()=>true) && corrupt.Blocked,"malformed update intent remains blocked even if recovery claims success");
        Check(new SelfUpdateInterlock(Path.Combine(root,"update-missing-directory")).Blocked,"missing update data directory fails closed");
        string recoveryData=Path.Combine(root,"update-recovery-data"),fixtureName="update-fixture-"+Guid.NewGuid().ToString("N"),recoveryFixture=Path.Combine(recoveryData,fixtureName);Directory.CreateDirectory(recoveryFixture);File.WriteAllText(Path.Combine(recoveryFixture,".runner-update-test-root"),"fixture");Directory.CreateDirectory(Path.Combine(recoveryFixture,"current"));Directory.CreateDirectory(Path.Combine(recoveryFixture,"staged"));File.WriteAllText(Path.Combine(recoveryFixture,"current","Runner.fixture"),"old");File.WriteAllText(Path.Combine(recoveryFixture,"staged","Runner.fixture"),"new");
        var recoverGate=new SelfUpdateInterlock(recoveryData);bool reservedForRecovery=recoverGate.TryReserve(()=>true,fixtureName);var helperInfo=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};helperInfo.ArgumentList.Add("fixture-update-transaction-delayed");helperInfo.ArgumentList.Add(recoveryFixture);helperInfo.ArgumentList.Add("新程序已移动");using(var helper=Process.Start(helperInfo)!){bool recorded=recoverGate.TryRecordHelper(helper);bool running=recoverGate.RecoverIsolatedFixture().Contains("仍在运行");bool exited=helper.WaitForExit(15000);string recovered=recoverGate.RecoverIsolatedFixture();Check(reservedForRecovery && recorded && running && exited && helper.ExitCode==73 && recovered.Contains("已恢复旧程序") && !recoverGate.Blocked && File.ReadAllText(Path.Combine(recoveryFixture,"current","Runner.fixture"))=="old","isolated recovery waits for recorded helper and restores after interruption");}
        string unknownDir=Path.Combine(root,"update-recovery-unknown");Directory.CreateDirectory(unknownDir);var unknownGate=new SelfUpdateInterlock(unknownDir);bool unknownReserved=unknownGate.TryReserve(()=>true,"update-fixture-"+Guid.NewGuid().ToString("N"));Check(unknownReserved && unknownGate.RecoverIsolatedFixture().Contains("身份不明") && unknownGate.Blocked,"isolated recovery keeps intent blocked when helper identity is missing");
        string raceDir=Path.Combine(root,"update-race");var raceStore=new Store(raceDir);raceStore.Tasks.Add(Spec("race","echo should-not-start"));var raceGate=new SelfUpdateInterlock(raceDir);var raceEngine=new RunnerEngine(raceStore,()=>{}){UpdateHandoffPending=()=>raceGate.Blocked};using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var attempted=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reserve=Task.Run(()=>raceEngine.TryReserveUpdate(raceGate,()=>{entered.TrySetResult(true);return release.Wait(TimeSpan.FromSeconds(5));}));await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var start=Task.Run(async()=>{attempted.TrySetResult(true);await raceEngine.Start();});await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));release.Set();bool reserved=await reserve;await start;
        Check(reserved && raceGate.Blocked && !raceEngine.Running && raceStore.History.Count==0,"update reservation and task start are serialized under race");
    }
    static void SelfUpdateTransactionTests()
    {
        string NewFixture(string name)
        {
            string path=Path.Combine(root,"update-transaction",name);Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path,".runner-update-test-root"),"fixture");Directory.CreateDirectory(Path.Combine(path,"current"));Directory.CreateDirectory(Path.Combine(path,"staged"));
            File.WriteAllText(Path.Combine(path,"current","Runner.dll"),"old");File.WriteAllText(Path.Combine(path,"staged","Runner.dll"),"new");return path;
        }
        var success=NewFixture("success");SelfUpdateTestTransaction.Install(success);Check(SelfUpdateTestTransaction.Recover(success)=="完成" && File.ReadAllText(Path.Combine(success,"current","Runner.dll"))=="new" && File.ReadAllText(Path.Combine(success,"previous","Runner.dll"))=="old","update transaction retains verified previous version after success");
        bool repeated=false;try{SelfUpdateTestTransaction.Install(success);}catch(IOException){repeated=true;}Check(repeated && File.ReadAllText(Path.Combine(success,"current","Runner.dll"))=="new" && File.ReadAllText(Path.Combine(success,"previous","Runner.dll"))=="old","update transaction refuses repeated installation without changing versions");
        foreach(string phase in new[]{"准备","旧程序已移动","新程序已移动"})
        {
            string fixture=NewFixture("interrupt-"+phase);var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};info.ArgumentList.Add("fixture-update-transaction");info.ArgumentList.Add(fixture);info.ArgumentList.Add(phase);
            using var process=Process.Start(info)!;bool exited=process.WaitForExit(15000);bool recovered=exited && process.ExitCode==73 && SelfUpdateTestTransaction.Recover(fixture)=="已恢复" && File.ReadAllText(Path.Combine(fixture,"current","Runner.dll"))=="old";
            Check(recovered,"update transaction independent process interrupt recovers "+phase);
        }
        foreach(string phase in new[]{"隔离新程序","还原旧程序"})
        {
            string fixture=NewFixture("recovery-interrupt-"+phase);try{SelfUpdateTestTransaction.Install(fixture,p=>{if(p=="新程序已移动")throw new IOException("fixture");});}catch(IOException){}
            var info=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};info.ArgumentList.Add("fixture-update-recovery");info.ArgumentList.Add(fixture);info.ArgumentList.Add(phase);
            using var process=Process.Start(info)!;bool exited=process.WaitForExit(15000);bool recovered=exited && process.ExitCode==73 && SelfUpdateTestTransaction.Recover(fixture)=="已恢复" && File.ReadAllText(Path.Combine(fixture,"current","Runner.dll"))=="old";
            Check(recovered,"update transaction recovery can resume after interrupt "+phase);
        }
        string finished=NewFixture("after-finish");var doneInfo=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};doneInfo.ArgumentList.Add("fixture-update-transaction");doneInfo.ArgumentList.Add(finished);doneInfo.ArgumentList.Add("完成");using(var process=Process.Start(doneInfo)!){bool exited=process.WaitForExit(15000);Check(exited && process.ExitCode==73 && SelfUpdateTestTransaction.Recover(finished)=="完成" && File.ReadAllText(Path.Combine(finished,"current","Runner.dll"))=="new","completed update remains committed after helper exits");}
        string conflict=NewFixture("conflict");try{SelfUpdateTestTransaction.Install(conflict,p=>{if(p=="旧程序已移动")throw new IOException("fixture");});}catch(IOException){}File.WriteAllText(Path.Combine(conflict,"previous","Runner.dll"),"changed");bool refused=false;try{SelfUpdateTestTransaction.Recover(conflict);}catch(InvalidDataException){refused=true;}Check(refused && !Directory.Exists(Path.Combine(conflict,"current")),"update transaction refuses recovery when backup changed");
        string locked=NewFixture("locked");using(var held=new FileStream(Path.Combine(locked,".update-lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)){bool denied=false;try{SelfUpdateTestTransaction.Install(locked);}catch(IOException){denied=true;}Check(denied && File.ReadAllText(Path.Combine(locked,"current","Runner.dll"))=="old","update transaction concurrent helper is blocked");}
        string occupied=NewFixture("occupied");using(var held=new FileStream(Path.Combine(occupied,"current","Runner.dll"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)){bool denied=false;try{SelfUpdateTestTransaction.Install(occupied);}catch(IOException){denied=true;}Check(denied,"update transaction refuses a locked program file");}Check(File.ReadAllText(Path.Combine(occupied,"current","Runner.dll"))=="old","locked file leaves prior version unchanged");
        string userdata=NewFixture("userdata");File.WriteAllText(Path.Combine(userdata,"current","data.json"),"protected");bool deniedData=false;try{SelfUpdateTestTransaction.Install(userdata);}catch(InvalidDataException){deniedData=true;}Check(deniedData && File.ReadAllText(Path.Combine(userdata,"current","data.json"))=="protected","update transaction refuses to move user data");
    }
    static void SelfUpdatePackageTests()
    {
        string dir=Path.Combine(root,"self-update-package");Directory.CreateDirectory(dir);
        using var key=System.Security.Cryptography.RSA.Create(2048);using var wrong=System.Security.Cryptography.RSA.Create(2048);
        byte[] content=Encoding.UTF8.GetBytes("controlled update fixture");string hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        string Create(string name,string path="Runner.dll",string? recordedHash=null,bool tamperSignature=false,bool duplicate=false,string version="1.20.0")
        {
            byte[] manifest=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new SelfUpdatePackage.Manifest(version,[new(path,content.Length,recordedHash ?? hash)]));
            byte[] signature=key.SignData(manifest,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pss);if(tamperSignature)signature[0]^=1;
            string file=Path.Combine(dir,name+".zip");using var archive=System.IO.Compression.ZipFile.Open(file,System.IO.Compression.ZipArchiveMode.Create);
            void Add(string entry,byte[] bytes){using var output=archive.CreateEntry(entry).Open();output.Write(bytes);}
            Add(".v3-update/manifest.json",manifest);Add(".v3-update/manifest.sig",signature);Add(path,content);if(duplicate)Add(path.ToUpperInvariant(),content);return file;
        }
        bool Reject(string file,System.Security.Cryptography.RSA verifyKey){try{SelfUpdatePackage.Verify(file,verifyKey,new Version(1,19,0));return false;}catch(Exception e) when(e is InvalidDataException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException){return true;}}
        string valid=Create("valid");Check(SelfUpdatePackage.Verify(valid,key,new Version(1,19,0)).Version=="1.20.0","self update accepts signed valid newer package");
        string stage=SelfUpdatePackage.Stage(valid,key,new Version(1,19,0),new Version(1,20,0),dir);Check(File.ReadAllBytes(Path.Combine(stage,"Runner.dll")).SequenceEqual(content),"self update stages verified files in a new directory");
        bool mismatched=false;try{SelfUpdatePackage.Stage(valid,key,new Version(1,19,0),new Version(1,21,0),dir);}catch(InvalidDataException){mismatched=true;}Check(mismatched,"self update rejects signed package with wrong release version");
        Check(Reject(valid,wrong),"self update rejects untrusted signing key");Check(Reject(Create("signature",tamperSignature:true),key),"self update rejects tampered signature");
        Check(Reject(Create("digest",recordedHash:new string('0',64)),key),"self update rejects mismatched file digest");Check(Reject(Create("old",version:"1.19.0"),key),"self update rejects non-newer version");
        Check(Reject(Create("traversal",path:"../Runner.dll"),key),"self update rejects traversal paths");Check(Reject(Create("userdata",path:"data.json"),key),"self update rejects user data in package");Check(Reject(Create("reserved",path:"CON.txt"),key),"self update rejects Windows reserved file names");
        Check(Reject(Create("duplicates",duplicate:true),key),"self update rejects duplicate case-insensitive paths");
    }
    static void QqWindowEvidenceTests()
    {
        string dir=Path.Combine(root,"qq-window");Directory.CreateDirectory(dir);var applied=new MobileSettings();using var sender=new MobileNotifications(_=>{});
        var window=new MobileSettingsWindow(dir,new MobileSettings{ServerEnabled=true,ServerKey="SCTFake123",QqEnabled=true,QqAppId="123",QqSecret="fake-secret",QqOpenId="mock_user"},sender,value=>applied=value,"",false);
        IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject parent){foreach(var child in System.Windows.LogicalTreeHelper.GetChildren(parent).OfType<System.Windows.DependencyObject>()){yield return child;foreach(var descendant in Descendants(child))yield return descendant;}}
        var nodes=Descendants(window).ToArray();var check=nodes.OfType<System.Windows.Controls.CheckBox>().Single(x=>x.Name=="QqNoticeEnabledInput");var status=nodes.OfType<System.Windows.Controls.TextBlock>().Single(x=>x.Name=="QqNoticeStatus");var unbind=nodes.OfType<System.Windows.Controls.Button>().Single(x=>x.Name=="QqNoticeUnbindInput");
        var result=nodes.OfType<System.Windows.Controls.TextBlock>().Single(x=>x.Name=="MobileNoticeResult");var save=nodes.OfType<System.Windows.Controls.Button>().Single(x=>x.Name=="SaveMobileNoticeInput");
        bool shown=check.IsChecked==true && status.Text=="QQ：已绑定并启用";check.IsChecked=false;save.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));bool disabled=status.Text=="QQ：已绑定，尚未启用";check.IsChecked=true;save.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(disabled && status.Text=="QQ：已绑定并启用","QQ saved binding state refreshes without showing window");result.Text="QQ：服务已接受";unbind.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));var saved=MobileSettingsStore.Load(dir);
        Check(shown && !saved.QqEnabled && saved.QqSecret=="" && saved.ServerEnabled && saved.ServerKey=="SCTFake123" && status.Text.Contains("已解绑") && result.Text=="","QQ unbind preserves other channels and clears old status without showing window");window.Close();
        var fresh=new MobileSettingsWindow(dir,new MobileSettings(),sender,_=>{},"",false);var freshNodes=Descendants(fresh).ToArray();Check(freshNodes.OfType<System.Windows.Controls.CheckBox>().Single(x=>x.Name=="QqNoticeEnabledInput").IsChecked==false && freshNodes.OfType<System.Windows.Controls.TextBlock>().Single(x=>x.Name=="QqNoticeStatus").Text.Contains("未绑定"),"QQ settings window defaults unbound and disabled");fresh.Close();
    }
    static async Task QqNotificationTests()
    {
        QqWindowEvidenceTests();
        var fake=new FakeQqHandler();using var client=new QqBotClient(fake);using var session=await client.CreateAsync(CancellationToken.None);
        Check(session.Url.StartsWith("https://q.qq.com/qqbot/openclaw/connect.html?task_id=") && session.Key.Length==32,"QQ bind creates local random key and official QR URL");
        using(var generator=new QRCoder.QRCodeGenerator())using(var qrData=generator.CreateQrCode(session.Url,QRCoder.QRCodeGenerator.ECCLevel.Q))using(var qr=new QRCoder.PngByteQRCode(qrData))Check(qr.GetGraphic(5).AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"QQ QR code is rendered locally");
        Check((await client.PollAsync(session,CancellationToken.None)).Status==1,"QQ pending binding remains pending");fake.PollStatus=3;Check((await client.PollAsync(session,CancellationToken.None)).Status==3,"QQ expired binding does not produce credentials");fake.PollStatus=2;var done=await client.PollAsync(session,CancellationToken.None);Check(done.Credentials is {AppId:"123",Secret:"fake-secret",OpenId:"mock_user"},"QQ bound credentials decrypt with authenticated AES GCM");
        fake.Tamper=true;bool tampered=false;try{await client.PollAsync(session,CancellationToken.None);}catch(System.Security.Cryptography.CryptographicException){tampered=true;}fake.Tamper=false;Check(tampered,"QQ tampered binding credentials are rejected");fake.InvalidUser=true;bool badUser=false;try{await client.PollAsync(session,CancellationToken.None);}catch(InvalidDataException){badUser=true;}fake.InvalidUser=false;Check(badUser,"QQ invalid recipient is rejected");
        string dir=Path.Combine(root,"qq-notice");Directory.CreateDirectory(dir);var s=new MobileSettings{QqEnabled=true,QqAppId=done.Credentials!.AppId,QqSecret=done.Credentials.Secret,QqOpenId=done.Credentials.OpenId};MobileSettingsStore.Save(dir,s);var raw=File.ReadAllBytes(Path.Combine(dir,"mobile-notifications.dat"));var saved=MobileSettingsStore.Load(dir);Check(saved.QqEnabled && saved.QqSecret=="fake-secret" && !Encoding.UTF8.GetString(raw).Contains("fake-secret"),"QQ credentials persist only encrypted");
        await client.SendAsync(done.Credentials,new("任务需要处理","请查看"),CancellationToken.None);Check(fake.Sends==1,"QQ sends only to bound user via simulated service");
        fake.Deny=true;bool rejected=false;try{await client.SendAsync(done.Credentials,new("测试","测试"),CancellationToken.None);}catch(System.Net.Http.HttpRequestException){rejected=true;}Check(rejected,"QQ denied service is not counted as accepted");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();bool stopped=false;try{await client.CreateAsync(cancelled.Token);}catch(OperationCanceledException){stopped=true;}Check(stopped,"QQ cancelled binding does not complete");
        var events=new List<string>();var queueHandler=new FakeQqHandler();using(var notices=new MobileNotifications(x=>{lock(events)events.Add(x);},qqFactory:()=>new QqBotClient(queueHandler)))
        {notices.Publish(s,new("需要处理","测试",true));for(int i=0;i<100;i++){lock(events)if(events.Count>0)break;await Task.Delay(20);}lock(events)Check(events.Any(x=>x.Contains("服务已接受")) && events.All(x=>!x.Contains("fake-secret")),"QQ queue reports acceptance without leaking credentials");}
        var deniedEvents=new List<string>();using(var notices=new MobileNotifications(x=>{lock(deniedEvents)deniedEvents.Add(x);},qqFactory:()=>new QqBotClient(new FakeQqHandler{Deny=true})))
        {notices.Publish(s,new("需要处理","测试",true));for(int i=0;i<100;i++){lock(deniedEvents)if(deniedEvents.Count>0)break;await Task.Delay(20);}lock(deniedEvents)Check(deniedEvents.Any(x=>x.Contains("发送失败")) && deniedEvents.All(x=>!x.Contains("private-response") && !x.Contains("fake-secret")),"QQ send failure is isolated and sanitized");}
        var lateEvents=new List<string>();var lateHandler=new FakeQqHandler{SendEntered=new(),SendRelease=new()};using(var notices=new MobileNotifications(x=>{lock(lateEvents)lateEvents.Add(x);},qqFactory:()=>new QqBotClient(lateHandler)))
        {notices.Publish(s,new("需要处理","测试",true));await lateHandler.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));notices.InvalidatePending();lateHandler.SendRelease.SetResult(true);await Task.Delay(100);lock(lateEvents)Check(lateEvents.Count==0,"QQ stale in-flight result does not restore old status after unbind");}
        var unbound=new MobileSettings{QqEnabled=true};bool invalid=false;try{MobileNotifications.Validate(unbound);}catch(ArgumentException){invalid=true;}Check(invalid && !new MobileSettings().QqEnabled,"QQ defaults off and requires binding");
    }
    sealed class FakeMobileHandler : System.Net.Http.HttpMessageHandler
    {
        public readonly List<string> Bodies=[];
        public System.Net.HttpStatusCode Code=System.Net.HttpStatusCode.OK;
        public bool Throw;
        public bool Timeout;
        public TaskCompletionSource<bool>? Gate;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken cancellationToken)
        {
            if(Throw)throw new System.Net.Http.HttpRequestException("secret-must-not-leak");
            if(Timeout)throw new TaskCanceledException();
            if(Gate!=null)await Gate.Task.WaitAsync(cancellationToken);
            string body=await request.Content!.ReadAsStringAsync(cancellationToken);lock(Bodies)Bodies.Add(body);
            return new System.Net.Http.HttpResponseMessage(Code){Content=new System.Net.Http.StringContent("{\"code\":0}")};
        }
    }
    sealed class LoopbackMobileHandler(int port) : System.Net.Http.DelegatingHandler(new System.Net.Http.HttpClientHandler { AllowAutoRedirect=false, UseProxy=false })
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken cancellationToken)
        {
            request.RequestUri=new Uri($"http://127.0.0.1:{port}/mock");request.Headers.Host=null;return base.SendAsync(request,cancellationToken);
        }
    }
    static async Task MobileNotificationTests()
    {
        string dir=Path.Combine(root,"mobile");Directory.CreateDirectory(dir);
        var empty=MobileSettingsStore.Load(dir);Check(!empty.ServerEnabled && !empty.WebhookEnabled,"mobile notifications default off");
        var settings=new MobileSettings{ServerEnabled=true,WebhookEnabled=true,ServerKey="SCTTestSecret123",WebhookUrl="https://example.invalid/secret-webhook",Template="{\"title\":\"{title}\",\"content\":\"{content}\"}"};
        MobileSettingsStore.Save(dir,settings);byte[] disk=File.ReadAllBytes(Path.Combine(dir,"mobile-notifications.dat"));var loaded=MobileSettingsStore.Load(dir);
        Check(loaded.ServerKey==settings.ServerKey && loaded.WebhookUrl==settings.WebhookUrl && loaded.ServerEnabled && loaded.WebhookEnabled,"mobile protected settings round trip");
        Check(!Encoding.UTF8.GetString(disk).Contains("SCTTestSecret123") && !Encoding.UTF8.GetString(disk).Contains("secret-webhook"),"mobile credentials are encrypted on disk");
        string recoverDir=Path.Combine(root,"mobile-recovery");Directory.CreateDirectory(recoverDir);string original=Path.Combine(recoverDir,"mobile-notifications.dat");File.WriteAllBytes(original,[1,2,3]);bool unreadable=false;try{MobileSettingsStore.Load(recoverDir);}catch(System.Security.Cryptography.CryptographicException){unreadable=true;}
        MobileSettingsStore.Save(recoverDir,settings,true);Check(unreadable && File.ReadAllBytes(original).SequenceEqual(new byte[]{1,2,3}) && MobileSettingsStore.Load(recoverDir).ServerKey==settings.ServerKey,"mobile recovery preserves unreadable original and uses new configuration");
        Check(MobileNotifications.ServerUri("SCTTestSecret123").Host=="sctapi.ftqq.com" && MobileNotifications.ServerUri("sctp123tExample123").Host=="123.push.ft07.com","mobile serverchan versions use separate endpoints");
        bool invalidKey=false;try{MobileNotifications.ServerUri("sctp"+new string('9',200)+"tExample");}catch(ArgumentException){invalidKey=true;}Check(invalidKey,"mobile malformed serverchan identifier is rejected safely");
        bool bad=false;try{MobileNotifications.Validate(new MobileSettings{WebhookEnabled=true,WebhookUrl="http://example.invalid"});}catch(ArgumentException){bad=true;}Check(bad,"mobile webhook requires HTTPS");
        string payload=MobileNotifications.RenderTemplate(settings.Template,new LocalNotice("a\"b","line\nvalue"));using(var doc=System.Text.Json.JsonDocument.Parse(payload))Check(doc.RootElement.GetProperty("title").GetString()=="a\"b" && doc.RootElement.GetProperty("content").GetString()=="line\nvalue","mobile JSON template escapes substituted values");
        Check(!MobileNotifications.ShouldSend(new("任务完成","ok")) && MobileNotifications.ShouldSend(new("队列已完成","ok")) && MobileNotifications.ShouldSend(new("等待人工处理","wait",true)),"mobile defaults to exception and queue end");
        var problemNotices=new List<LocalNotice>();var observer=new LocalNotifications(()=>true,problemNotices.Add);observer.ObserveProblem("engine","保存失败");observer.ObserveProblem("engine","保存失败");observer.ObserveProblem("engine",null);observer.ObserveProblem("engine","保存失败");Check(problemNotices.Count==2,"mobile repeated ongoing storage error is deduplicated until recovered");
        var messages=new List<string>();var fake=new FakeMobileHandler();using(var sender=new MobileNotifications(s=>{lock(messages)messages.Add(s);},fake))
        {
            sender.Publish(settings,new("任务完成","not sent"));await Task.Delay(100);lock(fake.Bodies)Check(fake.Bodies.Count==0,"mobile ignores ordinary task success");
            sender.Publish(settings,new("队列已完成","all done"));for(int i=0;i<100;i++){lock(fake.Bodies)if(fake.Bodies.Count==2)break;await Task.Delay(20);}lock(fake.Bodies)Check(fake.Bodies.Count==2 && fake.Bodies.Any(x=>x.Contains("all+done")) && fake.Bodies.Any(x=>x.Contains("all done")),"mobile independent channels send via fake handler");
            for(int i=0;i<100;i++){lock(messages)if(messages.Count>=2)break;await Task.Delay(20);}lock(messages)Check(messages.Any(x=>x.Contains("服务已接受")) && messages.Any(x=>x.Contains("HTTP 请求成功")) && messages.All(x=>x.Contains("自行确认")),"mobile acceptance is not claimed as delivery");
        }
        var failuresSeen=new List<string>();var rejected=new FakeMobileHandler{Code=System.Net.HttpStatusCode.Forbidden};using(var sender=new MobileNotifications(s=>{lock(failuresSeen)failuresSeen.Add(s);},rejected))
        { sender.Publish(settings,new("需要处理","wait",true));for(int i=0;i<100;i++){lock(failuresSeen)if(failuresSeen.Count>=2)break;await Task.Delay(20);}lock(failuresSeen)Check(failuresSeen.Count>=2 && failuresSeen.All(x=>x.Contains("服务拒绝")) && failuresSeen.All(x=>!x.Contains("Secret")),"mobile failure is isolated and sanitized"); }
        var offline=new List<string>();using(var sender=new MobileNotifications(s=>{lock(offline)offline.Add(s);},new FakeMobileHandler{Throw=true}))
        {sender.Publish(settings,new("需要处理","wait",true));for(int i=0;i<100;i++){lock(offline)if(offline.Count>=2)break;await Task.Delay(20);}lock(offline)Check(offline.Count>=2 && offline.All(x=>!x.Contains("secret")),"mobile offline errors are sanitized");}
        var timed=new List<string>();using(var sender=new MobileNotifications(s=>{lock(timed)timed.Add(s);},new FakeMobileHandler{Timeout=true}))
        {sender.Publish(settings,new("需要处理","wait",true));for(int i=0;i<100;i++){lock(timed)if(timed.Count>=2)break;await Task.Delay(20);}lock(timed)Check(timed.Count>=2 && timed.All(x=>x.Contains("超时")),"mobile timeout reported without blocking");}
        var overflow=new List<string>();var blocked=new FakeMobileHandler{Gate=new()};using(var sender=new MobileNotifications(s=>{lock(overflow)overflow.Add(s);},blocked))
        {for(int i=0;i<40;i++)sender.Publish(settings,new("需要处理","wait",true));lock(overflow)Check(overflow.Any(x=>x.Contains("队列已满")),"mobile queue remains bounded");blocked.Gate.SetResult(true);}
        var gateHandler=new FakeMobileHandler{Gate=new()};var gateResults=new List<string>();using(var sender=new MobileNotifications(s=>{lock(gateResults)gateResults.Add(s);},gateHandler))
        {sender.Publish(settings,new("需要处理","one",true));await Task.Delay(100);sender.Publish(settings,new("需要处理","two",true));sender.InvalidatePending();gateHandler.Gate.SetResult(true);await Task.Delay(300);lock(gateHandler.Bodies)Check(gateHandler.Bodies.Count<=1 && gateHandler.Bodies.All(x=>!x.Contains("two")),"mobile setting change discards pending old notifications");}
        var probe=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);probe.Start();int port=((System.Net.IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
        using(var listener=new System.Net.HttpListener())
        {
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");try { listener.Start(); } catch (System.Net.HttpListenerException ex) { Console.WriteLine($"DETAIL local mock listener: {ex.ErrorCode} {ex.Message}"); throw; } var results=new List<string>();
            using(var sender=new MobileNotifications(s=>{lock(results)results.Add(s);},new LoopbackMobileHandler(port)))
            {
                sender.Publish(settings,new("队列已完成","isolated",false));
                for(int i=0;i<2;i++)
                {
                    System.Net.HttpListenerContext context;try{context=await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));}catch(TimeoutException){lock(results)Console.WriteLine("DETAIL local mock: "+string.Join(" | ",results));throw;}using var reader=new StreamReader(context.Request.InputStream);_ = await reader.ReadToEndAsync();byte[] response=Encoding.UTF8.GetBytes("{\"code\":0}");context.Response.ContentLength64=response.Length;await context.Response.OutputStream.WriteAsync(response);context.Response.Close();
                }
                for(int i=0;i<100;i++){lock(results)if(results.Count>=2)break;await Task.Delay(20);}lock(results)Check(results.Count==2 && results.Any(x=>x.Contains("服务已接受")) && results.Any(x=>x.Contains("HTTP 请求成功")),"mobile channels reach temporary local mock service");
            }
            listener.Stop();
        }
        Check(!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x=>x.Address.Equals(System.Net.IPAddress.Loopback) && x.Port==port),"mobile temporary mock service listener closes after testing");
    }
    static async Task NotificationTests()
    {
        var legacy=NewStore("notice-legacy");
        await File.WriteAllTextAsync(Path.Combine(root,"notice-legacy","data.json"),"{\"Format\":\"SequentialRunnerV3/1\"}");
        legacy.Load(); Check(legacy.LocalNotificationsEnabled,"local notifications default enabled for legacy data");
        legacy.LocalNotificationsEnabled=false; legacy.Save(); var reload=NewStore("notice-legacy"); reload.Load();
        Check(!reload.LocalNotificationsEnabled,"local notification setting persists");
        var notices=new List<LocalNotice>(); bool enabled=true; var center=new LocalNotifications(()=>enabled, notices.Add);
        center.ObserveProblem("scheduler","定时记录保存失败"); center.ObserveProblem("scheduler","定时记录保存失败");
        Check(notices.Count==1 && notices[0].IsWarning,"notification saves failure only once per state");
        center.ObserveProblem("scheduler",""); center.ObserveProblem("scheduler","定时记录保存失败");
        Check(notices.Count==2,"notification reports a new failure after recovery");
        enabled=false;center.Publish(new("关闭","忽略"));Check(notices.Count==2,"disabled notifications are suppressed");
        enabled=true;var throwing=new LocalNotifications(()=>true,_=>throw new InvalidOperationException("unavailable"));
        bool safe=true;try { throwing.Publish(new("完成","测试")); }catch { safe=false; }Check(safe,"notification sink failure never affects caller");
        var st=NewStore("notice-engine");st.Tasks.Add(Spec("first","echo first"));st.Tasks.Add(Spec("second","echo second"));
        var engine=new RunnerEngine(st,()=>{}){Notify=center.Publish};notices.Clear();await engine.Start();
        Check(notices.Count(x=>x.Title=="任务完成")==2 && notices.Count(x=>x.Title=="队列已完成")==1 && st.History.Single().AllSucceeded==true,"notifications cover each successful task and batch once");
        var bad=NewStore("notice-failure");var fail=Spec("failure","exit /b 3");fail.ContinueOnFailure=true;bad.Tasks.Add(fail);bad.Tasks.Add(Spec("next","echo next"));notices.Clear();await new RunnerEngine(bad,()=>{}){Notify=center.Publish}.Start();
        Check(notices.Count(x=>x.Title=="任务需要处理")==1 && notices.All(x=>x.Title!="队列已完成") && bad.History.Single().AllSucceeded==false,"failure notifications never claim batch success");
        var manual=NewStore("notice-manual");var task=Spec("manual","echo done");task.Completion="人工确认";manual.Tasks.Add(task);notices.Clear();var waiting=new RunnerEngine(manual,()=>{}){Notify=center.Publish};var run=waiting.Start();for(int i=0;i<150 && !waiting.Waiting;i++)await Task.Delay(20);bool informed=notices.Count(x=>x.Title=="等待人工处理")==1;waiting.Stop();await run;
        Check(informed && notices.Count(x=>x.Title=="队列已停止")==1 && notices.All(x=>x.Title!="队列已完成"),"manual wait and user stop each notify once without success");
        var noUi=NewStore("notice-throwing");noUi.Tasks.Add(Spec("safe","echo safe"));await new RunnerEngine(noUi,()=>{}){Notify=_=>throw new InvalidOperationException()}.Start();
        Check(noUi.History.Single().AllSucceeded==true,"notification exception cannot break queue");
    }
    static async Task MaaResumeTests()
    {
        await Maa622ClueTests();
        await MaaEvidenceCounterexamples();
        await MaaExternalWriterChain();
        await ShortLivedProcessObservation();
        await MaaInstanceEvidence();
        await MaaChannelBoundaryTests();
        var parser=new MaaLogEvidence();parser.Feed("开始任");parser.Feed("务: A\n任务已全部");parser.Feed("完成！\n");Check(parser.Started && parser.Succeeded && !parser.Failed,"MAA evidence recognizes split start and success");parser.ResetForRestart();Check(!parser.Started && !parser.Succeeded,"MAA restart clears old completion evidence");parser.Feed("开始任务: A\n任务出错: A\n任务已全部完成！\n");Check(parser.Failed,"MAA error remains evidence despite completion text");
        var later=new MaaLogEvidence();later.Feed("开始任务: A\n任务已全部完成！\n开始任务: B\n");Check(later.Started && !later.Succeeded,"MAA later task start invalidates earlier completion");
        var observed=new List<string>();var liveEvidence=new MaaLogEvidence(observed.Add);liveEvidence.Feed("开始任务: A\n任务已全部完成！\n开始任务: B\n任务出错: B\n已停止\n");Check(observed.Count(x=>x.Contains("任务开始"))==2 && observed.Any(x=>x.Contains("完成文字")) && observed.Any(x=>x.Contains("任务错误")) && observed.Any(x=>x.Contains("任务停止")) && !liveEvidence.Succeeded,"MAA live evidence reports transitions and invalidates old completion");
        foreach (string error in new[]{"任务已完成，但出现错误！", "任务已结束，以下任务出现错误: A", "未选择任务"}) { var partial=new MaaLogEvidence();partial.Feed("开始任务: A\n任务已全部完成！\n"+error+"\n");Check(partial.Failed,"MAA post-success failure remains blocking: "+error); }
        var interrupted=new MaaLogEvidence();interrupted.Feed("开始任务: A\n任务已全部完成！\n已停止\n");Check(interrupted.Stopped,"MAA post-success stop remains blocking");
        var truncatedLine=new MaaLogEvidence();truncatedLine.Feed("开始任务: A\n"+new string('x',4097)+"\n任务已全部完成！\n");Check(truncatedLine.Uncertain,"MAA overlong line keeps evidence uncertain after success");
        Check(!new TaskSpec().MaaExclusiveLog && !new TaskSpec().MaaCloseOnCompletion && new TaskSpec().Completion=="进程退出","MAA mode, close request and exclusive confirmation default off");
        var settings=NewStore("maa-settings");settings.Tasks.Add(new TaskSpec{Name="maa",Completion="MAA日志",MaaExclusiveLog=true,MaaCloseOnCompletion=true,LogPath="sample"});settings.Save();var settingsAgain=NewStore("maa-settings");settingsAgain.Load();Check(settingsAgain.Tasks.Single().Copy().MaaExclusiveLog && settingsAgain.Tasks.Single().Copy().MaaCloseOnCompletion && settingsAgain.Tasks.Single().Completion=="MAA日志","MAA configuration round trip and snapshot");
        await MaaControlledStartTests();
        async Task Run(string mode,bool shouldAdvance,bool exclusive=true,bool shared=false,bool trusted=true,bool supported=true)
        {
            string dir=Path.Combine(root,"maa-"+mode+"-"+exclusive+"-"+shared+"-"+trusted+"-"+supported);Directory.CreateDirectory(dir);string file=Path.Combine(dir,"gui.log");await File.WriteAllTextAsync(file,"开始任务: OLD\n任务已全部完成！\n");var st=new Store(dir);var spec=new TaskSpec{Name="maa",Program=Environment.ProcessPath!,Arguments="fixture-maa-"+mode+" \""+file+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="MAA日志",LogPath=file,MaaExclusiveLog=exclusive,SharedLog=shared,TrustCompletion=trusted,ContinueOnFailure=true};st.Tasks.Add(spec);st.Tasks.Add(Spec("after-maa","echo next"));var e=new RunnerEngine(st,()=>{}){MaaVersionSupported=_=>supported};var run=e.Start();for(int i=0;i<450 && !run.IsCompleted && !e.Waiting;i++)await Task.Delay(30);bool advanced=st.History.Single().Events.Any(x=>x.Contains("已启动 after-maa"));bool ok=advanced==shouldAdvance && (shouldAdvance || e.Waiting) && (mode!="update" || st.History.Single().Events.Any(x=>x.Contains("Job 事件核对") && x.Contains("不明：")));if(!ok)Console.WriteLine("DETAIL MAA "+mode+" "+e.Status+" "+string.Join(" | ",st.History.Single().Events));if(!run.IsCompleted)e.Stop();await run;Check(ok,"MAA "+mode+" advance="+shouldAdvance+" exclusive="+exclusive+" shared="+shared+" trust="+trusted+" version="+supported);
        }
        await Run("success",false);await Run("error",false);await Run("stop",false);await Run("old",false);await Run("success",false,exclusive:false);await Run("success",false,shared:true);await Run("success",false,trusted:false);await Run("success",false,supported:false);await Run("replace",false);await Run("truncate",false);await Run("invalid-utf8",false);await Run("exit-nonzero",false);await Run("update",false);await Run("update-old",false);await Run("overlap",false);
        string childDir=Path.Combine(root,"maa-extra-child");Directory.CreateDirectory(childDir);string childLog=Path.Combine(childDir,"gui.log");await File.WriteAllTextAsync(childLog,"old\n");var childStore=new Store(childDir);childStore.Tasks.Add(new TaskSpec{Name="maa",Program=Environment.ProcessPath!,Arguments="fixture-maa-extra-child \""+childLog+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="MAA日志",LogPath=childLog,MaaExclusiveLog=true,TrustCompletion=true});childStore.Tasks.Add(Spec("after-extra-child","echo wrong"));var childEngine=new RunnerEngine(childStore,()=>{}){MaaVersionSupported=_=>true};var childRun=childEngine.Start();for(int i=0;i<400 && !childEngine.Waiting;i++)await Task.Delay(30);var childEvents=childStore.History.Single().Events;bool childSafe=childEngine.Waiting && childEvents.Any(x=>x.Contains("MAA 受管成员观察：") && x.Contains("组归属已核实")) && childEvents.Any(x=>x.Contains("累计进程数 2") && x.Contains("重启候选 无") && x.Contains("候选查询不明 否")) && !childEvents.Any(x=>x.Contains("已启动 after-extra-child"));childEngine.Stop();await childRun;Check(childSafe,"MAA observed child records separate pause reasons without relaxing safety");
        string saveDir=Path.Combine(root,"maa-save-failure");Directory.CreateDirectory(saveDir);string saveLog=Path.Combine(saveDir,"gui.log");await File.WriteAllTextAsync(saveLog,"old\n");bool failSave=false;var failedStore=new Store(saveDir,p=>failSave?new FaultWriteStream(p):new FileStream(p,FileMode.CreateNew,FileAccess.Write,FileShare.None));failedStore.Tasks.Add(new TaskSpec{Name="maa",Program=Environment.ProcessPath!,Arguments="fixture-maa-success \""+saveLog+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="MAA日志",LogPath=saveLog,MaaExclusiveLog=true,TrustCompletion=true});failedStore.Tasks.Add(Spec("after-save-failure","echo wrong"));RunnerEngine? failedEngine=null;failedEngine=new RunnerEngine(failedStore,()=>{if(failedEngine!=null && failedEngine.Status.Contains("已启动 maa"))failSave=true;}){MaaVersionSupported=_=>true};await failedEngine.Start();Check(failSave && !failedStore.History.Single().Events.Any(x=>x.Contains("已启动 after-save-failure")) && failedStore.History.Single().AllSucceeded==false,"MAA evidence save failure blocks next task");
        string stopDir=Path.Combine(root,"maa-user-stop");Directory.CreateDirectory(stopDir);string stopLog=Path.Combine(stopDir,"gui.log");await File.WriteAllTextAsync(stopLog,"old\n");var stopStore=new Store(stopDir);stopStore.Tasks.Add(new TaskSpec{Name="maa",Program=Environment.ProcessPath!,Arguments="fixture-maa-success \""+stopLog+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="MAA日志",LogPath=stopLog,MaaExclusiveLog=true,TrustCompletion=true});stopStore.Tasks.Add(Spec("after-user-stop","echo wrong"));var stopEngine=new RunnerEngine(stopStore,()=>{}){MaaVersionSupported=_=>true};var stopped=stopEngine.Start();for(int i=0;i<100 && !stopStore.History.Single().Events.Any(x=>x.Contains("已启动 maa"));i++)await Task.Delay(20);stopEngine.Stop();await stopped;Check(stopStore.History.Single().State=="已停止" && !stopStore.History.Single().Events.Any(x=>x.Contains("已启动 after-user-stop")),"MAA user stop blocks next task");
        string lockDir=Path.Combine(root,"maa-log-locked");Directory.CreateDirectory(lockDir);string lockLog=Path.Combine(lockDir,"gui.log");await File.WriteAllTextAsync(lockLog,"old\n");using(var held=new FileStream(lockLog,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){var lockStore=new Store(lockDir);lockStore.Tasks.Add(new TaskSpec{Name="maa",Program=Environment.ProcessPath!,Arguments="fixture-maa-success \""+lockLog+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="MAA日志",LogPath=lockLog,MaaExclusiveLog=true,TrustCompletion=true,ContinueOnFailure=true});lockStore.Tasks.Add(Spec("after-locked-log","echo wrong"));var lockEngine=new RunnerEngine(lockStore,()=>{}){MaaVersionSupported=_=>true};var locked=lockEngine.Start();for(int i=0;i<100 && !lockEngine.Waiting;i++)await Task.Delay(20);bool safe=lockEngine.Waiting && !lockStore.History.Single().Events.Any(x=>x.Contains("已启动 after-locked-log"));lockEngine.Stop();await locked;Check(safe,"MAA unreadable log blocks failure continuation");}
        var missingStore=NewStore("maa-program-missing");missingStore.Tasks.Add(new TaskSpec{Name="maa",Program=Path.Combine(root,"missing-maa.exe"),Completion="MAA日志",MaaExclusiveLog=true,TrustCompletion=true,ContinueOnFailure=true});missingStore.Tasks.Add(Spec("after-missing-maa","echo wrong"));var missingEngine=new RunnerEngine(missingStore,()=>{});var missingRun=missingEngine.Start();for(int i=0;i<100 && !missingEngine.Waiting;i++)await Task.Delay(20);bool missingSafe=missingEngine.Waiting && !missingStore.History.Single().Events.Any(x=>x.Contains("已启动 after-missing-maa"));missingEngine.Stop();await missingRun;Check(missingSafe,"missing MAA program blocks failure continuation");
    }
    static async Task Maa622ClueTests()
    {
        async Task Run(int index, string order, bool is622 = true, bool maa = true, bool old = false, string custom = "")
        {
            string dir = Path.Combine(root, $"maa622-{index}-{order}-{is622}-{maa}-{old}-{custom.Length}");
            Directory.CreateDirectory(dir); string log = Path.Combine(dir, "gui.log");
            await File.WriteAllTextAsync(log, old ? UpdateMarkers.Maa622[index] + "\n" : "old\n");
            var store = new Store(dir);
            store.Tasks.Add(new TaskSpec { Name = "maa", Program = Environment.ProcessPath!, Arguments = $"fixture-maa622 \"{log}\" {index} {(old ? "none" : order)}", WorkingDirectory = AppContext.BaseDirectory, Completion = maa ? "MAA日志" : "日志信号", LogSource = "日志文件", LogPath = log, SuccessText = "任务已全部完成！", MaaExclusiveLog = maa, MaaCloseOnCompletion = maa && is622 && !old, TrustCompletion = true, UpdateMarkers = custom });
            store.Tasks.Add(Spec("after-622", "echo next")); int closeCalls = 0;
            var engine = new RunnerEngine(store, () => {}) { MaaVersionSupported = _ => true, MaaVersion622 = _ => is622, RequestOwnedWindowClose = _ => { closeCalls++; return false; } };
            var run = engine.Start();
            for (int i = 0; i < 500 && !engine.Waiting && !run.IsCompleted; i++) await Task.Delay(30);
            bool advance = store.History.Single().Events.Any(x => x.Contains("已启动 after-622"));
            bool marker = store.History.Single().Events.Any(x => x.Contains("检测到更新或重启线索"));
            bool shouldBlock = maa && is622 && !old;
            bool explicitBlock = order == "custom" || store.History.Single().Events.Any(x => x.Contains("MAA 6.22.0 更新线索"));
            bool ok = shouldBlock ? engine.Waiting && marker && explicitBlock && !advance && closeCalls == 0 : maa ? engine.Waiting && !advance && !marker : advance && !marker;
            if (!ok) Console.WriteLine("DETAIL MAA622 " + string.Join(" | ", store.History.Single().Events));
            if (!run.IsCompleted) engine.Stop(); await run.WaitAsync(TimeSpan.FromSeconds(15));
            Check(ok, $"MAA622 clue {index} {order} version622={is622} maa={maa} old={old}");
        }
        for (int i = 0; i < UpdateMarkers.Maa622.Length; i++) await Run(i, "before");
        await Run(0, "after-split");
        await Run(0, "before", is622: false);
        await Run(0, "before", maa: false);
        await Run(0, "before", old: true);
        await Run(0, "before", custom: "MyCustomMarker");
        await Run(0, "custom", custom: "MyCustomMarker");
        string versionDir = Path.Combine(root, "maa622-version-unknown"); Directory.CreateDirectory(versionDir); string versionLog = Path.Combine(versionDir, "gui.log"); await File.WriteAllTextAsync(versionLog, "old\n");
        string noVersionFile = Path.Combine(versionDir, "no-version.exe"); await File.WriteAllTextAsync(noVersionFile, "fixture");
        Check(new RunnerEngine(new Store(versionDir), () => {}).MaaVersion622(noVersionFile) == null, "MAA622 file with no version remains unknown");
        var versionStore = new Store(versionDir); versionStore.Tasks.Add(new TaskSpec { Name = "maa", Program = Environment.ProcessPath!, Arguments = $"fixture-maa622 \"{versionLog}\" 0 none", WorkingDirectory = AppContext.BaseDirectory, Completion = "MAA日志", LogPath = versionLog, MaaExclusiveLog = true, TrustCompletion = true }); versionStore.Tasks.Add(Spec("after-version-unknown", "echo wrong"));
        var versionEngine = new RunnerEngine(versionStore, () => {}) { MaaVersionSupported = _ => true, MaaVersion622 = _ => null }; var versionRun = versionEngine.Start();
        for (int i = 0; i < 100 && !versionEngine.Waiting; i++) await Task.Delay(20);
        bool versionBlocked = versionEngine.Waiting && !versionStore.History.Single().Events.Any(x => x.Contains("已启动 maa")) && !versionStore.History.Single().Events.Any(x => x.Contains("已启动 after-version-unknown"));
        versionEngine.Stop(); await versionRun.WaitAsync(TimeSpan.FromSeconds(10)); Check(versionBlocked, "MAA622 unknown version blocks before launch");
        string dir = Path.Combine(root, "maa622-stop"); Directory.CreateDirectory(dir); string log = Path.Combine(dir, "gui.log"); await File.WriteAllTextAsync(log, "old\n");
        var stoppedStore = new Store(dir); stoppedStore.Tasks.Add(new TaskSpec { Name = "maa", Program = Environment.ProcessPath!, Arguments = $"fixture-maa622 \"{log}\" 0 before", WorkingDirectory = AppContext.BaseDirectory, Completion = "MAA日志", LogPath = log, MaaExclusiveLog = true, TrustCompletion = true }); stoppedStore.Tasks.Add(Spec("after-622-stop", "echo wrong"));
        var stoppedEngine = new RunnerEngine(stoppedStore, () => {}) { MaaVersionSupported = _ => true, MaaVersion622 = _ => true }; var stopped = stoppedEngine.Start();
        for (int i = 0; i < 150 && !stoppedStore.History.Single().Events.Any(x => x.Contains("检测到更新或重启线索")); i++) await Task.Delay(30);
        bool sawMarker = stoppedStore.History.Single().Events.Any(x => x.Contains("检测到更新或重启线索")); stoppedEngine.Stop(); await stopped.WaitAsync(TimeSpan.FromSeconds(15));
        Check(sawMarker && stoppedStore.History.Single().State == "已停止" && !stoppedStore.History.Single().Events.Any(x => x.Contains("已启动 after-622-stop")), "MAA622 clue and stop never advance");
        string failDir = Path.Combine(root, "maa622-save-failure"); Directory.CreateDirectory(failDir); string failLog = Path.Combine(failDir, "gui.log"); await File.WriteAllTextAsync(failLog, "old\n");
        bool failSave = false; RunnerEngine? failedEngine = null;
        var failedStore = new Store(failDir, p => { if (failedEngine?.Status.Contains("检测到更新或重启线索") == true) { failSave = true; return new FaultWriteStream(p); } return new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None); });
        failedStore.Tasks.Add(new TaskSpec { Name = "maa", Program = Environment.ProcessPath!, Arguments = $"fixture-maa622 \"{failLog}\" 0 before", WorkingDirectory = AppContext.BaseDirectory, Completion = "MAA日志", LogPath = failLog, MaaExclusiveLog = true, TrustCompletion = true }); failedStore.Tasks.Add(Spec("after-622-save", "echo wrong"));
        failedEngine = new RunnerEngine(failedStore, () => {}) { MaaVersionSupported = _ => true, MaaVersion622 = _ => true };
        await failedEngine.Start().WaitAsync(TimeSpan.FromSeconds(20));
        Check(failSave && failedStore.History.Single().Events.Any(x => x.Contains("检测到更新或重启线索")) && !failedStore.History.Single().Events.Any(x => x.Contains("已启动 after-622-save")) && failedStore.History.Single().AllSucceeded == false, "MAA622 marker save failure never advances");
    }
    static async Task MaaEvidenceCounterexamples()
    {
        foreach (string mode in new[] { "helper", "silent-change" })
        {
            string dir = Path.Combine(root, "maa-evidence-counterexample-" + mode);
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "gui.log"), asset = Path.Combine(dir, "fixture-resource.txt");
            await File.WriteAllTextAsync(log, "old\n");
            await File.WriteAllTextAsync(asset, "before");
            var store = new Store(dir);
            store.Tasks.Add(new TaskSpec { Name = "maa", Program = Environment.ProcessPath!, Arguments = $"fixture-evidence-parent {mode} \"{log}\" \"{asset}\"", WorkingDirectory = AppContext.BaseDirectory, Completion = "MAA日志", LogPath = log, MaaExclusiveLog = true, TrustCompletion = true });
            store.Tasks.Add(Spec("after-counterexample", "echo wrong"));
            var engine = new RunnerEngine(store, () => {}) { MaaVersionSupported = _ => true };
            var run = engine.Start();
            for (int i = 0; i < 500 && !engine.Waiting && !run.IsCompleted; i++) await Task.Delay(30);
            var events = store.History.Single().Events;
            bool evidence = (await File.ReadAllTextAsync(log)).Contains("开始任务: A\n任务已全部完成！\n");
            bool changed = (await File.ReadAllTextAsync(asset)) == "changed";
            bool safe = engine.Waiting && evidence && changed == (mode == "silent-change") && !events.Any(x => x.Contains("已启动 after-counterexample")) && events.Any(x => x.Contains("累计进程数 2"));
            if (!run.IsCompleted) engine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            Check(safe, "MAA identical business log with " + mode + " child remains blocked");
        }
        string mixedDir = Path.Combine(root, "maa-evidence-external-append");
        Directory.CreateDirectory(mixedDir);
        string mixedLog = Path.Combine(mixedDir, "gui.log");
        await File.WriteAllTextAsync(mixedLog, "old\n");
        using (var reader = new FileStream(mixedLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            reader.Seek(0, SeekOrigin.End);
            var anchor = new MaaLogAnchor(reader); anchor.Capture();
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("fixture-evidence-append"); start.ArgumentList.Add(mixedLog);
            using var writer = Process.Start(start)!;
            await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var text = new StreamReader(reader, new UTF8Encoding(false, true), false, 1024, true);
            var evidence = new MaaLogEvidence(); evidence.Feed(await text.ReadToEndAsync()); evidence.Finish();
            Check(writer.ExitCode == 0 && anchor.IsUnchanged() && evidence.Started && evidence.Succeeded, "MAA log anchor cannot authenticate a separate writer");
        }
        string restored = Path.Combine(mixedDir, "restored.txt");
        await File.WriteAllTextAsync(restored, "before");
        DateTime originalTime = File.GetLastWriteTimeUtc(restored);
        string originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(restored)));
        await File.WriteAllTextAsync(restored, "changed");
        await File.WriteAllTextAsync(restored, "before");
        File.SetLastWriteTimeUtc(restored, originalTime);
        string restoredHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(restored)));
        Check(originalHash == restoredHash && File.GetLastWriteTimeUtc(restored) == originalTime, "unchanged sampled fingerprint cannot prove no intermediate modification");
    }
    static async Task<bool> WaitForLog(string path, string text, int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                if ((await reader.ReadToEndAsync()).Contains(text)) return true;
            }
            catch (IOException) { }
            await Task.Delay(30);
        }
        return false;
    }
    // The runner keeps the log open for reading, so fixtures append through an explicitly shared handle like the MAA fixtures do.
    static void AppendLog(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        writer.Write(text);
    }
    // Work 1: one managed root that writes nothing, plus a separate process outside the job that appends the MAA text.
    // This probe records what the product actually does; it deliberately carries no pass/fail verdict about the outcome.
    static async Task MaaExternalWriterChain()
    {
        Directory.CreateDirectory(root);
        string evidence = Path.Combine(root, "evidence-maa-external-writer.txt");
        var lines = new List<string>();
        void Record(string text) { lines.Add(text); Console.WriteLine("EVIDENCE " + text); }
        foreach (bool selfWrite in new[] { false, true })
        {
            string mode = selfWrite ? "root-writes-log" : "outside-writer";
            string dir = Path.Combine(root, "maa-external-chain-" + mode);
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "gui.log");
            await File.WriteAllTextAsync(log, "old\n");
            string prefix = "dsh-maa-chain-" + Guid.NewGuid().ToString("N");
            string noteFile = Path.Combine(dir, "outside.txt");
            string afterName = "after-" + mode;
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-ready");
            using var written = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-written");
            using var release = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-release");
            var store = new Store(dir);
            store.Tasks.Add(new TaskSpec { Name = "maa", Program = Environment.ProcessPath!, Arguments = $"fixture-maa-chain-root \"{log}\" {prefix} {(selfWrite ? 1 : 0)}", WorkingDirectory = AppContext.BaseDirectory, Completion = "MAA日志", LogPath = log, MaaExclusiveLog = true, MaaCloseOnCompletion = true, TrustCompletion = true, ContinueOnFailure = true });
            store.Tasks.Add(Spec(afterName, "echo next"));
            int closeCalls = 0;
            var engine = new RunnerEngine(store, () => { }) { MaaVersionSupported = _ => true, RequestOwnedWindowClose = _ => { closeCalls++; return true; } };
            var run = engine.Start();
            bool rootReady = ready.WaitOne(TimeSpan.FromSeconds(20));
            Check(rootReady, "external chain root signals ready: " + mode);
            Process? outsider = null;
            string note = "";
            bool outsiderExited = false;
            try
            {
                if (!selfWrite)
                {
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                    start.ArgumentList.Add("fixture-maa-chain-outsider"); start.ArgumentList.Add(log); start.ArgumentList.Add(prefix); start.ArgumentList.Add(noteFile);
                    outsider = Process.Start(start)!;
                    outsiderExited = outsider.WaitForExit(20000);
                    note = File.Exists(noteFile) ? (await File.ReadAllTextAsync(noteFile)).Trim() : "missing";
                }
                bool textSeen = selfWrite ? await WaitForLog(log, "任务已全部完成！", 20000) : written.WaitOne(TimeSpan.FromSeconds(20));
                await Task.Delay(3000);
                Check(textSeen && closeCalls == 0 && !run.IsCompleted, "MAA unauthenticated live log never requests close: " + mode);
                release.Set();
                for (int i = 0; i < 700 && !run.IsCompleted && !engine.Waiting; i++) await Task.Delay(30);
                var record = store.History[0];
                var events = record.Events;
                bool advanced = events.Any(x => x.Contains("已启动 " + afterName));
                bool success = events.Any(x => x.Contains("MAA 日志观察到任务开始与全部完成"));
                string logText = await File.ReadAllTextAsync(log);
                bool onlyDeclaredWriter = selfWrite ? textSeen : logText == "old\n开始任务: A\n任务已全部完成！\n";
                bool outsiderOutside = outsider == null || !events.Any(x => x.Contains("Job 进程加入通知：PID " + outsider.Id));
                string firstBlock = events.FirstOrDefault(x => x.Contains("结果不明") || x.Contains("证据冲突") || x.Contains("暂停依据")) ?? "无";
                if (!selfWrite && outsider != null)
                {
                    Check(outsiderExited && outsider.ExitCode == 0 && note.Contains("WRITTEN=1") && note.Contains("PID=" + outsider.Id), "outside fixture appends the log and exits normally");
                    if (!note.Contains("INJOB=0")) Console.WriteLine("NOTE outside fixture inherits a host job; group separation is still proven by the absence of a job join notification");
                }
                Check(onlyDeclaredWriter, "only the declared writer changed the log: " + mode);
                Check(outsiderOutside, "outside fixture never appears as a managed job member: " + mode);
                Check(engine.Waiting && !advanced && success && !events.Any(x => x.Contains("maa 成功")) && closeCalls == 0 && events.Any(x => x.Contains("MAA 日志来源无法确认")), "MAA unauthenticated log blocks success and next: " + mode);
                if (selfWrite) { engine.Continue(); await run.WaitAsync(TimeSpan.FromSeconds(20)); Check(events.Any(x => x.Contains("该项未判定成功")) && events.Any(x => x.Contains("已启动 " + afterName)) && record.AllSucceeded == false, "MAA manual skip continues without claiming success"); }
                Record($"mode={mode} advanced={advanced} state={record.State} waiting={engine.Waiting} successLine={success} closeCalls={closeCalls} closeConfigured=true groupPause={events.Any(x => x.Contains("累计进程数"))} block={firstBlock} records={store.History.Count} logBytes={logText.Length} outside={note}");
                foreach (var e in events) Record($"mode={mode} event={e}");
            }
            finally
            {
                release.Set();
                if (!run.IsCompleted) engine.Stop();
                await run.WaitAsync(TimeSpan.FromSeconds(20));
                if (outsider != null) { if (!outsider.HasExited) outsider.Kill(); outsider.Dispose(); }
            }
        }
        string aloneDir = Path.Combine(root, "maa-external-chain-root-exit");
        Directory.CreateDirectory(aloneDir);
        string aloneLog = Path.Combine(aloneDir, "gui.log");
        await File.WriteAllTextAsync(aloneLog, "old\n");
        string alonePrefix = "dsh-maa-chain-" + Guid.NewGuid().ToString("N");
        using (var aloneReady = new EventWaitHandle(false, EventResetMode.ManualReset, alonePrefix + "-ready"))
        using (var aloneRelease = new EventWaitHandle(false, EventResetMode.ManualReset, alonePrefix + "-release"))
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("fixture-maa-chain-root"); start.ArgumentList.Add(aloneLog); start.ArgumentList.Add(alonePrefix); start.ArgumentList.Add("0");
            using var alone = Process.Start(start)!;
            bool started = aloneReady.WaitOne(TimeSpan.FromSeconds(20));
            aloneRelease.Set();
            bool ended = alone.WaitForExit(20000);
            Check(started && ended && alone.ExitCode == 0 && (await File.ReadAllTextAsync(aloneLog)) == "old\n", "chain root fixture exits 0 without writing the log");
        }
        await File.WriteAllLinesAsync(evidence, lines);
        Console.WriteLine("EVIDENCE file: " + evidence);
    }
    static uint ParsePid(string line)
    {
        int index = line.IndexOf("PID ", StringComparison.Ordinal);
        if (index < 0) return 0;
        var digits = new string(line[(index + 4)..].TakeWhile(char.IsDigit).ToArray());
        return uint.TryParse(digits, out var value) ? value : 0;
    }
    // What a caller can still learn about one short-lived process at notification time.
    static (bool handle, bool path, bool member, string reason) SampleIdentity(OwnedProcess owner, uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.HasExited) return (false, false, false, "exited-before-sample");
            DateTime created;
            try { created = process.StartTime; } catch { return (false, false, false, "start-time-unavailable"); }
            string? path = null;
            string reason = "";
            try { path = process.MainModule?.FileName; } catch { reason = "path-unavailable"; }
            bool member = false;
            try { member = owner.ContainsProcess(process.SafeHandle); } catch { reason = reason.Length == 0 ? "membership-unavailable" : reason + "+membership-unavailable"; }
            return (created != default, path != null, member, reason);
        }
        catch { return (false, false, false, "process-open-failed"); }
    }
    // Work 2: event counts, verified identities and verified purpose are three different things.
    static async Task ShortLivedProcessObservation()
    {
        var evidence = new List<string>();
        // The last case mirrors the product loop (200 ms poll): the same fast-exiting children are then observed only after they are gone.
        foreach (var (mode, delay, pollMs) in new[] { ("normal-wait", 120, 20), ("quick-exit", 0, 20), ("quick-exit-production-poll", 0, 200) })
        {
            string dir = Path.Combine(root, "short-lived-" + mode);
            Directory.CreateDirectory(dir);
            var spec = new TaskSpec { Name = "parent", Program = Environment.ProcessPath!, Arguments = $"fixture-short-parent 6 {delay}", WorkingDirectory = AppContext.BaseDirectory, Completion = "MAA日志", TrustCompletion = true };
            using var owned = OwnedProcess.Start(spec);
            var observer = owned.JobEvents;
            var joins = new List<string>();
            var exits = new List<string>();
            var samples = new Dictionary<uint, (bool handle, bool path, bool member, string reason)>();
            void Collect(string line, OwnedProcess owner, List<string> join, List<string> exit, Dictionary<uint, (bool handle, bool path, bool member, string reason)> sampled)
            {
                if (line.Contains("加入通知"))
                {
                    join.Add(line);
                    uint pid = ParsePid(line);
                    if (pid != 0 && !sampled.ContainsKey(pid)) sampled[pid] = SampleIdentity(owner, pid);
                }
                else if (line.Contains("退出通知")) exit.Add(line);
            }
            uint total = 0;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                observer?.Poll(owned, line => Collect(line, owned, joins, exits, samples));
                var accounting = owned.Accounting();
                total = Math.Max(total, accounting.Total);
                if (owned.Root.HasExited && accounting.Active == 0) break;
                await Task.Delay(pollMs);
            }
            observer?.Poll(owned, line => Collect(line, owned, joins, exits, samples));
            total = Math.Max(total, owned.Accounting().Total);
            string summary = observer == null ? "no observer" : observer.Summary(total);
            int handle = samples.Values.Count(x => x.handle), path = samples.Values.Count(x => x.path), member = samples.Values.Count(x => x.member);
            int unverified = joins.Count(x => x.Contains("身份未确认"));
            string reasons = string.Join(";", samples.Values.Where(x => x.reason.Length > 0).GroupBy(x => x.reason).Select(g => g.Key + "×" + g.Count()));
            string lineText = $"short-lived mode={mode} pollMs={pollMs} expected=7 total={total} joinNotices={observer?.NewCount} exitNotices={observer?.ExitCount} joinLines={joins.Count} exitLines={exits.Count} pids={samples.Count} handleAndStartTime={handle} path={path} groupMember={member} unverified={unverified} sampleReasons=[{reasons}] purposeKnown=0 backlog={observer?.Backlog} summary={summary}";
            Console.WriteLine("EVIDENCE " + lineText);
            evidence.Add(lineText);
            Check(observer != null && total == 7, "short-lived " + mode + ": job accounting sees the parent and 6 children (total=" + total + ")");
            Check(samples.Count == 0 || handle > 0 || unverified > 0, "short-lived " + mode + ": every join notice is either resolved or reported unverified");
        }
        using var probe = OwnedProcess.Start(new TaskSpec { Name = "probe", Program = Environment.ProcessPath!, Arguments = "fixture-short-child 40", WorkingDirectory = AppContext.BaseDirectory, Completion = "MAA日志", TrustCompletion = true });
        var probeEvents = probe.JobEvents;
        if (probeEvents == null) Check(false, "short-lived: job event channel unavailable for the injection cases");
        else
        {
            var injected = new List<string>();
            probeEvents.Record(6, 999999u, _ => null, injected.Add);
            probeEvents.Record(6, 999999u, _ => null, injected.Add);
            probeEvents.Record(7, 888888u, _ => null, injected.Add);
            string mismatch = probeEvents.Summary((uint)(probeEvents.NewCount + 2));
            string injectedReasons = probeEvents.Reason;
            Console.WriteLine("EVIDENCE short-lived injected=" + string.Join(" | ", injected) + " | reasons=" + injectedReasons + " | summary=" + mismatch);
            evidence.Add("injected=" + string.Join(" | ", injected) + " | reasons=" + injectedReasons + " | summary=" + mismatch);
            Check(injected[0].Contains("身份未确认") && injected.Any(x => x.Contains("退出通知：PID 888888")) && injectedReasons.Contains("句柄或归属无法核实") && injectedReasons.Contains("重复或 PID 复用") && injectedReasons.Contains("退出事件缺少加入事件"), "short-lived: injected query failure, duplicate pid and exit without join stay reported");
            Check(mismatch.Contains("数量与累计进程数不一致"), "short-lived: injected missing notifications are reported as a count mismatch");
        }
        await File.WriteAllLinesAsync(Path.Combine(root, "evidence-short-lived.txt"), evidence);
    }
    // Work 3: the same completion text, a restart child, a rotated/truncated log and a missing
    // job notification must all end in "unknown".  The callback sample below is entirely
    // fictional: timestamps, PIDs, thread ids, task ids and the uuid are invented for this
    // fixture and only mirror the documented append_callback line shape.  It keeps the four
    // relations under test: an error can be followed by a completion callback, a completion
    // can be followed by a stop, one identifier can span several PIDs and runs, and neither
    // PID nor task id alone can authenticate the current instance.  No real MAA log is read
    // or fed into the product.
    static async Task MaaInstanceEvidence()
    {
        var evidence = new List<string>();
        void Record(string line) { evidence.Add(line); Console.WriteLine("EVIDENCE " + line); }

        string[] callbacks =
        {
            "[2031-01-01 00:00:01.000][INF][Px41001][Tx7001] Assistant::append_callback | TaskChainStart {\"taskchain\":\"Fixture-Alpha\",\"taskid\":7,\"uuid\":\"synthetic-fixture-0001\"}",
            "[2031-01-01 00:00:01.500][INF][Px41001][Tx7001] Assistant::append_callback | SubTaskError {\"class\":\"asst::FixtureTask\",\"details\":{},\"first\":[\"SyntheticStep\"],\"pre_task\":\"\",\"subtask\":\"FixtureTask\",\"taskchain\":\"Fixture-Alpha\",\"taskid\":7,\"uuid\":\"synthetic-fixture-0001\"}",
            "[2031-01-01 00:00:05.000][INF][Px41001][Tx7001] Assistant::append_callback | TaskChainCompleted {\"taskchain\":\"Fixture-Alpha\",\"taskid\":7,\"uuid\":\"synthetic-fixture-0001\"}",
            "[2031-01-01 00:00:05.250][INF][Px41001][Tx7001] Assistant::append_callback | AllTasksCompleted {\"finished_tasks\":[3,7],\"taskchain\":\"Fixture-Alpha\",\"taskid\":7,\"uuid\":\"synthetic-fixture-0001\"}",
            "[2031-01-01 00:00:09.000][INF][Px41002][Tx7002] Assistant::append_callback | AllTasksCompleted {\"finished_tasks\":[3,4,5],\"taskchain\":\"Fixture-Beta\",\"taskid\":11,\"uuid\":\"synthetic-fixture-0001\"}",
            "[2031-01-01 00:00:09.250][INF][Px41002][Tx7002] Assistant::append_callback | TaskChainStopped {\"taskchain\":\"Fixture-Beta\",\"taskid\":11,\"uuid\":\"synthetic-fixture-0001\"}"
        };

        static (string Event, int Pid, int TaskId, string Chain, string Uuid, int[] Finished, string[] First)? ParseCallback(string line)
        {
            var match = Regex.Match(line, @"^\[[^\]]+\]\[[A-Z]+\]\[Px(?<pid>\d+)\]\[Tx\d+\]\s+Assistant::append_callback\s+\|\s+(?<event>\w+)(?:\s+(?<json>\{.*\}))?$");
            if (!match.Success) return null;
            int pid = int.Parse(match.Groups["pid"].Value);
            int taskId = 0;
            string chain = "", uuid = "";
            int[] finished = Array.Empty<int>();
            string[] first = Array.Empty<string>();
            if (match.Groups["json"].Success)
            {
                using var doc = JsonDocument.Parse(match.Groups["json"].Value);
                var root = doc.RootElement;
                if (root.TryGetProperty("taskid", out var taskIdElement) && taskIdElement.ValueKind == JsonValueKind.Number) taskId = taskIdElement.GetInt32();
                if (root.TryGetProperty("taskchain", out var chainElement)) chain = chainElement.GetString() ?? "";
                if (root.TryGetProperty("uuid", out var uuidElement)) uuid = uuidElement.GetString() ?? "";
                int[] ReadInts(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>();
                string[] ReadStrings(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();
                finished = ReadInts("finished_tasks");
                first = ReadStrings("first");
            }
            return (match.Groups["event"].Value, pid, taskId, chain, uuid, finished, first);
        }

        var parsed = callbacks.Select(ParseCallback).ToArray();
        Check(parsed.All(x => x.HasValue), "MAA callback sample parses timestamp, PID, event name and JSON");
        if (parsed.All(x => x.HasValue))
        {
            var events = parsed.Select(x => x!.Value).ToArray();
            int errorAt = Array.FindIndex(events, x => x.Event == "SubTaskError");
            int completedAfterError = Array.FindIndex(events, errorAt + 1, x => x.Event == "AllTasksCompleted");
            Check(errorAt >= 0 && completedAfterError > errorAt && events[completedAfterError].Finished.Contains(7) && events[errorAt].First.Contains("SyntheticStep"), "MAA callback sample: SubTaskError can precede AllTasksCompleted and the failed task remains listed (fictional data)");
            int allAt = Array.FindLastIndex(events, x => x.Event == "AllTasksCompleted");
            int stoppedAfterAll = Array.FindIndex(events, allAt + 1, x => x.Event == "TaskChainStopped");
            Check(allAt >= 0 && stoppedAfterAll > allAt && events[allAt].TaskId == events[stoppedAfterAll].TaskId, "MAA callback sample: TaskChainStopped can follow AllTasksCompleted for the same task");
            Check(events.Select(x => x.Pid).Distinct().Count() > 1 && events.Select(x => x.Uuid).Distinct().Count() == 1, "MAA callback sample: one fixed uuid spans multiple PIDs and runs");
        }

        var structured = new MaaLogEvidence();
        structured.Feed(string.Join("\n", callbacks) + "\n");
        structured.Finish();
        Check(!structured.Started && !structured.Succeeded && !structured.Failed && !structured.Stopped, "current Chinese-only MAA evidence ignores structured callbacks");
        var unbound = new MaaLogEvidence();
        unbound.Feed("[Px100] 开始任务: A\n[Px999] 任务已全部完成！\n");
        unbound.Finish();
        Check(unbound.Started && unbound.Succeeded, "current MAA evidence accepts completion text without binding it to a PID");

        foreach (string mode in new[] { "success", "short-child", "update", "error", "stop", "roll", "replace", "truncate", "job-missing" })
        {
            string dir = Path.Combine(root, "maa-instance-" + mode);
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "asst.log");
            await File.WriteAllTextAsync(log, "old\n");
            var store = new Store(dir);
            store.Tasks.Add(new TaskSpec { Name = "maa", Program = Environment.ProcessPath!, Arguments = "fixture-maa-" + mode + " \"" + log + "\"", WorkingDirectory = AppContext.BaseDirectory, Completion = "MAA日志", LogPath = log, MaaExclusiveLog = true, MaaCloseOnCompletion = true, TrustCompletion = true, ContinueOnFailure = true });
            store.Tasks.Add(Spec("after-instance-" + mode, "echo wrong"));
            int closeCalls = 0;
            var engine = new RunnerEngine(store, () => { }) { MaaVersionSupported = _ => true, RequestOwnedWindowClose = _ => { closeCalls++; return true; } };
            if (mode == "job-missing") engine.JobEventsTestHook = observer => observer.MarkUncertain("测试：通知缺失");
            var run = engine.Start();
            for (int i = 0; i < 600 && !run.IsCompleted && !engine.Waiting; i++) await Task.Delay(30);
            bool waiting = engine.Waiting;
            if (!run.IsCompleted) engine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
            var record = store.History.Single();
            var events = record.Events;
            bool advanced = events.Any(x => x.Contains("已启动 after-instance-" + mode));
            bool sourceBlocked = events.Any(x => x.Contains("结果不明") || x.Contains("证据冲突"));
            bool paused = waiting && !advanced && closeCalls == 0 && record.AllSucceeded == false;
            string block = events.LastOrDefault(x => x.Contains("结果不明") || x.Contains("证据冲突") || x.Contains("暂停依据")) ?? "无";
            Record($"mode={mode} paused={paused} waiting={waiting} state={record.State} advanced={advanced} closeCalls={closeCalls} allSucceeded={record.AllSucceeded} block={block}");
            Check(paused && sourceBlocked, "MAA instance evidence " + mode + " stays paused without claiming success");
        }
        await File.WriteAllLinesAsync(Path.Combine(root, "evidence-maa-instance.txt"), evidence);
    }
    // Candidate channel evidence. Every callback line below is synthetic and entirely fictional
    // (2031 timestamps, Px41xxx, uuid synthetic-fixture-0001); no real MAA log is read, copied or quoted.
    static string SyntheticStart(int pid, int tx) => "[2031-01-01 00:00:01.000][INF][Px" + pid + "][Tx" + tx + "] Assistant::append_callback | TaskChainStart {\"taskchain\":\"Fixture-Alpha\",\"taskid\":7,\"uuid\":\"synthetic-fixture-0001\"}";
    static string SyntheticDone(int pid, int tx) => "[2031-01-01 00:00:09.250][INF][Px" + pid + "][Tx" + tx + "] Assistant::append_callback | AllTasksCompleted {\"finished_tasks\":[3,7],\"taskchain\":\"Fixture-Alpha\",\"taskid\":7,\"uuid\":\"synthetic-fixture-0001\"}";
    // The matcher a "all evidence lines carry the root PID" rule would use. It reads text only.
    static (bool Started, bool Completed, bool AllFromPid) MatchPidBound(string text, int pid)
    {
        bool started = false, completed = false, allFromPid = true;
        foreach (string line in text.Split('\n'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"\[Px(?<pid>\d+)\]\[[^\]]*\].*?\|\s*(?<ev>TaskChainStart|AllTasksCompleted)\b");
            if (!match.Success) continue;
            if (int.Parse(match.Groups["pid"].Value) != pid) allFromPid = false;
            if (match.Groups["ev"].Value == "TaskChainStart") started = true; else completed = true;
        }
        return (started, completed, allFromPid);
    }
    static Process StartQuiet(string arguments) => Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
    static bool SameStartTime(int pid, Process expected)
    {
        try { using var other = Process.GetProcessById(pid); return other.StartTime == expected.StartTime; }
        catch (Exception) { return false; }
    }
    // Four channel hypotheses, each run against fixtures this probe creates and holds itself:
    // 1) a PID-prefixed line from an outside writer, 2) an owned stdout pipe that only the root can write,
    // 3) the same pipe when the target writes completion to a file instead (MAA behaviour),
    // 4) a named-pipe receipt whose client identity can be checked against the owned handle.
    static async Task MaaChannelBoundaryTests()
    {
        Directory.CreateDirectory(root);
        string evidence = Path.Combine(root, "evidence-maa-channel-boundary.txt");
        var lines = new List<string>();
        void Record(string text) { lines.Add(text); Console.WriteLine("EVIDENCE " + text); }

        try
        {
            string dir1 = Path.Combine(root, "channel-pid-forged");
            Directory.CreateDirectory(dir1);
            string log1 = Path.Combine(dir1, "asst.log");
            await File.WriteAllTextAsync(log1, "");
            string prefix1 = "dsh-channel-" + Guid.NewGuid().ToString("N");
            using var ready1 = new EventWaitHandle(false, EventResetMode.ManualReset, prefix1 + "-ready");
            using var release1 = new EventWaitHandle(false, EventResetMode.ManualReset, prefix1 + "-release");
            using var root1 = StartQuiet("fixture-channel-root idle \"" + prefix1 + "\"");
            bool rootReady = ready1.WaitOne(TimeSpan.FromSeconds(20));
            using var outsider1 = StartQuiet("fixture-channel-outsider pid \"" + log1 + "\" " + root1.Id + " \"" + prefix1 + "\"");
            bool outsiderExited = outsider1.WaitForExit(20_000);
            bool rootAlive = !root1.HasExited;
            release1.Set();
            root1.WaitForExit(20_000);
            string forged = await File.ReadAllTextAsync(log1);
            var boundForged = MatchPidBound(forged, root1.Id);
            bool acceptedForged = boundForged.Started && boundForged.Completed && boundForged.AllFromPid;
            Record($"pid-forged ready={rootReady} outsiderExited={outsiderExited} rootAliveDuringWrite={rootAlive} started={boundForged.Started} completed={boundForged.Completed} allFromPid={boundForged.AllFromPid} pidBoundAccepted={acceptedForged}");
            Check(acceptedForged, "channel: a PID-bound matcher accepts completion lines written by a process outside the job");
        }
        catch (Exception ex) { failures++; Console.WriteLine("channel pid-forged error: " + ex); }

        try
        {
            string dir2 = Path.Combine(root, "channel-stdout-owned");
            Directory.CreateDirectory(dir2);
            string log2 = Path.Combine(dir2, "asst.log");
            await File.WriteAllTextAsync(log2, "old\n");
            using var root2 = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "fixture-channel-root stdout") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            string captured = await root2.StandardOutput.ReadToEndAsync();
            root2.WaitForExit(20_000);
            var boundOwned = MatchPidBound(captured, root2.Id);
            AppendLog(log2, SyntheticStart(root2.Id, 7001) + "\n" + SyntheticDone(root2.Id, 7001) + "\nOUTSIDE-WRITER\n");
            string logText = await File.ReadAllTextAsync(log2);
            bool pipeClean = !captured.Contains("OUTSIDE-WRITER") && !captured.Contains("old");
            Record($"stdout-owned exit={(root2.HasExited ? root2.ExitCode : -1)} started={boundOwned.Started} completed={boundOwned.Completed} logHasOutsideMarker={logText.Contains("OUTSIDE-WRITER")} pipeCarriedOutsideMarker={captured.Contains("OUTSIDE-WRITER")}");
            Check(boundOwned.Started && boundOwned.Completed && pipeClean, "channel: an owned stdout pipe carries only what the root process itself writes");
        }
        catch (Exception ex) { failures++; Console.WriteLine("channel stdout-owned error: " + ex); }

        try
        {
            string dir3 = Path.Combine(root, "channel-stdout-file-sided");
            Directory.CreateDirectory(dir3);
            string log3 = Path.Combine(dir3, "asst.log");
            await File.WriteAllTextAsync(log3, "");
            using var root3 = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "fixture-channel-root log \"" + log3 + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            string captured = await root3.StandardOutput.ReadToEndAsync();
            root3.WaitForExit(20_000);
            var boundFile = MatchPidBound(captured, root3.Id);
            string logText = await File.ReadAllTextAsync(log3);
            Record($"stdout-file-sided exit={(root3.HasExited ? root3.ExitCode : -1)} pipeStarted={boundFile.Started} pipeCompleted={boundFile.Completed} fileHasCompletion={logText.Contains("AllTasksCompleted")}");
            Check(!boundFile.Completed && logText.Contains("AllTasksCompleted"), "channel: a stdout pipe sees nothing when the target writes completion to a file, as MAA does");
        }
        catch (Exception ex) { failures++; Console.WriteLine("channel stdout-file-sided error: " + ex); }

        try
        {
            string prefix4 = "dsh-channel-" + Guid.NewGuid().ToString("N");
            using var release4 = new EventWaitHandle(false, EventResetMode.ManualReset, prefix4 + "-release");
            string pipeName = "dsh-receipt-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var root4 = StartQuiet("fixture-channel-root pipe \"" + pipeName + "\" \"" + prefix4 + "\"");
            var accept = server.WaitForConnectionAsync();
            bool connected = await Task.WhenAny(accept, Task.Delay(15_000)) == accept;
            uint clientPid = 0;
            bool identityOk = false;
            string receipt = "";
            if (connected)
            {
                receipt = (await new StreamReader(server).ReadToEndAsync()).Trim();
                GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out clientPid);
                identityOk = clientPid == (uint)root4.Id && SameStartTime((int)clientPid, root4);
            }
            string pipeName2 = "dsh-receipt-" + Guid.NewGuid().ToString("N");
            using var server2 = new NamedPipeServerStream(pipeName2, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            using var outsider4 = StartQuiet("fixture-channel-outsider pipe \"" + pipeName2 + "\"");
            var accept2 = server2.WaitForConnectionAsync();
            bool connected2 = await Task.WhenAny(accept2, Task.Delay(15_000)) == accept2;
            uint outsiderCallsign = 0;
            bool outsiderClaimedRoot = true;
            if (connected2)
            {
                await new StreamReader(server2).ReadToEndAsync();
                GetNamedPipeClientProcessId(server2.SafePipeHandle.DangerousGetHandle(), out outsiderCallsign);
                outsiderClaimedRoot = outsiderCallsign == (uint)root4.Id;
            }
            release4.Set();
            root4.WaitForExit(20_000);
            Record($"receipt-pipe connected={connected} clientPidIsRoot={identityOk} receiptText={receipt} outsiderConnected={connected2} outsiderPidIsRoot={outsiderClaimedRoot}");
            Check(connected && identityOk, "channel: a named-pipe client can be bound to the owned root PID and creation time");
            Check(connected2 && !outsiderClaimedRoot, "channel: a named-pipe client from another process fails the same identity check");
        }
        catch (Exception ex) { failures++; Console.WriteLine("channel receipt-pipe error: " + ex); }

        await File.WriteAllLinesAsync(evidence, lines);
    }
    static async Task MaaControlledStartTests()
    {
        Check(!new TaskSpec().MaaStartByRunner, "MAA controlled start defaults off");
        string input="--config \"a b\" --other=1";string withFlag=MaaStartController.WithSkipArgument(input);Check(withFlag==MaaStartController.SkipArgument+" "+input && MaaStartController.WithSkipArgument(withFlag)==withFlag && MaaStartController.WithSkipArgument("--SKIP-STARTUP-AUTO-RUN")==MaaStartController.SkipArgument+" --SKIP-STARTUP-AUTO-RUN","MAA skip argument preserves user arguments and does not duplicate");
        var saved=NewStore("maa-start-config");saved.Tasks.Add(new TaskSpec{Name="maa",MaaStartByRunner=true});saved.Save();var reloaded=NewStore("maa-start-config");reloaded.Load();Check(reloaded.Tasks.Single().Copy().MaaStartByRunner,"MAA controlled start persists in task snapshot");
        async Task RunCase(string mode, bool expectedClick, bool already=false)
        {
            string dir=Path.Combine(root,"maa-start-"+mode);Directory.CreateDirectory(dir);string log=Path.Combine(dir,"gui.log"), counter=Path.Combine(dir,"clicked.txt");await File.WriteAllTextAsync(log,"old\n");var st=new Store(dir);st.Tasks.Add(new TaskSpec{Name="maa",Program=Environment.ProcessPath!,Arguments="fixture-maa-start "+mode+" \""+log+"\" \""+counter+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="MAA日志",LogPath=log,MaaExclusiveLog=true,MaaStartByRunner=true,MaaCloseOnCompletion=true,TrustCompletion=true});var e=new RunnerEngine(st,()=>{}){MaaVersionSupported=_=>true,MaaStartTimeout=TimeSpan.FromSeconds(mode=="single" ? 10 : 3)};var running=e.Start();for(int i=0;i<400 && !running.IsCompleted && !e.Waiting;i++)await Task.Delay(30);var events=st.History.Single().Events;int clickCount=File.Exists(counter)?File.ReadAllLines(counter).Length:0;bool correct=clickCount==(expectedClick?1:0) && (expectedClick?events.Any(x=>x.Contains("已观察到本轮 MAA 任务开始")):already?events.Any(x=>x.Contains("任务自行开始")):e.Waiting && events.Any(x=>x.Contains("启动状态不明") || x.Contains("等待超时")));if(!correct)Console.WriteLine("DETAIL start "+mode+" "+e.Status+" "+string.Join(" | ",events));if(!running.IsCompleted)e.Stop();await running;Check(correct,"MAA controlled start "+mode);
        }
        await RunCase("single",true);await RunCase("duplicate",false);await RunCase("disabled",false);await RunCase("missing",false);await RunCase("already",false,true);
        async Task GuardCase(string mode)
        {
            string dir=Path.Combine(root,"maa-start-guard-"+mode);Directory.CreateDirectory(dir);string log=Path.Combine(dir,"gui.log"),counter=Path.Combine(dir,"clicked.txt");await File.WriteAllTextAsync(log,"old\n");int fail=0,calls=0;var st=new Store(dir,p=>Volatile.Read(ref fail)==1?new FaultWriteStream(p):new FileStream(p,FileMode.CreateNew,FileAccess.Write,FileShare.None));st.Tasks.Add(new TaskSpec{Name="maa",Program=Environment.ProcessPath!,Arguments="fixture-maa-start "+(mode is "update" or "error" or "stopped"?mode:"missing")+" \""+log+"\" \""+counter+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="MAA日志",LogPath=log,MaaExclusiveLog=true,MaaStartByRunner=true,TrustCompletion=true});st.Tasks.Add(Spec("guard-next","echo wrong"));var e=new RunnerEngine(st,()=>{}){MaaVersionSupported=_=>true,MaaStartTimeout=TimeSpan.FromSeconds(3),MaaInspectStart=_=>{Thread.Sleep(mode=="stop-invoke"?0:mode=="timeout"?4500:1200);if(mode=="save")Volatile.Write(ref fail,1);return new MaaStartController.Probe(mode=="identity"?"身份不明":"就绪");},MaaInvokeStart=(_,_,may)=>{if(mode=="stop-invoke")Thread.Sleep(1200);if(may())Interlocked.Increment(ref calls);return true;}};var running=e.Start();for(int i=0;i<100 && !st.History.Single().Events.Any(x=>x.Contains("已启动 maa"));i++)await Task.Delay(30);if(mode=="stop"){await Task.Delay(250);e.Stop();}if(mode=="stop-invoke"){for(int i=0;i<100 && !st.History.Single().Events.Any(x=>x.Contains("准备请求启动"));i++)await Task.Delay(30);e.Stop();}for(int i=0;i<350 && !running.IsCompleted && !e.Waiting;i++)await Task.Delay(30);if(!running.IsCompleted)e.Stop();await running;await Task.Delay(1300);var ev=st.History.Single().Events;bool evidence=mode switch {"stop" or "stop-invoke"=>st.History.Single().State=="已停止","update"=>ev.Any(x=>x.Contains("检测到更新或重启")),"identity"=>ev.Any(x=>x.Contains("启动状态不明：身份不明")),"timeout"=>ev.Any(x=>x.Contains("等待超时")),"error" or "stopped"=>ev.Any(x=>x.Contains("日志出现错误、停止或无法判定")),"save"=>e.NeedsRecovery,_=>false};Check(calls==0 && evidence && !ev.Any(x=>x.Contains("已启动 guard-next")),"MAA controlled start blocks "+mode);
        }
        await GuardCase("stop");await GuardCase("stop-invoke");await GuardCase("update");await GuardCase("identity");await GuardCase("save");await GuardCase("timeout");await GuardCase("error");await GuardCase("stopped");
        string outsideDir=Path.Combine(root,"maa-start-outside");Directory.CreateDirectory(outsideDir);string ownLog=Path.Combine(outsideDir,"own.log"),otherLog=Path.Combine(outsideDir,"other.log"),ownCount=Path.Combine(outsideDir,"own.txt"),otherCount=Path.Combine(outsideDir,"other.txt");await File.WriteAllTextAsync(ownLog,"old\n");await File.WriteAllTextAsync(otherLog,"old\n");string MakeArgs(string log,string count)=>MaaStartController.SkipArgument+" fixture-maa-start single \""+log+"\" \""+count+"\"";using(var owned=OwnedProcess.Start(new TaskSpec{Program=Environment.ProcessPath!,Arguments=MakeArgs(ownLog,ownCount),WorkingDirectory=AppContext.BaseDirectory})){using var outsider=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,MakeArgs(otherLog,otherCount)){UseShellExecute=false})!;for(int i=0;i<120 && (outsider.MainWindowHandle==IntPtr.Zero || owned.Root.MainWindowHandle==IntPtr.Zero);i++){await Task.Delay(25);outsider.Refresh();owned.Root.Refresh();}bool rejected=false;if(outsider.MainWindowHandle!=IntPtr.Zero){var top=System.Windows.Automation.AutomationElement.FromHandle(outsider.MainWindowHandle);var button=top.FindFirst(System.Windows.Automation.TreeScope.Descendants,new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty,"Link Start!"));if(button!=null)rejected=!MaaStartController.Invoke(owned,new MaaStartController.Probe("就绪",button,outsider.MainWindowHandle),()=>true);}await outsider.WaitForExitAsync();await owned.Root.WaitForExitAsync();Check(rejected && !File.Exists(ownCount) && !File.Exists(otherCount),"MAA controlled start refuses another process window");}
    }
    static async Task ControlledResumeTests(bool quick=false)
    {
        async Task Verify(string mode, bool expected, bool trusted=true, string completion="日志信号")
        {
            var st=NewStore("controlled-"+mode+"-"+trusted+"-"+completion);var first=new TaskSpec{Name="first",Program=Environment.ProcessPath!,Arguments="fixture-control-parent "+mode,WorkingDirectory=AppContext.BaseDirectory,Completion=completion,LogSource="程序输出",SuccessText="READY",TrustCompletion=trusted};var next=Spec("next","echo next");st.Tasks.Add(first);st.Tasks.Add(next);var e=new RunnerEngine(st,()=>{});string? pipeName=null;var running=e.StartControlledFixture([first,next],name=>pipeName=name);
            for(int i=0;i<350 && e.Running && !e.Waiting;i++)await Task.Delay(20);
            bool started=st.History.Single().Events.Any(x=>x.Contains("已启动 next"));bool paused=e.Waiting;
            bool closed=false;if(paused){try{using var client=new NamedPipeClientStream(".",pipeName!,PipeDirection.Out);client.Connect(100);}catch(Exception ex) when(ex is TimeoutException or IOException or UnauthorizedAccessException){closed=true;}}
            if(paused || e.Running)e.Stop();await running;var events=st.History.Single().Events;
            Check(expected ? started && st.History.Single().AllSucceeded==true && events.Any(x=>x.Contains("已核对重启进程身份")) && events.Any(x=>x.Contains("本次重启进程的业务成功证据")) : !started && paused && closed && st.History.Single().AllSucceeded==false, "controlled resume "+mode+" trusted="+trusted+" completion="+completion);
        }
        async Task VerifyTrackingUncertainty()
        {
            var st=NewStore("controlled-tracking-uncertain");var a=new TaskSpec{Name="first",Program=Environment.ProcessPath!,Arguments="fixture-control-parent success",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,Completion="日志信号",SuccessText="READY",TrustCompletion=true};var b=Spec("next","echo next");st.Tasks.Add(a);st.Tasks.Add(b);var e=new RunnerEngine(st,()=>{}){CandidateTrackerFactory=job=>new RestartCandidateTracker(job,()=>throw new System.ComponentModel.Win32Exception(5))};var run=e.StartControlledFixture([a,b]);for(int i=0;i<350 && e.Running && !e.Waiting;i++)await Task.Delay(20);bool safe=e.Waiting && st.History.Single().Events.Any(x=>x.Contains("已取得本次重启进程的业务成功证据")) && st.History.Single().Events.Any(x=>x.Contains("受控证据与重启候选身份不一致或不明确")) && !st.History.Single().Events.Any(x=>x.Contains("已启动 next"));e.Stop();await run;Check(safe && st.History.Single().AllSucceeded==false,"controlled success cannot bypass uncertain candidate tracking");
        }
        async Task VerifyOutside()
        {
            string name; bool accepted; var channel=new ControlledRestartChannel(); name=channel.Name;
            using(var owned=OwnedProcess.Start(new TaskSpec{Program=Environment.ProcessPath!,Arguments="fixture-restart-parent",WorkingDirectory=AppContext.BaseDirectory}))
            {
                var stdout=owned.Output.ReadToEndAsync();var stderr=owned.Error.ReadToEndAsync();channel.Start(owned,()=>true,"READY",_=>{});
                using var outsider=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-control-target success "+channel.Arguments){UseShellExecute=false})!;
                await outsider.WaitForExitAsync();accepted=await channel.Result!;while(owned.Accounting().Active!=0)await Task.Delay(30);await stdout;await stderr;
            }
            channel.Dispose();bool closed=false;try{using var client=new NamedPipeClientStream(".",name,PipeDirection.Out);client.Connect(100);}catch(Exception ex) when(ex is TimeoutException or IOException or UnauthorizedAccessException){closed=true;}Check(!accepted && closed,"outside process rejected and local test channel closes");
        }
        async Task VerifyTimeout()
        {
            using var channel=new ControlledRestartChannel(TimeSpan.FromMilliseconds(200));using var owned=OwnedProcess.Start(new TaskSpec{Program=Environment.ProcessPath!,Arguments="fixture-wait",WorkingDirectory=AppContext.BaseDirectory});var output=owned.Output.ReadToEndAsync();var error=owned.Error.ReadToEndAsync();channel.Start(owned,()=>true,"READY",_=>{});bool accepted=await channel.Result!;while(owned.Accounting().Active!=0)await Task.Delay(30);await output;await error;Check(!accepted && channel.Failure.Contains("OperationCanceledException"),"controlled channel timeout fails closed");
        }
        async Task VerifyPersistenceFailure()
        {
            bool inject=false;string dir=Path.Combine(root,"controlled-save-fail");var st=new Store(dir,path=>inject ? new FaultWriteStream(path) : new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None));var a=new TaskSpec{Name="first",Program=Environment.ProcessPath!,Arguments="fixture-control-parent success",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",LogSource="程序输出",SuccessText="READY",TrustCompletion=true};var b=Spec("next","echo next");st.Tasks.Add(a);st.Tasks.Add(b);RunnerEngine? e=null;e=new RunnerEngine(st,()=>{if(e!=null && e.Status.Contains("已核对重启进程身份"))inject=true;});await e.StartControlledFixture([a,b]);Check(inject && e.NeedsRecovery && !st.History.Single().Events.Any(x=>x.Contains("已启动 next")),"controlled proof save failure blocks subsequent task");
        }
        async Task VerifyManual()
        {
            var st=NewStore("controlled-manual-confirm");var a=new TaskSpec{Name="first",Program=Environment.ProcessPath!,Arguments="fixture-control-parent success",WorkingDirectory=AppContext.BaseDirectory,Completion="人工确认",SuccessText="READY"};var b=Spec("next","echo next");st.Tasks.Add(a);st.Tasks.Add(b);var e=new RunnerEngine(st,()=>{});var run=e.StartControlledFixture([a,b]);for(int i=0;i<100 && !e.Status.Contains("已启动 first");i++)await Task.Delay(20);e.Confirm();for(int i=0;i<350 && e.Running && !e.Waiting;i++)await Task.Delay(20);bool paused=e.Waiting && !st.History.Single().Events.Any(x=>x.Contains("已启动 next"));e.Confirm();await run;Check(paused && st.History.Single().AllSucceeded==true && st.History.Single().Events.Any(x=>x.Contains("已启动 next")),"controlled manual requires fresh confirmation after restart");
        }
        async Task VerifyStop()
        {
            var st=NewStore("controlled-stop");var a=new TaskSpec{Name="first",Program=Environment.ProcessPath!,Arguments="fixture-control-parent slow",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",SuccessText="READY",TrustCompletion=true};var b=Spec("next","echo next");st.Tasks.Add(a);st.Tasks.Add(b);var e=new RunnerEngine(st,()=>{});var run=e.StartControlledFixture([a,b]);for(int i=0;i<100 && !st.History.Single().Events.Any(x=>x.Contains("检测到更新或重启"));i++)await Task.Delay(20);e.Stop();await run;Check(st.History.Single().State=="已停止" && !st.History.Single().Events.Any(x=>x.Contains("已启动 next")),"stop prevents controlled resume");
        }
        await Verify("success",true);if(quick)return;await Verify("missing",false);await Verify("forged",false);await Verify("disconnect",false);await Verify("duplicate",false);await Verify("failure",false);await Verify("multiple",false);await Verify("success",false,false);await Verify("success",false,true,"人工确认");await VerifyTrackingUncertainty();await VerifyOutside();await VerifyTimeout();await VerifyPersistenceFailure();await VerifyStop();await VerifyManual();
    }
    static async Task UpdateRestartOwnershipProbe()
    {
        var owned=OwnedProcess.Start(new TaskSpec {Program=Environment.ProcessPath!,Arguments="fixture-restart-parent",WorkingDirectory=AppContext.BaseDirectory});
        using(owned)
        {
            var output=owned.Output.ReadToEndAsync();var errors=owned.Error.ReadToEndAsync();bool rootExitedWithChild=false;uint maxTotal=0;
            for(int i=0;i<150;i++) {var a=owned.Accounting();maxTotal=Math.Max(maxTotal,a.Total);if(owned.Root.HasExited && a.Active>0)rootExitedWithChild=true;if(owned.Root.HasExited && a.Active==0)break;await Task.Delay(30);}
            string text=await output;await errors;var final=owned.Accounting();Check(rootExitedWithChild && maxTotal>=3 && final.Active==0 && text.Contains("SIM_RESTART_DONE"),"controlled updater and restart remain within owned job until descendants exit");
        }
    }
    static async Task RestartCandidateTests()
    {
        async Task Observe(string mode, bool multiple, bool marker)
        {
            var st=NewStore("candidate-"+mode);var task=new TaskSpec{Name="candidate",Program=Environment.ProcessPath!,Arguments="fixture-identity-parent "+mode,WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,TrustCompletion=true,Completion="日志信号",SuccessText="READY"};st.Tasks.Add(task);st.Tasks.Add(Spec("after-candidate","echo bad"));var engine=new RunnerEngine(st,()=>{});var run=engine.Start();
            for(int i=0;i<250 && !engine.Waiting;i++)await Task.Delay(30);var ev=st.History.Single().Events;bool ok=engine.Waiting && ev.Any(x=>x.Contains("受管组内可能的重启程序")) && ev.Any(x=>x.Contains("已记录的重启候选进程已退出")) && (!multiple || ev.Any(x=>x.Contains("多个可能的重启程序"))) && (marker || !ev.Any(x=>x.Contains("检测到更新或重启"))) && !ev.Any(x=>x.Contains("已启动 after-candidate")) && !ev.Any(x=>x.Contains("candidate 成功"));if(!ok)Console.WriteLine("DETAIL "+mode+" "+string.Join(" | ",ev));engine.Stop();await run;Check(ok,"restart candidate "+mode+" is identified but cannot reuse success evidence");
        }
        await Observe("single",false,true);await Observe("multiple",true,true);await Observe("silent",false,false);
        var timeline=NewStore("candidate-timeline");timeline.Tasks.Add(new TaskSpec{Name="trace",Program=Environment.ProcessPath!,Arguments="fixture-identity-parent multiple",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true});var timelineEngine=new RunnerEngine(timeline,()=>{});var timelineRun=timelineEngine.Start();for(int i=0;i<250 && !timelineEngine.Waiting;i++)await Task.Delay(30);var timelineEvents=timeline.History.Single().Events;bool timelineOk=timelineEvents.Count(x=>x.Contains("受管候选出现"))>=2 && timelineEvents.Any(x=>x.Contains("其他候选尚未记录退出，可能重叠")) && timelineEvents.Count(x=>x.Contains("候选进程已退出（PID"))>=2;timelineEngine.Stop();await timelineRun;Check(timelineOk,"candidate timeline records overlapping identities and exits");
        var stopped=NewStore("candidate-stop");stopped.Tasks.Add(new TaskSpec{Name="stop",Program=Environment.ProcessPath!,Arguments="fixture-identity-parent single",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,TrustCompletion=true});stopped.Tasks.Add(Spec("after-stop","echo bad"));var se=new RunnerEngine(stopped,()=>{});var sr=se.Start();for(int i=0;i<100 && !stopped.History.Single().Events.Any(x=>x.Contains("受管组内可能的重启程序"));i++)await Task.Delay(30);bool observed=stopped.History.Single().Events.Any(x=>x.Contains("受管组内可能的重启程序"));se.Stop();await sr;Check(observed && stopped.History.Single().State=="已停止" && !stopped.History.Single().Events.Any(x=>x.Contains("已启动 after-stop")),"stop while candidate runs prevents next task");
        var clock=new FakeClock();var timed=NewStore("candidate-timeout");timed.Tasks.Add(new TaskSpec{Name="timed",Program=Environment.ProcessPath!,Arguments="fixture-identity-parent single",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,TrustCompletion=true});timed.Tasks.Add(Spec("after-timeout","echo bad"));var te=new RunnerEngine(timed,()=>{},clock);var tr=te.Start();for(int i=0;i<100 && !timed.History.Single().Events.Any(x=>x.Contains("受管组内可能的重启程序"));i++)await Task.Delay(30);clock.Seconds=301;for(int i=0;i<40 && !timed.History.Single().Events.Any(x=>x.Contains("更新跟踪已等待"));i++)await Task.Delay(30);bool warned=timed.History.Single().Events.Any(x=>x.Contains("更新跟踪已等待")) && !timed.History.Single().Events.Any(x=>x.Contains("已启动 after-timeout"));te.Stop();await tr;Check(warned,"candidate tracking timeout warns and blocks next task");
        var faulty=NewStore("candidate-query-failed");faulty.Tasks.Add(new TaskSpec{Name="query",Program=Environment.ProcessPath!,Arguments="fixture-identity-parent silent",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,TrustCompletion=true,Completion="日志信号",SuccessText="READY"});faulty.Tasks.Add(Spec("after-fault","echo bad"));var fe=new RunnerEngine(faulty,()=>{}){CandidateTrackerFactory=owned=>new RestartCandidateTracker(owned,()=>throw new System.ComponentModel.Win32Exception(5))};var ft=fe.Start();for(int i=0;i<250 && !fe.Waiting;i++)await Task.Delay(30);bool faultSafe=fe.Waiting && faulty.History.Single().Events.Any(x=>x.Contains("身份或归属无法核实")) && !faulty.History.Single().Events.Any(x=>x.Contains("已启动 after-fault"));fe.Stop();await ft;Check(faultSafe,"candidate query failure stops automatic advance");
        foreach(var failKind in new[]{"handle","membership"})
        {
            var bad=NewStore("candidate-"+failKind);bad.Tasks.Add(new TaskSpec{Name="bad",Program=Environment.ProcessPath!,Arguments="fixture-identity-parent silent",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,TrustCompletion=true,Completion="日志信号",SuccessText="READY"});bad.Tasks.Add(Spec("after-bad","echo bad"));var be=new RunnerEngine(bad,()=>{}){CandidateTrackerFactory=job=>failKind=="handle" ? new RestartCandidateTracker(job,openProcess:_=>throw new System.ComponentModel.Win32Exception(5)) : new RestartCandidateTracker(job,containsProcess:_=>throw new System.ComponentModel.Win32Exception(5))};var bt=be.Start();for(int i=0;i<250 && !be.Waiting;i++)await Task.Delay(30);bool safe=be.Waiting && bad.History.Single().Events.Any(x=>x.Contains("身份或归属无法核实")) && !bad.History.Single().Events.Any(x=>x.Contains("已启动 after-bad"));be.Stop();await bt;Check(safe,"candidate "+failKind+" failure stops automatic advance");
        }
        using var outsider=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-identity-target"){UseShellExecute=false})!;var unrelated=NewStore("candidate-unrelated");unrelated.Tasks.Add(new TaskSpec{Name="other",Program=Environment.ProcessPath!,Arguments="fixture-split-update",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,TrustCompletion=true});var ue=new RunnerEngine(unrelated,()=>{});var ut=ue.Start();for(int i=0;i<150 && !ue.Waiting;i++)await Task.Delay(30);bool outsideSafe=ue.Waiting && !outsider.HasExited && !unrelated.History.Single().Events.Any(x=>x.Contains("受管组内可能的重启程序"));ue.Stop();await ut;await outsider.WaitForExitAsync();Check(outsideSafe,"same-path outside process is not adopted");
        using var owned=OwnedProcess.Start(new TaskSpec{Program=Environment.ProcessPath!,Arguments="fixture-wait",WorkingDirectory=AppContext.BaseDirectory});var stdout=owned.Output.ReadToEndAsync();var stderr=owned.Error.ReadToEndAsync();
        using(var tracker=new RestartCandidateTracker(owned,()=>throw new System.ComponentModel.Win32Exception(5))){tracker.Poll();Check(tracker.Uncertain && !tracker.HasCandidate,"candidate group query failure is uncertain");}
        var reasons=new List<string>();using(var tracker=new RestartCandidateTracker(owned,()=>throw new System.ComponentModel.Win32Exception(5),report:reasons.Add)){tracker.Poll();tracker.Poll();Check(tracker.Uncertain && reasons.Count==1 && reasons[0].Contains("查询进程组") && reasons[0].Contains("Win32 错误 5"),"candidate failure reports stage and code once");}
        var sequence=new List<string>();uint[] ids=[];using(var tracker=new RestartCandidateTracker(owned,()=>ids,containsProcess:_=>true,report:sequence.Add))
        {
            async Task SampleHeld()
            {
                using var p=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-identity-held"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true})!;
                string? ready=await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if(ready!="READY")throw new InvalidOperationException($"fixture not ready: PID {p.Id}, output {ready}");
                ids=[(uint)p.Id];tracker.Poll();
                if(!sequence.Any(x=>x.Contains($"PID {p.Id},") || x.Contains($"PID {p.Id}，")))Console.WriteLine($"DETAIL held PID {p.Id}; exited={p.HasExited}; at={DateTimeOffset.Now:O}; events={string.Join(" | ",sequence)}");
                await p.StandardInput.WriteLineAsync("release");await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));ids=[];tracker.Poll();
            }
            await SampleHeld();await SampleHeld();
            bool sequentialOk=tracker.Ambiguous && !tracker.Uncertain && sequence.Count(x=>x.Contains("受管候选出现"))==2 && sequence.Any(x=>x.Contains("已观察到此前候选退出")) && sequence.Count(x=>x.Contains("候选进程已退出"))==2;if(!sequentialOk)Console.WriteLine($"DETAIL sequential ambiguous={tracker.Ambiguous}; uncertain={tracker.Uncertain}; events={string.Join(" | ",sequence)}");Check(sequentialOk,"candidate timeline distinguishes sequential identities without relaxing ambiguity");
            Check(tracker.HasProvenSequentialCandidates(),"retained candidate handles prove non-overlapping sequence");
        }
        using(var tracker=new RestartCandidateTracker(owned,()=>[])){var first=DateTime.UtcNow;tracker.RecordIdentity(42,first);tracker.RecordIdentity(42,first.AddSeconds(1));Check(tracker.Ambiguous,"reused PID with different creation time is ambiguous");}
        while(owned.Accounting().Active!=0)await Task.Delay(30);await stdout;await stderr;
        var rootEvents=new List<string>();using(var tracker=new RestartCandidateTracker(owned,()=>[(uint)owned.Root.Id],openProcess:_=>throw new ArgumentException("should not reopen original"),report:rootEvents.Add)){tracker.Poll();Check(!tracker.Uncertain && !tracker.HasCandidate && rootEvents.Count==0,"exited original identified by retained handle is not reopened");}
        var missingEvents=new List<string>();using(var tracker=new RestartCandidateTracker(owned,()=>[(uint)int.MaxValue],openProcess:_=>throw new ArgumentException("gone"),report:missingEvents.Add)){tracker.Poll();Check(tracker.Uncertain && !tracker.HasCandidate && missingEvents.Any(x=>x.Contains("打开进程") && x.Contains("ArgumentException")),"unidentified vanished process remains uncertain");}
        using var pathTarget=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-identity-held"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true})!;await pathTarget.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));var pathEvents=new List<string>();using(var tracker=new RestartCandidateTracker(owned,()=>[(uint)pathTarget.Id],containsProcess:_=>true,report:pathEvents.Add,getPath:_=>null)){tracker.Poll();Check(tracker.Uncertain && !tracker.HasCandidate && pathEvents.Any(x=>x.Contains("程序路径不可用")),"unavailable candidate path remains uncertain");}await pathTarget.StandardInput.WriteLineAsync("release");await pathTarget.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var knownTarget=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-identity-held"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true})!;await knownTarget.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));uint knownId=(uint)knownTarget.Id;bool denyReopen=false;int opens=0;
        using var known=new RestartCandidateTracker(owned,()=>[knownId],openProcess:id=>{opens++;if(denyReopen)throw new ArgumentException("already exited");return Process.GetProcessById(id);},containsProcess:_=>true);
        using var invalid=new RestartCandidateTracker(owned,()=>[knownId],containsProcess:_=>true,handlePid:h=>{h.Dispose();return 0;});
        using var mismatched=new RestartCandidateTracker(owned,()=>[knownId],containsProcess:_=>true,handleCreated:_=>DateTime.UnixEpoch);
        using var queryError=new RestartCandidateTracker(owned,()=>[knownId],containsProcess:_=>true,handleCreated:_=>throw new System.ComponentModel.Win32Exception(5));
        known.Poll();invalid.Poll();mismatched.Poll();queryError.Poll();bool observedBeforeExit=known.HasCandidate && invalid.HasCandidate && mismatched.HasCandidate && queryError.HasCandidate;
        await knownTarget.StandardInput.WriteLineAsync("release");await knownTarget.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));denyReopen=true;known.Poll();invalid.Poll();mismatched.Poll();queryError.Poll();
        Check(observedBeforeExit && !known.Uncertain && known.CandidateExited && opens==1,"known exited candidate uses retained verified handle without reopening");
        Check(invalid.Uncertain && mismatched.Uncertain && queryError.Uncertain,"invalid candidate handle mismatched creation and query failure remain uncertain");
    }
    static async Task UpdateRestartTests()
    {
        Check(!new TaskSpec().TrackUpdateRestart,"update tracking defaults disabled for legacy tasks");
        var cfg=NewStore("restart-config");cfg.Tasks.Add(new TaskSpec{Name="enabled",TrackUpdateRestart=true});cfg.Save();var cfg2=NewStore("restart-config");cfg2.Load();Check(cfg2.Tasks.Single().TrackUpdateRestart && cfg2.Tasks.Single().Copy().TrackUpdateRestart,"tracking choice persists and is copied to snapshot");
        var crash=NewStore("restart-crash");crash.History.Add(new RunRecord{State="运行中",Events=["检测到更新或重启线索"]});crash.Save();var reloaded=NewStore("restart-crash");reloaded.Load();Check(new RunnerEngine(reloaded,()=>{}).NeedsRecovery && reloaded.History.Single().State=="中断","interrupted update remains blocked for verification");
        var st=NewStore("restart-owned");st.Tasks.Add(new TaskSpec{Name="restart",Program=Environment.ProcessPath!,Arguments="fixture-restart-parent",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,TrustCompletion=true});st.Tasks.Add(Spec("after-restart","echo bad"));var e=new RunnerEngine(st,()=>{});var run=e.Start();for(int i=0;i<200 && !e.Waiting;i++)await Task.Delay(30);var ev=st.History.Single().Events;bool observed=e.Waiting && ev.Any(x=>x.Contains("进程组仍有") && x.Contains("无法仅凭数量确认")) && ev.Any(x=>x.Contains("SIM_RESTART_DONE")) && !ev.Any(x=>x.Contains("已启动 after-restart")) && !ev.Any(x=>x.Contains("restart 成功"));e.Stop();await run;Check(observed && st.History.Single().AllSucceeded==false,"owned descendants are awaited but restart is not treated as success");
        var none=NewStore("restart-nochild");none.Tasks.Add(new TaskSpec{Name="nochild",Program=Environment.ProcessPath!,Arguments="fixture-split-update",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,TrustCompletion=true});var ne=new RunnerEngine(none,()=>{});var nr=ne.Start();for(int i=0;i<150 && !ne.Waiting;i++)await Task.Delay(30);bool noCandidate=ne.Waiting && none.History.Single().Events.Any(x=>x.Contains("未确认更新后程序归属"));ne.Stop();await nr;Check(noCandidate,"unconfirmed restarted process pauses");
        var tc=new FakeClock();var timed=NewStore("restart-timeout");timed.Tasks.Add(new TaskSpec{Name="wait",Program=Environment.ProcessPath!,Arguments="fixture-restart-wait",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true});timed.Tasks.Add(Spec("must-not-start","echo wrong"));var te=new RunnerEngine(timed,()=>{},tc);var tr=te.Start();for(int i=0;i<100 && !timed.History.Single().Events.Any(x=>x.Contains("检测到更新或重启"));i++)await Task.Delay(20);tc.Seconds=301;for(int i=0;i<100 && !timed.History.Single().Events.Any(x=>x.Contains("更新跟踪已等待"));i++)await Task.Delay(30);bool warning=timed.History.Single().Events.Any(x=>x.Contains("更新跟踪已等待")) && !timed.History.Single().Events.Any(x=>x.Contains("已启动 must-not-start"));te.Stop();await tr;Check(warning && timed.History.Single().State=="已停止","update tracking timeout warns without starting next or killing");
        var unrelated=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-wait"){UseShellExecute=false})!;var detached=NewStore("restart-unrelated");detached.Tasks.Add(new TaskSpec{Name="unknown",Program=Environment.ProcessPath!,Arguments="fixture-split-update",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,TrustCompletion=true});var de=new RunnerEngine(detached,()=>{});var dr=de.Start();for(int i=0;i<150 && !de.Waiting;i++)await Task.Delay(30);bool safe=de.Waiting && !unrelated.HasExited && detached.History.Single().Events.Any(x=>x.Contains("未确认更新后程序归属"));de.Stop();await dr;await unrelated.WaitForExitAsync();unrelated.Dispose();Check(safe,"unrelated process never adopted or terminated");
        var failure=NewStore("restart-failure");failure.Tasks.Add(new TaskSpec{Name="failed-update",Program=Environment.ProcessPath!,Arguments="fixture-restart-failure",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,ContinueOnFailure=true,TrustCompletion=true});failure.Tasks.Add(Spec("after-failed-update","echo bad"));var fengine=new RunnerEngine(failure,()=>{});var fr=fengine.Start();for(int i=0;i<150 && !fengine.Waiting;i++)await Task.Delay(30);bool failedSafe=fengine.Waiting && !failure.History.Single().Events.Any(x=>x.Contains("已启动 after-failed-update"));fengine.Stop();await fr;Check(failedSafe,"failed update and simulated dialog pause despite continue policy");
        var dialog=NewStore("restart-dialog");dialog.Tasks.Add(new TaskSpec{Name="dialog",Program=Environment.ProcessPath!,Arguments="fixture-restart-dialog",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,TrustCompletion=true});var dlgEngine=new RunnerEngine(dialog,()=>{});var dlgRun=dlgEngine.Start();for(int i=0;i<150 && !dlgEngine.Waiting;i++)await Task.Delay(30);bool dialogSafe=dlgEngine.Waiting && dialog.History.Single().Events.Any(x=>x.Contains("SIM_DIALOG_CLOSED")) && !dialog.History.Single().Events.Any(x=>x.Contains("dialog 成功"));dlgEngine.Stop();await dlgRun;Check(dialogSafe,"controlled update dialog does not count as completion");
        var multi=NewStore("restart-multiple");multi.Tasks.Add(new TaskSpec{Name="multi",Program=Environment.ProcessPath!,Arguments="fixture-restart-multiple",WorkingDirectory=AppContext.BaseDirectory,TrackUpdateRestart=true,TrustCompletion=true});var me=new RunnerEngine(multi,()=>{});var mr=me.Start();for(int i=0;i<200 && !me.Waiting;i++)await Task.Delay(30);bool multiSafe=me.Waiting && multi.History.Single().Events.Any(x=>x.Contains("无法仅凭数量确认"));me.Stop();await mr;Check(multiSafe,"multiple owned candidates cannot be assumed restarted task");
    }
    static async Task UpdateMarkerTests()
    {
        Check(UpdateMarkers.TryParse("  alpha  \r\n\r\n Beta  ",out var parsed) && parsed.SequenceEqual(new[]{"alpha","Beta"}), "update markers trim blank lines");
        Check(!UpdateMarkers.TryParse(string.Join("\n",Enumerable.Repeat("x",33)),out _) && !UpdateMarkers.TryParse(new string('x',257),out _) && UpdateMarkers.TryParse(new string('x',256),out _), "update marker limits are enforced");
        var s=NewStore("marker-roundtrip"); s.Tasks.Add(new TaskSpec { Name="marker",UpdateMarkers="  custom signal  \n Second " }); s.Save(); var reload=NewStore("marker-roundtrip");reload.Load();Check(reload.Tasks.Single().UpdateMarkers.Contains("custom signal") && new TaskSpec().UpdateMarkers=="", "custom markers and legacy default persist");
        var line=new LineEvidence(5); var seen=new List<string>();line.Feed("AB",seen.Add);line.Feed("C\nDE",seen.Add);line.Feed("F\rGHI",seen.Add);Check(seen.Contains("ABC") && seen.Contains("DEF") && !seen.Any(x=>x.Contains("CDE") || x.Contains("FGH")),"file evidence keeps chunks and separates lines");
        async Task RunPause(string name,string fixture,string source,string completion="进程退出")
        {
            var st=NewStore("marker-"+name);var spec=new TaskSpec {Name=name,Program=Environment.ProcessPath!,Arguments=fixture,WorkingDirectory=AppContext.BaseDirectory,TrustCompletion=true,ContinueOnFailure=true,UpdateMarkers="  MagicRestart  ",Completion=completion};if(completion=="日志信号") spec.SuccessText="READY";st.Tasks.Add(spec);st.Tasks.Add(Spec("after-marker","echo next"));var engine=new RunnerEngine(st,()=>{});var task=engine.Start();for(int i=0;i<150 && !engine.Waiting;i++)await Task.Delay(30);bool ok=engine.Waiting && st.History.Single().Events.Count(x=>x.Contains("检测到更新或重启线索"))==1 && st.History.Single().Events.Any(x=>x.Contains(source) && x.Contains("MagicRestart")) && !st.History.Single().Events.Any(x=>x.Contains("已启动 after-marker"));engine.Stop();await task;Check(ok,name+" custom marker pauses with source");
        }
        await RunPause("stdout","fixture-marker-out","标准输出","日志信号");
        await RunPause("stderr","fixture-marker-err","标准错误","人工确认");
        var noJoin=NewStore("marker-nojoin");noJoin.Tasks.Add(new TaskSpec{Name="nojoin",Program=Environment.ProcessPath!,Arguments="fixture-marker-nojoin",WorkingDirectory=AppContext.BaseDirectory,UpdateMarkers="MagicRestart",TrustCompletion=true});await new RunnerEngine(noJoin,()=>{}).Start();Check(noJoin.History.Single().AllSucceeded==true && !noJoin.History.Single().Events.Any(x=>x.Contains("检测到更新或重启")),"different lines and streams never form marker");
        var old=NewStore("marker-file-old");string path=Path.Combine(root,"marker-old.log");await File.WriteAllTextAsync(path,"MagicRestart\n");old.Tasks.Add(new TaskSpec{Name="old",Program=Environment.ProcessPath!,Arguments="fixture-append \""+path+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",LogSource="日志文件",LogPath=path,SuccessText="完成",TrustCompletion=true,UpdateMarkers="MagicRestart"});await new RunnerEngine(old,()=>{}).Start();Check(old.History.Single().AllSucceeded==true && !old.History.Single().Events.Any(x=>x.Contains("检测到更新或重启")),"old file marker ignored");
        var added=NewStore("marker-file-new");string path2=Path.Combine(root,"marker-new.log");await File.WriteAllTextAsync(path2,"old\n");added.Tasks.Add(new TaskSpec{Name="file",Program=Environment.ProcessPath!,Arguments="fixture-marker-file \""+path2+"\"",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",LogSource="日志文件",LogPath=path2,SuccessText="READY",TrustCompletion=true,UpdateMarkers="MagicRestart"});var fe=new RunnerEngine(added,()=>{});var ft=fe.Start();for(int i=0;i<150 && !fe.Waiting;i++)await Task.Delay(30);bool fileOk=fe.Waiting && added.History.Single().Events.Any(x=>x.Contains("日志文件：MagicRestart"));fe.Stop();await ft;Check(fileOk,"new split file marker pauses despite success");
        var longLine=NewStore("marker-long");longLine.Tasks.Add(new TaskSpec{Name="long",Program=Environment.ProcessPath!,Arguments="fixture-marker-long",WorkingDirectory=AppContext.BaseDirectory,TrustCompletion=true,UpdateMarkers="MagicRestart"});var le=new RunnerEngine(longLine,()=>{});var lt=le.Start();for(int i=0;i<150 && !le.Waiting;i++)await Task.Delay(30);bool longOk=le.Waiting && longLine.History.Single().Events.Any(x=>x.Contains("标准输出：MagicRestart"));le.Stop();await lt;Check(longOk,"custom marker at end of long line pauses");
        var snapshot=NewStore("marker-snapshot");var snapTask=new TaskSpec{Name="snapshot",Program=Environment.ProcessPath!,Arguments="fixture-marker-delayed",WorkingDirectory=AppContext.BaseDirectory,TrustCompletion=true,UpdateMarkers="MagicRestart"};snapshot.Tasks.Add(snapTask);var snEngine=new RunnerEngine(snapshot,()=>{});var snRun=snEngine.Start();snapTask.UpdateMarkers="different";for(int i=0;i<150 && !snEngine.Waiting;i++)await Task.Delay(30);bool snapOk=snEngine.Waiting && snapshot.History.Single().Events.Any(x=>x.Contains("MagicRestart"));snEngine.Stop();await snRun;Check(snapOk,"running queue uses marker snapshot");
        var faultDir=Path.Combine(root,"marker-save-fail");bool fail=false;var faulty=new Store(faultDir,path=>fail ? new FaultWriteStream(path) : new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None));faulty.Tasks.Add(new TaskSpec{Name="fault",Program=Environment.ProcessPath!,Arguments="fixture-marker-delayed",WorkingDirectory=AppContext.BaseDirectory,UpdateMarkers="MagicRestart",TrustCompletion=true});faulty.Tasks.Add(Spec("after-fault","echo bad"));var faultEngine=new RunnerEngine(faulty,()=>{});var faultRun=faultEngine.Start();for(int i=0;i<100 && !faultEngine.Status.Contains("已启动 fault");i++)await Task.Delay(20);fail=true;await faultRun;Check(!faulty.History.Single().Events.Any(x=>x.Contains("已启动 after-fault")),"failed marker event save blocks next task");
        var planned=NewStore("marker-plan");var planTask=new TaskSpec{Name="planned",Program=Environment.ProcessPath!,Arguments="fixture-marker-out",WorkingDirectory=AppContext.BaseDirectory,UpdateMarkers="MagicRestart",TrustCompletion=true};planned.Tasks.Add(planTask);var plan=new SchedulePlan{Name="marker-plan",Enabled=true,EffectiveFrom=new DateTimeOffset(2026,11,15,0,0,0,TimeSpan.Zero),Slots=[new ScheduleSlot{Time="09:00",Days=[DayOfWeek.Monday]}],Items=[new ScheduleItem{TaskId=planTask.Id,Days=[DayOfWeek.Monday]}]};planned.Plans.Add(plan);var pe=new RunnerEngine(planned,()=>{});var scheduler=new Scheduler(planned,pe,()=>{});var due=new DateTimeOffset(2026,11,16,9,0,0,TimeSpan.Zero);scheduler.Tick(due.AddSeconds(-10),TimeZoneInfo.Utc);scheduler.Tick(due,TimeZoneInfo.Utc);for(int i=0;i<150 && !pe.Waiting;i++)await Task.Delay(30);bool planPause=pe.Waiting && planned.History.Any(h=>h.Trigger=="定时" && h.Events.Any(x=>x.Contains("MagicRestart")));pe.Continue();for(int i=0;i<150 && pe.Running;i++)await Task.Delay(30);Check(planPause && planned.History.Single(h=>h.Trigger=="定时").AllSucceeded==false,"scheduled custom marker pauses and skipped run remains unsuccessful");
        var invalid=NewStore("marker-invalid");invalid.Tasks.Add(new TaskSpec{Name="invalid",Program=Environment.ProcessPath!,Arguments="fixture-wait",UpdateMarkers=new string('x',257)});var ie=new RunnerEngine(invalid,()=>{});var it=ie.Start();for(int i=0;i<100 && !ie.Waiting;i++)await Task.Delay(30);bool invalidOk=ie.Waiting && !invalid.History.Single().Events.Any(x=>x.Contains("已启动 invalid"));ie.Stop();await it;Check(invalidOk,"invalid saved marker fails closed before process start");
    }
    static async Task ScheduleTests()
    {
        var invalid=NewStore("schedule-invalid");string invalidPath=Path.Combine(root,"schedule-invalid","data.json");string invalidText="{\"Format\":\"SequentialRunnerV3/1\",\"Tasks\":[],\"History\":[],\"Plans\":[{\"Name\":\"bad\",\"Slots\":[{\"Time\":\"99:99\",\"Days\":[9]}]}]}"; await File.WriteAllTextAsync(invalidPath,invalidText);bool rejected=false;try { invalid.Load(); } catch(InvalidDataException) { rejected=true; }Check(rejected && await File.ReadAllTextAsync(invalidPath)==invalidText,"invalid schedule remains unchanged and is not activated");
        var utc=TimeZoneInfo.Utc; var before=new DateTimeOffset(2026,11,16,8,59,50,TimeSpan.Zero); var at=before.AddSeconds(15);
        var a=NewStore("schedule-basic"); TaskSpec t1=Spec("scheduled-first","echo first"), t2=Spec("scheduled-second","echo second"), t3=Spec("wrong-day","echo bad"); a.Tasks.Add(t1); a.Tasks.Add(t2); a.Tasks.Add(t3);
        var p=new SchedulePlan { Name="morning", Enabled=true, EffectiveFrom=before.AddDays(-1), Slots=[new ScheduleSlot { Time="09:00", Days=[DayOfWeek.Monday] },new ScheduleSlot { Time="09:01", Days=[DayOfWeek.Monday] }], Items=[new ScheduleItem { TaskId=t1.Id,Days=[DayOfWeek.Monday] }, new ScheduleItem { TaskId=t2.Id,Days=[DayOfWeek.Monday] }, new ScheduleItem { TaskId=t3.Id,Days=[DayOfWeek.Tuesday] }] };
        var p2=new SchedulePlan { Name="conflict", Enabled=true, EffectiveFrom=before.AddDays(-1), Slots=[new ScheduleSlot { Time="09:00", Days=[DayOfWeek.Monday] }], Items=[new ScheduleItem { TaskId=t1.Id,Days=[DayOfWeek.Monday] }] }; a.Plans.Add(p);a.Plans.Add(p2);
        var e=new RunnerEngine(a,()=>{}); var sch=new Scheduler(a,e,()=>{});sch.Tick(before,utc);sch.Tick(at,utc);sch.Tick(at.AddSeconds(2),utc);
        var persisted=System.Text.Json.JsonSerializer.Deserialize<Store.StoreData>(File.ReadAllText(Path.Combine(root,"schedule-basic","data.json")))!;
        Check(persisted.History.Any(h=>h.Events.Any(x=>x.Contains("已保存定时触发意图"))) && a.History.Count==2 && a.History.Any(h=>h.PlanName=="conflict" && h.State=="已跳过"),"scheduled intent persists before run and same-minute conflict skips");
        for(int i=0;i<100 && e.Running;i++) await Task.Delay(50);
        var events=a.History.First(h=>h.PlanName=="morning").Events; Check(events.FindIndex(x=>x.Contains("scheduled-first 成功"))>=0 && events.FindIndex(x=>x.Contains("已启动 scheduled-second"))>events.FindIndex(x=>x.Contains("scheduled-first 成功")) && !events.Any(x=>x.Contains("wrong-day")),"scheduled tasks respect order and weekdays");
        sch.Tick(at.AddMinutes(1),utc);for(int i=0;i<100 && e.Running;i++) await Task.Delay(50); Check(a.History.Count(h=>h.PlanName=="morning" && h.Trigger=="定时")==2,"second slot triggers and repeated tick does not duplicate");
        var read=NewStore("schedule-basic");read.Load();Check(read.Tasks[0].Id==t1.Id && read.Plans.Count==2 && read.Plans[0].Items[0].TaskId==t1.Id,"task and plan identifiers persist");
        var missed=NewStore("schedule-missed"); missed.Tasks.Add(t1.Copy()); missed.Plans.Add(p); missed.ScheduleCursorUtc=before.AddDays(-7); missed.ScheduleZoneId=utc.Id; missed.Save(); var me=new RunnerEngine(missed,()=>{}); var ms=new Scheduler(missed,me,()=>{});ms.Tick(at,utc); Check(!me.Running && missed.History.Any(h=>h.State=="已错过" && h.Events.Any(x=>x.Contains("时间点"))),"startup aggregates missed occurrences without running");
        var busy=NewStore("schedule-busy");busy.Tasks.Add(new TaskSpec { Name="already-running", Program=Environment.ProcessPath!,Arguments="fixture-wait",WorkingDirectory=AppContext.BaseDirectory });busy.Tasks.Add(t1.Copy());busy.Plans.Add(p);var be=new RunnerEngine(busy,()=>{});var bs=new Scheduler(busy,be,()=>{});bs.Tick(before,utc);var br=be.Start();bs.Tick(at,utc);Check(busy.History.Any(h=>h.PlanName=="morning" && h.State=="已跳过" && h.Events.Any(x=>x.Contains("已有任务"))),"existing running queue causes scheduled skip");be.Stop();await br;
        var resume=NewStore("schedule-resume");resume.Tasks.Add(t1.Copy());resume.Plans.Add(p);var rengine=new RunnerEngine(resume,()=>{});var rs=new Scheduler(resume,rengine,()=>{});rs.Tick(before,utc);rs.MarkResumed();rs.Tick(at,utc);Check(!rengine.Running && resume.History.Any(h=>h.State=="已错过"),"resume skips due occurrence");
        var later=NewStore("schedule-effective");later.Tasks.Add(t1.Copy());later.Plans.Add(new SchedulePlan { Name="late", Enabled=true, EffectiveFrom=at, Slots=[new ScheduleSlot { Time="09:00", Days=[DayOfWeek.Monday] }], Items=[new ScheduleItem { TaskId=t1.Id,Days=[DayOfWeek.Monday] }] });var le=new RunnerEngine(later,()=>{});var ls=new Scheduler(later,le,()=>{});ls.Tick(before,utc);ls.Tick(at,utc);Check(!le.Running && later.History.Count==0,"new or changed plan never backfills prior slot");
        var failed=NewStore("schedule-save-fail");failed.Tasks.Add(t1.Copy());failed.Plans.Add(p);var fe=new RunnerEngine(failed,()=>{});var fs=new Scheduler(failed,fe,()=>{});fs.Tick(before,utc);await File.WriteAllTextAsync(Path.Combine(root,"schedule-save-fail","data.json"),"external change");fs.Tick(at,utc);Check(!fe.Running && fs.Status.Contains("保存失败") && failed.History.Count==0,"failed intent save prevents scheduled launch");
        var recover=NewStore("schedule-interrupted");recover.History.Add(new RunRecord { Trigger="定时",PlanName="reserved",State="运行中" });recover.Save();var recover2=NewStore("schedule-interrupted");recover2.Load();Check(new RunnerEngine(recover2,()=>{}).NeedsRecovery && recover2.History.Single().State=="中断","persisted reservation requires recovery after interruption");
        var eastern=TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var fall=NewStore("schedule-fall");fall.Tasks.Add(t1.Copy());fall.Plans.Add(new SchedulePlan { Name="fall",Enabled=true,EffectiveFrom=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero),Slots=[new ScheduleSlot { Time="01:30",Days=[DayOfWeek.Sunday] }],Items=[new ScheduleItem { TaskId=t1.Id,Days=[DayOfWeek.Sunday] }] });
        var fengine=new RunnerEngine(fall,()=>{});var fsch=new Scheduler(fall,fengine,()=>{});var fallFirst=new DateTimeOffset(2026,11,1,5,29,50,TimeSpan.Zero);fsch.Tick(fallFirst,eastern);fsch.Tick(fallFirst.AddSeconds(15),eastern);for(int i=0;i<100 && fengine.Running;i++) await Task.Delay(50);fsch.Tick(fallFirst.AddHours(1),eastern);fsch.Tick(fallFirst.AddHours(1).AddSeconds(15),eastern);Check(fall.History.Count(h=>h.PlanName=="fall" && h.State!="已错过")==1,"daylight saving repeated local minute does not duplicate");
        var spring=NewStore("schedule-spring");spring.Tasks.Add(t1.Copy());spring.Plans.Add(new SchedulePlan { Name="spring",Enabled=true,EffectiveFrom=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero),Slots=[new ScheduleSlot { Time="02:30",Days=[DayOfWeek.Sunday] }],Items=[new ScheduleItem { TaskId=t1.Id,Days=[DayOfWeek.Sunday] }] });var speng=new RunnerEngine(spring,()=>{});var sps=new Scheduler(spring,speng,()=>{});sps.Tick(new DateTimeOffset(2026,3,8,6,59,50,TimeSpan.Zero),eastern);sps.Tick(new DateTimeOffset(2026,3,8,7,0,5,TimeSpan.Zero),eastern);Check(spring.History.Count==0 && !speng.Running,"nonexistent daylight saving minute is not invented");
        var z=NewStore("schedule-zone");var ze=new RunnerEngine(z,()=>{});var zs=new Scheduler(z,ze,()=>{});zs.Tick(before,utc);zs.Tick(at,eastern);Check(z.History.Any(h=>h.Events.Any(x=>x.Contains("时区已变化"))) && !ze.Running,"timezone change records and avoids automatic catchup");
    }
    static async Task StartupAndIntervalTests()
    {
        var utc=TimeZoneInfo.Utc; var monday=new DateTimeOffset(2026,11,16,8,59,50,TimeSpan.Zero);
        var store=NewStore("startup-modes");var one=Spec("startup-one","echo one");var two=Spec("startup-two","echo two");store.Tasks.Add(one);store.Tasks.Add(two);
        var always=new SchedulePlan { Name="always",Enabled=false,StartupMode="always",Items=[new ScheduleItem { TaskId=one.Id,Days=[DayOfWeek.Monday] }] };
        var daily=new SchedulePlan { Name="daily",Enabled=false,StartupMode="daily",Items=[new ScheduleItem { TaskId=two.Id,Days=[DayOfWeek.Monday,DayOfWeek.Tuesday] }] };
        var never=new SchedulePlan { Name="never",StartupMode="never",Items=[new ScheduleItem { TaskId=one.Id,Days=[DayOfWeek.Monday] }] };store.Plans.AddRange([always,daily,never]);
        var engine=new RunnerEngine(store,()=>{});await new StartupCoordinator(store,engine,()=>{}).StartAsync(monday,utc);
        var runs=store.History.Where(h=>h.Trigger=="启动计划").ToList();Check(runs.Count==2 && runs[0].PlanName=="always" && runs[1].PlanName=="daily" && store.StartupDates[daily.Id]=="2026-11-16", "startup modes order and independent disabled timer");
        await new StartupCoordinator(store,engine,()=>{}).StartAsync(monday.AddHours(1),utc);Check(store.History.Count(h=>h.Trigger=="启动计划" && h.PlanName=="always")==2 && store.History.Count(h=>h.Trigger=="启动计划" && h.PlanName=="daily")==1 && store.History.Any(h=>h.Events.Any(x=>x.Contains("今天已经触发"))),"daily startup once and always every launch");
        await new StartupCoordinator(store,engine,()=>{}).StartAsync(monday.AddDays(1),utc);Check(store.History.Count(h=>h.Trigger=="启动计划" && h.PlanName=="daily")==2 && store.History.Any(h=>h.Events.Any(x=>x.Contains("当天没有任务"))),"startup local date and weekday filter");
        int dailyBefore=store.History.Count(h=>h.Trigger=="启动计划" && h.PlanName=="daily");await new StartupCoordinator(store,engine,()=>{}).StartAsync(monday,utc);Check(store.History.Count(h=>h.Trigger=="启动计划" && h.PlanName=="daily")==dailyBefore && store.History.Any(h=>h.Events.Any(x=>x.Contains("日期回退"))),"daily startup does not repeat after local date rollback");
        var fail=NewStore("startup-save-fail");fail.Tasks.Add(one.Copy());fail.Plans.Add(always);fail.Save();await File.WriteAllTextAsync(Path.Combine(root,"startup-save-fail","data.json"),"other");var fe=new RunnerEngine(fail,()=>{});var fc=new StartupCoordinator(fail,fe,()=>{});await fc.StartAsync(monday,utc);Check(fc.Blocked && !fe.Running && fail.History.Count==0,"startup fails closed when queue reservation cannot save");
        var interrupted=NewStore("startup-interrupted");interrupted.Tasks.Add(one.Copy());interrupted.Plans.Add(always);interrupted.History.Add(new RunRecord { Trigger="启动队列",State="运行中" });interrupted.Save();var ir=NewStore("startup-interrupted");ir.Load();var ie=new RunnerEngine(ir,()=>{});var ic=new StartupCoordinator(ir,ie,()=>{});await ic.StartAsync(monday,utc);Check(ie.NeedsRecovery && !ie.Running && ir.History.Any(h=>h.Trigger=="启动队列" && h.State=="已跳过"),"interrupted startup requires recovery and does not rerun");
        var gap=NewStore("interval");gap.Tasks.Add(one.Copy());gap.Tasks.Add(two.Copy());var ge=new RunnerEngine(gap,()=>{});await ge.StartTasks(gap.Tasks.ToList(),null,[1,20]);var ev=gap.History.Single().Events;Check(ev.Count(x=>x.Contains("等待 1 秒"))==1 && !ev.Any(x=>x.Contains("等待 20 秒")) && ev.FindIndex(x=>x.Contains("间隔结束"))<ev.FindIndex(x=>x.Contains("已启动 startup-two")),"interval occurs before next task and not after last");
        var autoGap=NewStore("startup-interval");autoGap.Tasks.Add(one.Copy());autoGap.Tasks.Add(two.Copy());autoGap.Plans.Add(new SchedulePlan { Name="auto-gap",StartupMode="always",Items=[new ScheduleItem { TaskId=one.Id,Days=[DayOfWeek.Monday],IntervalSeconds=1 },new ScheduleItem { TaskId=two.Id,Days=[DayOfWeek.Monday] }] });var ae=new RunnerEngine(autoGap,()=>{});await new StartupCoordinator(autoGap,ae,()=>{}).StartAsync(monday,utc);Check(autoGap.History.Single(h=>h.Trigger=="启动计划").Events.Any(x=>x.Contains("等待 1 秒")),"startup plan applies per-item interval");
        var timedGap=NewStore("scheduled-interval");timedGap.Tasks.Add(one.Copy());timedGap.Tasks.Add(two.Copy());timedGap.Plans.Add(new SchedulePlan { Name="timed-gap",Enabled=true,EffectiveFrom=monday.AddDays(-1),Slots=[new ScheduleSlot { Time="09:00",Days=[DayOfWeek.Monday] }],Items=[new ScheduleItem { TaskId=one.Id,Days=[DayOfWeek.Monday],IntervalSeconds=1 },new ScheduleItem { TaskId=two.Id,Days=[DayOfWeek.Monday] }] });var tge=new RunnerEngine(timedGap,()=>{});var tgs=new Scheduler(timedGap,tge,()=>{});tgs.Tick(monday,utc);tgs.Tick(monday.AddSeconds(15),utc);for(int i=0;i<100 && tge.Running;i++) await Task.Delay(50);Check(timedGap.History.Single().Events.Any(x=>x.Contains("等待 1 秒")),"scheduled plan applies per-item interval");
        var stop=NewStore("interval-stop");stop.Tasks.Add(one.Copy());stop.Tasks.Add(two.Copy());var ste=new RunnerEngine(stop,()=>{});var st=ste.StartTasks(stop.Tasks.ToList(),null,[20,0]);for(int i=0;i<100 && !ste.Status.Contains("剩余");i++) await Task.Delay(50);var sw=Stopwatch.StartNew();ste.Stop();await st;Check(sw.Elapsed.TotalSeconds<2 && !stop.History.Single().Events.Any(x=>x.Contains("已启动 startup-two")),"stop interrupts interval promptly");
        var failure=NewStore("interval-failure");var bad=Spec("failed-item","exit /b 3");bad.ContinueOnFailure=true;failure.Tasks.Add(bad);failure.Tasks.Add(two.Copy());var fengine=new RunnerEngine(failure,()=>{});await fengine.StartTasks(failure.Tasks.ToList(),null,[1,0]);Check(failure.History.Single().Events.Any(x=>x.Contains("等待 1 秒")) && failure.History.Single().Events.Any(x=>x.Contains("startup-two 成功")),"configured failure continuation keeps interval");
        var pause=NewStore("interval-pause");var cautious=one.Copy();cautious.TrustCompletion=false;pause.Tasks.Add(cautious);pause.Tasks.Add(two.Copy());var pe=new RunnerEngine(pause,()=>{});var pr=pe.StartTasks(pause.Tasks.ToList(),null,[1,0]);for(int i=0;i<100 && !pe.Waiting;i++) await Task.Delay(50);bool noWaitYet=!pause.History.Single().Events.Any(x=>x.Contains("等待 1 秒"));pe.Continue();await pr;Check(noWaitYet && pause.History.Single().Events.Any(x=>x.Contains("等待 1 秒")),"manual pause resolved before interval starts");
        var startup=NewStore("startup-stop");var slow=new TaskSpec { Name="slow",Program=Environment.ProcessPath!,Arguments="fixture-wait",WorkingDirectory=AppContext.BaseDirectory };startup.Tasks.Add(slow);startup.Tasks.Add(two.Copy());startup.Plans.Add(new SchedulePlan { Name="first",StartupMode="always",Items=[new ScheduleItem { TaskId=slow.Id,Days=[DayOfWeek.Monday] }] });startup.Plans.Add(new SchedulePlan { Name="second",StartupMode="always",Items=[new ScheduleItem { TaskId=two.Id,Days=[DayOfWeek.Monday] }] });var se=new RunnerEngine(startup,()=>{});var sc=new StartupCoordinator(startup,se,()=>{});var sr=sc.StartAsync(monday,utc);for(int i=0;i<100 && !se.Running;i++) await Task.Delay(30);sc.Stop();await sr;Check(startup.History.Any(h=>h.Trigger=="启动队列" && h.State=="已停止") && !startup.History.Any(h=>h.PlanName=="second" && h.Trigger=="启动计划"),"stop cancels pending startup plans");
        var both=NewStore("startup-schedule-busy");both.Tasks.Add(slow.Copy());var bp=new SchedulePlan { Name="combined",Enabled=true,StartupMode="always",EffectiveFrom=monday.AddDays(-1),Slots=[new ScheduleSlot { Time="09:00",Days=[DayOfWeek.Monday] }],Items=[new ScheduleItem { TaskId=slow.Id,Days=[DayOfWeek.Monday] }] };both.Plans.Add(bp);var be=new RunnerEngine(both,()=>{});var bc=new StartupCoordinator(both,be,()=>{});var bs=new Scheduler(both,be,()=>{},()=>bc.IsActive || bc.Blocked);bs.Tick(monday,utc);var bt=bc.StartAsync(monday,utc);bs.Tick(monday.AddSeconds(15),utc);Check(both.History.Any(h=>h.Trigger=="定时" && h.State=="已跳过"),"scheduled slot skips while startup queue is active");bc.Stop();await bt;
        var old=NewStore("schedule-basic");old.Load();Check(old.Plans.All(p=>p.StartupMode=="never" && p.CompletionAction=="none" && p.CompletionDelayMinutes==0 && p.Items.All(i=>i.IntervalSeconds==0)),"old schedule defaults disable startup interval and completion action");
    }
    static async Task CompletionTests()
    {
        var legacyPlan=NewStore("completion-legacy");string legacyJson="{\"Format\":\"SequentialRunnerV3/1\",\"Tasks\":[{\"Id\":\"old-task\"}],\"History\":[],\"Plans\":[{\"Id\":\"old-plan\",\"Items\":[{\"TaskId\":\"old-task\",\"Days\":[1]}],\"Slots\":[]}]}";await File.WriteAllTextAsync(Path.Combine(root,"completion-legacy","data.json"),legacyJson);legacyPlan.Load();Check(legacyPlan.Plans.Single().CompletionAction=="none" && legacyPlan.Plans.Single().CompletionDelayMinutes==0,"legacy plan with missing fields defaults to no completion action");
        var goodPlan=new SchedulePlan { Name="successful",CompletionAction="exit",CompletionDelayMinutes=0 };
        var goodRun=new RunRecord { State="已结束",AllSucceeded=true }; int calls=0; var clock=new FakeClock();
        Task Fast(TimeSpan t,CancellationToken ct) { ct.ThrowIfCancellationRequested();clock.Seconds+=(long)Math.Ceiling(t.TotalSeconds);return Task.CompletedTask; }
        var success=NewStore("completion-success");var cc=new CompletionCoordinator(success,()=>{},_=>{calls++;return Task.FromResult("系统已接受请求；设备实际状态未核实");},clock,Fast);cc.Offer(goodPlan,goodRun);
        Check(calls==1 && clock.Seconds==60 && success.History.Single().State=="操作已请求" && success.History.Single().Events.Any(x=>x.Contains("实际状态未核实")),"successful completion waits 60 seconds and records request separately");
        var failed=NewStore("completion-failure");int failureCalls=0;var fc=new CompletionCoordinator(failed,()=>{},_=>{failureCalls++;return Task.FromResult("unexpected");});fc.Offer(goodPlan,new RunRecord { State="已结束",AllSucceeded=false });fc.Offer(goodPlan,new RunRecord { State="已停止",AllSucceeded=false });fc.Offer(goodPlan,new RunRecord { State="已结束" });Check(failureCalls==0 && failed.History.Count(x=>x.State=="操作未执行")==3,"failure stopped and unknown results do not dispatch");
        var conflict=NewStore("completion-conflict");var xc=new CompletionCoordinator(conflict,()=>{},_=>Task.FromResult("unexpected"));xc.OfferBatch([(goodPlan,goodRun),(new SchedulePlan { Name="second",CompletionAction="restart" },goodRun)],true,"批次");Check(conflict.History.Single().State=="操作未执行" && conflict.History.Single().Events.Any(x=>x.Contains("冲突")),"conflicting startup actions are not executed");
        var merged=NewStore("completion-merged");var mcClock=new FakeClock();int mergedCalls=0;Task Advance(TimeSpan t,CancellationToken ct) { ct.ThrowIfCancellationRequested();mcClock.Seconds+=(long)Math.Ceiling(t.TotalSeconds);return Task.CompletedTask; }var mc=new CompletionCoordinator(merged,()=>{},_=>{mergedCalls++;return Task.FromResult("已接受");},mcClock,Advance);mc.OfferBatch([(new SchedulePlan { CompletionAction="sleep",CompletionDelayMinutes=1 },goodRun),(new SchedulePlan { CompletionAction="sleep",CompletionDelayMinutes=2 },goodRun)],true,"批次");Check(mergedCalls==1 && mcClock.Seconds==180 && merged.History.Single().Events.Any(x=>x.Contains("额外等待 2 分钟")),"matching startup actions use longest delay and one request");
        var cancelled=NewStore("completion-cancel");int cancelCalls=0;var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var cclock=new FakeClock();var cancel=new CompletionCoordinator(cancelled,()=>{},_=>{cancelCalls++;return Task.FromResult("unexpected");},cclock,(t,ct)=>gate.Task.WaitAsync(ct));cancel.Offer(new SchedulePlan { CompletionAction="shutdown",CompletionDelayMinutes=1 },goodRun);bool wasDelay=cancel.Pending && cancel.Status.Contains("额外等待");bool cancelledOk=cancel.Cancel("用户取消");await Task.Delay(30);Check(wasDelay && cancelledOk && cancelCalls==0 && cancelled.History.Single().State=="操作已取消","extra delay is cancellable and persisted");
        var countdown=NewStore("completion-countdown");var gate2=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var cd=new CompletionCoordinator(countdown,()=>{},_=>{cancelCalls++;return Task.FromResult("unexpected");},new FakeClock(),(t,ct)=>gate2.Task.WaitAsync(ct));cd.Offer(goodPlan,goodRun);bool wasCountdown=cd.Status.Contains("倒计时");cd.Cancel("用户停止");await Task.Delay(30);Check(wasCountdown && cancelCalls==0 && countdown.History.Single().Events.Any(x=>x.Contains("用户停止")),"countdown is cancellable before dispatch");
        var saveFail=NewStore("completion-save-fail");saveFail.Save();await File.WriteAllTextAsync(Path.Combine(root,"completion-save-fail","data.json"),"external");int blockedCalls=0;var cf=new CompletionCoordinator(saveFail,()=>{},_=>{blockedCalls++;return Task.FromResult("unexpected");});cf.Offer(goodPlan,goodRun);Check(cf.Blocked && blockedCalls==0 && !cf.Cancel("新运行"),"failed reservation save blocks action and new runs");
        var cancelFail=NewStore("completion-cancel-fail");var gate3=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var cfail=new CompletionCoordinator(cancelFail,()=>{},_=>{blockedCalls++;return Task.FromResult("unexpected");},new FakeClock(),(t,ct)=>gate3.Task.WaitAsync(ct));cfail.Offer(goodPlan,goodRun);await File.WriteAllTextAsync(Path.Combine(root,"completion-cancel-fail","data.json"),"external");Check(!cfail.Cancel("新运行") && cfail.Blocked && blockedCalls==0,"cancellation save failure blocks subsequent starts");
        var interrupted=NewStore("completion-interrupted");interrupted.History.Add(new RunRecord { Trigger="完成后操作",State="操作等待" });interrupted.History.Add(new RunRecord { Trigger="完成后操作",State="操作请求中" });interrupted.Save();var loaded=NewStore("completion-interrupted");loaded.Load();Check(loaded.History[0].State=="操作已取消" && loaded.History[1].State=="操作结果不明","restart cancels pending and never replays uncertain request");
        var real=NewStore("completion-engine-result");var first=Spec("success-item","echo yes");var bad=Spec("bad-item","exit /b 2");bad.ContinueOnFailure=true;real.Tasks.Add(first);real.Tasks.Add(bad);var re=new RunnerEngine(real,()=>{});await re.Start();Check(real.History.Single().State=="已结束" && real.History.Single().AllSucceeded==false,"failure continued does not count as all successful");
        var skip=NewStore("completion-engine-skip");var untrusted=Spec("needs-review","echo done");untrusted.TrustCompletion=false;skip.Tasks.Add(untrusted);var sk=new RunnerEngine(skip,()=>{});var skTask=sk.Start();for(int i=0;i<100 && !sk.Waiting;i++) await Task.Delay(50);sk.Continue();await skTask;Check(skip.History.Single().AllSucceeded==false,"manual skip cannot trigger successful completion");
        var stopped=NewStore("completion-engine-stop");stopped.Tasks.Add(new TaskSpec { Name="wait",Program=Environment.ProcessPath!,Arguments="fixture-wait",WorkingDirectory=AppContext.BaseDirectory });var ste=new RunnerEngine(stopped,()=>{});var st=ste.Start();ste.Stop();await st;Check(stopped.History.Single().AllSucceeded==false,"stopped run cannot trigger successful completion");
        var planned=NewStore("completion-scheduled");var task=Spec("scheduled","echo yes");planned.Tasks.Add(task);var start=new DateTimeOffset(2026,11,16,8,59,50,TimeSpan.Zero);planned.Plans.Add(new SchedulePlan { Name="plan",Enabled=true,EffectiveFrom=start.AddDays(-1),CompletionAction="exit",Slots=[new ScheduleSlot { Time="09:00",Days=[DayOfWeek.Monday] }],Items=[new ScheduleItem { TaskId=task.Id,Days=[DayOfWeek.Monday] }] });int plannedCalls=0;var pclock=new FakeClock();Task PDelay(TimeSpan t,CancellationToken ct) { ct.ThrowIfCancellationRequested();pclock.Seconds+=(long)Math.Ceiling(t.TotalSeconds);return Task.CompletedTask; }var pc=new CompletionCoordinator(planned,()=>{},_=>{plannedCalls++;return Task.FromResult("已接受");},pclock,PDelay);var pe=new RunnerEngine(planned,()=>{});var ps=new Scheduler(planned,pe,()=>{},null,pc);ps.Tick(start,TimeZoneInfo.Utc);ps.Tick(start.AddSeconds(15),TimeZoneInfo.Utc);for(int i=0;i<100 && (pe.Running || plannedCalls==0);i++) await Task.Delay(50);Check(plannedCalls==1 && planned.History.Any(h=>h.Trigger=="定时" && h.AllSucceeded==true),"scheduled plan offers completion only after success");
        var lateFail=NewStore("completion-pre-dispatch-save");var lateClock=new FakeClock();int lateCalls=0;bool corrupted=false;Task LateDelay(TimeSpan t,CancellationToken ct) { lateClock.Seconds+=(long)Math.Ceiling(t.TotalSeconds);if(!corrupted) { File.WriteAllText(Path.Combine(root,"completion-pre-dispatch-save","data.json"),"external");corrupted=true; } return Task.CompletedTask; }var lc=new CompletionCoordinator(lateFail,()=>{},_=>{lateCalls++;return Task.FromResult("unexpected");},lateClock,LateDelay);lc.Offer(goodPlan,goodRun);Check(lc.Blocked && lateCalls==0 && lateFail.History.Single().State=="操作未执行","save failure immediately before dispatch prevents any system request");
        var postFail=NewStore("completion-post-dispatch");var pfClock=new FakeClock();Task PFDelay(TimeSpan t,CancellationToken ct) { pfClock.Seconds+=(long)Math.Ceiling(t.TotalSeconds);return Task.CompletedTask; }var pf=new CompletionCoordinator(postFail,()=>{},_=>{File.WriteAllText(Path.Combine(root,"completion-post-dispatch","data.json"),"external");return Task.FromResult("已接受");},pfClock,PFDelay);pf.Offer(goodPlan,goodRun);Check(pf.Blocked && postFail.History.Single().State=="操作失败或结果不明" && postFail.History.Single().Events.Any(x=>x.Contains("可能已发出")),"save failure after request never claims action was not sent");
        var executing=NewStore("completion-executing");var eclock=new FakeClock();var response=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);Task EDelay(TimeSpan t,CancellationToken ct) { eclock.Seconds+=(long)Math.Ceiling(t.TotalSeconds);return Task.CompletedTask; }var ec=new CompletionCoordinator(executing,()=>{},_=>response.Task,eclock,EDelay);ec.Offer(goodPlan,goodRun);bool cannotCancel=ec.Executing && !ec.Cancel("新运行");response.SetResult("已接受，状态未核实");for(int i=0;i<50 && ec.Executing;i++) await Task.Delay(10);Check(cannotCancel && executing.History.Single().State=="操作已请求","new run blocked once system request has started");
        var jump=NewStore("completion-delay-jump");var jclock=new FakeClock();int jumpCalls=0;Task JDelay(TimeSpan t,CancellationToken ct) { jclock.Seconds+=10;return Task.CompletedTask; }var jc=new CompletionCoordinator(jump,()=>{},_=>{jumpCalls++;return Task.FromResult("unexpected");},jclock,JDelay);jc.Offer(goodPlan,goodRun);Check(jumpCalls==0 && jump.History.Single().State=="操作已取消","interrupted countdown cancels rather than performing action");
        var collision=NewStore("completion-new-schedule");var future=Spec("future","echo yes");collision.Tasks.Add(future);var instant=new DateTimeOffset(2026,11,16,8,59,50,TimeSpan.Zero);collision.Plans.Add(new SchedulePlan { Name="due",Enabled=true,EffectiveFrom=instant.AddDays(-1),Slots=[new ScheduleSlot { Time="09:00",Days=[DayOfWeek.Monday] }],Items=[new ScheduleItem { TaskId=future.Id,Days=[DayOfWeek.Monday] }] });var hold=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var cp=new CompletionCoordinator(collision,()=>{},_=>Task.FromResult("unexpected"),new FakeClock(),(t,ct)=>hold.Task.WaitAsync(ct));var ce=new RunnerEngine(collision,()=>{});var cs=new Scheduler(collision,ce,()=>{},null,cp);cs.Tick(instant,TimeZoneInfo.Utc);cp.Offer(goodPlan,goodRun);cs.Tick(instant.AddSeconds(15),TimeZoneInfo.Utc);for(int i=0;i<100 && ce.Running;i++) await Task.Delay(30);Check(collision.History.Any(h=>h.Trigger=="完成后操作" && h.State=="操作已取消" && h.Events.Any(x=>x.Contains("定时计划到点"))) && collision.History.Any(h=>h.Trigger=="定时" && h.State=="已结束"),"due schedule cancels pending action then runs");
        var multi=NewStore("completion-startup-merge");multi.Tasks.Add(future.Copy());multi.Plans.Add(new SchedulePlan { Name="one",StartupMode="always",CompletionAction="sleep",CompletionDelayMinutes=1,Items=[new ScheduleItem { TaskId=future.Id,Days=[DayOfWeek.Monday] }] });multi.Plans.Add(new SchedulePlan { Name="two",StartupMode="always",CompletionAction="sleep",CompletionDelayMinutes=2,Items=[new ScheduleItem { TaskId=future.Id,Days=[DayOfWeek.Monday] }] });multi.Plans.Add(new SchedulePlan { Name="skipped",StartupMode="always",CompletionAction="restart",Items=[new ScheduleItem { TaskId=future.Id,Days=[DayOfWeek.Tuesday] }] });var miclock=new FakeClock();int microCalls=0;Task MIDelay(TimeSpan t,CancellationToken ct) { miclock.Seconds+=(long)Math.Ceiling(t.TotalSeconds);return Task.CompletedTask; }var mic=new CompletionCoordinator(multi,()=>{},_=>{microCalls++;return Task.FromResult("已接受");},miclock,MIDelay);var mie=new RunnerEngine(multi,()=>{});await new StartupCoordinator(multi,mie,()=>{},mic).StartAsync(instant,TimeZoneInfo.Utc);Check(microCalls==1 && miclock.Seconds==180 && multi.History.Single(h=>h.Trigger=="完成后操作").State=="操作已请求","startup batch combines successful runs and ignores normal weekday skip");
        var unsaved=NewStore("completion-exit-unsaved");var usClock=new FakeClock();Task USDelay(TimeSpan t,CancellationToken ct) { usClock.Seconds+=(long)Math.Ceiling(t.TotalSeconds);return Task.CompletedTask; }var usc=new CompletionCoordinator(unsaved,()=>{},_=>throw new CompletionNotSentException("仍有未保存的配置"),usClock,USDelay);usc.Offer(goodPlan,goodRun);Check(!usc.Blocked && unsaved.History.Single().State=="操作未执行" && unsaved.History.Single().Events.Any(x=>x.Contains("未保存")),"known preflight refusal is not reported as sent or unknown");
        var denied=NewStore("completion-dispatch-fail");var dclock=new FakeClock();Task DDelay(TimeSpan t,CancellationToken ct) { dclock.Seconds+=(long)Math.Ceiling(t.TotalSeconds);return Task.CompletedTask; }var dc=new CompletionCoordinator(denied,()=>{},_=>throw new InvalidOperationException("权限不足"),dclock,DDelay);dc.Offer(goodPlan,goodRun);Check(denied.History.Single().State=="操作失败或结果不明" && denied.History.Single().Events.Any(x=>x.Contains("权限不足")),"system rejection is recorded without fallback");
    }
    static async Task RecoveryTests()
    {
        var s=NewStore("recovery-multiple"); var run=new RunRecord { Trigger="手动计划",PlanName="A",State="中断",Events=["原始运行证据"] }; var action=new RunRecord { Trigger="完成后操作",PlanName="B",State="操作结果不明",Events=["原始请求证据"] }; s.History.AddRange([run,action]); s.Tasks.Add(Spec("blocked","echo blocked")); s.Save();
        var uiEngine=new RunnerEngine(s,()=>{}); var uiCompletion=new CompletionCoordinator(s,()=>{},_=>Task.FromResult("替身")); var window=new RecoveryWindow(s,uiEngine,uiCompletion,()=>{}); window.Show(); var panel=(System.Windows.Controls.DockPanel)window.Content; var list=panel.Children.OfType<System.Windows.Controls.ListBox>().Single(); var detail=panel.Children.OfType<System.Windows.Controls.TextBox>().Single(); Check(list.Items.Count==2 && detail.Text.Contains("原始运行证据") && detail.Text.Contains("可能遗留"),"recovery window displays pending item and process guidance"); list.SelectedIndex=1; Check(detail.Text.Contains("原始请求证据") && detail.Text.Contains("系统操作是否已经发生"),"recovery window shows separate system action guidance"); window.Close();
        var e=new RunnerEngine(s,()=>{}); await e.Start(); Check(e.NeedsRecovery && s.History.Count==2 && Recovery.Pending(s).Count==2,"both interrupted run and unknown action block manual start");
        e.AcknowledgeRecovery(run); Check(e.NeedsRecovery && run.State=="中断" && run.Events.Contains("原始运行证据") && run.RecoveryAcknowledgedAt!=null && action.RecoveryAcknowledgedAt==null,"acknowledgement is per item and preserves original evidence");
        var reloaded=NewStore("recovery-multiple"); reloaded.Load(); var re=new RunnerEngine(reloaded,()=>{}); Check(re.NeedsRecovery && Recovery.Pending(reloaded).Count==1 && reloaded.History[0].RecoveryAcknowledgedAt!=null,"partial acknowledgement survives restart and leaves other item blocked");
        re.AcknowledgeRecovery(reloaded.History[1]); await re.Start(); Check(!re.NeedsRecovery && reloaded.History[1].State=="操作结果不明" && reloaded.History.Last().Events.Any(x=>x.Contains("blocked 成功")),"last acknowledgement unlocks without changing original result");
        var fail=NewStore("recovery-ack-fail");var item=new RunRecord {State="需人工核实",Events=["evidence"]};fail.History.Add(item);fail.Save();var fe=new RunnerEngine(fail,()=>{});using(var file=new FileStream(Path.Combine(root,"recovery-ack-fail","data.json"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { bool threw=false;try { fe.AcknowledgeRecovery(item); } catch(IOException) { threw=true; } Check(threw && fe.NeedsRecovery && item.RecoveryAcknowledgedAt==null && item.Events.Count==1,"failed acknowledgement save rolls back and keeps blocking"); }
        var reopen=NewStore("recovery-ack-fail");reopen.Load();Check(new RunnerEngine(reopen,()=>{}).NeedsRecovery && Recovery.Pending(reopen).Count==1,"failed acknowledgement remains blocked after restart");
        var now=new DateTimeOffset(2026,11,16,8,59,50,TimeSpan.Zero);var timed=NewStore("recovery-schedule");var task=Spec("must-not-run","echo no");timed.Tasks.Add(task);timed.History.Add(new RunRecord { Trigger="完成后操作",State="操作结果不明" });timed.Plans.Add(new SchedulePlan {Name="timed",Enabled=true,StartupMode="always",EffectiveFrom=now.AddDays(-1),Slots=[new ScheduleSlot {Time="09:00",Days=[DayOfWeek.Monday]}],Items=[new ScheduleItem {TaskId=task.Id,Days=[DayOfWeek.Monday]}]});var te=new RunnerEngine(timed,()=>{});var ts=new Scheduler(timed,te,()=>{});ts.Tick(now,TimeZoneInfo.Utc);ts.Tick(now.AddSeconds(15),TimeZoneInfo.Utc);Check(timed.History.Any(x=>x.Trigger=="定时" && x.State=="已跳过" && x.Events.Any(y=>y.Contains("待核实"))) && !te.Running,"unknown system action makes timed trigger skip");await new StartupCoordinator(timed,te,()=>{}).StartAsync(now,TimeZoneInfo.Utc);Check(timed.History.Any(x=>x.Trigger=="启动队列" && x.State=="已跳过") && !timed.History.Any(x=>x.Trigger=="启动计划"),"unknown system action blocks startup plans");
        var oldError=NewStore("recovery-legacy-exception");oldError.History.Add(new RunRecord { State="异常",Events=["旧版未知异常"] });oldError.Save();var oldReload=NewStore("recovery-legacy-exception");oldReload.Load();Check(new RunnerEngine(oldReload,()=>{}).NeedsRecovery && Recovery.Pending(oldReload).Single().State=="异常","legacy exceptional state remains visible and blocks until reviewed");
        var pre=NewStore("recovery-request-boundary");pre.History.Add(new RunRecord {Trigger="完成后操作",State="操作等待"});pre.History.Add(new RunRecord {Trigger="完成后操作",State="操作请求中"});pre.Save();var post=NewStore("recovery-request-boundary");post.Load();Check(post.History[0].State=="操作已取消" && post.History[1].State=="操作结果不明" && Recovery.Pending(post).Count==1,"restart distinguishes cancelled wait from unknown dispatched request");
        var runtime=NewStore("recovery-dispatch-failure");var fc=new FakeClock();Task Delay(TimeSpan t,CancellationToken ct){fc.Seconds+=(long)Math.Ceiling(t.TotalSeconds);return Task.CompletedTask;} var cc=new CompletionCoordinator(runtime,()=>{},_=>throw new InvalidOperationException("unknown"),fc,Delay);cc.Offer(new SchedulePlan {CompletionAction="sleep"},new RunRecord {State="已结束",AllSucceeded=true});var rr=new RunnerEngine(runtime,()=>{});Check(cc.Blocked && rr.NeedsRecovery,"runtime unknown system request is recoverable and blocks starts");rr.AcknowledgeRecovery(Recovery.Pending(runtime).Single());cc.RecoveryAcknowledged();Check(!rr.NeedsRecovery && !cc.Blocked && runtime.History.Single().State=="操作失败或结果不明","verified runtime unknown can clear its own block without replay");
    }
    static async Task ManualSkipTests()
    {
        var store=NewStore("manual-skip-repro");var first=Spec("manual", "echo done");first.Completion="人工确认";store.Tasks.Add(first);store.Tasks.Add(Spec("next", "echo next"));var engine=new RunnerEngine(store,()=>{});var run=engine.Start();
        for(int i=0;i<150 && !engine.Status.Contains("等待人工确认");i++)await Task.Delay(30);
        bool waited=engine.Status.Contains("等待人工确认");engine.Continue();
        var completed=await Task.WhenAny(run,Task.Delay(2000))==run;
        if(!completed){engine.Stop();await run;}
        var history=store.History.Single();Check(waited && completed && history.Events.Any(x=>x.Contains("未判定成功")) && history.Events.Any(x=>x.Contains("已启动 next")) && history.AllSucceeded==false,"manual wait skip records non-success and advances");
        var last=NewStore("manual-skip-last");var lastTask=Spec("last", "echo done");lastTask.Completion="人工确认";last.Tasks.Add(lastTask);var le=new RunnerEngine(last,()=>{});var lr=le.Start();for(int i=0;i<150 && !le.Status.Contains("等待人工确认");i++)await Task.Delay(30);le.Continue();le.Continue();bool lastDone=await Task.WhenAny(lr,Task.Delay(3000))==lr;if(!lastDone){le.Stop();await lr;}Check(lastDone && last.History.Single().State=="已结束" && last.History.Single().AllSucceeded==false && last.History.Single().Events.Count(x=>x.Contains("未判定成功"))==1,"last manual task skips once without success");
        var early=NewStore("manual-skip-early");early.Tasks.Add(new TaskSpec{Name="slow-manual",Program=Environment.ProcessPath!,Arguments="fixture-wait",WorkingDirectory=AppContext.BaseDirectory,Completion="人工确认"});early.Tasks.Add(Spec("after-slow","echo next"));var ee=new RunnerEngine(early,()=>{});var er=ee.Start();for(int i=0;i<100 && !ee.Status.Contains("已启动 slow-manual");i++)await Task.Delay(20);ee.Continue();await Task.Delay(300);bool stillRunning=ee.Running && !early.History.Single().Events.Any(x=>x.Contains("已启动 after-slow"));for(int i=0;i<150 && !ee.Status.Contains("等待人工确认");i++)await Task.Delay(30);bool noCached=ee.Status.Contains("等待人工确认") && !early.History.Single().Events.Any(x=>x.Contains("已启动 after-slow"));ee.Continue();bool earlyDone=await Task.WhenAny(er,Task.Delay(3000))==er;if(!earlyDone){ee.Stop();await er;}Check(stillRunning && noCached && earlyDone && early.History.Single().Events.Any(x=>x.Contains("已启动 after-slow")),"skip while process runs is not cached");
        var confirmedEarly=NewStore("manual-confirm-early");confirmedEarly.Tasks.Add(new TaskSpec{Name="slow-manual",Program=Environment.ProcessPath!,Arguments="fixture-wait",WorkingDirectory=AppContext.BaseDirectory,Completion="人工确认"});confirmedEarly.Tasks.Add(Spec("after-slow","echo next"));var earlyConfirmEngine=new RunnerEngine(confirmedEarly,()=>{});var earlyConfirmRun=earlyConfirmEngine.Start();for(int i=0;i<100 && !earlyConfirmEngine.Status.Contains("已启动 slow-manual");i++)await Task.Delay(20);earlyConfirmEngine.Confirm();await Task.Delay(300);bool confirmedButWaiting=earlyConfirmEngine.Running && !confirmedEarly.History.Single().Events.Any(x=>x.Contains("已启动 after-slow"));await earlyConfirmRun;Check(confirmedButWaiting && confirmedEarly.History.Single().AllSucceeded==true && confirmedEarly.History.Single().Events.Any(x=>x.Contains("仍需等待程序退出")),"confirmation while running waits for process exit");
        var directStop=NewStore("manual-wait-stop");var directStopTask=Spec("manual", "echo done");directStopTask.Completion="人工确认";directStop.Tasks.Add(directStopTask);directStop.Tasks.Add(Spec("never", "echo no"));var dse=new RunnerEngine(directStop,()=>{});var dsr=dse.Start();for(int i=0;i<150 && !dse.Status.Contains("等待人工确认");i++)await Task.Delay(30);dse.Stop();await dsr;Check(directStop.History.Single().State=="已停止" && !directStop.History.Single().Events.Any(x=>x.Contains("已启动 never")),"stop directly from manual wait prevents next task");
        var stopped=NewStore("manual-skip-stop");var stopTask=Spec("manual", "echo done");stopTask.Completion="人工确认";stopped.Tasks.Add(stopTask);stopped.Tasks.Add(Spec("never", "echo no"));var se=new RunnerEngine(stopped,()=>{});var sr=se.Start();for(int i=0;i<150 && !se.Status.Contains("等待人工确认");i++)await Task.Delay(30);se.Continue();se.Stop();se.Confirm();await sr;Check(stopped.History.Single().State=="已停止" && stopped.History.Single().AllSucceeded==false && !stopped.History.Single().Events.Any(x=>x.Contains("已启动 never")),"stop overrides a just-requested skip");
        var chosen=NewStore("manual-skip-choice");var chosenTask=Spec("manual", "echo done");chosenTask.Completion="人工确认";chosen.Tasks.Add(chosenTask);var ce=new RunnerEngine(chosen,()=>{});var cr=ce.Start();for(int i=0;i<150 && !ce.Status.Contains("等待人工确认");i++)await Task.Delay(30);ce.Continue();ce.Confirm();await cr;Check(chosen.History.Single().Events.Count(x=>x.Contains("未判定成功"))==1 && !chosen.History.Single().Events.Any(x=>x.Contains("manual 成功")) && !chosen.History.Single().Events.Any(x=>x.Contains("已确认当前任务业务完成")),"skip excludes subsequent confirmation");
        var confirm=NewStore("manual-confirm-choice");var confirmTask=Spec("manual", "echo done");confirmTask.Completion="人工确认";confirm.Tasks.Add(confirmTask);var co=new RunnerEngine(confirm,()=>{});var cor=co.Start();for(int i=0;i<150 && !co.Status.Contains("等待人工确认");i++)await Task.Delay(30);co.Confirm();co.Continue();await cor;Check(confirm.History.Single().AllSucceeded==true && !confirm.History.Single().Events.Any(x=>x.Contains("未判定成功")) && !confirm.History.Single().Events.Any(x=>x.Contains("仍需等待程序退出")),"confirmation after exit excludes skip and stale waiting text");
        var failed=NewStore("manual-skip-save-fail");var failedTask=Spec("manual", "echo done");failedTask.Completion="人工确认";failed.Tasks.Add(failedTask);failed.Tasks.Add(Spec("never", "echo no"));var fe=new RunnerEngine(failed,()=>{});var fr=fe.Start();for(int i=0;i<150 && !fe.Status.Contains("等待人工确认");i++)await Task.Delay(30);using(var file=new FileStream(Path.Combine(root,"manual-skip-save-fail","data.json"),FileMode.Open,FileAccess.ReadWrite,FileShare.None)){fe.Continue();await fr;}Check(failed.History.Single().State=="需人工核实" && failed.History.Single().AllSucceeded==false && !failed.History.Single().Events.Any(x=>x.Contains("已启动 never")),"failed skip save prevents next task");
        var interval=NewStore("manual-skip-interval");var intervalTask=Spec("manual", "echo done");intervalTask.Completion="人工确认";interval.Tasks.Add(intervalTask);interval.Tasks.Add(Spec("next", "echo next"));var ie=new RunnerEngine(interval,()=>{});var ir=ie.StartTasks(interval.Tasks.ToList(),null,[1,0]);for(int i=0;i<150 && !ie.Status.Contains("等待人工确认");i++)await Task.Delay(30);ie.Continue();await ir;Check(interval.History.Single().Events.Any(x=>x.Contains("等待 1 秒")) && interval.History.Single().AllSucceeded==false,"manual skip respects plan interval");
        int calls=0;var actions=NewStore("manual-skip-action");var coordinator=new CompletionCoordinator(actions,()=>{},_=>{calls++;return Task.FromResult("unexpected");});coordinator.Offer(new SchedulePlan{CompletionAction="sleep"},history);Check(calls==0 && actions.History.Any(x=>x.State=="操作未执行"),"manual skip prevents completion action");
    }
    static async Task BoundedOutputTests()
    {
        var lines=new List<string>();var segments=new List<string>();await BoundedOutputReader.ReadAsync(new SmallReader("\r\nabc\rdef\n\nghi",1), segments.Add, lines.Add, 4);
        Check(lines.SequenceEqual(new[]{"", "abc", "def", "", "ghi"}), "bounded reader preserves CR LF CRLF empty and EOF lines");
        var longLines=new List<string>();bool found=false;int observedMax=0;string huge=new string('x',100000)+"REA"+"DY";
        await BoundedOutputReader.ReadAsync(new SmallReader(huge,3), x=>{observedMax=Math.Max(observedMax,x.Length);found|=x.Contains("READY",StringComparison.Ordinal);},longLines.Add,4);
        Check(found && observedMax<=7 && longLines.Count==1 && longLines[0].Length==4097 && longLines[0].EndsWith("…"),"bounded reader finds split signal and limits long line prefix");
        string maxSignal=new string('A',4095)+"Z";bool maxFound=false;int maxWindow=0;await BoundedOutputReader.ReadAsync(new SmallReader("prefix"+maxSignal,17),x=>{maxFound|=x.Contains(maxSignal,StringComparison.Ordinal);maxWindow=Math.Max(maxWindow,x.Length);},_=>{},4095);Check(maxFound && maxWindow<=8191,"maximum length success signal survives many chunk boundaries");
        bool crossed=false;await BoundedOutputReader.ReadAsync(new SmallReader("REA\nDY",1),x=>crossed|=x.Contains("READY",StringComparison.Ordinal),_=>{},4);Check(!crossed,"bounded reader never joins signals across lines");
        foreach(int size in new[]{16777216,33554432})
        {
            var store=NewStore("bounded-"+size);store.Tasks.Add(new TaskSpec{Name="long",Program=Environment.ProcessPath!,Arguments="fixture-long-output "+size,WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",LogSource="程序输出",SuccessText="READY",TrustCompletion=true});
            await new RunnerEngine(store,()=>{}).Start();var result=store.History.Single();var output=result.Events.FirstOrDefault(x=>x.Contains("long 输出："));Check(result.AllSucceeded==true && result.Events.Any(x=>x.Contains("long 成功")) && output!=null && output.EndsWith("…") && output.Length<4200,"long unbroken output has bounded record and final signal "+size);
        }
        var utf=NewStore("bounded-utf8");utf.Tasks.Add(new TaskSpec{Name="utf",Program=Environment.ProcessPath!,Arguments="fixture-split-utf8",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",SuccessText="完成",TrustCompletion=true});await new RunnerEngine(utf,()=>{}).Start();Check(utf.History.Single().AllSucceeded==true,"split UTF-8 stdout retains success evidence");
        var split=NewStore("bounded-stream-split");split.Tasks.Add(new TaskSpec{Name="split",Program=Environment.ProcessPath!,Arguments="fixture-stream-split",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",SuccessText="READY",TrustCompletion=true});var se=new RunnerEngine(split,()=>{});var st=se.Start();for(int i=0;i<150 && !se.Waiting;i++)await Task.Delay(30);Check(se.Waiting && se.Status.Contains("未发现"),"stdout and stderr cannot combine into success signal");se.Stop();await st;
        var stderr=NewStore("bounded-stderr-only");stderr.Tasks.Add(new TaskSpec{Name="stderr",Program=Environment.ProcessPath!,Arguments="fixture-stderr-ready",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",SuccessText="READY",TrustCompletion=true});var ee=new RunnerEngine(stderr,()=>{});var et=ee.Start();for(int i=0;i<150 && !ee.Waiting;i++)await Task.Delay(30);Check(ee.Waiting && ee.Status.Contains("未发现"),"stderr alone cannot satisfy stdout success signal");ee.Stop();await et;
        var update=NewStore("bounded-update");update.Tasks.Add(new TaskSpec{Name="update",Program=Environment.ProcessPath!,Arguments="fixture-split-update",WorkingDirectory=AppContext.BaseDirectory,TrustCompletion=true});var ue=new RunnerEngine(update,()=>{});var ut=ue.Start();for(int i=0;i<150 && !ue.Waiting;i++)await Task.Delay(30);Check(ue.Waiting && update.History.Single().Events.Any(x=>x.Contains("检测到更新或重启")),"update marker split across stdout chunks pauses");ue.Stop();await ut;
        var longUpdate=NewStore("bounded-long-update");longUpdate.Tasks.Add(new TaskSpec{Name="longupdate",Program=Environment.ProcessPath!,Arguments="fixture-long-update",WorkingDirectory=AppContext.BaseDirectory,TrustCompletion=true});var lu=new RunnerEngine(longUpdate,()=>{});var lt=lu.Start();for(int i=0;i<200 && !lu.Waiting;i++)await Task.Delay(30);Check(lu.Waiting && longUpdate.History.Single().Events.Any(x=>x.Contains("检测到更新或重启")),"update marker at end of long line pauses");lu.Stop();await lt;
        var both=NewStore("bounded-both-streams");both.Tasks.Add(new TaskSpec{Name="both",Program=Environment.ProcessPath!,Arguments="fixture-both-streams",WorkingDirectory=AppContext.BaseDirectory,Completion="日志信号",SuccessText="READY",TrustCompletion=true});await new RunnerEngine(both,()=>{}).Start();Check(both.History.Single().AllSucceeded==true && both.History.Single().Events.Any(x=>x.Contains("both 成功")),"simultaneous stdout and stderr drain without losing stdout signal");
        var stop=NewStore("bounded-stop");stop.Tasks.Add(new TaskSpec{Name="longstop",Program=Environment.ProcessPath!,Arguments="fixture-long-wait",WorkingDirectory=AppContext.BaseDirectory,TrustCompletion=true});stop.Tasks.Add(Spec("must-not-start","echo wrong"));var be=new RunnerEngine(stop,()=>{});var bt=be.Start();for(int i=0;i<100 && !be.Status.Contains("已启动 longstop");i++)await Task.Delay(30);be.Stop();await bt;Check(!stop.History.Single().Events.Any(x=>x.Contains("已启动 must-not-start")),"stop during long output prevents later task");
    }
    sealed class SmallReader(string data, int chunk) : StringReader(data)
    {
        public override Task<int> ReadAsync(char[] buffer, int index, int count) => Task.FromResult(Read(buffer,index,Math.Min(count,chunk)));
    }
    static async Task HistoryStorageTests()
    {
        var original=NewStore("stream-json");original.Tasks.Add(Spec("data","echo test"));original.History.Add(new RunRecord {Started=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero),State="已结束",PlanName="plan",Events=["记录","completed"]});original.NoticeSeen=true;original.Save();
        var expected=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Store.StoreData { Tasks=[.. original.Tasks],History=original.History,NoticeSeen=original.NoticeSeen,Plans=original.Plans,ScheduleCursorUtc=original.ScheduleCursorUtc,ScheduleZoneId=original.ScheduleZoneId,StartupDates=original.StartupDates },new System.Text.Json.JsonSerializerOptions {WriteIndented=true});var saved=await File.ReadAllBytesAsync(Path.Combine(root,"stream-json","data.json"));Check(saved.SequenceEqual(expected) && !Encoding.UTF8.GetString(saved).Contains("\"Display\""),"stream serialization preserves existing JSON bytes and fields");
        var reread=NewStore("stream-json");reread.Load();Check(reread.Tasks.Single().Name=="data" && reread.History.Single().Events.SequenceEqual(new[]{"记录","completed"}),"stream saved configuration and full history reload");
        var locked=NewStore("stream-replace-lock");locked.Save();string lockedPath=Path.Combine(root,"stream-replace-lock","data.json");byte[] before=await File.ReadAllBytesAsync(lockedPath);locked.Tasks.Add(Spec("not-saved","echo test"));bool failed=false;using(var held=new FileStream(lockedPath,FileMode.Open,FileAccess.Read,FileShare.Read)){try {locked.Save();} catch(Exception ex) when (ex is IOException or UnauthorizedAccessException){failed=true;}}Check(failed && before.SequenceEqual(File.ReadAllBytes(lockedPath)),"replace failure leaves original file intact");locked.Save();var lockReload=NewStore("stream-replace-lock");lockReload.Load();Check(lockReload.Tasks.Single().Name=="not-saved","save can recover after replace lock released");
        string faultDir=Path.Combine(root,"stream-write-fail");bool inject=false;var faulty=new Store(faultDir,path=>inject ? new FaultWriteStream(path) : new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None));faulty.Tasks.Add(Spec("no-run","echo no"));faulty.Save();string faultPath=Path.Combine(faultDir,"data.json");byte[] old=await File.ReadAllBytesAsync(faultPath);inject=true;bool threw=false;try{faulty.Save();}catch(IOException){threw=true;}Check(threw && old.SequenceEqual(File.ReadAllBytes(faultPath)),"partial temporary write failure preserves original data");var failureEngine=new RunnerEngine(faulty,()=>{});await failureEngine.Start();Check(failureEngine.NeedsRecovery && !faulty.History.Any(h=>h.Events.Any(e=>e.Contains("已启动"))) && old.SequenceEqual(File.ReadAllBytes(faultPath)),"temporary write failure prevents task startup");
        var notified=new RunRecord {State="运行中"};bool changedDisplay=false;notified.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(RunRecord.Display))changedDisplay=true;};notified.State="已结束";Check(changedDisplay && notified.Display.Contains("已结束"),"history state raises display change notification");
        string uiDir=Path.Combine(root,"history-ui");Directory.CreateDirectory(uiDir);var info=new ProcessStartInfo(Environment.ProcessPath!) {UseShellExecute=false};info.ArgumentList.Add("fixture-history-ui");info.ArgumentList.Add(uiDir);using var child=Process.Start(info)!;await child.WaitForExitAsync();string report=Path.Combine(uiDir,"ui-result.json");bool uiOk=child.ExitCode==0 && File.Exists(report) && (await File.ReadAllTextAsync(report))=="ok";Check(uiOk,"history UI and MAA draft defaults save and discard");
    }
    sealed class FaultWriteStream(string path) : FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
    {
        public override void Write(ReadOnlySpan<byte> data) { base.Write(data[..Math.Min(data.Length,128)]); throw new IOException("受控临时写入失败"); }
        public override void Write(byte[] buffer,int offset,int count) { base.Write(buffer,offset,Math.Min(count,128)); throw new IOException("受控临时写入失败"); }
    }
    delegate bool WindowCallback(IntPtr hwnd, IntPtr param);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback, IntPtr param);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, WindowCallback callback, IntPtr param);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumThreadWindows(uint threadId, WindowCallback callback, IntPtr param);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool IsProcessInJob(IntPtr process, IntPtr job, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] out bool result);
    static void AnswerOwnSaveDialog(int answer)
    {
        uint thread=GetCurrentThreadId();var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(50) };
        timer.Tick+=(_,_)=>EnumThreadWindows(thread,(hwnd,_)=>{var title=new StringBuilder(256);GetWindowText(hwnd,title,title.Capacity);if(title.ToString()=="保存配置"){PostMessage(hwnd,0x0111,(IntPtr)answer,IntPtr.Zero);timer.Stop();return false;}return true;},IntPtr.Zero);timer.Start();
    }
    static int HistoryUIFixture(string dir)
    {
        var seed=new Store(dir){NoticeSeen=true};seed.History.Add(new RunRecord {State="已结束",PlanName="one",Events=["old"]});seed.History.Add(new RunRecord {State="已结束",PlanName="two",Events=["other"]});seed.Save();Environment.SetEnvironmentVariable("RUNNER_V3_DATA_DIR",dir);var app=new System.Windows.Application {ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};var window=new MainWindow();app.MainWindow=window;
        _=app.Dispatcher.BeginInvoke(()=>{try {var store=(Store)typeof(MainWindow).GetField("store",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(window)!;var navigate=typeof(MainWindow).GetMethod("Navigate",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;bool navOk=true;window.Width=850;window.Height=600;foreach(var pair in new[]{("Tasks","TasksPage"),("Plans","PlansPage"),("Help","HelpPage"),("Run","RunPage"),("History","HistoryPage")}){navigate.Invoke(window,[new System.Windows.Controls.Button{Tag=pair.Item1},new System.Windows.RoutedEventArgs()]);window.UpdateLayout();navOk &= ((System.Windows.UIElement)window.FindName(pair.Item2)).IsVisible && ((System.Windows.UIElement)window.FindName("Status")).IsVisible;}var list=(System.Windows.Controls.ComboBox)window.FindName("HistoryList");var events=(System.Windows.Controls.TextBox)window.FindName("Events");var refresh=typeof(MainWindow).GetMethod("Refresh",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;list.SelectedIndex=0;var first=store.History[0];var text=new System.Windows.Controls.TextBlock();text.SetBinding(System.Windows.Controls.TextBlock.TextProperty,new System.Windows.Data.Binding("Display"){Source=first});int resets=0;((System.Collections.Specialized.INotifyCollectionChanged)list.Items).CollectionChanged+=(_,e)=>{if(e.Action==System.Collections.Specialized.NotifyCollectionChangedAction.Reset)resets++;};refresh.Invoke(window,null);bool noReset=resets==0;first.State="需人工核实";first.Events.Add("new detail");refresh.Invoke(window,null);window.UpdateLayout();bool visibleState=VisualTextContains(list,"需人工核实");bool live=list.DisplayMemberPath=="Display" && text.Text.Contains("需人工核实") && visibleState && events.Text.Contains("new detail") && ReferenceEquals(list.SelectedItem,first);store.History.Add(new RunRecord {State="已结束",PlanName="three"});refresh.Invoke(window,null);bool added=list.Items.Count==3 && resets>0 && ReferenceEquals(list.SelectedItem,first);navigate.Invoke(window,[new System.Windows.Controls.Button{Tag="Tasks"},new System.Windows.RoutedEventArgs()]);var add=typeof(MainWindow).GetMethod("Add",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;add.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);var name=(System.Windows.Controls.TextBox)window.FindName("TaskNameInput");window.UpdateLayout();name.Text="draft-name";name.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();var draftMarker=(System.Windows.Controls.TextBox)window.FindName("UpdateMarkersInput");draftMarker.Text="discard me";draftMarker.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();AnswerOwnSaveDialog(2);navigate.Invoke(window,[new System.Windows.Controls.Button{Tag="Help"},new System.Windows.RoutedEventArgs()]);var cancelVisibility=((System.Windows.UIElement)window.FindName("TasksPage")).Visibility;string cancelText=name.Text;bool cancelled=cancelVisibility==System.Windows.Visibility.Visible && cancelText=="draft-name";AnswerOwnSaveDialog(7);navigate.Invoke(window,[new System.Windows.Controls.Button{Tag="Help"},new System.Windows.RoutedEventArgs()]);bool discarded=((System.Windows.UIElement)window.FindName("HelpPage")).Visibility==System.Windows.Visibility.Visible && store.Tasks.Count==0;navigate.Invoke(window,[new System.Windows.Controls.Button{Tag="Tasks"},new System.Windows.RoutedEventArgs()]);add.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);window.UpdateLayout();name.Text="saved-name";name.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();var markerInput=(System.Windows.Controls.TextBox)window.FindName("UpdateMarkersInput");markerInput.Text="SavedMarker";markerInput.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();var trackingInput=(System.Windows.Controls.CheckBox)window.FindName("TrackUpdateRestartInput");trackingInput.IsChecked=true;trackingInput.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateSource();AnswerOwnSaveDialog(6);navigate.Invoke(window,[new System.Windows.Controls.Button{Tag="History"},new System.Windows.RoutedEventArgs()]);bool saved=((System.Windows.UIElement)window.FindName("HistoryPage")).Visibility==System.Windows.Visibility.Visible && store.Tasks.Count==1 && store.Tasks[0].Name=="saved-name" && store.Tasks[0].UpdateMarkers=="SavedMarker" && store.Tasks[0].TrackUpdateRestart;navigate.Invoke(window,[new System.Windows.Controls.Button{Tag="Tasks"},new System.Windows.RoutedEventArgs()]);var addMaa=typeof(MainWindow).GetMethod("AddMaa",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;addMaa.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);var taskList=(System.Windows.Controls.ListBox)window.FindName("TaskList");var maa=taskList.SelectedItem as TaskSpec;bool maaDefaults=maa is {Name:"MAA",Completion:"人工确认",Program:"",Arguments:"",LogPath:"",SuccessText:"",TrustCompletion:false,ContinueOnFailure:false,TrackUpdateRestart:false,MaaStartByRunner:false};AnswerOwnSaveDialog(7);navigate.Invoke(window,[new System.Windows.Controls.Button{Tag="Help"},new System.Windows.RoutedEventArgs()]);bool maaDiscard=store.Tasks.Count==1;navigate.Invoke(window,[new System.Windows.Controls.Button{Tag="Tasks"},new System.Windows.RoutedEventArgs()]);addMaa.Invoke(window,[window,new System.Windows.RoutedEventArgs()]);var maaStartInput=(System.Windows.Controls.CheckBox)window.FindName("MaaStartByRunnerInput");maaStartInput.IsChecked=true;maaStartInput.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateSource();AnswerOwnSaveDialog(6);navigate.Invoke(window,[new System.Windows.Controls.Button{Tag="History"},new System.Windows.RoutedEventArgs()]);var reloaded=new Store(dir);reloaded.Load();bool maaSaved=reloaded.Tasks.Count==2 && reloaded.Tasks[0].UpdateMarkers=="SavedMarker" && reloaded.Tasks[0].TrackUpdateRestart && reloaded.Tasks[1] is {Name:"MAA",Completion:"人工确认",Program:"",TrustCompletion:false,ContinueOnFailure:false,MaaStartByRunner:true};File.WriteAllText(Path.Combine(dir,"ui-result.json"),navOk && noReset && live && added && cancelled && discarded && saved && maaDefaults && maaDiscard && maaSaved ? "ok" : $"failed navOk={navOk} noReset={noReset} live={live} added={added} cancelled={cancelled} visibility={cancelVisibility} text={cancelText} discarded={discarded} saved={saved} maaDefaults={maaDefaults} maaDiscard={maaDiscard} maaSaved={maaSaved}");typeof(MainWindow).GetMethod("RequestExit",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(window,[false]);}catch(Exception ex){File.WriteAllText(Path.Combine(dir,"ui-result.json"),ex.ToString());app.Shutdown(1);}});return app.Run(window);
    }
    static bool VisualTextContains(System.Windows.DependencyObject root, string value)
    {
        if (root is System.Windows.Controls.TextBlock text && text.Text.Contains(value)) return true;
        for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++) if(VisualTextContains(System.Windows.Media.VisualTreeHelper.GetChild(root,i),value)) return true;
        return false;
    }
    [STAThread] static int Main(string[] args)
    {
        bool skipStartup = args.Length > 0 && args[0] == MaaStartController.SkipArgument;
        if (skipStartup) args = args[1..];
        if (args.Length > 0 && args[0] == "probe-regression-local") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await Tests(true); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-job-events") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await JobEventTests(); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-maa622-clues") { try { Maa622ClueTests().GetAwaiter().GetResult(); } catch (Exception ex) { failures++; Console.WriteLine(ex); } Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length == 4 && args[0] == "fixture-maa622")
        {
            string path = args[1], marker = UpdateMarkers.Maa622[int.Parse(args[2])], order = args[3];
            void Write(string s) { using var f = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true }; f.Write(s); }
            Thread.Sleep(300);
            if (order == "after-split") { Write("开始任务: A\n任务已全部完成！\n"); Thread.Sleep(300); Write(marker[..(marker.Length / 2)]); Thread.Sleep(300); Write(marker[(marker.Length / 2)..] + "\n"); }
            else { if (order != "none") Write((order == "custom" ? "MyCustomMarker" : marker) + "\n"); Thread.Sleep(300); Write("开始任务: A\n任务已全部完成！\n"); }
            Thread.Sleep(2200); return 0;
        }
        if (args.Length > 0 && args[0] == "probe-maa-evidence-counterexamples") { try { MaaEvidenceCounterexamples().GetAwaiter().GetResult(); } catch (Exception ex) { failures++; Console.WriteLine(ex); } Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-maa-external-writer") { try { MaaExternalWriterChain().GetAwaiter().GetResult(); } catch (Exception ex) { failures++; Console.WriteLine(ex); } Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-maa-instance-evidence") { try { MaaInstanceEvidence().GetAwaiter().GetResult(); } catch (Exception ex) { failures++; Console.WriteLine(ex); } Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-maa-channel-boundary") { try { MaaChannelBoundaryTests().GetAwaiter().GetResult(); } catch (Exception ex) { failures++; Console.WriteLine(ex); } Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-short-lived-processes") { try { ShortLivedProcessObservation().GetAwaiter().GetResult(); } catch (Exception ex) { failures++; Console.WriteLine(ex); } Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        // One managed root: writes nothing, waits on the release event, exits 0.
        if (args.Length == 4 && args[0] == "fixture-maa-chain-root")
        {
            string log = args[1], prefix = args[2];
            bool selfWrite = args[3] == "1";
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-ready");
            using var release = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-release");
            ready.Set();
            if (selfWrite) AppendLog(log, "开始任务: A\n任务已全部完成！\n");
            bool released = release.WaitOne(TimeSpan.FromSeconds(30));
            return released ? 0 : 4;
        }
        // Started by the test itself, not by the runner: appends the MAA text to the same log, then exits normally.
        if (args.Length == 4 && args[0] == "fixture-maa-chain-outsider")
        {
            string log = args[1], prefix = args[2], noteFile = args[3];
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-ready");
            using var written = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-written");
            bool sawReady = ready.WaitOne(TimeSpan.FromSeconds(30));
            IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out bool anyJob);
            string note = $"PID={Environment.ProcessId};READY={(sawReady ? 1 : 0)};INJOB={(anyJob ? 1 : 0)};";
            if (sawReady) { AppendLog(log, "开始任务: A\n任务已全部完成！\n"); note += "WRITTEN=1;"; written.Set(); }
            File.WriteAllText(noteFile, note);
            return sawReady ? 0 : 3;
        }
        if (args.Length == 3 && args[0] == "fixture-short-parent")
        {
            int count = int.Parse(args[1]), delay = int.Parse(args[2]);
            var children = Enumerable.Range(0, count).Select(_ => Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "fixture-short-child " + delay) { UseShellExecute = false })!).ToArray();
            foreach (var child in children) { child.WaitForExit(); child.Dispose(); }
            return 0;
        }
        if (args.Length == 2 && args[0] == "fixture-short-child") { Thread.Sleep(int.Parse(args[1])); return 0; }
        if (args.Length == 2 && args[0] == "fixture-evidence-append") { File.AppendAllText(args[1], "开始任务: A\n任务已全部完成！\n", new UTF8Encoding(false)); return 0; }
        if (args.Length == 3 && args[0] == "fixture-evidence-child") { if (args[1] == "silent-change") File.WriteAllText(args[2], "changed"); Thread.Sleep(500); return 0; }
        if (args.Length == 4 && args[0] == "fixture-evidence-parent")
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("fixture-evidence-child"); start.ArgumentList.Add(args[1]); start.ArgumentList.Add(args[3]);
            using var child = Process.Start(start)!; child.WaitForExit();
            File.AppendAllText(args[2], "开始任务: A\n任务已全部完成！\n", new UTF8Encoding(false));
            Thread.Sleep(500); return 0;
        }
        if (args.Length > 0 && args[0] == "fixture-job-parent") { var children=Enumerable.Range(0,6).Select(_=>Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-job-short"){UseShellExecute=false})!).ToArray();foreach(var child in children){child.WaitForExit();child.Dispose();}Thread.Sleep(400);return 0; }
        if (args.Length > 0 && args[0] == "fixture-job-short") { Thread.Sleep(40);return 0; }
        if (args.Length > 0 && args[0] == "probe-maa-start") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await MaaControlledStartTests(); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-startup-errors") { StartupErrorTests().GetAwaiter().GetResult(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-task-trial") { TaskTrialTests().GetAwaiter().GetResult(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-generic-start") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await GenericStartTests(); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-ocr-helper") { OcrHelperTests().GetAwaiter().GetResult(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-visual-evidence") { VisualEvidenceTests(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length == 3 && args[0] == "probe-rendered-image") { RenderedImageEvidenceTests(args[1],args[2]); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-visual-start") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await VisualStartTests(); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-visual-single") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await VisualStartTests(true); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-visual-icon") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await VisualStartTests(iconOnly:true); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-visual-real-screen") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await VisualStartTests(actualScreen:true); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length == 5 && args[0] == "fixture-visual-start")
        {
            string mode=args[1],log=args[2],counter=args[3],template=args[4];var app=new System.Windows.Application {ShutdownMode=System.Windows.ShutdownMode.OnMainWindowClose};var win=new System.Windows.Window {Title="V3 visual fixture",Width=600,Height=300,WindowStyle=System.Windows.WindowStyle.None,WindowStartupLocation=System.Windows.WindowStartupLocation.CenterScreen,Topmost=true,ShowInTaskbar=true};var panel=new System.Windows.Controls.StackPanel {Background=System.Windows.Media.Brushes.White};win.Content=panel;
            void AddTarget(){System.Windows.FrameworkElement target;if(mode.StartsWith("text"))target=new System.Windows.Controls.TextBlock {Text="开始运行",FontSize=42,Foreground=System.Windows.Media.Brushes.Black,Background=System.Windows.Media.Brushes.White,Height=70};else target=new System.Windows.Controls.Image {Source=new System.Windows.Media.Imaging.BitmapImage(new Uri(template)),Width=40,Height=32,HorizontalAlignment=System.Windows.HorizontalAlignment.Left};panel.Children.Add(target);}
            AddTarget();if(mode.EndsWith("duplicate"))AddTarget();win.Loaded+=(_,_)=>{win.Activate();var source=System.Windows.PresentationSource.FromVisual(win) as System.Windows.Interop.HwndSource;source!.AddHook((IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled)=>{if(msg==0x0202){File.AppendAllText(counter,"click\n");File.AppendAllText(log,"DONE\n");}return IntPtr.Zero;});var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(18)};timer.Tick+=(_,_)=>{timer.Stop();win.Close();};timer.Start();};return app.Run(win);
        }
        if (args.Length > 0 && args[0] == "probe-mobile-regression") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await QqNotificationTests(); await MobileNotificationTests(); await NotificationTests(); await ScheduleTests(); await StartupAndIntervalTests(); await CompletionTests(); var s=NewStore("mobile-recovery-regression");s.History.Add(new RunRecord{State="中断"});s.Tasks.Add(Spec("blocked","echo blocked"));s.Save();var loaded=NewStore("mobile-recovery-regression");loaded.Load();var e=new RunnerEngine(loaded,()=>{});await e.Start();Check(e.NeedsRecovery && loaded.History.Count==1,"mobile regression interrupted run remains blocked"); } catch(Exception ex) { failures++; Console.WriteLine(ex.GetType().Name); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "--isolated-update-helper") return SelfUpdateSimulation.RunHelper(args);
        if (args.Length > 0 && args[0] == "fixture-update-verified-install")
        {
            try
            {
                string fixture=Path.GetFullPath(args[1]);string prefix=fixture+Path.DirectorySeparatorChar;string package=Path.GetFullPath(args[2]),publicKey=Path.GetFullPath(args[3]);
                if(!File.Exists(Path.Combine(fixture,".runner-update-test-root")) || !package.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) || !publicKey.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))return 1;
                using var key=System.Security.Cryptography.RSA.Create();key.ImportSubjectPublicKeyInfo(File.ReadAllBytes(publicKey),out _);string stage=SelfUpdatePackage.Stage(package,key,new Version(1,19,0),new Version(1,20,0),fixture);Directory.Move(stage,Path.Combine(fixture,"staged"));SelfUpdateTestTransaction.Install(fixture);return 0;
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException){return 5;}
        }
        if (args.Length > 0 && args[0] == "fixture-update-transaction") { SelfUpdateTestTransaction.Install(args[1],phase=>{if(phase==args[2])Environment.Exit(73);}); return 0; }
        if (args.Length > 0 && args[0] == "fixture-update-transaction-delayed") { Thread.Sleep(2000); SelfUpdateTestTransaction.Install(args[1],phase=>{if(phase==args[2])Environment.Exit(73);}); return 0; }
        if (args.Length > 0 && args[0] == "fixture-update-recovery") { SelfUpdateTestTransaction.Recover(args[1],phase=>{if(phase==args[2])Environment.Exit(73);}); return 0; }
        if (args.Length > 0 && args[0] == "probe-self-update-package") { SelfUpdatePackageTests(); SelfUpdateReleaseTests().GetAwaiter().GetResult(); SelfUpdateCoordinatorTests().GetAwaiter().GetResult(); SelfUpdateInterlockTests().GetAwaiter().GetResult(); SelfUpdateTransactionTests(); SelfUpdateFullFixtureTests().GetAwaiter().GetResult(); SelfUpdateSimulationTests().GetAwaiter().GetResult(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-qq-notifications") { QqNotificationTests().GetAwaiter().GetResult(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-mobile-notifications") { MobileNotificationTests().GetAwaiter().GetResult(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-notifications") { NotificationTests().GetAwaiter().GetResult(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-maa-resume") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await MaaResumeTests(); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length == 4 && args[0] == "fixture-maa-start")
        {
            string mode=args[1], log=args[2], counter=args[3];if(!skipStartup)return 9;bool genericFixture=mode.StartsWith("generic-",StringComparison.Ordinal);if(genericFixture)mode=mode[8..];
            void Append(string s) { using var w=new StreamWriter(new FileStream(log,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete),new UTF8Encoding(false)){AutoFlush=true};w.Write(s); }
            var app=new System.Windows.Application { ShutdownMode=System.Windows.ShutdownMode.OnMainWindowClose };var win=new System.Windows.Window { Title="V3 controlled start fixture",Width=420,Height=260,ShowInTaskbar=true,Topmost=genericFixture };var panel=new System.Windows.Controls.StackPanel();win.Content=panel;
            void AddButton(bool enabled) { var b=new System.Windows.Controls.Button { Content="Link Start!",IsEnabled=enabled,Height=45 };b.Click+=(_,_)=>{File.AppendAllText(counter,"clicked\n");Append("开始任务: A\n任务已全部完成！\n");};panel.Children.Add(b); }
            if(mode=="single" || mode=="already")AddButton(true);else if(mode=="duplicate"){AddButton(true);AddButton(true);}else if(mode=="disabled")AddButton(false);
            win.Loaded+=(_,_)=>{if(mode=="already")Append("开始任务: A\n任务已全部完成！\n");if(mode=="update"){Console.WriteLine("updating");Console.Out.Flush();}if(mode=="error")Append("任务出错: A\n");if(mode=="stopped")Append("已停止\n");var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(mode=="single" ? 15 : 5)};timer.Tick+=(_,_)=>{timer.Stop();win.Close();};timer.Start();};return app.Run(win);
        }
        if (args.Length == 2 && args[0].StartsWith("fixture-maa-", StringComparison.Ordinal))
        {
            string mode=args[0]["fixture-maa-".Length..], path=args[1];
            void Append(string s){using var f=new StreamWriter(new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete),new UTF8Encoding(false)){AutoFlush=true};f.Write(s);}
            if(mode=="window" || mode=="window-update") { var app=new System.Windows.Application { ShutdownMode=System.Windows.ShutdownMode.OnMainWindowClose };var win=new System.Windows.Window{Title="V3 controlled MAA window",Width=300,Height=180,ShowInTaskbar=true};win.Loaded+=(_,_)=>{var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};timer.Tick+=(_,_)=>{timer.Stop();Append("开始任务: A\n任务已全部完成！\n");if(mode=="window-update"){Console.WriteLine("updating");Console.Out.Flush();}};timer.Start();};return app.Run(win); }
            if(mode=="extra-child") { using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-child"){UseShellExecute=false})!;child.WaitForExit();Append("开始任务: A\n任务已全部完成！\n");Thread.Sleep(400);return 0; }
            if(mode=="short-child") { using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-short-child 20"){UseShellExecute=false})!;child.WaitForExit();Append("开始任务: A\n任务已全部完成！\n");Thread.Sleep(400);return 0; }
            if(mode is "update" or "update-old" or "overlap") { Append("开始任务: 旧轮\n任务已全部完成！\n");Console.WriteLine("updating");Console.Out.Flush();using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-maa-child-"+(mode=="overlap"?"update":mode)+" \""+path+"\""){UseShellExecute=false})!;if(mode=="overlap"){using var other=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-maa-child-update \""+path+"\""){UseShellExecute=false})!;}Thread.Sleep(400);return 0; }
            if(mode.StartsWith("child-",StringComparison.Ordinal)) {Thread.Sleep(1200);if(mode=="child-update")Append("开始任务: 新轮\n任务已全部完成！\n");Thread.Sleep(400);return 0;}
            if(mode=="roll"){File.Move(path,path+".1");File.WriteAllText(path,"开始任务: A\n任务已全部完成！\n");Thread.Sleep(350);return 0;}
            Thread.Sleep(250);
            if(mode=="replace"){File.Move(path,path+".old");File.WriteAllText(path,"开始任务: A\n任务已全部完成！\n");Thread.Sleep(350);return 0;}
            if(mode=="truncate"){File.WriteAllText(path,"changed-content-larger-than-original-and-no-signal\n");Thread.Sleep(350);return 0;}
            if(mode=="invalid-utf8"){using var bytes=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);bytes.WriteByte(0xff);bytes.Flush();Thread.Sleep(350);return 0;}
            if(mode!="old") {Append("开始任");Thread.Sleep(100);Append("务: A\n");if(mode=="error")Append("任务出错: A\n");if(mode=="stop")Append("已停止\n");Append("任务已全部完成！\n");}
            Thread.Sleep(mode=="stubborn" ? 16000 : mode=="live" ? 4000 : 400);return mode=="exit-nonzero" ? 3 : 0;
        }
        if (args.Length > 0 && args[0] == "probe-restart-candidates") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await RestartCandidateTests(); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 1 && args[0] == "fixture-identity-parent") { if(args[1]!="silent")Console.WriteLine("updating");Console.WriteLine("READY");Console.Out.Flush();ProcessStartInfo Make(){var psi=new ProcessStartInfo(cmd){UseShellExecute=false,CreateNoWindow=true};psi.ArgumentList.Add("/d");psi.ArgumentList.Add("/c");psi.ArgumentList.Add("call");psi.ArgumentList.Add(Environment.ProcessPath!);psi.ArgumentList.Add("fixture-identity-target");return psi;}using var a=Process.Start(Make())!;if(args[1]=="multiple"){using var b=Process.Start(Make())!;}Thread.Sleep(100);return 0; }
        if (args.Length > 0 && args[0] == "fixture-identity-target") { Thread.Sleep(1800); return 0; }
        if (args.Length > 0 && args[0] == "fixture-identity-held") { Console.WriteLine("READY");Console.Out.Flush();Console.ReadLine();return 0; }
        if (args.Length > 0 && args[0] == "probe-controlled-success") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await ControlledResumeTests(true); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-controlled-resume") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await ControlledResumeTests(); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length == 4 && args[0] == "fixture-control-parent") { Console.WriteLine("updating");Console.Out.Flush();using var p=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-control-updater "+args[1]+" "+args[2]+" "+args[3]){UseShellExecute=false})!;Thread.Sleep(100);return 0; }
        if (args.Length == 4 && args[0] == "fixture-control-updater") { using var p=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-control-target "+args[1]+" "+args[2]+" "+args[3]){UseShellExecute=false})!;if(args[1]=="multiple"){using var q=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-control-hold"){UseShellExecute=false})!;}Thread.Sleep(100);return 0; }
        if (args.Length > 0 && args[0] == "fixture-control-hold") {Thread.Sleep(1800);return 0;}
        if (args.Length == 4 && args[0] == "fixture-control-target") { Thread.Sleep(args[1]=="slow" ? 2000 : 700);if(args[1]=="missing")return 0;try {using var pipe=new NamedPipeClientStream(".",args[2],PipeDirection.Out,PipeOptions.CurrentUserOnly);pipe.Connect(1500);using var writer=new StreamWriter(pipe,new UTF8Encoding(false)){AutoFlush=true};writer.WriteLine(args[1]=="forged" ? "BADTOKEN" : args[3]);writer.WriteLine("READY");if(args[1]!="disconnect")writer.WriteLine(args[1]=="failure" ? "FAILURE" : "SUCCESS:READY");if(args[1]=="duplicate")writer.WriteLine("SUCCESS:READY");}catch(IOException){}Thread.Sleep(400);return 0; }
        if (args.Length > 0 && args[0] == "probe-restart-tracking") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await UpdateRestartOwnershipProbe(); await UpdateRestartTests(); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "fixture-restart-wait") {Console.WriteLine("updating");Console.Out.Flush();Thread.Sleep(1500);return 0;}
        if (args.Length > 0 && args[0] == "fixture-restart-dialog") {Console.WriteLine("updating");Console.Out.Flush();uint thread=GetCurrentThreadId();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(100)};timer.Tick+=(_,_)=>EnumThreadWindows(thread,(hwnd,_)=>{var title=new StringBuilder(256);GetWindowText(hwnd,title,title.Capacity);if(title.ToString()=="受控更新提示"){PostMessage(hwnd,0x0010,IntPtr.Zero,IntPtr.Zero);timer.Stop();return false;}return true;},IntPtr.Zero);timer.Start();System.Windows.MessageBox.Show("仅用于隔离测试", "受控更新提示");Console.WriteLine("SIM_DIALOG_CLOSED");return 0;}
        if (args.Length > 0 && args[0] == "fixture-restart-failure") {Console.WriteLine("updating");Console.WriteLine("SIM_DIALOG_UPDATE_FAILED");return 3;}
        if (args.Length > 0 && args[0] == "fixture-restart-multiple") {Console.WriteLine("updating");Console.Out.Flush();using var a=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-restart-target"){UseShellExecute=false})!;using var b=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-restart-target"){UseShellExecute=false})!;Thread.Sleep(100);return 0;}
        if (args.Length > 0 && args[0] == "probe-restart-ownership") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await UpdateRestartOwnershipProbe(); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "fixture-restart-parent") { Console.WriteLine("updating");Console.Out.Flush();using var p=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-restart-updater"){UseShellExecute=false})!;Thread.Sleep(150);return 0; }
        if (args.Length > 0 && args[0] == "fixture-restart-updater") { Thread.Sleep(300);using var p=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"fixture-restart-target"){UseShellExecute=false})!;Thread.Sleep(150);return 0; }
        if (args.Length > 0 && args[0] == "fixture-restart-target") { Thread.Sleep(900);Console.WriteLine("SIM_RESTART_DONE");return 0; }
        if (args.Length > 0 && args[0] == "probe-update-markers") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await UpdateMarkerTests(); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "fixture-marker-out") { Console.Write("mAGIc");Console.Out.Flush();Thread.Sleep(100);Console.WriteLine("rESTART READY");return 0; }
        if (args.Length > 0 && args[0] == "fixture-marker-err") { Console.Error.WriteLine("magicrestart");return 0; }
        if (args.Length > 0 && args[0] == "fixture-marker-long") { Console.Write(new string('x',30000)+"magicrestart");return 0; }
        if (args.Length > 0 && args[0] == "fixture-marker-delayed") { Thread.Sleep(500);Console.WriteLine("MagicRestart");return 0; }
        if (args.Length > 0 && args[0] == "fixture-marker-nojoin") { Console.WriteLine("Magic");Console.WriteLine("Restart");Console.Write("Magic");Console.Error.Write("Restart");return 0; }
        if (args.Length > 1 && args[0] == "fixture-marker-file") { using var f=new StreamWriter(new FileStream(args[1],FileMode.Append,FileAccess.Write,FileShare.ReadWrite),new UTF8Encoding(false)) { AutoFlush=true };f.Write("Mag");Thread.Sleep(300);f.WriteLine("icRestart READY");Thread.Sleep(300);return 0; }
        if (args.Length > 0 && args[0] == "probe-manual-skip") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await ManualSkipTests(); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 0 && args[0] == "probe-history-storage") { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await HistoryStorageTests(); } catch(Exception ex) { failures++; Console.WriteLine(ex); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1; }
        if (args.Length > 1 && args[0] == "fixture-history-ui") return HistoryUIFixture(args[1]);
        if (args.Length > 1 && args[0] == "fixture-long-output") { var output=Console.OpenStandardOutput();byte[] chunk=Enumerable.Repeat((byte)'x',8192).ToArray();int remaining=int.Parse(args[1]);while(remaining>0){int n=Math.Min(remaining,chunk.Length);output.Write(chunk,0,n);remaining-=n;}output.Write(Encoding.UTF8.GetBytes("READY"));output.Flush();return 0; }
        if (args.Length > 0 && args[0] == "fixture-long-update") { var output=Console.OpenStandardOutput();byte[] chunk=Enumerable.Repeat((byte)'x',8192).ToArray();for(int i=0;i<2048;i++)output.Write(chunk);output.Write(Encoding.UTF8.GetBytes("UPDATING"));output.Flush();return 0; }
        if (args.Length > 0 && args[0] == "fixture-both-streams") { var a=Task.Run(()=>{for(int i=0;i<3000;i++)Console.Out.WriteLine("normal-"+i);Console.Out.WriteLine("READY");});var b=Task.Run(()=>{for(int i=0;i<3000;i++)Console.Error.WriteLine("error-"+i);});Task.WaitAll(a,b);return 0; }
        if (args.Length > 0 && args[0] == "fixture-split-utf8") { var output=Console.OpenStandardOutput();var b=Encoding.UTF8.GetBytes("完成");foreach(byte c in b){output.WriteByte(c);output.Flush();Thread.Sleep(30);}return 0; }
        if (args.Length > 0 && args[0] == "fixture-stream-split") { Console.Out.Write("REA");Console.Out.Flush();Console.Error.Write("DY");Console.Error.Flush();return 0; }
        if (args.Length > 0 && args[0] == "fixture-stderr-ready") { Console.Error.Write("READY");Console.Error.Flush();return 0; }
        if (args.Length > 0 && args[0] == "fixture-split-update") { Console.Out.Write("UPDAT");Console.Out.Flush();Thread.Sleep(300);Console.Out.Write("ING");Console.Out.Flush();return 0; }
        if (args.Length > 0 && args[0] == "fixture-long-wait") { Console.Out.Write(new string('x',30000));Console.Out.Flush();Thread.Sleep(1500);return 0; }
        if (args.Length > 0 && args[0] == "fixture-flood") { for (int i=0; i<12000; i++) Console.WriteLine("output " + i); return 0; }
        if (args.Length > 0 && args[0] == "fixture-wait") { Thread.Sleep(1500); return 0; }
        if (args.Length > 0 && args[0] == "fixture-child") { Thread.Sleep(4000); return 0; }
        if (args.Length > 1 && args[0] == "fixture-parent") { CloseHandle(GetStdHandle(-11)); CloseHandle(GetStdHandle(-12)); using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "fixture-child") { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true })!; File.WriteAllText(args[1], child.Id.ToString()); return 0; }
        if (args.Length > 1 && args[0] == "fixture-append") { byte[] b=Encoding.UTF8.GetBytes("完成"); using var f=new FileStream(args[1],FileMode.Append,FileAccess.Write,FileShare.ReadWrite); f.Write(b,0,2); f.Flush(); Thread.Sleep(500); f.Write(b,2,b.Length-2); f.Flush(); Thread.Sleep(500); return 0; }
        if (args.Length > 1 && args[0] == "fixture-rotate-wait") { Thread.Sleep(400); File.Move(args[1],args[1]+".previous"); File.WriteAllText(args[1],"READY\n"); Thread.Sleep(12000); return 0; }
        if (args.Length > 1 && args[0] == "fixture-rotate") { Thread.Sleep(400); File.Move(args[1],args[1]+".previous"); File.WriteAllText(args[1],"READY\n"); Thread.Sleep(600); return 0; }
        if (args.Length > 1 && args[0] == "fixture-channel-root")
        {
            if (args[1] == "idle" && args.Length > 2) { using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, args[2] + "-ready"); using var release = new EventWaitHandle(false, EventResetMode.ManualReset, args[2] + "-release"); ready.Set(); release.WaitOne(TimeSpan.FromSeconds(30)); return 0; }
            if (args[1] == "stdout") { Console.WriteLine(SyntheticStart(41001, 7001)); Console.WriteLine(SyntheticDone(41001, 7001)); Console.Out.Flush(); Thread.Sleep(300); return 0; }
            if (args[1] == "log" && args.Length > 2) { AppendLog(args[2], SyntheticStart(41001, 7001) + "\n" + SyntheticDone(41001, 7001) + "\n"); Thread.Sleep(600); return 0; }
            if (args[1] == "pipe" && args.Length > 3) { using (var client = new NamedPipeClientStream(".", args[2], PipeDirection.Out)) { client.Connect(10000); using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true }; writer.WriteLine("RECEIPT root-instance"); } using var release = new EventWaitHandle(false, EventResetMode.ManualReset, args[3] + "-release"); release.WaitOne(TimeSpan.FromSeconds(30)); return 0; }
            return 2;
        }
        if (args.Length > 2 && args[0] == "fixture-channel-outsider")
        {
            if (args[1] == "pid" && args.Length > 4) { using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, args[4] + "-ready"); ready.WaitOne(TimeSpan.FromSeconds(30)); int claimed = int.Parse(args[3]); Thread.Sleep(200); AppendLog(args[2], SyntheticStart(claimed, 7001) + "\n" + SyntheticDone(claimed, 7001) + "\nOUTSIDE-WRITER\n"); return 0; }
            if (args[1] == "pipe") { using var client = new NamedPipeClientStream(".", args[2], PipeDirection.Out); client.Connect(10000); using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true }; writer.WriteLine("RECEIPT outsider-claim"); return 0; }
            return 2;
        }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await Tests(); } catch(Exception e) { failures++; Console.WriteLine(e); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } });
        Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1;
    }
    static bool TrySave(Store store) { try { store.Save(); return true; } catch(IOException) { return false; } }
    sealed class FakeClock : TimeProvider { public long Seconds; public override long TimestampFrequency => 1; public override long GetTimestamp() => Seconds; }
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
