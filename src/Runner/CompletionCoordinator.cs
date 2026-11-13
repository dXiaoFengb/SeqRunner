namespace Runner;

public sealed class CompletionNotSentException(string message) : Exception(message);

public sealed class CompletionCoordinator
{
    readonly Store store;
    readonly Action changed;
    readonly Func<string,Task<string>> dispatch;
    readonly TimeProvider clock;
    readonly Func<TimeSpan,CancellationToken,Task> delay;
    CancellationTokenSource? cancellation;
    RunRecord? pending;
    public bool Blocked { get; private set; }
    public bool Pending => pending?.State == "操作等待";
    public bool Executing => pending?.State == "操作请求中";
    public string Status { get; private set; } = "";
    public CompletionCoordinator(Store store, Action changed, Func<string,Task<string>> dispatch, TimeProvider? clock=null, Func<TimeSpan,CancellationToken,Task>? delay=null)
    { this.store=store; this.changed=changed; this.dispatch=dispatch; this.clock=clock ?? TimeProvider.System; this.delay=delay ?? ((duration,token)=>Task.Delay(duration,token)); }
    public bool Cancel(string reason)
    {
        if (Blocked) return false;
        if (Executing) { Status="系统请求已发出，无法取消；请等待结果"; changed(); return false; }
        if (!Pending) return true;
        cancellation!.Cancel(); pending!.State="操作已取消"; pending.Events.Add($"{DateTimeOffset.Now:O} 已取消：{reason}");
        try { store.Save(); } catch(Exception ex) { Blocked=true; Status="完成后操作取消记录保存失败，已阻止后续启动："+ex.Message; changed(); return false; }
        pending=null; Status="完成后操作已取消："+reason; changed(); return true;
    }
    public void Offer(SchedulePlan plan,RunRecord run) => OfferBatch([(plan,run)],run.AllSucceeded==true && run.State=="已结束",plan.Name);
    public void OfferBatch(IReadOnlyList<(SchedulePlan Plan,RunRecord Run)> completed, bool batchSuccess, string name)
    {
        if (Blocked || completed.Count==0) return;
        var configured=completed.Where(x=>x.Plan.CompletionAction!="none").ToList();
        if(configured.Count==0) return;
        var actions=configured.Select(x=>x.Plan.CompletionAction).Distinct().ToList();
        string? reason=!batchSuccess || completed.Any(x=>x.Run.AllSucceeded!=true || x.Run.State!="已结束") ? "本轮包含未成功、停止或结果不明的任务" : actions.Count!=1 ? "多个计划的完成后操作冲突" : null;
        var entry=new RunRecord { Trigger="完成后操作",PlanName=name,State=reason==null ? "操作等待" : "操作未执行" };
        entry.Events.Add(reason==null ? $"{DateTimeOffset.Now:O} 已安排 {Label(actions[0])}；额外等待 {configured.Max(x=>x.Plan.CompletionDelayMinutes)} 分钟后进入 60 秒倒计时" : $"{DateTimeOffset.Now:O} 未执行：{reason}");
        if(Pending || Executing) { entry.State="操作未执行";entry.Events.Add("已有完成后操作，未安排新操作");reason="已有待处理操作"; }
        store.History.Add(entry);
        try { store.Save(); } catch(Exception ex) { store.History.Remove(entry); Blocked=true; Status="完成后操作记录保存失败，已阻止后续启动："+ex.Message; changed(); return; }
        if(reason!=null) { Status="完成后操作未执行："+reason; changed(); return; }
        pending=entry; cancellation=new CancellationTokenSource(); changed(); _=WaitAndDispatch(actions[0],configured.Max(x=>x.Plan.CompletionDelayMinutes),entry,cancellation.Token);
    }
    async Task WaitAndDispatch(string action,int extraMinutes,RunRecord entry,CancellationToken token)
    {
        bool dispatchStarted=false;
        try
        {
            if(extraMinutes>0) await WaitPhase("额外等待",TimeSpan.FromMinutes(extraMinutes),token);
            token.ThrowIfCancellationRequested();
            entry.Events.Add($"{DateTimeOffset.Now:O} 进入 60 秒可取消倒计时"); store.Save();
            await WaitPhase("倒计时",TimeSpan.FromSeconds(60),token);
            token.ThrowIfCancellationRequested();
            entry.State="操作请求中"; entry.Events.Add($"{DateTimeOffset.Now:O} 即将请求 {Label(action)}；实际设备状态尚未核实"); store.Save();
            Status="正在请求"+Label(action)+"；系统请求发出后无法取消"; changed();
            dispatchStarted=true;
            string result=await dispatch(action);
            entry.State="操作已请求"; entry.Events.Add($"{DateTimeOffset.Now:O} {result}"); store.Save();
            pending=null; Status="完成后操作："+result; changed();
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception ex)
        {
            if (ex is CompletionNotSentException) { entry.State="操作未执行"; entry.Events.Add($"{DateTimeOffset.Now:O} 未执行：{ex.Message}"); }
            else if (dispatchStarted) { Blocked=true; entry.State="操作失败或结果不明"; entry.Events.Add($"{DateTimeOffset.Now:O} 请求可能已发出，结果或记录未确认：{ex.Message}；请人工核实"); }
            else { entry.State="操作未执行"; entry.Events.Add($"{DateTimeOffset.Now:O} 等待或保存失败，未请求操作：{ex.Message}"); }
            try { store.Save(); } catch { Blocked=true; }
            pending=null; Status="完成后操作未确认："+ex.Message; changed();
        }
    }
    async Task WaitPhase(string label,TimeSpan duration,CancellationToken token)
    {
        long started=clock.GetTimestamp(), previous=started;
        while (clock.GetElapsedTime(started)<duration)
        {
            token.ThrowIfCancellationRequested(); var remaining=duration-clock.GetElapsedTime(started);
            Status=$"完成后操作{label}：剩余 {Math.Ceiling(remaining.TotalSeconds)} 秒"; changed();
            await delay(remaining<TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1),token);
            if (clock.GetElapsedTime(previous)>TimeSpan.FromSeconds(5)) { Cancel("等待中断或系统休眠，请人工核实"); token.ThrowIfCancellationRequested(); }
            previous=clock.GetTimestamp();
        }
    }
    public static string Label(string action) => action switch { "exit"=>"退出软件", "shutdown"=>"关机", "restart"=>"重启", "sleep"=>"睡眠", "hibernate"=>"休眠", _=>"无操作" };
}
