using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;

namespace Runner;

internal sealed record SelfUpdateRelease(Version Version, string Tag, Uri PackageUri);

internal sealed class SelfUpdateClient : IDisposable
{
    const string Api = "https://api.github.com/repos/dXiaoFengb/SeqRunner/releases/latest";
    readonly HttpClient http;
    internal SelfUpdateClient(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SequentialRunnerV3-Updater/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal async Task<SelfUpdateRelease?> CheckAsync(Version current, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(Api, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var buffer = new MemoryStream(); byte[] bytes = new byte[8192]; int count;
        while ((count = await input.ReadAsync(bytes, cancellation)) > 0) { if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("更新信息过大"); buffer.Write(bytes, 0, count); }
        using var json = JsonDocument.Parse(buffer.ToArray()); var root = json.RootElement;
        if (!root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False || !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("更新不是正式版本");
        if (!root.TryGetProperty("tag_name", out var tagNode) || tagNode.ValueKind != JsonValueKind.String) throw new InvalidDataException("更新版本无效");
        string tag = tagNode.GetString() ?? "";
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) || version.Build < 0 || version.Revision >= 0 || tag != $"v{version.Major}.{version.Minor}.{version.Build}") throw new InvalidDataException("更新版本无效");
        if (version <= new Version(current.Major, current.Minor, Math.Max(0, current.Build))) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) throw new InvalidDataException("更新附件缺失");
        string expectedName = "SequentialRunnerV3-" + tag + ".zip";
        Uri? package = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name) || name.GetString() != expectedName) continue;
            if (package != null || !asset.TryGetProperty("browser_download_url", out var url) || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/dXiaoFengb/SeqRunner/releases/download/" + tag + "/" + expectedName)
                throw new InvalidDataException("更新附件来源不明");
            package = uri;
        }
        return new SelfUpdateRelease(version, tag, package ?? throw new InvalidDataException("更新附件缺失"));
    }

    internal async Task<string> DownloadAsync(SelfUpdateRelease release, string isolatedDirectory, CancellationToken cancellation)
    {
        if (!Directory.Exists(isolatedDirectory)) throw new DirectoryNotFoundException("更新暂存目录不存在");
        string expectedPath = "/dXiaoFengb/SeqRunner/releases/download/" + release.Tag + "/SequentialRunnerV3-" + release.Tag + ".zip";
        Uri uri = release.PackageUri;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.IsDefaultPort || uri.AbsolutePath != expectedPath || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new InvalidDataException("更新附件来源不明");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirects == 3 || response.Headers.Location == null) throw new InvalidDataException("更新下载重定向无效");
                uri = new Uri(uri, response.Headers.Location);
                if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("更新下载来源不明");
                continue;
            }
            response.EnsureSuccessStatusCode();
            const long maxBytes = 512L * 1024 * 1024;
            if (response.Content.Headers.ContentLength is long length && length > maxBytes) throw new InvalidDataException("更新下载过大");
            string path = Path.Combine(isolatedDirectory, "update-" + Guid.NewGuid().ToString("N") + ".unverified");
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            byte[] buffer = new byte[65536]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token)) > 0)
            {
                total += count; if (total > maxBytes) throw new InvalidDataException("更新下载过大");
                await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
            }
            await output.FlushAsync(deadline.Token); output.Flush(true);
            return path;
        }
        throw new InvalidDataException("更新下载失败");
    }
    public void Dispose() => http.Dispose();
}
