using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Security.Cryptography;

namespace Runner;

public sealed class TaskSpec
{
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
}

public sealed class RunRecord
{
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public string State { get; set; } = "运行中";
    public List<string> Events { get; set; } = [];
    public override string ToString() => $"{Started:yyyy-MM-dd HH:mm:ss}　{State}";
}

public sealed class Store
{
    readonly string dir;
    byte[]? knownHash;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public ObservableCollection<TaskSpec> Tasks { get; set; } = [];
    public List<RunRecord> History { get; set; } = [];
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
        Tasks = new(data.Tasks); History = data.History;
        foreach (var r in History.Where(r => r.State == "运行中")) { r.State = "中断"; r.Events.Add($"{DateTimeOffset.Now:O} 异常中断；请核实，不会自动重跑"); }
        Save();
    }
    public void Save()
    {
        string path = Path.Combine(dir, "data.json");
        string tmp = Path.Combine(dir, $"data.{Guid.NewGuid():N}.tmp");
        bool exists = File.Exists(path);
        if (exists != (knownHash != null) || (exists && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), knownHash!))) throw new IOException("数据文件被其他程序更改，请核实后重新打开；未覆盖");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new StoreData { Tasks = [.. Tasks], History = History }, Json);
        using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(bytes); f.Flush(true); }
        File.Move(tmp, path, exists);
        knownHash = SHA256.HashData(bytes);
    }
    public sealed class StoreData { public string Format { get; set; } = "SequentialRunnerV3/1"; public List<TaskSpec> Tasks { get; set; } = []; public List<RunRecord> History { get; set; } = []; }
}
