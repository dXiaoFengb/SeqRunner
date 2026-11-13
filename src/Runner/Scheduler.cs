using System.Globalization;

namespace Runner;

public sealed class SchedulePlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新计划";
    public bool Enabled { get; set; }
    public string StartupMode { get; set; } = "never";
    public DateTimeOffset EffectiveFrom { get; set; } = DateTimeOffset.UtcNow;
    public List<ScheduleItem> Items { get; set; } = [];
    public List<ScheduleSlot> Slots { get; set; } = [];
    public override string ToString() => $"{Name} ({(Enabled ? "启用" : "停用")})";
}
public sealed class ScheduleItem
{
    public string TaskId { get; set; } = "";
    public string TaskName { get; set; } = "";
    public int IntervalSeconds { get; set; }
    public List<DayOfWeek> Days { get; set; } = Enum.GetValues<DayOfWeek>().ToList();
    public override string ToString() => $"{TaskName}　{ScheduleText.Days(Days)}　间隔 {IntervalSeconds} 秒";
}
public sealed class ScheduleSlot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Time { get; set; } = "09:00";
    public List<DayOfWeek> Days { get; set; } = Enum.GetValues<DayOfWeek>().ToList();
    public override string ToString() => $"{(Enabled ? "启用" : "停用")}　{Time}　{ScheduleText.Days(Days)}";
}
public static class ScheduleText
{
    public static string Days(IEnumerable<DayOfWeek> days) => string.Join("、", days.Select(d => "周" + "日一二三四五六"[(int)d]));
}

public sealed class Scheduler
{
    readonly Store store;
    readonly RunnerEngine engine;
    readonly Action changed;
    readonly Func<bool> startupBusy;
    bool first = true;
    bool resumed;
    DateTimeOffset? lastTick;
    bool failed;
    public string Status { get; private set; } = "";
    public Scheduler(Store store, RunnerEngine engine, Action changed, Func<bool>? startupBusy = null) { this.store = store; this.engine = engine; this.changed = changed; this.startupBusy = startupBusy ?? (() => false); }
    public void MarkResumed() => resumed = true;
    static DateTimeOffset Minute(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
    public void Tick(DateTimeOffset utcNow, TimeZoneInfo zone)
    {
        if (failed) return;
        var now = Minute(utcNow.ToUniversalTime());
        var previous = store.ScheduleCursorUtc;
        bool startup = first || resumed || (lastTick != null && utcNow.ToUniversalTime() - lastTick.Value > TimeSpan.FromSeconds(90)); first = false; resumed = false; lastTick = utcNow.ToUniversalTime();
        if (previous is null || store.ScheduleZoneId != zone.Id)
        {
            var oldZone = store.ScheduleZoneId;
            store.ScheduleCursorUtc = now; store.ScheduleZoneId = zone.Id;
            if (previous != null) store.History.Add(new RunRecord { Trigger="定时", State="需查看", Events=[$"时区已变化（{oldZone} → {zone.Id}），未自动补跑过去的计划"] });
            try { store.Save(); } catch (Exception e) { failed=true; Status="定时记录保存失败，自动调度已停止："+e.Message; changed(); }
            return;
        }
        var cursor = previous.Value.ToUniversalTime();
        if (now <= cursor) { if (now < cursor) { Status="系统时间回退，定时计划暂停至时间追上已记录时刻"; changed(); } return; }
        var originalCount = store.History.Count;
        var due = new List<(SchedulePlan Plan, DateTimeOffset Time)>();
        DateTime startDay = TimeZoneInfo.ConvertTime(cursor, zone).Date;
        DateTime endDay = TimeZoneInfo.ConvertTime(now, zone).Date;
        if (endDay < startDay) { failed=true; Status="本地时间异常，定时计划已暂停"; changed(); return; }
        int days = (endDay - startDay).Days;
        if (days > 36600) { failed=true; Status="离线时间过长，定时计划已暂停，请人工核实"; changed(); return; }
        foreach (var plan in store.Plans.Where(p => p.Enabled))
        {
            int missed = 0; DateTimeOffset? earliest = null, latest = null;
            for (int i = 0; i <= days; i++)
            {
                DateTime date = startDay.AddDays(i);
                foreach (var slot in plan.Slots.Where(s => s.Enabled && s.Days.Contains(date.DayOfWeek)))
                {
                    if (!TimeSpan.TryParseExact(slot.Time, @"hh\:mm", CultureInfo.InvariantCulture, out var time)) continue;
                    DateTime local = DateTime.SpecifyKind(date + time, DateTimeKind.Unspecified);
                    if (zone.IsInvalidTime(local)) continue;
                    var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
                    var at = new DateTimeOffset(local, offset).ToUniversalTime();
                    if (at <= cursor || at > now || at < plan.EffectiveFrom.ToUniversalTime()) continue;
                    if (!startup && at == now) due.Add((plan, at));
                    else { missed++; if (earliest == null || at < earliest) earliest = at; if (latest == null || at > latest) latest = at; }
                }
            }
            if (missed > 0) store.History.Add(new RunRecord { Trigger="定时", PlanName=plan.Name, State="已错过", Due=earliest, Events=[$"未自动补跑：{missed} 个时间点；{TimeZoneInfo.ConvertTime(earliest!.Value, zone):O} 至 {TimeZoneInfo.ConvertTime(latest!.Value, zone):O}"] });
        }
        RunRecord? reservation = null; List<TaskSpec>? selected = null; List<int>? intervals = null;
        foreach (var candidate in due.GroupBy(x => x.Plan.Id).Select(g => g.First()))
        {
            var plan = candidate.Plan;
            var day = TimeZoneInfo.ConvertTime(candidate.Time, zone).DayOfWeek;
            var items = plan.Items.Where(i => i.Days.Contains(day)).ToList();
            var chosen = items.Select(i => store.Tasks.FirstOrDefault(t => t.Id == i.TaskId)).ToList();
            string? reason = engine.Running || startupBusy() || reservation != null ? "已有任务运行或等待处理" : engine.NeedsRecovery ? "存在待核实的中断" : chosen.Count == 0 ? "当天没有任务" : chosen.Any(t => t == null) ? "计划引用的任务不存在" : null;
            var entry = new RunRecord { Trigger="定时", PlanName=plan.Name, Due=candidate.Time, State=reason == null ? "运行中" : "已跳过" };
            entry.Events.Add(reason == null ? $"已保存定时触发意图；预定时间 {TimeZoneInfo.ConvertTime(candidate.Time, zone):O}" : $"定时跳过：{reason}；预定时间 {TimeZoneInfo.ConvertTime(candidate.Time, zone):O}");
            store.History.Add(entry);
            if (reason == null) { reservation=entry; selected=chosen.Select(t => t!.Copy()).ToList(); intervals=items.Select(i => i.IntervalSeconds).ToList(); }
        }
        store.ScheduleCursorUtc = now;
        try { store.Save(); }
        catch (Exception e) { store.ScheduleCursorUtc = previous; store.History.RemoveRange(originalCount, store.History.Count-originalCount); failed=true; Status="定时记录保存失败，自动调度已停止："+e.Message; changed(); return; }
        Status = ""; changed();
        if (reservation != null && selected != null) _ = engine.StartTasks(selected, reservation, intervals);
    }
}
