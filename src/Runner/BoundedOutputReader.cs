using System.IO;
using System.Text;

namespace Runner;

public static class BoundedOutputReader
{
    public static async Task ReadAsync(TextReader reader, Action<string> evidence, Action<string> line, int overlapLength)
    {
        if (overlapLength < 0 || overlapLength > 4095) throw new ArgumentOutOfRangeException(nameof(overlapLength));
        var prefix = new StringBuilder(4096);
        var buffer = new char[4096];
        string tail = "";
        bool truncated = false, previousCR = false;
        int completed = 0, blocks = 0;
        void Segment(int start, int count)
        {
            if (count == 0) return;
            int remaining = 4096 - prefix.Length;
            int keep = Math.Min(remaining, count);
            if (keep > 0) prefix.Append(buffer, start, keep);
            if (count > keep) truncated = true;
            string observed = tail + new string(buffer, start, count);
            evidence(observed);
            tail = overlapLength == 0 ? "" : observed.Length <= overlapLength ? observed : observed[^overlapLength..];
        }
        void EndLine()
        {
            line(prefix.ToString() + (truncated ? "…" : ""));
            prefix.Clear(); truncated = false; tail = ""; completed++;
        }
        int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            int start = 0;
            for (int i = 0; i < count; i++)
            {
                char c = buffer[i];
                if (previousCR)
                {
                    previousCR = false;
                    if (c == '\n') { start = i + 1; continue; }
                }
                if (c != '\r' && c != '\n') continue;
                Segment(start, i - start); EndLine(); previousCR = c == '\r'; start = i + 1;
            }
            Segment(start, count - start);
            if (completed >= 128 || ++blocks >= 32) { completed = 0; blocks = 0; await Task.Yield(); }
        }
        if (prefix.Length > 0 || truncated) EndLine();
    }
}
