using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;

namespace Runner;

public sealed class TaskSpec
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新任务";
    public string Program { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Completion { get; set; } = "进程退出";
    public string LogSource { get; set; } = "程序输出";
    public string LogPath { get; set; } = "";
    public string SuccessText { get; set; } = "";
    public bool ContinueOnFailure { get; set; }
    public bool TrustCompletion { get; set; }
    public bool SharedLog { get; set; }
    public int TimeoutMinutes { get; set; }
    public TaskSpec Copy() => (TaskSpec)MemberwiseClone();
}

public sealed class RunRecord
{
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public string State { get; set; } = "运行中";
    public string Trigger { get; set; } = "手动队列";
    public string PlanName { get; set; } = "";
    public DateTimeOffset? Due { get; set; }
    public List<string> Events { get; set; } = [];
    public override string ToString() => $"{Started:yyyy-MM-dd HH:mm:ss}　{Trigger}　{PlanName}　{State}";
}

public sealed class Store
{
    readonly string dir;
    byte[]? knownHash;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public ObservableCollection<TaskSpec> Tasks { get; set; } = [];
    public List<RunRecord> History { get; set; } = [];
    public bool NoticeSeen { get; set; }
    public List<SchedulePlan> Plans { get; set; } = [];
    public DateTimeOffset? ScheduleCursorUtc { get; set; }
    public string ScheduleZoneId { get; set; } = "";
    public Dictionary<string,string> StartupDates { get; set; } = [];
    public Store(string dir) { this.dir = dir; Directory.CreateDirectory(dir); }
    public void Load()
    {
        string path = Path.Combine(dir, "data.json");
        if (!File.Exists(path)) return;
        byte[] bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("Format", out var format) || format.GetString() != "SequentialRunnerV3/1") throw new InvalidDataException("数据格式不属于本软件，拒绝覆盖");
        var data = JsonSerializer.Deserialize<StoreData>(bytes, Json) ?? throw new InvalidDataException("数据为空");
        knownHash = SHA256.HashData(bytes);
        if (data.Tasks == null || data.History == null || data.Plans == null || data.StartupDates == null || data.StartupDates.Any(x => string.IsNullOrWhiteSpace(x.Key) || !DateOnly.TryParseExact(x.Value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) || data.Tasks.Any(t => t == null || string.IsNullOrWhiteSpace(t.Id)) || data.Tasks.Select(t => t.Id).Distinct().Count() != data.Tasks.Count || data.Plans.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id) || p.Items == null || p.Slots == null || p.StartupMode is not ("never" or "always" or "daily") || p.Items.Any(i => i == null || i.IntervalSeconds < 0 || i.IntervalSeconds > 86400 || string.IsNullOrWhiteSpace(i.TaskId) || i.Days == null || i.Days.Count == 0 || i.Days.Any(d => (int)d < 0 || (int)d > 6)) || p.Slots.Any(s => s == null || string.IsNullOrWhiteSpace(s.Id) || s.Days == null || s.Days.Count == 0 || s.Days.Any(d => (int)d < 0 || (int)d > 6) || !TimeSpan.TryParseExact(s.Time, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out _)))) throw new InvalidDataException("任务或计划格式无效，拒绝覆盖");
        if (data.Plans.Select(p => p.Id).Distinct().Count() != data.Plans.Count || data.Plans.Any(p => p.Slots.Select(s => s.Id).Distinct().Count() != p.Slots.Count)) throw new InvalidDataException("任务或计划标识重复，拒绝覆盖");
        Tasks = new(data.Tasks); History = data.History; NoticeSeen = data.NoticeSeen; Plans = data.Plans; ScheduleCursorUtc = data.ScheduleCursorUtc; ScheduleZoneId = data.ScheduleZoneId ?? ""; StartupDates = data.StartupDates ?? [];
        foreach (var r in History.Where(r => r.State == "运行中")) { r.State = "中断"; r.Events.Add($"{DateTimeOffset.Now:O} 异常中断；请核实，不会自动重跑"); }
        Save();
    }
    public void Save()
    {
        string lockId = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToUpperInvariant())));
        using var gate = new Mutex(false, "Global\\SequentialRunnerV3-Store-" + lockId);
        bool acquired = false;
        try { try { acquired = gate.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { acquired = true; } if (!acquired) throw new IOException("数据保存正被占用，请稍后重试"); SaveLocked(); }
        finally { if (acquired) gate.ReleaseMutex(); }
    }
    void SaveLocked()
    {
        string path = Path.Combine(dir, "data.json");
        string tmp = Path.Combine(dir, $"data.{Guid.NewGuid():N}.tmp");
        bool exists = File.Exists(path);
        if (exists != (knownHash != null) || (exists && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), knownHash!))) throw new IOException("数据文件被其他程序更改，请核实后重新打开；未覆盖");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new StoreData { Tasks = [.. Tasks], History = History, NoticeSeen = NoticeSeen, Plans = Plans, ScheduleCursorUtc = ScheduleCursorUtc, ScheduleZoneId = ScheduleZoneId, StartupDates = StartupDates }, Json);
        using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(bytes); f.Flush(true); }
        File.Move(tmp, path, exists);
        knownHash = SHA256.HashData(bytes);
    }
    public sealed class StoreData { public string Format { get; set; } = "SequentialRunnerV3/1"; public List<TaskSpec> Tasks { get; set; } = []; public List<RunRecord> History { get; set; } = []; public bool NoticeSeen { get; set; } public List<SchedulePlan> Plans { get; set; } = []; public DateTimeOffset? ScheduleCursorUtc { get; set; } public string ScheduleZoneId { get; set; } = ""; public Dictionary<string,string> StartupDates { get; set; } = []; }
}
