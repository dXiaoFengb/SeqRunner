using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;

namespace Runner;

internal static class VisualStartController
{
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    delegate bool EnumCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumWindows(EnumCallback callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PrintWindow(IntPtr window, IntPtr context, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    internal sealed record Selection(int X, int Y, int Width, int Height, int WindowWidth, int WindowHeight);
    internal sealed record Match(IntPtr Window, Point Point, byte[] FrameHash, byte[]? TemplateHash);
    internal sealed record Probe(string Status, Match? Match = null);
    internal static Action<byte[]>? FrameTestHook { get; set; }
    internal static Func<Bitmap,Rectangle,Bitmap>? ScreenTestHook { get; set; }

    static IntPtr RootAt(Point point) => GetAncestor(WindowFromPoint(new NativePoint { X=point.X, Y=point.Y }), 2);
    static Rectangle Bounds(IntPtr window)
    {
        if (!GetWindowRect(window, out var r)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);
    }
    static bool Clear(IntPtr window, Rectangle area)
    {
        bool reached=false, covered=false, failed=false;
        _=EnumWindows((other,_) => { if(other==window){reached=true;return false;} if(!IsWindowVisible(other))return true; if(!GetWindowRect(other,out var r)){failed=true;return false;} if(Rectangle.Intersect(area,Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom)).IsEmpty)return true;covered=true;return false; },IntPtr.Zero);
        if(!reached || covered || failed)return false;
        foreach(var (x,y) in new[]{(0.1,0.1),(0.9,0.1),(0.5,0.5),(0.1,0.9),(0.9,0.9)})
            if(RootAt(new Point(area.Left+(int)(area.Width*x),area.Top+(int)(area.Height*y)))!=window)return false;
        return true;
    }
    internal static Selection? Select(Point first, Point last, string program)
    {
        try
        {
            IntPtr window=RootAt(first);if(window==IntPtr.Zero || RootAt(last)!=window || GetForegroundWindow()!=window || GetDpiForWindow(window)!=96)return null;
            if(GetWindowThreadProcessId(window,out uint pid)==0)return null;
            using var process=Process.GetProcessById(checked((int)pid));process.Refresh();string? path=process.MainModule?.FileName;
            if(path==null || !string.Equals(Path.GetFullPath(path),Path.GetFullPath(program),StringComparison.OrdinalIgnoreCase) || process.MainWindowHandle!=window)return null;
            var bounds=Bounds(window);var region=Rectangle.FromLTRB(Math.Min(first.X,last.X),Math.Min(first.Y,last.Y),Math.Max(first.X,last.X),Math.Max(first.Y,last.Y));
            if(region.Width<10 || region.Height<10 || (long)region.Width*region.Height>262144 || !bounds.Contains(region) || !Clear(window,region))return null;
            return new(region.X-bounds.X,region.Y-bounds.Y,region.Width,region.Height,bounds.Width,bounds.Height);
        }
        catch(Exception e) when(e is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or IOException or OverflowException){return null;}
    }
    static bool Owned(OwnedProcess owned, IntPtr window, TaskSpec task, out Rectangle bounds)
    {
        bounds=Rectangle.Empty;var root=owned.Root;root.Refresh();
        if(root.HasExited || root.StartTime.ToUniversalTime()!=owned.CreatedUtc || !owned.ContainsProcess(root.SafeHandle) || root.MainWindowHandle!=window || window==IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window) || GetForegroundWindow()!=window || GetDpiForWindow(window)!=96 || GetWindowThreadProcessId(window,out uint pid)==0 || pid!=(uint)root.Id)return false;
        bounds=Bounds(window);
        return bounds.Width==task.VisualWindowWidth && bounds.Height==task.VisualWindowHeight && bounds.Width>0 && bounds.Height>0 && (long)bounds.Width*bounds.Height<=4000000;
    }
    static byte[] Capture(IntPtr window, Rectangle bounds, Rectangle region)
    {
        using var bitmap=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(bitmap))
        {
            IntPtr hdc=graphics.GetHdc();try{if(!PrintWindow(window,hdc,2))throw new IOException("无法读取窗口画面");}finally{graphics.ReleaseHdc(hdc);}
        }
        using var stream=new MemoryStream();bitmap.Save(stream,ImageFormat.Png);byte[] result=stream.ToArray();FrameTestHook?.Invoke(result);
        using var screen=ScreenTestHook?.Invoke(bitmap,region) ?? new Bitmap(region.Width,region.Height,PixelFormat.Format32bppArgb);
        if(ScreenTestHook==null)using(var graphics=Graphics.FromImage(screen))graphics.CopyFromScreen(new Point(bounds.Left+region.X,bounds.Top+region.Y),Point.Empty,region.Size,CopyPixelOperation.SourceCopy);
        using var crop=bitmap.Clone(region,PixelFormat.Format32bppArgb);
        var left=crop.LockBits(new Rectangle(0,0,crop.Width,crop.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);var right=screen.LockBits(new Rectangle(0,0,screen.Width,screen.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try { if(left.Stride!=crop.Width*4 || right.Stride!=screen.Width*4)throw new IOException("窗口与屏幕画面不一致");int[] a=new int[crop.Width*crop.Height],b=new int[a.Length];Marshal.Copy(left.Scan0,a,0,a.Length);Marshal.Copy(right.Scan0,b,0,b.Length);for(int i=0;i<a.Length;i++)if((a[i]&0xffffff)!=(b[i]&0xffffff))throw new IOException("窗口与屏幕画面不一致"); }
        finally { crop.UnlockBits(left);screen.UnlockBits(right); }
        return result;
    }
    internal static Point? FindImage(byte[] screen,byte[] template,Rectangle region)
    {
        using var input=new MemoryStream(screen);using var templateStream=new MemoryStream(template);using var source=new Bitmap(input);using var image=new Bitmap(templateStream);
        if(image.Width<10 || image.Height<10 || image.Width>512 || image.Height>256 || image.Width>source.Width || image.Height>source.Height)return null;
        using var normalized=new Bitmap(image.Width,image.Height,PixelFormat.Format32bppArgb);using(var g=Graphics.FromImage(normalized))g.DrawImageUnscaled(image,0,0);
        var srcBits=source.LockBits(new Rectangle(0,0,source.Width,source.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);var imgBits=normalized.LockBits(new Rectangle(0,0,image.Width,image.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try
        {
            if(srcBits.Stride!=source.Width*4 || imgBits.Stride!=image.Width*4)return null;
            int[] a=new int[source.Width*source.Height],b=new int[image.Width*image.Height];Marshal.Copy(srcBits.Scan0,a,0,a.Length);Marshal.Copy(imgBits.Scan0,b,0,b.Length);
            if(b.Any(x=>(uint)x>>24!=255) || b.Select(x=>x&0xffffff).Distinct().Take(2).Count()<2)return null;
            Point? match=null;var timer=Stopwatch.StartNew();int mid=b.Length/2;
            for(int y=0;y<=source.Height-image.Height;y++) { if(timer.Elapsed>TimeSpan.FromSeconds(4))return null;for(int x=0;x<=source.Width-image.Width;x++) {if((x&255)==0 && timer.Elapsed>TimeSpan.FromSeconds(4))return null;int i=y*source.Width+x;if((a[i]&0xffffff)!=(b[0]&0xffffff) || (a[i+(mid/image.Width)*source.Width+mid%image.Width]&0xffffff)!=(b[mid]&0xffffff))continue;bool same=true;for(int j=0;j<image.Height && same;j++)for(int k=0;k<image.Width;k++)if((a[i+j*source.Width+k]&0xffffff)!=(b[j*image.Width+k]&0xffffff)){same=false;break;}if(!same)continue;if(match!=null)return null;match=new Point(x+image.Width/2,y+image.Height/2);}}
            return match is Point p && region.Contains(p) && region.Contains(new Rectangle(p.X-image.Width/2,p.Y-image.Height/2,image.Width,image.Height)) ? p : null;
        }
        finally { source.UnlockBits(srcBits);normalized.UnlockBits(imgBits); }
    }
    internal static async Task<Probe> InspectAsync(OwnedProcess owned,TaskSpec task,CancellationToken cancellation = default)
    {
        try
        {
            owned.Root.Refresh();IntPtr window=owned.Root.MainWindowHandle;if(window==IntPtr.Zero)return new("等待窗口");
            if(!Owned(owned,window,task,out var bounds) || !Clear(window,bounds))return new("窗口身份、大小、缩放或遮挡不明");
            var region=new Rectangle(task.VisualX,task.VisualY,task.VisualWidth,task.VisualHeight);if(!new Rectangle(0,0,bounds.Width,bounds.Height).Contains(region))return new("目标区域无效");
            byte[] frame=Capture(window,bounds,region);Point? point=null;byte[]? templateHash=null;
            if(task.VisualMode=="文字")
            {
                var lines=await OcrHelperClient.RecognizeBytesAsync(frame,OcrHelperClient.ResolveHelperPath(),TimeSpan.FromSeconds(20),cancellation);
                var matches=lines.Where(x=>string.Equals(x.Text.Trim(),task.VisualText.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
                if(matches.Length!=1 || matches[0].Confidence is not >=0.9 || matches[0].Points.Count!=4)return new("文字未唯一确认");
                double left=matches[0].Points.Min(p=>p.X),right=matches[0].Points.Max(p=>p.X),top=matches[0].Points.Min(p=>p.Y),bottom=matches[0].Points.Max(p=>p.Y);
                if(left<region.Left || top<region.Top || right>region.Right || bottom>region.Bottom)return new("文字不在目标区域内");
                point=new Point((int)((left+right)/2),(int)((top+bottom)/2));
            }
            else if(task.VisualMode=="图片") { if(new FileInfo(task.VisualTemplatePath).Length>8000000)return new("图片模板过大");byte[] template=await File.ReadAllBytesAsync(task.VisualTemplatePath);templateHash=SHA256.HashData(template);point=FindImage(frame,template,region); }
            if(point==null)return new("图片未唯一确认");
            var screenPoint=new Point(bounds.Left+point.Value.X,bounds.Top+point.Value.Y);
            if(!Owned(owned,window,task,out var current) || current!=bounds || !Clear(window,bounds))return new("识别期间窗口已变化");
            return new("就绪",new Match(window,screenPoint,SHA256.HashData(frame),templateHash));
        }
        catch(OperationCanceledException){return new("识别已停止");}
        catch(IOException e) when(e.Message is "无法读取窗口画面" or "窗口与屏幕画面不一致"){return new(e.Message);}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception or TimeoutException or System.Text.Json.JsonException or ExternalException or OverflowException){return new("视觉识别不明（"+e.GetType().Name+"）");}
    }
    internal static bool Invoke(OwnedProcess owned,TaskSpec task,Match match,Func<Func<bool>,bool> dispatch)
    {
        try
        {
            var region=new Rectangle(task.VisualX,task.VisualY,task.VisualWidth,task.VisualHeight);
            bool TemplateUnchanged() => match.TemplateHash==null || (new FileInfo(task.VisualTemplatePath).Length<=8000000 && CryptographicOperations.FixedTimeEquals(match.TemplateHash,SHA256.HashData(File.ReadAllBytes(task.VisualTemplatePath))));
            if(!Owned(owned,match.Window,task,out var bounds) || !Clear(match.Window,bounds) || !TemplateUnchanged() || !CryptographicOperations.FixedTimeEquals(match.FrameHash,SHA256.HashData(Capture(match.Window,bounds,region))))return false;
            var pt=new NativePoint{X=match.Point.X,Y=match.Point.Y};IntPtr target=WindowFromPoint(pt);
            if(target==IntPtr.Zero || GetAncestor(target,2)!=match.Window || GetWindowThreadProcessId(target,out uint pid)==0 || pid!=(uint)owned.Root.Id || !ScreenToClient(target,ref pt) || pt.X<0 || pt.Y<0 || pt.X>short.MaxValue || pt.Y>short.MaxValue)return false;
            return dispatch(() => { if(!Owned(owned,match.Window,task,out var finalBounds) || finalBounds!=bounds || !Clear(match.Window,bounds) || !TemplateUnchanged() || !CryptographicOperations.FixedTimeEquals(match.FrameHash,SHA256.HashData(Capture(match.Window,bounds,region))))return false;var screenPoint=new NativePoint{X=match.Point.X,Y=match.Point.Y};if(WindowFromPoint(screenPoint)!=target || GetAncestor(target,2)!=match.Window || GetWindowThreadProcessId(target,out uint finalPid)==0 || finalPid!=(uint)owned.Root.Id)return false;IntPtr lParam=(IntPtr)((pt.Y<<16)|(pt.X&0xffff));bool down=PostMessage(target,0x0201,(IntPtr)1,lParam);bool up=PostMessage(target,0x0202,IntPtr.Zero,lParam);return down && up; });
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception or ExternalException or OverflowException){return false;}
    }
}
