using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Runner;

// Isolated fixture transaction only. It is not connected to the installed application.
internal static class SelfUpdateTestTransaction
{
    sealed record Journal(string OldHash, string NewHash, string Phase);
    const string Marker = ".runner-update-test-root";

    internal static void Install(string root, Action<string>? checkpoint = null)
    {
        root = ValidateRoot(root);
        using var gate = Acquire(root);
        string current = Path.Combine(root, "current"), staged = Path.Combine(root, "staged"), previous = Path.Combine(root, "previous");
        if (File.Exists(Path.Combine(root, "journal.json")) || Directory.Exists(previous) || Directory.Exists(Path.Combine(root, "interrupted"))) throw new IOException("存在未完成事务或旧备份");
        string oldHash = Snapshot(current), newHash = Snapshot(staged);
        if (oldHash == newHash) throw new InvalidDataException("新旧程序内容相同，无法区分事务状态");
        var journal = new Journal(oldHash, newHash, "准备"); Write(root, journal); checkpoint?.Invoke("准备");
        Directory.Move(current, previous); checkpoint?.Invoke("旧程序已移动");
        Write(root, journal with { Phase = "旧程序已移动" });
        if (Snapshot(previous) != oldHash || Snapshot(staged) != newHash) throw new IOException("更新前文件发生变化");
        Directory.Move(staged, current); checkpoint?.Invoke("新程序已移动");
        if (Snapshot(current) != newHash || Snapshot(previous) != oldHash) throw new IOException("更新后文件校验失败");
        Write(root, journal with { Phase = "完成" }); checkpoint?.Invoke("完成");
    }

    internal static string Recover(string root, Action<string>? checkpoint = null)
    {
        root = ValidateRoot(root);
        using var gate = Acquire(root);
        string path = Path.Combine(root, "journal.json");
        if (!File.Exists(path)) return "无事务";
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新记录不能是链接");
        Journal journal;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { if (file.Length > 65536) throw new InvalidDataException("更新记录过大"); journal = JsonSerializer.Deserialize<Journal>(file) ?? throw new InvalidDataException("更新记录无效"); }
        if (journal.OldHash == null || journal.NewHash == null || journal.OldHash.Length != 64 || journal.NewHash.Length != 64 || journal.Phase is not ("准备" or "旧程序已移动" or "完成" or "已恢复")) throw new InvalidDataException("更新记录无效");
        string current = Path.Combine(root, "current"), previous = Path.Combine(root, "previous"), interrupted = Path.Combine(root, "interrupted");
        string? currentHash = Directory.Exists(current) ? Snapshot(current) : null;
        string? previousHash = Directory.Exists(previous) ? Snapshot(previous) : null;
        string? interruptedHash = Directory.Exists(interrupted) ? Snapshot(interrupted) : null;
        if (journal.Phase == "完成")
        {
            if (currentHash != journal.NewHash || previousHash != journal.OldHash || interruptedHash != null) throw new InvalidDataException("已完成事务内容不明");
            return "完成";
        }
        if (currentHash == journal.OldHash && previousHash == null && (interruptedHash == null || interruptedHash == journal.NewHash)) { Write(root, journal with { Phase = "已恢复" }); return "已恢复"; }
        if (previousHash != journal.OldHash || (currentHash != null && currentHash != journal.NewHash) || (interruptedHash != null && interruptedHash != journal.NewHash) || (currentHash != null && interruptedHash != null))
            throw new InvalidDataException("更新状态不明，需要人工核实");
        if (currentHash != null) { Directory.Move(current, interrupted); checkpoint?.Invoke("隔离新程序"); }
        Directory.Move(previous, current); checkpoint?.Invoke("还原旧程序");
        if (Snapshot(current) != journal.OldHash) throw new InvalidDataException("恢复后内容不明");
        Write(root, journal with { Phase = "已恢复" }); return "已恢复";
    }

    static string ValidateRoot(string root)
    {
        string path = Path.GetFullPath(root);
        if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, Marker))) throw new InvalidDataException("仅允许独立测试目录");
        if ((File.GetAttributes(Path.Combine(path, Marker)) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("测试标记不能是链接");
        for (DirectoryInfo? dir = new(path); dir != null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("测试目录包含链接");
        return path;
    }

    static FileStream Acquire(string root) => new(Path.Combine(root, ".update-lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    static void Write(string root, Journal journal)
    {
        string tmp = Path.Combine(root, "journal-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, journal); file.Flush(true); }
        File.Move(tmp, Path.Combine(root, "journal.json"), true);
    }
    static string Snapshot(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("事务目录不存在");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新目录包含链接");
        var files = new List<string>();
        void Visit(string dir)
        {
            foreach (var item in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新目录包含链接");
                if ((item.Attributes & FileAttributes.Directory) != 0) Visit(item.FullName);
                else
                {
                    if (item.Name.Equals("data.json", StringComparison.OrdinalIgnoreCase) || item.Name.StartsWith("mobile-notifications", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新目录包含用户数据");
                    files.Add(item.FullName);
                }
                if (files.Count > 1024) throw new InvalidDataException("更新目录文件过多");
            }
        }
        Visit(directory); if (files.Count == 0) throw new InvalidDataException("更新目录为空");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            byte[] name = Encoding.UTF8.GetBytes(relative); hash.AppendData(BitConverter.GetBytes(name.Length)); hash.AppendData(name);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            hash.AppendData(SHA256.HashData(input));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
