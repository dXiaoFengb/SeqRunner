using System.Text.Json;

namespace Runner;

public sealed class StartupCoordinator
{
    readonly Store store;
    readonly RunnerEngine engine;
    readonly Action changed;
    bool started, stop;
    public bool IsActive { get; private set; }
    public bool Blocked { get; private set; }
    public string Status { get; private set; } = "";
    public StartupCoordinator(Store store, RunnerEngine engine, Action changed) { this.store=store; this.engine=engine; this.changed=changed; }
    public void Stop() { if (!IsActive) return; stop=true; engine.Stop(); }
    public async Task StartAsync(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (started) return; started=true;
        var plans=JsonSerializer.Deserialize<List<SchedulePlan>>(JsonSerializer.Serialize(store.Plans.Where(p=>p.StartupMode!="never").ToList()))!;
        if (plans.Count==0) return;
        var tasks=store.Tasks.ToDictionary(t=>t.Id,t=>t.Copy());
        var local=TimeZoneInfo.ConvertTime(now,zone); string today=local.ToString("yyyy-MM-dd");
        if (engine.NeedsRecovery)
        {
            Status="存在未核实的中断，启动计划不会自动运行";
            var skipped=new RunRecord { Trigger="启动队列", State="已跳过", Events=[$"{local:O} 存在未核实的中断，启动计划未运行："+string.Join("、",plans.Select(p=>p.Name))] };
            store.History.Add(skipped);
            try { store.Save(); } catch(Exception ex) { store.History.Remove(skipped); Blocked=true; Status="启动跳过记录保存失败："+ex.Message; }
            changed(); return;
        }
        IsActive=true; var batch=new RunRecord { Trigger="启动队列", State="运行中" };
        batch.Events.Add($"{local:O} 启动计划已排队："+string.Join("、",plans.Select(p=>p.Name))); store.History.Add(batch);
        try { store.Save(); } catch(Exception ex) { store.History.Remove(batch); Blocked=true; IsActive=false; Status="启动队列保存失败，未启动："+ex.Message; changed(); return; }
        try
        {
            var previousRun = engine.Running ? store.History.LastOrDefault(r => !ReferenceEquals(r,batch) && r.State=="运行中") : null;
            while(engine.Running && !stop) await Task.Delay(200);
            if (engine.StopRequested) stop=true;
            if (previousRun?.State is "异常" or "需人工核实" or "中断") Blocked=true;
            foreach(var plan in plans)
            {
                if(stop || engine.NeedsRecovery || Blocked) break;
                string? reason=plan.StartupMode=="daily" && store.StartupDates.TryGetValue(plan.Id,out var date) && string.CompareOrdinal(date,today)>=0 ? "今天已经触发或系统日期回退" : null;
                var items=plan.Items.Where(i=>i.Days.Contains(local.DayOfWeek)).ToList();
                if(reason==null && items.Count==0) reason="当天没有任务";
                if(reason==null && items.Any(i=>!tasks.ContainsKey(i.TaskId))) reason="计划引用的任务不存在";
                if(reason!=null) { batch.Events.Add($"{DateTimeOffset.Now:O} {plan.Name} 已跳过：{reason}"); store.Save(); changed(); continue; }
                var run=new RunRecord { Trigger="启动计划",PlanName=plan.Name,State="运行中",Due=now };
                run.Events.Add($"{DateTimeOffset.Now:O} 已保存启动计划触发意图");store.History.Add(run);
                bool had=store.StartupDates.TryGetValue(plan.Id,out var oldDate);
                if(plan.StartupMode=="daily") store.StartupDates[plan.Id]=today;
                try { store.Save(); }
                catch { store.History.Remove(run); if(plan.StartupMode=="daily") { if(had) store.StartupDates[plan.Id]=oldDate!; else store.StartupDates.Remove(plan.Id); } throw; }
                batch.Events.Add($"{DateTimeOffset.Now:O} 开始启动计划：{plan.Name}");
                await engine.StartTasks(items.Select(i=>tasks[i.TaskId].Copy()).ToList(),run,items.Select(i=>i.IntervalSeconds).ToList());
                if(engine.StopRequested) stop=true;
                if(run.State is "异常" or "需人工核实" or "中断") { Blocked=true; break; }
            }
            batch.State=Blocked || engine.NeedsRecovery ? "需人工核实" : stop ? "已停止" : "已结束";
            batch.Events.Add($"{DateTimeOffset.Now:O} 启动队列{batch.State}；未启动的计划不会自动接续");
            store.Save();
        }
        catch(Exception ex) { Blocked=true; batch.State="需人工核实"; Status="启动计划记录失败，已停止自动推进："+ex.Message; }
        finally { IsActive=false; changed(); }
    }
}
