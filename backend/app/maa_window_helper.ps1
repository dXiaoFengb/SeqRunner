param([string]$Mode, [long]$WindowHandle, [int]$TargetPid, [double]$Started,
      [int]$ClientWidth, [int]$ClientHeight, [int]$X = 0, [int]$Y = 0, [int]$Width = 0, [int]$Height = 0)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RegionNative {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    public delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr data);
    public static bool IsRegionVisible(IntPtr target, int left, int top, int width, int height) {
        int sx = GetSystemMetrics(76), sy = GetSystemMetrics(77);
        if (left < sx || top < sy || left + width > sx + GetSystemMetrics(78) || top + height > sy + GetSystemMetrics(79)) return false;
        bool found = false, blocked = false;
        EnumWindows((hwnd, data) => {
            if (hwnd == target) { found = true; return false; }
            RECT r;
            if (IsWindowVisible(hwnd) && !IsIconic(hwnd) && GetWindowRect(hwnd, out r) &&
                r.Left < left + width && r.Right > left && r.Top < top + height && r.Bottom > top) blocked = true;
            return true;
        }, IntPtr.Zero);
        return found && !blocked;
    }
}
'@
if ([RegionNative]::SetThreadDpiAwarenessContext([IntPtr]::new(-4)) -eq [IntPtr]::Zero) { throw '无法确认屏幕缩放，窗口读取已暂停' }
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Drawing
$handle = [IntPtr]::new($WindowHandle)
function CheckIdentity {
    [uint32]$windowPid = 0
    [void][RegionNative]::GetWindowThreadProcessId($handle, [ref]$windowPid)
    if ($windowPid -ne $TargetPid) { throw '目标窗口身份已变化' }
    $process = Get-Process -Id $TargetPid -ErrorAction Stop
    $actual = ([DateTimeOffset]$process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000.0
    if ([Math]::Abs($actual - $Started) -ge 1) { throw '目标窗口身份已变化' }
}
CheckIdentity
$rect = [RegionNative+RECT]::new()
$origin = [RegionNative+POINT]::new()
if (-not [RegionNative]::IsWindowVisible($handle) -or [RegionNative]::IsIconic($handle) -or
    -not [RegionNative]::GetClientRect($handle, [ref]$rect) -or
    -not [RegionNative]::ClientToScreen($handle, [ref]$origin)) { throw '目标窗口不可见或已最小化' }
$actualWidth = $rect.Right - $rect.Left
$actualHeight = $rect.Bottom - $rect.Top
if ($actualWidth -ne $ClientWidth -or $actualHeight -ne $ClientHeight) { throw '窗口客户区尺寸已改变，请重新框选' }
if ($Mode -eq 'select') {
    [void][RegionNative]::SetForegroundWindow($handle)
    $window = [System.Windows.Window]::new()
    $window.WindowStyle = [System.Windows.WindowStyle]::None
    $window.ResizeMode = [System.Windows.ResizeMode]::NoResize
    $window.AllowsTransparency = $true
    $window.Background = [System.Windows.Media.Brushes]::Transparent
    $window.Topmost = $true
    $window.ShowInTaskbar = $false
    $canvas = [System.Windows.Controls.Canvas]::new()
    $canvas.Background = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromArgb(40, 0, 0, 0))
    $border = [System.Windows.Shapes.Rectangle]::new()
    $border.Stroke = [System.Windows.Media.Brushes]::DeepSkyBlue
    $border.StrokeThickness = 2
    $border.Fill = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromArgb(45, 0, 140, 255))
    [void]$canvas.Children.Add($border)
    $window.Content = $canvas
    $window.Add_SourceInitialized({
        $source = [System.Windows.PresentationSource]::FromVisual($window)
        $transform = $source.CompositionTarget.TransformFromDevice
        $s = $transform.Transform([System.Windows.Point]::new($actualWidth, $actualHeight))
        $window.Width = $s.X; $window.Height = $s.Y
        $overlay = [System.Windows.Interop.WindowInteropHelper]::new($window).Handle
        if (-not [RegionNative]::SetWindowPos($overlay, [IntPtr]::Zero, $origin.X, $origin.Y, $actualWidth, $actualHeight, 0x0014)) { throw '无法定位框选区域' }
    })
    $state = @{start = $null; result = $null}
    $canvas.Add_MouseLeftButtonDown({ param($sender, $event)
        $state.start = $event.GetPosition($canvas)
        [void]$canvas.CaptureMouse()
    })
    $canvas.Add_MouseMove({ param($sender, $event)
        if ($null -eq $state.start) { return }
        $p = $event.GetPosition($canvas)
        [System.Windows.Controls.Canvas]::SetLeft($border, [Math]::Min($state.start.X, $p.X))
        [System.Windows.Controls.Canvas]::SetTop($border, [Math]::Min($state.start.Y, $p.Y))
        $border.Width = [Math]::Abs($p.X - $state.start.X)
        $border.Height = [Math]::Abs($p.Y - $state.start.Y)
    })
    $canvas.Add_MouseLeftButtonUp({ param($sender, $event)
        if ($null -eq $state.start) { return }
        $p = $event.GetPosition($canvas)
        $source = [System.Windows.PresentationSource]::FromVisual($window)
        $a = $source.CompositionTarget.TransformToDevice.Transform($state.start)
        $b = $source.CompositionTarget.TransformToDevice.Transform($p)
        $left = [Math]::Max(0, [int][Math]::Floor([Math]::Min($a.X, $b.X))); $top = [Math]::Max(0, [int][Math]::Floor([Math]::Min($a.Y, $b.Y)))
        $right = [Math]::Min($actualWidth, [int][Math]::Ceiling([Math]::Max($a.X, $b.X))); $bottom = [Math]::Min($actualHeight, [int][Math]::Ceiling([Math]::Max($a.Y, $b.Y)))
        if ($right - $left -ge 5 -and $bottom - $top -ge 5) {
            $state.result = @{x=$left; y=$top; width=$right-$left; height=$bottom-$top}
            $window.Close()
        }
        $canvas.ReleaseMouseCapture(); $state.start = $null
    })
    $window.Add_KeyDown({ param($sender, $event) if ($event.Key -eq 'Escape') { $window.Close() } })
    [void]$window.ShowDialog()
    if ($null -eq $state.result) { @{canceled=$true} | ConvertTo-Json -Compress } else { $state.result | ConvertTo-Json -Compress }
    return
}
if ($Mode -eq 'frame') {
    if (-not [RegionNative]::IsRegionVisible($handle, $origin.X, $origin.Y, $actualWidth, $actualHeight)) { throw '日志区域被遮挡或位于屏幕外' }
    $frame = [System.Drawing.Bitmap]::new($actualWidth, $actualHeight)
    try {
        $g = [System.Drawing.Graphics]::FromImage($frame)
        try { $g.CopyFromScreen($origin.X, $origin.Y, 0, 0, $frame.Size) } finally { $g.Dispose() }
        CheckIdentity
        $after = [RegionNative+POINT]::new()
        $afterRect = [RegionNative+RECT]::new()
        if (-not [RegionNative]::GetClientRect($handle, [ref]$afterRect) -or -not [RegionNative]::ClientToScreen($handle, [ref]$after) -or
            $after.X -ne $origin.X -or $after.Y -ne $origin.Y -or $afterRect.Right -ne $actualWidth -or $afterRect.Bottom -ne $actualHeight -or
            -not [RegionNative]::IsRegionVisible($handle, $origin.X, $origin.Y, $actualWidth, $actualHeight)) { throw '识别期间目标窗口位置或尺寸已改变' }
        $memory = [System.IO.MemoryStream]::new()
        try { $frame.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png); @{image=[Convert]::ToBase64String($memory.ToArray())} | ConvertTo-Json -Compress }
        finally { $memory.Dispose() }
    } finally { $frame.Dispose() }
    return
}
if ($Mode -ne 'ocr') { throw '不支持的操作' }
if ($X -lt 0 -or $Y -lt 0 -or $Width -le 0 -or $Height -le 0 -or
    $X + $Width -gt $actualWidth -or $Y + $Height -gt $actualHeight) { throw '日志区域已失效' }
