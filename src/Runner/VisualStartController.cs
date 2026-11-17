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

    internal sealed record Selection(int X, int Y, int Width, int Height, int WindowWidth, int WindowHeight, string WindowHash, string ScreenHash, string TemplateHash);
    internal sealed record Match(IntPtr Window, Point Point, byte[] FrameHash, byte[] ScreenHash, byte[]? TemplateHash);
    internal sealed record Probe(string Status, Match? Match = null);
    sealed record Frames(byte[] Window, byte[] Screen);
    internal static Action<byte[]>? FrameTestHook { get; set; }
    internal static Func<Bitmap,Rectangle,Bitmap>? ScreenTestHook { get; set; }
    internal static Action<string>? DiagnosticTestHook { get; set; }

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
    internal static Selection? Select(Point first, Point last, string program, string? templatePath = null)
    {
        try
        {
            IntPtr window=RootAt(first);if(window==IntPtr.Zero || RootAt(last)!=window || GetForegroundWindow()!=window || GetDpiForWindow(window)!=96)return null;
            if(GetWindowThreadProcessId(window,out uint pid)==0)return null;
            using var process=Process.GetProcessById(checked((int)pid));process.Refresh();string? path=process.MainModule?.FileName;
            if(path==null || !string.Equals(Path.GetFullPath(path),Path.GetFullPath(program),StringComparison.OrdinalIgnoreCase) || process.MainWindowHandle!=window)return null;
            var bounds=Bounds(window);if(bounds.Width<=0 || bounds.Height<=0 || (long)bounds.Width*bounds.Height>4000000)return null;
            var region=Rectangle.FromLTRB(Math.Min(first.X,last.X),Math.Min(first.Y,last.Y),Math.Max(first.X,last.X),Math.Max(first.Y,last.Y));
            if(region.Width<10 || region.Height<10 || (long)region.Width*region.Height>262144 || !bounds.Contains(region) || !Clear(window,region))return null;
            string templateHash="";
            if(templatePath!=null)
            {
                var info=new FileInfo(templatePath);if(!info.Exists || info.Length>8000000)return null;
                templateHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(templatePath)));
            }
            var local=new Rectangle(region.X-bounds.X,region.Y-bounds.Y,region.Width,region.Height);
            var frames=Capture(window,bounds,local);
            if(GetForegroundWindow()!=window || Bounds(window)!=bounds || !Clear(window,bounds))return null;
            return new(local.X,local.Y,local.Width,local.Height,bounds.Width,bounds.Height,RegionHash(frames.Window,local),RegionHash(frames.Screen,local),templateHash);
        }
        catch(Exception e) when(e is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or IOException or OverflowException or ExternalException){return null;}
    }
    static bool Owned(OwnedProcess owned, IntPtr window, TaskSpec task, out Rectangle bounds)
    {
        bounds=Rectangle.Empty;var root=owned.Root;root.Refresh();
        if(root.HasExited || root.StartTime.ToUniversalTime()!=owned.CreatedUtc || !owned.ContainsProcess(root.SafeHandle) || root.MainWindowHandle!=window || window==IntPtr.Zero || !IsWindowVisible(window) || IsIconic(window) || GetForegroundWindow()!=window || GetDpiForWindow(window)!=96 || GetWindowThreadProcessId(window,out uint pid)==0 || pid!=(uint)root.Id)return false;
        bounds=Bounds(window);
        return bounds.Width==task.VisualWindowWidth && bounds.Height==task.VisualWindowHeight && bounds.Width>0 && bounds.Height>0 && (long)bounds.Width*bounds.Height<=4000000;
    }
    static Frames Capture(IntPtr window, Rectangle bounds, Rectangle region)
    {
        using var bitmap=new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(bitmap))
        {
            IntPtr hdc=graphics.GetHdc();try{if(!PrintWindow(window,hdc,2))throw new IOException("无法读取窗口画面");}finally{graphics.ReleaseHdc(hdc);}
        }
        using var stream=new MemoryStream();bitmap.Save(stream,ImageFormat.Png);byte[] result=stream.ToArray();FrameTestHook?.Invoke(result);
        var whole=new Rectangle(0,0,bounds.Width,bounds.Height);
        using var screen=ScreenTestHook?.Invoke(bitmap,whole) ?? new Bitmap(bounds.Width,bounds.Height,PixelFormat.Format32bppArgb);
        if(screen.Width!=bounds.Width || screen.Height!=bounds.Height)throw new IOException("屏幕采样尺寸不一致");
        if(ScreenTestHook==null)using(var graphics=Graphics.FromImage(screen))graphics.CopyFromScreen(bounds.Location,Point.Empty,bounds.Size,CopyPixelOperation.SourceCopy);
        using var screenStream=new MemoryStream();screen.Save(screenStream,ImageFormat.Png);
        return new(result,screenStream.ToArray());
    }
    internal static string RegionHash(byte[] image,Rectangle region)
    {
        using var stream=new MemoryStream(image);using var bitmap=new Bitmap(stream);
        if(!new Rectangle(0,0,bitmap.Width,bitmap.Height).Contains(region))throw new ArgumentException("目标区域无效");
        using var crop=bitmap.Clone(region,PixelFormat.Format32bppArgb);var bits=crop.LockBits(new Rectangle(0,0,crop.Width,crop.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try { if(bits.Stride!=crop.Width*4)throw new IOException("画面格式不支持");int[] pixels=new int[crop.Width*crop.Height];Marshal.Copy(bits.Scan0,pixels,0,pixels.Length);for(int i=0;i<pixels.Length;i++)pixels[i]&=0xffffff;return Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan()))); }
        finally { crop.UnlockBits(bits); }
    }
    internal static bool ImageReferencesMatch(TaskSpec task,byte[] window,byte[] screen,byte[] template,Rectangle region)
    {
        if(string.IsNullOrEmpty(task.VisualWindowRegionHash) || string.IsNullOrEmpty(task.VisualScreenRegionHash) || string.IsNullOrEmpty(task.VisualTemplateReferenceHash))return false;
        return string.Equals(task.VisualWindowRegionHash,RegionHash(window,region),StringComparison.OrdinalIgnoreCase)
            && string.Equals(task.VisualScreenRegionHash,RegionHash(screen,region),StringComparison.OrdinalIgnoreCase)
            && string.Equals(task.VisualTemplateReferenceHash,Convert.ToHexString(SHA256.HashData(template)),StringComparison.OrdinalIgnoreCase);
    }
    internal static RectangleF? FindText(IReadOnlyList<OcrHelperClient.Line> lines,string text,Rectangle region)
    {
        var matches=lines.Where(x=>string.Equals(x.Text.Trim(),text.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
        if(matches.Length!=1 || matches[0].Confidence is not >=0.8 || matches[0].Points.Count!=4)return null;
        var points=matches[0].Points;
        if(points.Any(p=>!double.IsFinite(p.X) || !double.IsFinite(p.Y)))return null;
        float left=(float)points.Min(p=>p.X),right=(float)points.Max(p=>p.X),top=(float)points.Min(p=>p.Y),bottom=(float)points.Max(p=>p.Y);
        if(left<region.Left || top<region.Top || right>region.Right || bottom>region.Bottom || right<=left || bottom<=top)return null;
        return RectangleF.FromLTRB(left,top,right,bottom);
    }
    internal static Point? FindRenderedImage(byte[] imageBytes, byte[] templateBytes, Rectangle region)
    {
        using var image=OpenCvSharp.Cv2.ImDecode(imageBytes,OpenCvSharp.ImreadModes.Color);
        using var original=OpenCvSharp.Cv2.ImDecode(templateBytes,OpenCvSharp.ImreadModes.Unchanged);
        if(image.Empty() || original.Empty() || original.Width<10 || original.Height<10 || original.Width>512 || original.Height>256 || original.Width>image.Width || original.Height>image.Height)return null;
        using var template=new OpenCvSharp.Mat();
        if(original.Channels()==4)
        {
            using var alpha=new OpenCvSharp.Mat();OpenCvSharp.Cv2.ExtractChannel(original,alpha,3);OpenCvSharp.Cv2.MinMaxLoc(alpha,out double minimum,out double _);
            if(minimum!=255)return null;
            OpenCvSharp.Cv2.CvtColor(original,template,OpenCvSharp.ColorConversionCodes.BGRA2BGR);
        }
        else if(original.Channels()==3)original.CopyTo(template);
        else if(original.Channels()==1)OpenCvSharp.Cv2.CvtColor(original,template,OpenCvSharp.ColorConversionCodes.GRAY2BGR);
        else return null;
        OpenCvSharp.Cv2.MeanStdDev(template,out _,out var stddev);
        if(Math.Max(stddev.Val0,Math.Max(stddev.Val1,stddev.Val2))<5)return null;
        using var scores=new OpenCvSharp.Mat();OpenCvSharp.Cv2.MatchTemplate(image,template,scores,OpenCvSharp.TemplateMatchModes.CCoeffNormed);
        OpenCvSharp.Cv2.MinMaxLoc(scores,out _,out double best,out _,out OpenCvSharp.Point position);
        if(!double.IsFinite(best) || best<0.96)return null;
        var candidate=new Rectangle(position.X,position.Y,template.Width,template.Height);if(!region.Contains(candidate))return null;
        scores.Set(position.Y,position.X,-1f);
        OpenCvSharp.Cv2.MinMaxLoc(scores,out double _,out double second);if(!double.IsFinite(second) || second>=0.96)return null;
        using var patch=new OpenCvSharp.Mat(image,new OpenCvSharp.Rect(position.X,position.Y,template.Width,template.Height));
        var corner=template.At<OpenCvSharp.Vec3b>(0,0);var border=new OpenCvSharp.Scalar(corner.Item0,corner.Item1,corner.Item2);
        double minimumMean=double.MaxValue,maximumAtMinimum=double.MaxValue;
        for(int dx=-4;dx<=4;dx++)for(int dy=-4;dy<=4;dy++)
        {
            using var matrix=OpenCvSharp.Mat.FromArray(new double[,]{{1,0,dx/8.0},{0,1,dy/8.0}});
            using var rendered=new OpenCvSharp.Mat();OpenCvSharp.Cv2.WarpAffine(template,rendered,matrix,new OpenCvSharp.Size(template.Width,template.Height),OpenCvSharp.InterpolationFlags.Linear,OpenCvSharp.BorderTypes.Constant,border);
            using var difference=new OpenCvSharp.Mat();OpenCvSharp.Cv2.Absdiff(patch,rendered,difference);var mean=OpenCvSharp.Cv2.Mean(difference);double average=(mean.Val0+mean.Val1+mean.Val2)/3;
            if(average<minimumMean){using var flat=difference.Reshape(1);OpenCvSharp.Cv2.MinMaxLoc(flat,out double _,out double high);minimumMean=average;maximumAtMinimum=high;}
        }
        return minimumMean<=3 && maximumAtMinimum<=48 ? new Point(position.X+template.Width/2,position.Y+template.Height/2) : null;
    }
    internal static Point? AgreeOnText(IReadOnlyList<OcrHelperClient.Line> window,IReadOnlyList<OcrHelperClient.Line> screen,string text,Rectangle region)
    {
        var a=FindText(window,text,region);var b=FindText(screen,text,region);
        if(a==null || b==null || Math.Abs(a.Value.Left-b.Value.Left)>5 || Math.Abs(a.Value.Right-b.Value.Right)>5 || Math.Abs(a.Value.Top-b.Value.Top)>5 || Math.Abs(a.Value.Bottom-b.Value.Bottom)>5)return null;
        var first=new Point((int)((a.Value.Left+a.Value.Right)/2),(int)((a.Value.Top+a.Value.Bottom)/2));var second=new Point((int)((b.Value.Left+b.Value.Right)/2),(int)((b.Value.Top+b.Value.Bottom)/2));
        return Math.Abs(first.X-second.X)<=3 && Math.Abs(first.Y-second.Y)<=3 ? first : null;
    }
    internal static async Task<Probe> InspectAsync(OwnedProcess owned,TaskSpec task,CancellationToken cancellation = default)
    {
        try
        {
            owned.Root.Refresh();IntPtr window=owned.Root.MainWindowHandle;if(window==IntPtr.Zero || GetForegroundWindow()!=window)return new("等待窗口");
            if(!Owned(owned,window,task,out var bounds))return new("窗口身份、大小或缩放不明");
            if(!Clear(window,bounds))return new("窗口前景或遮挡不明");
            var region=new Rectangle(task.VisualX,task.VisualY,task.VisualWidth,task.VisualHeight);if(!new Rectangle(0,0,bounds.Width,bounds.Height).Contains(region))return new("目标区域无效");
            var frames=Capture(window,bounds,region);Point? point=null;Point? screenPointLocal=null;byte[]? templateHash=null;
            if(task.VisualMode=="文字")
            {
                var first=await OcrHelperClient.RecognizeBytesAsync(frames.Window,OcrHelperClient.ResolveHelperPath(),TimeSpan.FromSeconds(20),cancellation);var second=await OcrHelperClient.RecognizeBytesAsync(frames.Screen,OcrHelperClient.ResolveHelperPath(),TimeSpan.FromSeconds(20),cancellation);
                point=AgreeOnText(first,second,task.VisualText,region);screenPointLocal=point;
                DiagnosticTestHook?.Invoke($"文字目标：窗口={FindText(first,task.VisualText,region)?.ToString() ?? "未识别"}；屏幕={FindText(second,task.VisualText,region)?.ToString() ?? "未识别"}");
                var whole=new Rectangle(0,0,bounds.Width,bounds.Height);
                if(point==null && ((FindText(first,task.VisualText,whole)!=null && FindText(first,task.VisualText,region)==null) || (FindText(second,task.VisualText,whole)!=null && FindText(second,task.VisualText,region)==null)))return new("文字未完整位于所选区域，请重新选择");
            }
            else if(task.VisualMode=="图片") { if(new FileInfo(task.VisualTemplatePath).Length>8000000)return new("图片模板过大");byte[] template=await File.ReadAllBytesAsync(task.VisualTemplatePath);if(!ImageReferencesMatch(task,frames.Window,frames.Screen,template,region))return new("图片目标基准缺失或已变化，请重新选择区域");templateHash=SHA256.HashData(template);point=FindRenderedImage(frames.Window,template,region);screenPointLocal=FindRenderedImage(frames.Screen,template,region); }
            DiagnosticTestHook?.Invoke($"目标中心：窗口={point?.ToString() ?? "未识别"}；屏幕={screenPointLocal?.ToString() ?? "未识别"}");if(point==null || screenPointLocal==null || Math.Abs(point.Value.X-screenPointLocal.Value.X)>3 || Math.Abs(point.Value.Y-screenPointLocal.Value.Y)>3)return new("两路画面目标未唯一对应");
            var screenPoint=new Point(bounds.Left+point.Value.X,bounds.Top+point.Value.Y);
            if(!Owned(owned,window,task,out var current) || current!=bounds || !Clear(window,bounds))return new("识别期间窗口已变化");
            return new("就绪",new Match(window,screenPoint,SHA256.HashData(frames.Window),SHA256.HashData(frames.Screen),templateHash));
        }
        catch(OperationCanceledException){return new("识别已停止");}
        catch(IOException e) when(e.Message is "无法读取窗口画面" or "窗口与屏幕画面不一致"){return new(e.Message);}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception or TimeoutException or System.Text.Json.JsonException or ExternalException or OverflowException or OpenCvSharp.OpenCVException or DllNotFoundException or TypeInitializationException or BadImageFormatException){return new("视觉识别不明（"+e.GetType().Name+"）");}
    }
    internal static bool Invoke(OwnedProcess owned,TaskSpec task,Match match,Func<Func<bool>,bool> dispatch)
    {
        try
        {
            var region=new Rectangle(task.VisualX,task.VisualY,task.VisualWidth,task.VisualHeight);
            bool TemplateUnchanged() => match.TemplateHash==null || (new FileInfo(task.VisualTemplatePath).Length<=8000000 && CryptographicOperations.FixedTimeEquals(match.TemplateHash,SHA256.HashData(File.ReadAllBytes(task.VisualTemplatePath))));
            bool FramesUnchanged(Rectangle current) { var frames=Capture(match.Window,current,region);bool a=CryptographicOperations.FixedTimeEquals(match.FrameHash,SHA256.HashData(frames.Window)),b=CryptographicOperations.FixedTimeEquals(match.ScreenHash,SHA256.HashData(frames.Screen));if(!a || !b)DiagnosticTestHook?.Invoke($"画面变化：窗口={!a} 屏幕={!b}");return a && b; }
            if(!Owned(owned,match.Window,task,out var bounds)){DiagnosticTestHook?.Invoke("窗口归属变化");return false;}if(!Clear(match.Window,bounds)){DiagnosticTestHook?.Invoke("窗口遮挡变化");return false;}if(!TemplateUnchanged()){DiagnosticTestHook?.Invoke("模板变化");return false;}if(!FramesUnchanged(bounds))return false;
            var pt=new NativePoint{X=match.Point.X,Y=match.Point.Y};IntPtr target=WindowFromPoint(pt);
            if(target==IntPtr.Zero || GetAncestor(target,2)!=match.Window || GetWindowThreadProcessId(target,out uint pid)==0 || pid!=(uint)owned.Root.Id || !ScreenToClient(target,ref pt) || pt.X<0 || pt.Y<0 || pt.X>short.MaxValue || pt.Y>short.MaxValue){DiagnosticTestHook?.Invoke("点击目标归属或坐标不明");return false;}
            return dispatch(() => { if(!Owned(owned,match.Window,task,out var finalBounds) || finalBounds!=bounds || !Clear(match.Window,bounds) || !TemplateUnchanged() || !FramesUnchanged(bounds))return false;var screenPoint=new NativePoint{X=match.Point.X,Y=match.Point.Y};if(WindowFromPoint(screenPoint)!=target || GetAncestor(target,2)!=match.Window || GetWindowThreadProcessId(target,out uint finalPid)==0 || finalPid!=(uint)owned.Root.Id)return false;IntPtr lParam=(IntPtr)((pt.Y<<16)|(pt.X&0xffff));bool down=PostMessage(target,0x0201,(IntPtr)1,lParam);bool up=PostMessage(target,0x0202,IntPtr.Zero,lParam);return down && up; });
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception or ExternalException or OverflowException){return false;}
    }
}
