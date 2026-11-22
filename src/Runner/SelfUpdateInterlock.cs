using System.IO;
using System.Text.Json;
using System.Diagnostics;

namespace Runner;

// A persisted intent blocks task starts until a future authenticated recovery clears it.
internal sealed class SelfUpdateInterlock(string dataDirectory)
{
    readonly string path = Path.Combine(dataDirectory, "self-update-intent.json");
    sealed record Intent(string Format, DateTimeOffset Created, string? Fixture = null, int? HelperPid = null, long? HelperStartTicks = null);
    internal bool Blocked
    {
        get
        {
            try { _ = File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
        }
    }

    internal bool TryReserve(Func<bool> safeToStart, string? fixture = null)
    {
        if (!safeToStart() || Blocked || (fixture != null && !ValidFixture(fixture))) return false;
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, new Intent("SequentialRunnerV3/update-intent/1", DateTimeOffset.UtcNow, fixture));
            file.Flush(true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static bool ValidFixture(string name) => name.Length == 47 && name.StartsWith("update-fixture-", StringComparison.Ordinal) && name[15..].All(char.IsAsciiHexDigit);

    internal bool TryRecordHelper(Process process)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            Intent? intent;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { if (input.Length > 4096) return false; intent = JsonSerializer.Deserialize<Intent>(input); }
            if (intent?.Format != "SequentialRunnerV3/update-intent/1" || intent.Fixture == null || !ValidFixture(intent.Fixture) || intent.HelperPid != null) return false;
            long start = process.StartTime.ToUniversalTime().Ticks; int pid = process.Id;
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, intent with { HelperPid = pid, HelperStartTicks = start }); file.Flush(true); }
            File.Move(temp, path, true); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { return false; }
    }

    internal string RecoverIsolatedFixture()
    {
        try
        {
            if (!SelfUpdateSimulation.Allowed(dataDirectory) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return "更新记录需要人工核实";
            Intent? intent;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { if (input.Length > 4096) return "更新记录需要人工核实"; intent = JsonSerializer.Deserialize<Intent>(input); }
            if (intent?.Format != "SequentialRunnerV3/update-intent/1" || intent.Fixture == null || !ValidFixture(intent.Fixture) || intent.HelperPid is not > 0 || intent.HelperStartTicks == null) return "更新进程身份不明，需要人工核实";
            try
            {
                using var process = Process.GetProcessById(intent.HelperPid.Value);
                if (process.StartTime.ToUniversalTime().Ticks != intent.HelperStartTicks.Value) return "更新进程身份不明，需要人工核实";
                if (!process.HasExited) return "更新测试进程仍在运行，请稍后再核实";
            }
            catch (ArgumentException) { /* The recorded process no longer exists. */ }
            string fixture = Path.Combine(dataDirectory, intent.Fixture);
            if (!SelfUpdateSimulation.Allowed(fixture)) return "隔离测试目录无法核实";
            string outcome = SelfUpdateTestTransaction.Recover(fixture);
            bool verified = outcome is "完成" or "已恢复";
            if (!verified && outcome == "无事务")
            {
                string currentDirectory = Path.Combine(fixture, "current"), current = Path.Combine(currentDirectory, "Runner.fixture");
                verified = Directory.Exists(currentDirectory) && (File.GetAttributes(currentDirectory) & FileAttributes.ReparsePoint) == 0 && File.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0 && new FileInfo(current).Length == 3 && File.ReadAllText(current) == "old" && !Directory.Exists(Path.Combine(fixture, "previous"));
            }
            if (!verified || !TryAcknowledge(() => true)) return "更新结果不明，需要人工核实";
            return outcome == "完成" ? "隔离测试已完成，正式程序没有更新" : "隔离测试已恢复旧程序，正式程序没有更新";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { return "更新状态不明，需要人工核实"; }
    }

    internal bool TryAcknowledge(Func<bool> transactionVerified)
    {
        try
        {
            if (!Blocked || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > 4096) return false;
            using var document = JsonDocument.Parse(file);
            if (!document.RootElement.TryGetProperty("Format", out var format) || format.GetString() != "SequentialRunnerV3/update-intent/1" || !transactionVerified()) return false;
            File.Move(path, path + ".resolved-" + Guid.NewGuid().ToString("N")); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return false; }
    }
}