if (-not [RegionNative]::IsRegionVisible($handle, $origin.X + $X, $origin.Y + $Y, $Width, $Height)) { throw '日志区域被遮挡或位于屏幕外' }
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[void][Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Globalization.Language, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Foundation, ContentType=WindowsRuntime]
[void][Windows.Storage.Streams.DataWriter, Windows.Foundation, ContentType=WindowsRuntime]
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('zh-CN'))
if ($null -eq $engine) { throw 'Windows 中文 OCR 不可用' }
$bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
try {
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($origin.X + $X, $origin.Y + $Y, 0, 0, $bitmap.Size)
    } finally { $graphics.Dispose() }
    CheckIdentity
    $afterRect = [RegionNative+RECT]::new()
    $afterOrigin = [RegionNative+POINT]::new()
    if (-not [RegionNative]::IsWindowVisible($handle) -or [RegionNative]::IsIconic($handle) -or
        -not [RegionNative]::GetClientRect($handle, [ref]$afterRect) -or
        -not [RegionNative]::ClientToScreen($handle, [ref]$afterOrigin) -or
        $afterOrigin.X -ne $origin.X -or $afterOrigin.Y -ne $origin.Y -or
        ($afterRect.Right - $afterRect.Left) -ne $actualWidth -or
        ($afterRect.Bottom - $afterRect.Top) -ne $actualHeight) { throw '识别期间目标窗口位置或尺寸已改变' }
    if (-not [RegionNative]::IsRegionVisible($handle, $origin.X + $X, $origin.Y + $Y, $Width, $Height)) { throw '识别期间日志区域被遮挡' }
        $memory = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            $stream = [Windows.Storage.Streams.InMemoryRandomAccessStream]::new()
            $writer = [Windows.Storage.Streams.DataWriter]::new($stream)
            $writer.WriteBytes($memory.ToArray())
            $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
            function AwaitResult($operation, [type]$resultType) {
                $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
                $task.GetAwaiter().GetResult()
            }
            [void](AwaitResult ($writer.StoreAsync()) ([uint32]))
            $writer.DetachStream() | Out-Null
            $stream.Seek(0)
            $decoder = AwaitResult ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $image = AwaitResult ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
            $result = AwaitResult ($engine.RecognizeAsync($image)) ([Windows.Media.Ocr.OcrResult])
            @{text=($result.Lines | ForEach-Object { $_.Text }) -join "`n"} | ConvertTo-Json -Compress
        } finally { $memory.Dispose() }
} finally { $bitmap.Dispose() }
