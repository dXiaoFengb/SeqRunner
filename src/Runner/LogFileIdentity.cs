using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Runner;

internal static class LogFileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    struct Information
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Information info);
    static (uint, uint, uint) Identify(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核实日志文件身份");
        return (info.Volume, info.IndexHigh, info.IndexLow);
    }
    public static bool IsSame(FileStream opened, string path)
    {
        using var current = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Identify(opened.SafeFileHandle) == Identify(current);
    }
}
