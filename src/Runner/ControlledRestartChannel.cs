using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ComponentModel;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Runner;

// Internal test-fixture protocol only; ordinary tasks never create this endpoint.
internal sealed class ControlledRestartChannel : IDisposable
{
    readonly NamedPipeServerStream server;
    readonly CancellationTokenSource cancel;
    int disposed;
    readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal string Name { get; } = "SequentialRunnerV3-Test-" + Guid.NewGuid().ToString("N");
    internal string Arguments => Name + " " + token;
    internal Task<bool>? Result { get; private set; }
    internal int? VerifiedClientId { get; private set; }
    internal DateTime VerifiedClientCreated { get; private set; }
    internal string Failure { get; private set; } = "未完成";
    bool Fail(string reason) { Failure = reason; return false; }
    internal ControlledRestartChannel(TimeSpan? timeout = null)
    {
        cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        server = CreateLocalServer(Name);
    }
    static NamedPipeServerStream CreateLocalServer(string name)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法确认当前用户");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(D;;GA;;;NU)(A;;GA;;;{sid})", 1, out IntPtr descriptor, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建本地管道安全描述符");
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipe(@"\\.\pipe\" + name, 0x40080001, 0x00000008, 1, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "无法创建仅本机测试管道"); }
            try { return new NamedPipeServerStream(PipeDirection.In, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("advapi32.dll", EntryPoint="ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", EntryPoint="CreateNamedPipeW", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    internal void Start(OwnedProcess owned, Func<bool> updateSeen, string expected, Action<string> evidence) => Result = Observe(owned, updateSeen, expected, evidence);
    async Task<bool> Observe(OwnedProcess owned, Func<bool> updateSeen, string expected, Action<string> evidence)
    {
        try
        {
            await server.WaitForConnectionAsync(cancel.Token);
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid) || pid == 0) return Fail("无法查询管道客户端");
            using var client = Process.GetProcessById(checked((int)pid));
            DateTime created = client.StartTime.ToUniversalTime();
            string? clientPath = client.MainModule?.FileName;
            if (clientPath == null || !string.Equals(Path.GetFullPath(clientPath), owned.Executable, StringComparison.OrdinalIgnoreCase)) return Fail("客户端程序路径不匹配");
            if (!updateSeen() || !owned.Root.HasExited || created <= owned.CreatedUtc || client.HasExited || !owned.ContainsProcess(client.SafeHandle) || !IsOnlyClient(owned, pid)) return Fail("客户端不满足进程组或时序条件");
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
            if (await ReadLine(reader, cancel.Token) != token || await ReadLine(reader, cancel.Token) != "READY") return Fail("身份消息不匹配");
            if (client.HasExited || client.StartTime.ToUniversalTime() != created || !owned.ContainsProcess(client.SafeHandle) || !IsOnlyClient(owned, pid)) return Fail("客户端不满足进程组或时序条件");
            VerifiedClientId = client.Id;
            VerifiedClientCreated = created;
            evidence($"受控测试：已核对重启进程身份（PID {pid}，创建时间 {created:O}），属于本轮进程组");
            string? result = await ReadLine(reader, cancel.Token);
            if (result != "SUCCESS:" + expected) return Fail("成功消息不匹配");
            if (client.HasExited || client.StartTime.ToUniversalTime() != created || !owned.ContainsProcess(client.SafeHandle) || !IsOnlyClient(owned, pid)) return Fail("客户端不满足进程组或时序条件");
            if (await ReadLine(reader, cancel.Token) != null) return Fail("消息重复"); // EOF, not a duplicate or second message.
            await client.WaitForExitAsync(cancel.Token);
            if (client.ExitCode != 0) return Fail("客户端退出失败");
            evidence("受控测试：已取得本次重启进程的业务成功证据，且该进程正常退出");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException or OverflowException) { return Fail(e.GetType().Name + "：" + e.Message); }
        finally { server.Dispose(); }
    }
    static bool IsOnlyClient(OwnedProcess owned, uint pid) { var ids=owned.ProcessIds(); return ids.Length == 1 && ids[0] == pid; }
    static async Task<string?> ReadLine(StreamReader reader, CancellationToken ct)
    {
        var b=new StringBuilder(); var c=new char[1];
        while (await reader.ReadAsync(c.AsMemory(), ct) != 0)
        {
            if (c[0] == '\n') return b.ToString().TrimEnd('\r');
            if (b.Length >= 4096) throw new IOException("受控测试消息过长");
            b.Append(c[0]);
        }
        if (b.Length != 0) throw new IOException("受控测试消息未结束");
        return null;
    }
    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; cancel.Cancel(); server.Dispose(); cancel.Dispose(); }
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientPid);
}
