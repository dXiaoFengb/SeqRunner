using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Point = System.Windows.Point;

namespace Runner;

internal static class GenericStartController
{
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    delegate bool EnumWindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumWindows(EnumWindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    internal sealed record Selection(string Name, string Id);
    internal sealed record Probe(string Status, AutomationElement? Button = null, IntPtr Window = default);

    internal static Selection? Select(Point point, string program)
    {
        try
        {
            var element = AutomationElement.FromPoint(point);
            while (element != null && element.Current.ControlType != ControlType.Button) element = TreeWalker.ControlViewWalker.GetParent(element);
            if (element == null || !element.Current.IsEnabled || element.Current.IsOffscreen || !element.TryGetCurrentPattern(InvokePattern.Pattern, out _)) return null;
            using var process = Process.GetProcessById(element.Current.ProcessId);
            string? executable = process.MainModule?.FileName;
            IntPtr window = process.MainWindowHandle;
            if (executable == null || !string.Equals(Path.GetFullPath(executable), Path.GetFullPath(program), StringComparison.OrdinalIgnoreCase) || window == IntPtr.Zero || !IsWindowVisible(window)) return null;
            if (GetAncestor(WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }), 2) != window) return null;
            string name = element.Current.Name ?? "", id = element.Current.AutomationId ?? "";
            return name.Length == 0 && id.Length == 0 ? null : new(name, id);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or ArgumentException or IOException or COMException) { return null; }
    }

    static bool IsOwned(OwnedProcess owned, IntPtr window)
    {
        var process = owned.Root; process.Refresh();
        return !process.HasExited && process.StartTime.ToUniversalTime() == owned.CreatedUtc && owned.ContainsProcess(process.SafeHandle) && window != IntPtr.Zero && process.MainWindowHandle == window && IsWindowVisible(window) && GetWindowThreadProcessId(window, out uint pid) != 0 && pid == (uint)process.Id;
    }
    static bool Clickable(AutomationElement button, IntPtr window)
    {
        try
        {
            if (!button.TryGetClickablePoint(out Point point)) return false;
            return GetAncestor(WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }), 2) == window;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 5)
        {
            var area = button.Current.BoundingRectangle;
            if (area.IsEmpty || area.Width < 2 || area.Height < 2 || !GetWindowRect(window, out var owner) || area.Left < owner.Left || area.Top < owner.Top || area.Right > owner.Right || area.Bottom > owner.Bottom) return false;
            bool reached = false, covered = false, failed = false;
            _ = EnumWindows((other, _) =>
            {
                if (other == window) { reached = true; return false; }
                if (!IsWindowVisible(other)) return true;
                if (!GetWindowRect(other, out var bounds)) { failed = true; return false; }
                if (bounds.Left < area.Right && bounds.Right > area.Left && bounds.Top < area.Bottom && bounds.Bottom > area.Top) { covered = true; return false; }
                return true;
            }, IntPtr.Zero);
            if (failed || covered || !reached) return false;
            foreach (var (x, y) in new[] { (0.1, 0.1), (0.9, 0.1), (0.5, 0.5), (0.1, 0.9), (0.9, 0.9) })
                if (GetAncestor(WindowFromPoint(new NativePoint { X = (int)(area.Left + area.Width * x), Y = (int)(area.Top + area.Height * y) }), 2) != window) return false;
            return true;
        }
    }
    internal static Probe Inspect(OwnedProcess owned, string name, string id)
    {
        string stage = "检查配置";
        try
        {
            if (name.Length == 0 && id.Length == 0) return new("未选择按钮");
            stage = "查询窗口";
            owned.Root.Refresh(); IntPtr window = owned.Root.MainWindowHandle;
            if (window == IntPtr.Zero) return new("等待窗口");
            stage = "核实进程归属";
            if (!IsOwned(owned, window)) return new("窗口身份不明");
            stage = "读取控件";
            var top = AutomationElement.FromHandle(window);
            var buttons = top.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            var matches = buttons.Cast<AutomationElement>().Where(x => (name.Length == 0 || x.Current.Name == name) && (id.Length == 0 || x.Current.AutomationId == id)).ToList();
            if (matches.Count == 0) return new("等待按钮");
            if (matches.Count != 1) return new("按钮不唯一");
            var button = matches[0];
            stage = "检查按钮属性";
            if (button.Current.ProcessId != owned.Root.Id || !button.Current.IsEnabled || button.Current.IsOffscreen || !button.TryGetCurrentPattern(InvokePattern.Pattern, out _)) return new("按钮不可用");
            stage = "获取按钮可点击点";
            if (!Clickable(button, window)) return new("按钮被遮挡");
            return new("就绪", button, window);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or ArgumentException or IOException or COMException) { return new("无法核实窗口或按钮（" + stage + "；" + e.GetType().Name + (e is Win32Exception w ? ":" + w.NativeErrorCode : "") + "）"); }
    }
    internal static bool Invoke(OwnedProcess owned, Probe probe, string name, string id, Func<bool> mayInvoke)
    {
        try
        {
            var current = Inspect(owned, name, id);
            if (current.Status != "就绪" || current.Window != probe.Window || probe.Button == null || current.Button == null || !Automation.Compare(probe.Button, current.Button) || !mayInvoke()) return false;
            if (!current.Button.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern)) return false;
            ((InvokePattern)pattern).Invoke(); return true;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or ArgumentException or IOException or COMException) { return false; }
    }
}
