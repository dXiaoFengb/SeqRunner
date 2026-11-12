using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
    static async Task Tests()
    {
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
        h2.Tasks.Add(Spec("recovery", "echo resumed")); var he = new RunnerEngine(h2,()=>{}); await he.Start(); Check(he.NeedsRecovery && h2.History.Count==1, "unresolved interruption prevents a new run"); he.AcknowledgeRecovery(); await he.Start(); Check(!he.NeedsRecovery && h2.History.Last().Events.Any(x=>x.Contains("recovery 成功")), "explicit recovery confirmation unlocks execution");
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
        var x=NewStore("concurrent-store"); x.Save(); var y=NewStore("concurrent-store"); y.Load(); x.Tasks.Add(Spec("writer-one", "echo 1")); y.Tasks.Add(Spec("writer-two", "echo 2"));
        var writes=await Task.WhenAll(Task.Run(()=>TrySave(x)), Task.Run(()=>TrySave(y))); var verify=NewStore("concurrent-store"); verify.Load(); Check(writes.Count(v=>v)==1 && verify.Tasks.Count==1, "cooperating stores reject conflicting simultaneous save");
    }
    [STAThread] static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "fixture-flood") { for (int i=0; i<12000; i++) Console.WriteLine("output " + i); return 0; }
        if (args.Length > 0 && args[0] == "fixture-wait") { Thread.Sleep(1500); return 0; }
        if (args.Length > 0 && args[0] == "fixture-child") { Thread.Sleep(4000); return 0; }
        if (args.Length > 1 && args[0] == "fixture-parent") { CloseHandle(GetStdHandle(-11)); CloseHandle(GetStdHandle(-12)); using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "fixture-child") { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true })!; File.WriteAllText(args[1], child.Id.ToString()); return 0; }
        if (args.Length > 1 && args[0] == "fixture-append") { byte[] b=Encoding.UTF8.GetBytes("完成"); using var f=new FileStream(args[1],FileMode.Append,FileAccess.Write,FileShare.ReadWrite); f.Write(b,0,2); f.Flush(); Thread.Sleep(500); f.Write(b,2,b.Length-2); f.Flush(); Thread.Sleep(500); return 0; }
        if (args.Length > 1 && args[0] == "fixture-rotate-wait") { Thread.Sleep(400); File.Move(args[1],args[1]+".previous"); File.WriteAllText(args[1],"READY\n"); Thread.Sleep(12000); return 0; }
        if (args.Length > 1 && args[0] == "fixture-rotate") { Thread.Sleep(400); File.Move(args[1],args[1]+".previous"); File.WriteAllText(args[1],"READY\n"); Thread.Sleep(600); return 0; }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await Tests(); } catch(Exception e) { failures++; Console.WriteLine(e); } finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } });
        Dispatcher.Run(); Console.WriteLine("Failures: " + failures); return failures == 0 ? 0 : 1;
    }
    static bool TrySave(Store store) { try { store.Save(); return true; } catch(IOException) { return false; } }
    sealed class FakeClock : TimeProvider { public long Seconds; public override long TimestampFrequency => 1; public override long GetTimestamp() => Seconds; }
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
