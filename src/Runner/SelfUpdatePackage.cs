using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Runner;

// The manifest is signed as exact UTF-8 bytes, before its contents are trusted.
internal static class SelfUpdatePackage
{
    internal sealed record FileEntry(string Path, long Size, string Sha256);
    internal sealed record Manifest(string Version, FileEntry[] Files);
    const long MaxFile = 256L * 1024 * 1024;
    const long MaxTotal = 512L * 1024 * 1024;
    const int MaxManifest = 1024 * 1024;

    internal static Manifest Verify(string package, RSA trustedKey, Version currentVersion)
    {
        using var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return VerifyArchive(zip, trustedKey, currentVersion);
    }

    static Manifest VerifyArchive(ZipArchive zip, RSA trustedKey, Version currentVersion)
    {
        if (zip.Entries.Count > 1026) throw new InvalidDataException("更新包文件过多");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            string name = ValidatePath(entry.FullName);
            if (!entries.TryAdd(name, entry)) throw new InvalidDataException("更新包包含重复路径");
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType != 0 && unixType != 0x8000) throw new InvalidDataException("更新包包含非普通文件");
        }
        if (!entries.Remove(".v3-update/manifest.json", out var manifestEntry) || !entries.Remove(".v3-update/manifest.sig", out var signatureEntry))
            throw new InvalidDataException("更新包缺少签名清单");
        byte[] manifestBytes = ReadBounded(manifestEntry, MaxManifest);
        byte[] signature = ReadBounded(signatureEntry, 8192);
        if (!trustedKey.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("更新包签名无效");
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes) ?? throw new InvalidDataException("更新清单无效");
        if (!System.Version.TryParse(manifest.Version, out var version) || version.Build < 0 || version.Revision >= 0 || manifest.Version != $"{version.Major}.{version.Minor}.{version.Build}" || version <= new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build)) || manifest.Files == null || manifest.Files.Length == 0 || manifest.Files.Length != entries.Count)
            throw new InvalidDataException("更新版本或文件清单无效");
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Files)
        {
            if (item == null) throw new InvalidDataException("文件清单无效");
            string name = ValidatePath(item.Path);
            if (name.StartsWith(".v3-update/", StringComparison.OrdinalIgnoreCase) || !seen.Add(name) || !entries.TryGetValue(name, out var entry) || item.Size < 0 || item.Size > MaxFile || entry.Length != item.Size)
                throw new InvalidDataException("更新包文件不匹配");
            total = checked(total + item.Size);
            if (total > MaxTotal || item.Sha256 == null || item.Sha256.Length != 64) throw new InvalidDataException("更新包大小或摘要无效");
            byte[] expected;
            try { expected = Convert.FromHexString(item.Sha256); } catch (FormatException) { throw new InvalidDataException("更新包摘要无效"); }
            using var content = entry.Open(); using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536]; long actual = 0; int read;
            while ((read = content.Read(buffer)) > 0) { actual += read; if (actual > item.Size) throw new InvalidDataException("更新包文件超出声明大小"); digest.AppendData(buffer, 0, read); }
            if (actual != item.Size || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), expected)) throw new InvalidDataException("更新包摘要不匹配");
        }
        return manifest;
    }

    internal static string Stage(string package, RSA trustedKey, Version currentVersion, Version expectedVersion, string isolatedRoot)
    {
        if (!Directory.Exists(isolatedRoot)) throw new DirectoryNotFoundException("更新暂存目录不存在");
        var root = new DirectoryInfo(Path.GetFullPath(isolatedRoot));
        for (DirectoryInfo? current = root; current != null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("更新暂存目录包含链接");
        using var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Manifest manifest = VerifyArchive(zip, trustedKey, currentVersion);
        if (manifest.Version != $"{expectedVersion.Major}.{expectedVersion.Minor}.{expectedVersion.Build}") throw new InvalidDataException("更新包版本与发布信息不一致");
        string stage = Path.Combine(root.FullName, "stage-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(stage)) throw new IOException("更新暂存目录已存在");
        Directory.CreateDirectory(stage);
        foreach (var item in manifest.Files)
        {
            string target = Path.GetFullPath(Path.Combine(stage, item.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包路径越界");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = zip.GetEntry(item.Path)?.Open() ?? throw new InvalidDataException("更新文件不存在");
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536]; long total = 0; int count;
            while ((count = input.Read(buffer)) > 0) { total += count; if (total > item.Size) throw new InvalidDataException("更新文件过大"); output.Write(buffer, 0, count); hash.AppendData(buffer, 0, count); }
            output.Flush(true);
            if (total != item.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(item.Sha256))) throw new InvalidDataException("更新暂存校验失败");
        }
        return stage;
    }

    static byte[] ReadBounded(ZipArchiveEntry entry, int limit)
    {
        if (entry.Length > limit) throw new InvalidDataException("更新包元数据过大");
        using var input = entry.Open(); using var memory = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = input.Read(buffer)) > 0) { if (memory.Length + count > limit) throw new InvalidDataException("更新包元数据过大"); memory.Write(buffer, 0, count); }
        return memory.ToArray();
    }

    static string ValidatePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || value.Contains('\\') || value.StartsWith('/') || value.EndsWith('/') || value.Contains(':'))
            throw new InvalidDataException("更新包路径无效");
        string[] parts = value.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c))))
            throw new InvalidDataException("更新包路径无效");
        foreach (string part in parts)
        {
            string stem = part.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
                throw new InvalidDataException("更新包包含 Windows 保留文件名");
        }
        string filename = parts[^1];
        if (filename.Equals("data.json", StringComparison.OrdinalIgnoreCase) || filename.StartsWith("mobile-notifications", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包不得包含用户数据");
        return value;
    }
}
