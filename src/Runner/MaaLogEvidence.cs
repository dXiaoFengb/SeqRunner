using System.Text;
using System.IO;

namespace Runner;

// These messages are evidence, not an authenticated statement about the game.
internal sealed class MaaLogEvidence(Action<string>? observe = null)
{
    readonly StringBuilder line = new();
    bool overlong;
    internal bool Started { get; private set; }
    internal bool Succeeded { get; private set; }
    internal bool Failed { get; private set; }
    internal bool Stopped { get; private set; }
    internal bool Uncertain { get; private set; }
    internal void ResetForRestart() { bool hadEvidence = Started || Succeeded; line.Clear(); overlong = false; Started = false; Succeeded = false; if (hadEvidence) observe?.Invoke("检测到更新或重启，先前 MAA 完成线索已作废"); }
    internal void Feed(string text)
    {
        foreach (char c in text)
        {
            if (c is '\r' or '\n') { FinishLine(); continue; }
            if (line.Length < 4096) line.Append(c); else overlong = true;
        }
    }
    internal void Finish() { if (line.Length > 0 || overlong) FinishLine(); }
    void FinishLine()
    {
        bool wasStarted = Started, wasSucceeded = Succeeded, wasFailed = Failed, wasStopped = Stopped;
        if (overlong) Uncertain = true;
        string text = line.ToString(); line.Clear(); overlong = false;
        if (text.Contains("开始任务:", StringComparison.Ordinal) || text.Contains("Start Task Chain:", StringComparison.Ordinal)) { if (Succeeded) Succeeded = false; Started = true; }
        if (text.Contains("任务出错:", StringComparison.Ordinal) || text.Contains("任务已完成，但出现错误", StringComparison.Ordinal) || text.Contains("任务已结束，以下任务出现错误", StringComparison.Ordinal) || text.Contains("未选择任务", StringComparison.Ordinal)) Failed = true;
        if (text.Contains("已停止", StringComparison.Ordinal)) Stopped = true;
        if (text.Contains("任务已全部完成！", StringComparison.Ordinal) && Started) Succeeded = true;
        if ((!wasStarted && Started) || (wasSucceeded && !Succeeded)) observe?.Invoke("MAA 日志观察到任务开始；业务结果仍待确认");
        if (!wasFailed && Failed) observe?.Invoke("MAA 日志观察到任务错误；需人工核实");
        if (!wasStopped && Stopped) observe?.Invoke("MAA 日志观察到任务停止；需人工核实");
        if (!wasSucceeded && Succeeded) observe?.Invoke("MAA 日志观察到完成文字；仍需等待进程退出和最终判定");
    }
}

internal sealed class MaaLogAnchor(FileStream file)
{
    byte[] expected = [];
    long offset;
    byte[] Read(long position, int count)
    {
        byte[] result = new byte[count]; int read = 0;
        while (read < count) { int n = RandomAccess.Read(file.SafeFileHandle, result.AsSpan(read), position + read); if (n == 0) throw new IOException("MAA 日志检查位置已失效"); read += n; }
        return result;
    }
    internal void Capture()
    {
        long end = file.Position; int count = (int)Math.Min(end, 256); offset = end - count; expected = Read(offset, count);
    }
    internal bool IsUnchanged() => expected.Length == 0 || Read(offset, expected.Length).AsSpan().SequenceEqual(expected);
}
