namespace Runner;

internal sealed record LocalNotice(string Title, string Message, bool IsWarning = false);

// Delivery is best effort and must never influence task execution.
internal sealed class LocalNotifications(Func<bool> enabled, Action<LocalNotice> send)
{
    readonly Dictionary<string, string> activeProblems = new();
    internal void Publish(LocalNotice notice)
    {
        if (!enabled()) return;
        try { send(notice); } catch (Exception) { }
    }
    internal void ObserveProblem(string source, string? status)
    {
        bool problem = !string.IsNullOrEmpty(status) && (status.Contains("保存失败", StringComparison.Ordinal) || status.Contains("记录失败", StringComparison.Ordinal));
        if (!problem) { activeProblems.Remove(source); return; }
        if (activeProblems.TryGetValue(source, out var previous) && previous == status) return;
        activeProblems[source] = status!;
        Publish(new("需要处理", "运行记录或配置保存失败，请打开软件查看。", true));
    }
    internal static string Name(string? value)
    {
        string name = string.IsNullOrWhiteSpace(value) ? "任务" : value.Trim();
        name = string.Concat(name.Select(c => char.IsControl(c) ? ' ' : c));
        return name.Length > 60 ? name[..60] + "…" : name;
    }
}
