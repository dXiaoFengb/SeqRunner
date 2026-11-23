namespace Runner;

internal static class UpdateMarkers
{
    internal static readonly string[] BuiltIn = ["正在更新", "更新中", "正在重启", "重启中", "updating", "restarting"];
    internal static readonly string[] Maa622 = [
        "Pending update package detected, applying before full startup",
        "Pending update package handed off to external updater, exiting current process",
        "Delegated pending update failed. Reason:"];
    internal static bool TryParse(string? input, out string[] markers)
    {
        markers = (input ?? "").Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        return markers.Length <= 32 && markers.All(x => x.Length <= 256);
    }
    internal static string? Find(string text, string[] markers) => markers.FirstOrDefault(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
}

internal sealed class LineEvidence
{
    readonly int overlap;
    string tail = "";
    internal LineEvidence(int overlap) => this.overlap = overlap;
    internal void Feed(string text, Action<string> observe)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n')) continue;
            Segment(text.AsSpan(start, i - start), observe); tail = ""; start = i + 1;
        }
        Segment(text.AsSpan(start), observe);
    }
    void Segment(ReadOnlySpan<char> segment, Action<string> observe)
    {
        if (segment.IsEmpty) return;
        string combined = tail + segment.ToString(); observe(combined);
        tail = overlap == 0 ? "" : combined.Length <= overlap ? combined : combined[^overlap..];
    }
}
