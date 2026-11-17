using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Runner;

internal static class OcrHelperClient
{
    internal sealed record Line(string Text, double? Confidence, IReadOnlyList<(double X, double Y)> Points);
    internal static string ResolveHelperPath()
    {
        string installed = Path.Combine(AppContext.BaseDirectory, "视觉组件", "OcrHelper.exe");
        return File.Exists(installed) ? installed : Path.Combine(Environment.CurrentDirectory, "工具", "视觉组件", "bin", "Debug", "net10.0-windows", "win-x64", "OcrHelper.exe");
    }

    internal static async Task<IReadOnlyList<Line>> RecognizeAsync(string imagePath, string helperPath, TimeSpan timeout)
    {
        if (!File.Exists(imagePath)) throw new FileNotFoundException("图片不存在");
        return await RunAsync(Path.GetFullPath(imagePath), null, helperPath, timeout);
    }

    internal static Task<IReadOnlyList<Line>> RecognizeBytesAsync(byte[] image, string helperPath, TimeSpan timeout, CancellationToken cancellation = default) => RunAsync("--stdin", image, helperPath, timeout, cancellation);

    static async Task<IReadOnlyList<Line>> RunAsync(string argument, byte[]? image, string helperPath, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (!File.Exists(helperPath)) throw new FileNotFoundException("OCR 组件不可用，请检查安装");
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = image != null, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("无法启动 OCR 组件");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            async Task WriteImage() { if (image != null) { await process.StandardInput.BaseStream.WriteAsync(image); process.StandardInput.Close(); } }
            await Task.WhenAll(WriteImage(), process.WaitForExitAsync()).WaitAsync(timeout, cancellation);
        }
        catch (TimeoutException)
        {
            try { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } } catch (InvalidOperationException) { }
            throw new TimeoutException("OCR 识别超时");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } } catch (InvalidOperationException) { }
            throw;
        }
        string json = await output; _ = await errors;
        if (process.ExitCode != 0) throw new IOException("OCR 组件未能识别图片");
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array) throw new InvalidDataException("OCR 结果格式无效");
        var result = new List<Line>();
        foreach (var line in lines.EnumerateArray())
        {
            string text = line.GetProperty("text").GetString() ?? "";
            var confidence = line.GetProperty("confidence");
            double? score = confidence.ValueKind == JsonValueKind.Number ? confidence.GetDouble() : null;
            var points = line.GetProperty("points").EnumerateArray().Select(p => (p.GetProperty("x").GetDouble(), p.GetProperty("y").GetDouble())).ToArray();
            if (points.Length != 4) throw new InvalidDataException("OCR 坐标无效");
            result.Add(new Line(text, score, points));
        }
        return result;
    }
}
