using System.Diagnostics;
using System.IO;
using System.Text;
using Runner;

internal static class Utf8EvidenceProbe
{
    internal sealed record ProbeResult(bool Success, bool StopConfirmed, bool TimedOut);
    sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
    }
    sealed class PrefixThenOneByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        bool first = true;
        int Limit(int count) { int limit = first ? 3 : 1; first = false; return Math.Min(count, limit); }
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Limit(count));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Limit(buffer.Length)]);
    }

    internal static string DecodeStream(byte[] bytes, Encoding encoding)
    {
        using var stream = new OneByteStream(bytes);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
    internal static string DecodeStreamWithCompletePrefix(byte[] bytes, Encoding encoding)
    {
        using var stream = new PrefixThenOneByteStream(bytes);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static async Task<byte[]> CaptureFixtureBytes()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("apphost unavailable");
        if (!string.Equals(Path.GetFileNameWithoutExtension(executable), "Runner.Checks", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("run Runner.Checks.exe apphost");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("fixture-split-utf8");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("fixture start failed");
        DateTime created = process.StartTime.ToUniversalTime();
        Console.WriteLine($"DETAIL utf8 raw fixture started pid={process.Id} created={created:O}");
        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var errors = process.StandardError.ReadToEndAsync();
        try { await Task.WhenAll(copy, errors, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException)
        {
            Console.WriteLine("DETAIL utf8 raw fixture timed out; exit unconfirmed; no forced termination");
            throw;
        }
        Console.WriteLine($"DETAIL utf8 raw fixture exited={process.HasExited} exitCode={process.ExitCode} stderrLength={errors.Result.Length}");
        if (process.ExitCode != 0 || output.Length > 128) throw new InvalidDataException("invalid fixture output");
        return output.ToArray();
    }

    internal static string DecodeOneByteAtATime(byte[] bytes, Encoding encoding)
    {
        var decoder = encoding.GetDecoder(); var builder = new StringBuilder(); var chars = new char[8];
        for (int i = 0; i < bytes.Length; i++)
        {
            int count = decoder.GetChars(bytes, i, 1, chars, 0, i == bytes.Length - 1);
            builder.Append(chars, 0, count);
        }
        return builder.ToString();
    }

    internal static async Task<ProbeResult> RunEngine(string root, string fixture = "fixture-split-utf8")
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("apphost unavailable");
        if (!string.Equals(Path.GetFileNameWithoutExtension(executable), "Runner.Checks", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("run Runner.Checks.exe apphost");
        var store = new Store(root);
        if (fixture is not ("fixture-split-utf8" or "fixture-split-utf8-bom" or "fixture-utf8-missing")) throw new ArgumentException("unknown fixture");
        store.Tasks.Add(new TaskSpec { Name="utf", Program=executable, Arguments=fixture, WorkingDirectory=AppContext.BaseDirectory, Completion="日志信号", SuccessText="完成", TrustCompletion=true });
        var engine = new RunnerEngine(store, () => { });
        var run = engine.Start(); var watch = Stopwatch.StartNew();
        while (!run.IsCompleted && !engine.Waiting && watch.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(50);
        bool timedOut = !run.IsCompleted;
        var history = store.History.SingleOrDefault();
        Console.WriteLine($"DETAIL utf8 engine fixture={fixture} receiverCodePage={Console.OutputEncoding.CodePage} completed={run.IsCompleted} waiting={engine.Waiting} elapsedMs={watch.ElapsedMilliseconds} status={engine.Status}");
        if (history != null) foreach (var item in history.Events.Where(x => x.Contains("已启动 utf（PID", StringComparison.Ordinal) || x.Contains("utf 输出：", StringComparison.Ordinal) || x.Contains("utf 成功", StringComparison.Ordinal) || x.Contains("未发现", StringComparison.Ordinal)))
        {
            int path = item.IndexOf("，路径 ", StringComparison.Ordinal);
            Console.WriteLine("DETAIL utf8 engine event=" + (path >= 0 ? item[..path] : item));
        }
        if (timedOut)
        {
            engine.Stop();
            try { await run.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException) { Console.WriteLine("DETAIL utf8 stop unconfirmed; no forced termination"); return new(false, false, true); }
            Console.WriteLine("DETAIL utf8 stop confirmed after waiting or timeout");
            return new(false, true, true);
        }
        await run;
        return new(history?.AllSucceeded == true, true, false);
    }
}
