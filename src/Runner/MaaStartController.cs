using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Runner;

internal sealed class MaaStartController
{
    internal const string SkipArgument = "--skip-startup-auto-run";
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr window);

    internal static string WithSkipArgument(string args)
    {
        IntPtr argv = CommandLineToArgvW("runner " + args, out int count);
        if (argv == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (int i = 1; i < count; i++)
                if (string.Equals(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)), SkipArgument, StringComparison.Ordinal)) return args;
        }
        finally { LocalFree(argv); }
        return SkipArgument + (string.IsNullOrWhiteSpace(args) ? "" : " " + args);
    }

    internal sealed record Probe(string Status, AutomationElement? Button = null, IntPtr Window = default);

    internal static Probe Inspect(OwnedProcess owned)
    {
        try
        {
            Process root = owned.Root;
            root.Refresh();
            if (root.HasExited || root.StartTime.ToUniversalTime() != owned.CreatedUtc || !owned.ContainsProcess(root.SafeHandle)) return new("身份不明");
            IntPtr window = root.MainWindowHandle;
            if (window == IntPtr.Zero || !IsWindowVisible(window)) return new("等待窗口");
            if (GetWindowThreadProcessId(window, out uint pid) == 0 || pid != (uint)root.Id) return new("身份不明");
            AutomationElement? top = AutomationElement.FromHandle(window);
            if (top == null || top.Current.ProcessId != root.Id) return new("身份不明");
            var buttons = top.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "Link Start!")));
            if (buttons.Count == 0) return new("等待按钮");
            if (buttons.Count != 1) return new("按钮不唯一");
            AutomationElement button = buttons[0];
            if (button.Current.ProcessId != root.Id || !button.Current.IsEnabled || button.Current.IsOffscreen || !button.TryGetCurrentPattern(InvokePattern.Pattern, out _)) return new("按钮不可用");
            return new("就绪", button, window);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or ArgumentException or System.IO.IOException or System.Runtime.InteropServices.COMException)
        { return new("无法核实窗口或按钮"); }
    }

    internal static bool Invoke(OwnedProcess owned, Probe probe, Func<bool> mayInvoke)
    {
        try
        {
            var root = owned.Root;
            root.Refresh();
            if (probe.Button == null || root.HasExited || root.StartTime.ToUniversalTime() != owned.CreatedUtc || !owned.ContainsProcess(root.SafeHandle) || root.MainWindowHandle != probe.Window || !IsWindowVisible(probe.Window) || GetWindowThreadProcessId(probe.Window, out uint pid) == 0 || pid != (uint)root.Id) return false;
            var current = probe.Button.Current;
            if (current.ProcessId != root.Id || current.Name != "Link Start!" || !current.IsEnabled || current.IsOffscreen || !probe.Button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern) || !mayInvoke()) return false;
            ((InvokePattern)pattern).Invoke();
            return true;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or ArgumentException or System.IO.IOException or System.Runtime.InteropServices.COMException) { return false; }
    }
}
