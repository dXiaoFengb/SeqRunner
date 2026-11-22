using System.Globalization;

namespace Runner;

internal sealed class SelfUpdateCoordinator
{
    readonly Store store;
    readonly Action changed;
    readonly Func<bool> trustedKeyAvailable;
    readonly Func<CancellationToken, Task<SelfUpdateRelease?>> check;
    bool checking;
    string attemptedDate = "";
    internal string Status { get; private set; } = "首版发布前，更新通道尚未启用";

    internal SelfUpdateCoordinator(Store store, Action changed, Func<bool> trustedKeyAvailable, Func<CancellationToken, Task<SelfUpdateRelease?>> check)
    { this.store = store; this.changed = changed; this.trustedKeyAvailable = trustedKeyAvailable; this.check = check; }

    internal async Task CheckAsync(CancellationToken cancellation = default)
    {
        if (checking) return;
        if (!trustedKeyAvailable()) { Status = "尚未配置可信发布公钥，更新通道未启用"; changed(); return; }
        checking = true; Status = "正在检查更新"; changed();
        try { var release = await check(cancellation); Status = release == null ? "没有发现新版本" : $"发现版本 {release.Tag}；更新包尚未验证"; }
        catch (OperationCanceledException) { Status = "更新检查已取消或超时"; }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.IO.IOException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException) { Status = "更新检查失败，请检查网络或稍后重试"; }
        finally { checking = false; changed(); }
    }

    internal Task OnStartupAsync() => store.UpdateCheckOnStartup ? CheckAsync() : Task.CompletedTask;

    internal async Task TickAsync(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!store.UpdateCheckScheduled) return;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        string day = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (local.ToString("HH:mm", CultureInfo.InvariantCulture) != store.UpdateCheckTime || string.CompareOrdinal(day, store.UpdateLastCheckDate) <= 0 || attemptedDate == day) return;
        attemptedDate = day;
        if (!trustedKeyAvailable()) { await CheckAsync(); return; }
        string previous = store.UpdateLastCheckDate; store.UpdateLastCheckDate = day;
        try { store.Save(); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { store.UpdateLastCheckDate = previous; Status = "更新检查记录保存失败，未发起检查"; changed(); return; }
        await CheckAsync();
    }
}
