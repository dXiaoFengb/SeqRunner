using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Runner;

// This entry point is restricted to the workspace's isolated .build directories.
internal static class SelfUpdateSimulation
{
    internal static bool Allowed(string directory)
    {
        try
        {
            string workspace = Path.GetFullPath(Environment.CurrentDirectory);
            if (!File.Exists(Path.Combine(workspace, "src", "Runner", "Runner.csproj")) || !File.Exists(Path.Combine(workspace, "tests", "Runner.Checks", "Runner.Checks.csproj"))) return false;
            string build = Path.Combine(workspace, ".build");
            string full = Path.GetFullPath(directory);
            if (!full.StartsWith(build + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full)) return false;
            bool reachedBuild = false;
            for (DirectoryInfo? dir = new(full); dir != null; dir = dir.Parent)
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(dir.FullName, build, StringComparison.OrdinalIgnoreCase)) reachedBuild = true;
            }
            return reachedBuild;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }

    internal static async Task RunAsync(string dataDirectory, SelfUpdateInterlock gate, Func<bool> idle, Func<string, bool> reserve, Action<string> progress, CancellationToken cancellation)
    {
        if (!Allowed(dataDirectory)) throw new InvalidOperationException("隔离测试仅能在工作区 .build 目录中运行");
        bool reserved = false, helperStarted = false, helperExited = false; string? fixture = null;
        void Report(string message)
        {
            if (fixture == null) { progress(message); return; }
            string temporary = Path.Combine(fixture, "status-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, new { Status = message, Time = DateTimeOffset.UtcNow }); file.Flush(true); }
            File.Move(temporary, Path.Combine(fixture, "status.json"), true); progress(message);
        }
        try
        {
            fixture = Path.Combine(dataDirectory, "update-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture, ".runner-update-test-root"), "fixture");
            Directory.CreateDirectory(Path.Combine(fixture, "current")); File.WriteAllText(Path.Combine(fixture, "current", "Runner.fixture"), "old");
            using var key = RSA.Create(2048); byte[] payload = Encoding.UTF8.GetBytes("new");
            var manifest = new SelfUpdatePackage.Manifest("1.20.0", [new("Runner.fixture", payload.Length, Convert.ToHexString(SHA256.HashData(payload)))]);
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest); byte[] signature = key.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
            {
                void Add(string name, byte[] bytes) { using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
                Add(".v3-update/manifest.json", manifestBytes); Add(".v3-update/manifest.sig", signature); Add("Runner.fixture", payload);
            }
            using var client = new SelfUpdateClient(new FixtureHandler(archive.ToArray()));
            Report("隔离测试：检查模拟版本"); var release = await client.CheckAsync(new Version(1, 19, 0), cancellation) ?? throw new InvalidDataException("测试版本不存在");
            Report("隔离测试：下载模拟包"); string package = await client.DownloadAsync(release, fixture, cancellation);
            Report("隔离测试：验证签名与文件"); var verified = SelfUpdatePackage.Verify(package, key, new Version(1, 19, 0));
            if (verified.Version != "1.20.0") throw new InvalidDataException("测试版本不一致");
            string publicKey = Path.Combine(fixture, "public-key.der"); File.WriteAllBytes(publicKey, key.ExportSubjectPublicKeyInfo());
            Report("隔离测试：等待任务空闲");
            while (!idle()) { cancellation.ThrowIfCancellationRequested(); await Task.Delay(250, cancellation); }
            cancellation.ThrowIfCancellationRequested();
            if (!reserve(Path.GetFileName(fixture))) throw new IOException("无法建立更新意图"); reserved = true;
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--isolated-update-helper"); start.ArgumentList.Add(fixture); start.ArgumentList.Add(package); start.ArgumentList.Add(publicKey);
            Report("隔离测试：独立进程安装模拟程序");
            using var helper = Process.Start(start) ?? throw new IOException("无法启动测试进程");
            helperStarted = true;
            _ = gate.TryRecordHelper(helper); // Failure leaves the intent blocked if the parent exits unexpectedly.
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            helperExited = true;
            if (helper.ExitCode != 0 || SelfUpdateTestTransaction.Recover(fixture) != "完成") throw new IOException("测试安装未确认");
            if (!gate.TryAcknowledge(() => SelfUpdateTestTransaction.Recover(fixture) == "完成")) throw new IOException("测试结果保存失败");
            reserved = false; Report("隔离测试：完成；正式程序没有更新");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidOperationException or OperationCanceledException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            if (helperStarted && !helperExited) { try { Report("隔离测试：测试进程状态不明，更新意图待人工核实"); } catch (Exception write) when (write is IOException or UnauthorizedAccessException) { progress("隔离测试：状态保存失败，测试进程待人工核实"); } return; }
            if (reserved && fixture != null)
            {
                try { Report("隔离测试：检查并恢复模拟程序"); } catch (Exception write) when (write is IOException or UnauthorizedAccessException) { progress("隔离测试：状态保存失败，正在检查恢复"); }
                try
                {
                    string current = Path.Combine(fixture, "current", "Runner.fixture");
                    bool safe = File.Exists(Path.Combine(fixture, "journal.json")) ? SelfUpdateTestTransaction.Recover(fixture) == "已恢复" : File.Exists(current) && File.ReadAllText(current) == "old" && !Directory.Exists(Path.Combine(fixture, "previous"));
                    if (safe) gate.TryAcknowledge(() => true);
                }
                catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException or JsonException) { }
            }
            string failure = gate.Blocked ? "隔离测试：失败，更新意图待人工核实" : "隔离测试：失败或取消；正式程序没有更新";
            try { Report(failure); } catch (Exception write) when (write is IOException or UnauthorizedAccessException) { progress("隔离测试：状态保存失败，请检查隔离目录"); }
        }
    }

    internal static int RunHelper(string[] args)
    {
        try
        {
            if (args.Length != 4) return 1;
            string root = Path.GetFullPath(args[1]), package = Path.GetFullPath(args[2]), publicKey = Path.GetFullPath(args[3]);
            string prefix = root + Path.DirectorySeparatorChar;
            if (!Allowed(root) || !File.Exists(Path.Combine(root, ".runner-update-test-root")) || !package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !publicKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(package), root, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(publicKey), root, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(package) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(publicKey) & FileAttributes.ReparsePoint) != 0 || new FileInfo(publicKey).Length > 8192) return 1;
            using var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(File.ReadAllBytes(publicKey), out _);
            string stage = SelfUpdatePackage.Stage(package, key, new Version(1, 19, 0), new Version(1, 20, 0), root);
            Directory.Move(stage, Path.Combine(root, "staged")); SelfUpdateTestTransaction.Install(root); return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException) { return 5; }
    }

    sealed class FixtureHandler(byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (request.RequestUri?.Host == "api.github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v1.20.0\",\"assets\":[{\"name\":\"SequentialRunnerV3-v1.20.0.zip\",\"browser_download_url\":\"https://github.com/dXiaoFengb/SeqRunner/releases/download/v1.20.0/SequentialRunnerV3-v1.20.0.zip\"}]}") });
            if (request.RequestUri?.Host == "github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
            throw new InvalidOperationException("非模拟地址");
        }
    }
}
