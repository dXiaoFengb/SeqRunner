using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Runner;

public static class WindowsPowerActions
{
    public static async Task<string> RequestAsync(string action)
    {
        if (action is "shutdown" or "restart")
        {
            string executable=System.IO.Path.Combine(Environment.SystemDirectory,"shutdown.exe");
            var info=new ProcessStartInfo(executable) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true };
            info.ArgumentList.Add(action=="shutdown" ? "/s" : "/r"); info.ArgumentList.Add("/t"); info.ArgumentList.Add("0");
            using var process=Process.Start(info) ?? throw new InvalidOperationException("无法发起系统请求");
            var errors=process.StandardError.ReadToEndAsync();
            var wait=process.WaitForExitAsync();
            if(await Task.WhenAny(wait,Task.Delay(TimeSpan.FromSeconds(10))) != wait) throw new InvalidOperationException("系统请求结果未确认，请手动核实");
            string error=await errors; if(process.ExitCode!=0) throw new InvalidOperationException($"系统拒绝请求（{process.ExitCode}）：{error}");
            return "系统已接受请求；设备实际状态未核实";
        }
        if(action is "sleep" or "hibernate")
        {
            bool hibernate=action=="hibernate";
            return await Task.Run(() => Suspend(hibernate));
        }
        throw new ArgumentException("不支持的操作");
    }
    static string Suspend(bool hibernate)
    {
        if (hibernate && !IsPwrHibernateAllowed()) throw new CompletionNotSentException("当前系统不支持休眠或未启用休眠文件");
        if (!OpenProcessToken(GetCurrentProcess(),0x20 | 0x8,out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!LookupPrivilegeValue(null,"SeShutdownPrivilege",out var luid)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var requested=new TokenPrivileges { Count=1,Luid=luid,Attributes=2 };
            if (!AdjustTokenPrivileges(token,false,ref requested,(uint)Marshal.SizeOf<TokenPrivileges>(),out var previous,out _) || Marshal.GetLastWin32Error()!=0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { if(!SetSuspendState(hibernate,false,false)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
            finally { AdjustTokenPrivileges(token,false,ref previous,(uint)Marshal.SizeOf<TokenPrivileges>(),out _,out _); }
        }
        finally { CloseHandle(token); }
        return "系统请求已返回；设备实际状态未核实";
    }
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool LookupPrivilegeValue(string? system,string name,out Luid luid);
    [DllImport("advapi32.dll",SetLastError=true)] static extern bool AdjustTokenPrivileges(IntPtr token,bool disable,ref TokenPrivileges state,uint length,out TokenPrivileges previous,out uint returned);
    [DllImport("powrprof.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.U1)] static extern bool IsPwrHibernateAllowed();
    [DllImport("powrprof.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.U1)] static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,[MarshalAs(UnmanagedType.U1)] bool force,[MarshalAs(UnmanagedType.U1)] bool disableWake);
}
