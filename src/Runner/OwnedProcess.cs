using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Runner;

internal sealed class ProcessLaunchUncertainException(string message, Exception inner) : Exception(message, inner);

// The process is created suspended and assigned before any application code runs.
internal sealed class OwnedProcess : IDisposable
{
    readonly SafeFileHandle job;
    public Process Root { get; }
    public StreamReader Output { get; }
    public StreamReader Error { get; }
    public DateTime CreatedUtc { get; }
    public string Executable { get; }

    OwnedProcess(SafeFileHandle job, Process root, StreamReader output, StreamReader error, DateTime createdUtc, string executable)
    { this.job = job; Root = root; Output = output; Error = error; CreatedUtc=createdUtc; Executable=executable; }

    public static OwnedProcess Start(TaskSpec task)
    {
        using SafeFileHandle job = Native.CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建进程归属对象");
        var sa = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = true };
        SafeFileHandle? readOut = null, writeOut = null, readErr = null, writeErr = null, nul = null;
        IntPtr processHandle = IntPtr.Zero, threadHandle = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, jobList = IntPtr.Zero;
        bool attributesInitialized = false;
        SafeFileHandle? heldJob = null;
        StreamReader? stdout = null, stderr = null;
        Process? process = null;
        bool resumed = false;
        try
        {
            if (!Native.CreatePipe(out readOut, out writeOut, ref sa, 0) || !Native.SetHandleInformation(readOut, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建输出管道");
            if (!Native.CreatePipe(out readErr, out writeErr, ref sa, 0) || !Native.SetHandleInformation(readErr, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建错误管道");
            nul = Native.CreateFile("NUL", 0x80000000, 3, ref sa, 3, 0, IntPtr.Zero);
            if (nul.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开空输入");
            nuint size = 0;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法获取进程属性大小");
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!Native.InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建进程属性");
            attributesInitialized = true;
            handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handles, 0, nul.DangerousGetHandle()); Marshal.WriteIntPtr(handles, IntPtr.Size, writeOut.DangerousGetHandle()); Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, writeErr.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020002, handles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法限制继承句柄");
            jobList = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobList, job.DangerousGetHandle());
            if (!Native.UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x0002000D, jobList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置创建时进程归属");
            var startup = new Native.StartupInfoEx { Info = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x100, StdInput = nul.DangerousGetHandle(), StdOutput = writeOut.DangerousGetHandle(), StdError = writeErr.DangerousGetHandle() }, Attributes = attributes };
            string fullPath = Path.GetFullPath(task.Program);
            string working = string.IsNullOrWhiteSpace(task.WorkingDirectory) ? Path.GetDirectoryName(fullPath)! : Path.GetFullPath(task.WorkingDirectory);
            var command = new StringBuilder("\"" + fullPath + "\"" + (string.IsNullOrWhiteSpace(task.Arguments) ? "" : " " + task.Arguments));
            if (!Native.CreateProcess(fullPath, command, IntPtr.Zero, IntPtr.Zero, true, 0x00000004 | 0x00000400 | 0x00080000, IntPtr.Zero, working, ref startup, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动程序");
            processHandle = info.Process; threadHandle = info.Thread;
            if (!Native.IsProcessInJob(processHandle, job, out bool assigned)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法查询进程归属，程序未开始执行");
            if (!assigned) throw new InvalidOperationException("创建的进程不属于预期的进程组，程序未开始执行");
            process = Process.GetProcessById(checked((int)info.ProcessId));
            _ = process.Handle;
            DateTime createdUtc = process.StartTime.ToUniversalTime();
            if (!Native.DuplicateHandle(Native.GetCurrentProcess(), job, Native.GetCurrentProcess(), out heldJob, 0, false, 2)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保留进程归属句柄");
            stdout = new StreamReader(new FileStream(readOut, FileAccess.Read, 4096, false), Console.OutputEncoding, true); readOut = null;
            stderr = new StreamReader(new FileStream(readErr, FileAccess.Read, 4096, false), Console.OutputEncoding, true); readErr = null;
            if (Native.ResumeThread(threadHandle) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复受管进程");
            resumed = true;
            writeOut.Dispose(); writeOut = null; writeErr.Dispose(); writeErr = null; nul.Dispose(); nul = null;
            var owned = new OwnedProcess(heldJob, process, stdout, stderr, createdUtc, fullPath); heldJob = null; return owned;
        }
        catch (Exception error)
        {
            bool uncertain = resumed;
            if (processHandle != IntPtr.Zero && !resumed)
                uncertain = !Native.TerminateProcess(processHandle, 1) || Native.WaitForSingleObject(processHandle, 5000) != 0;
            stdout?.Dispose(); stderr?.Dispose(); heldJob?.Dispose(); process?.Dispose();
            if (uncertain) throw new ProcessLaunchUncertainException("启动未完成，无法证明所创建进程已经退出；请人工核实。", error);
            throw;
        }
        finally
        {
            if (processHandle != IntPtr.Zero) Native.CloseHandle(processHandle);
            if (threadHandle != IntPtr.Zero) Native.CloseHandle(threadHandle);
            readOut?.Dispose(); writeOut?.Dispose(); readErr?.Dispose(); writeErr?.Dispose(); nul?.Dispose();
            if (attributes != IntPtr.Zero) { if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (jobList != IntPtr.Zero) Marshal.FreeHGlobal(jobList);
        }
    }
    public (uint Active, uint Total) Accounting()
    {
        if (!Native.QueryInformationJobObject(job, 1, out var info, (uint)Marshal.SizeOf<Native.Accounting>(), IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法验证进程组状态");
        return (info.Active, info.Total);
    }
    public void Dispose() { Output.Dispose(); Error.Dispose(); Root.Dispose(); job.Dispose(); }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public int X,Y,XSize,YSize,XChars,YChars,FillAttribute,Flags; public short ShowWindow,Reserved2; public IntPtr ReservedPtr,StdInput,StdOutput,StdError; }
        [StructLayout(LayoutKind.Sequential)] public struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] public struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] public struct Accounting { public long User,Kernel,PeriodUser,PeriodKernel; public uint PageFaults,Total,Active,Terminated; }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern SafeFileHandle CreateJobObject(IntPtr attrs, string? name);
        [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attrs, uint size);
        [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="CreateFileW")] public static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes attrs, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true, EntryPoint="CreateProcessW")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttrs, IntPtr threadAttrs, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string workingDirectory, ref StartupInfoEx startup, out ProcessInfo info);
        [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool QueryInformationJobObject(SafeFileHandle job, int type, out Accounting info, uint size, IntPtr returned);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess, out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(IntPtr handle);
    }
}
