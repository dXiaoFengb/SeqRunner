using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Runner.Checks")]

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

public sealed class RunRecord : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    string state = "运行中";
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public string State
    {
        get => state;
        set { if (state == value) return; state = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display))); }
    }
    public bool? AllSucceeded { get; set; }
    public DateTimeOffset? RecoveryAcknowledgedAt { get; set; }
    public string Trigger { get; set; } = "手动队列";
    public string PlanName { get; set; } = "";
    public DateTimeOffset? Due { get; set; }
    public List<string> Events { get; set; } = [];
    [JsonIgnore]
    public string Display => ToString();
    public override string ToString() => $"{Started:yyyy-MM-dd HH:mm:ss}　{Trigger}　{PlanName}　{State}";
}

public sealed class Store
{
    readonly string dir;
    readonly Func<string, FileStream> openTemporary;
    byte[]? knownHash;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public ObservableCollection<TaskSpec> Tasks { get; set; } = [];
    public List<RunRecord> History { get; set; } = [];
    public bool NoticeSeen { get; set; }
    public List<SchedulePlan> Plans { get; set; } = [];
    public DateTimeOffset? ScheduleCursorUtc { get; set; }
    public string ScheduleZoneId { get; set; } = "";
    public Dictionary<string,string> StartupDates { get; set; } = [];
    public Store(string dir) : this(dir, path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
    internal Store(string dir, Func<string, FileStream> openTemporary) { this.dir = dir; this.openTemporary = openTemporary; Directory.CreateDirectory(dir); }
    public void Load()
    {
        string path = Path.Combine(dir, "data.json");
        if (!File.Exists(path)) return;
        byte[] bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("Format", out var format) || format.GetString() != "SequentialRunnerV3/1") throw new InvalidDataException("数据格式不属于本软件，拒绝覆盖");
        var data = JsonSerializer.Deserialize<StoreData>(bytes, Json) ?? throw new InvalidDataException("数据为空");
        knownHash = SHA256.HashData(bytes);
        if (data.Tasks == null || data.History == null || data.Plans == null || data.StartupDates == null || data.StartupDates.Any(x => string.IsNullOrWhiteSpace(x.Key) || !DateOnly.TryParseExact(x.Value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) || data.Tasks.Any(t => t == null || string.IsNullOrWhiteSpace(t.Id)) || data.Tasks.Select(t => t.Id).Distinct().Count() != data.Tasks.Count || data.Plans.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id) || p.Items == null || p.Slots == null || p.StartupMode is not ("never" or "always" or "daily") || p.CompletionAction is not ("none" or "exit" or "shutdown" or "restart" or "sleep" or "hibernate") || p.CompletionDelayMinutes < 0 || p.CompletionDelayMinutes > 10080 || p.Items.Any(i => i == null || i.IntervalSeconds < 0 || i.IntervalSeconds > 86400 || string.IsNullOrWhiteSpace(i.TaskId) || i.Days == null || i.Days.Count == 0 || i.Days.Any(d => (int)d < 0 || (int)d > 6)) || p.Slots.Any(s => s == null || string.IsNullOrWhiteSpace(s.Id) || s.Days == null || s.Days.Count == 0 || s.Days.Any(d => (int)d < 0 || (int)d > 6) || !TimeSpan.TryParseExact(s.Time, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out _)))) throw new InvalidDataException("任务或计划格式无效，拒绝覆盖");
        if (data.Plans.Select(p => p.Id).Distinct().Count() != data.Plans.Count || data.Plans.Any(p => p.Slots.Select(s => s.Id).Distinct().Count() != p.Slots.Count)) throw new InvalidDataException("任务或计划标识重复，拒绝覆盖");
        Tasks = new(data.Tasks); History = data.History; NoticeSeen = data.NoticeSeen; Plans = data.Plans; ScheduleCursorUtc = data.ScheduleCursorUtc; ScheduleZoneId = data.ScheduleZoneId ?? ""; StartupDates = data.StartupDates ?? [];
        foreach (var r in History.Where(r => r.State == "运行中")) { r.State = "中断"; r.Events.Add($"{DateTimeOffset.Now:O} 异常中断；请核实，不会自动重跑"); }
        foreach (var r in History.Where(r => r.State is "操作等待" or "操作请求中")) { var requested=r.State=="操作请求中"; r.State=requested ? "操作结果不明" : "操作已取消"; r.Events.Add($"{DateTimeOffset.Now:O} 软件重启；{(requested ? "系统请求结果未核实" : "未执行的完成后操作已取消")}，不会自动恢复"); }
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
        if (exists != (knownHash != null)) throw new IOException("数据文件被其他程序更改，请核实后重新打开；未覆盖");
        if (exists)
        {
            using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(current), knownHash!)) throw new IOException("数据文件被其他程序更改，请核实后重新打开；未覆盖");
        }
        byte[] writtenHash;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        using (var f = openTemporary(tmp))
        using (var hashing = new HashingWriteStream(f, hash))
        {
            JsonSerializer.Serialize(hashing, new StoreData { Tasks = [.. Tasks], History = History, NoticeSeen = NoticeSeen, Plans = Plans, ScheduleCursorUtc = ScheduleCursorUtc, ScheduleZoneId = ScheduleZoneId, StartupDates = StartupDates }, Json);
            f.Flush(true);
            writtenHash = hash.GetHashAndReset();
        }
        File.Move(tmp, path, exists);
        knownHash = writtenHash;
    }
    sealed class HashingWriteStream(Stream target, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => target.Flush();
        public override void Write(byte[] buffer, int offset, int count) { target.Write(buffer, offset, count); hash.AppendData(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { target.Write(buffer); hash.AppendData(buffer); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    public sealed class StoreData { public string Format { get; set; } = "SequentialRunnerV3/1"; public List<TaskSpec> Tasks { get; set; } = []; public List<RunRecord> History { get; set; } = []; public bool NoticeSeen { get; set; } public List<SchedulePlan> Plans { get; set; } = []; public DateTimeOffset? ScheduleCursorUtc { get; set; } public string ScheduleZoneId { get; set; } = ""; public Dictionary<string,string> StartupDates { get; set; } = []; }
}
