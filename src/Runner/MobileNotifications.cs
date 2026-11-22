using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Runner;

internal sealed class MobileSettings
{
    public bool ServerEnabled { get; set; }
    public bool WebhookEnabled { get; set; }
    public bool QqEnabled { get; set; }
    public string ServerKey { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public string QqAppId { get; set; } = "";
    public string QqSecret { get; set; } = "";
    public string QqOpenId { get; set; } = "";
    public string Template { get; set; } = "{\"title\":\"{title}\",\"content\":\"{content}\"}";
}

internal static class MobileSettingsStore
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError=true, CharSet=CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError=true, CharSet=CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr ptr);
    static byte[] Transform(byte[] input, bool protect)
    {
        var source=new Blob { Size=input.Length, Data=Marshal.AllocHGlobal(input.Length) }; Blob output=default;
        try { Marshal.Copy(input,0,source.Data,input.Length); bool ok=protect ? CryptProtectData(ref source,null,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output) : CryptUnprotectData(ref source,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output); if(!ok)throw new CryptographicException("无法保护或读取当前用户的通知配置"); var bytes=new byte[output.Size];Marshal.Copy(output.Data,bytes,0,bytes.Length);return bytes; }
        finally { if(output.Data!=IntPtr.Zero){if(!protect && output.Size>0)Marshal.Copy(new byte[output.Size],0,output.Data,output.Size);LocalFree(output.Data);} if(source.Data!=IntPtr.Zero){Marshal.Copy(new byte[input.Length],0,source.Data,input.Length);Marshal.FreeHGlobal(source.Data);} }
    }
    internal static MobileSettings Load(string dir)
    {
        string recovered=Path.Combine(dir,"mobile-notifications-recovered.dat");string path=File.Exists(recovered)?recovered:Path.Combine(dir,"mobile-notifications.dat");if(!File.Exists(path))return new();
        if(new FileInfo(path).Length>262144)throw new InvalidDataException("通知配置过大");
        byte[] plain=Transform(File.ReadAllBytes(path),false);try{var data=JsonSerializer.Deserialize<MobileSettings>(plain) ?? throw new InvalidDataException();if(data.ServerKey==null || data.WebhookUrl==null || data.Template==null || data.QqAppId==null || data.QqSecret==null || data.QqOpenId==null)throw new InvalidDataException("通知配置无效");return data;}finally{CryptographicOperations.ZeroMemory(plain);}
    }
    internal static void Save(string dir,MobileSettings settings,bool recover=false)
    {
        byte[] plain=JsonSerializer.SerializeToUtf8Bytes(settings);byte[] encrypted;
        try{encrypted=Transform(plain,true);}finally{CryptographicOperations.ZeroMemory(plain);}
        string recovered=Path.Combine(dir,"mobile-notifications-recovered.dat");if(recover && File.Exists(recovered))throw new IOException("恢复配置已存在，拒绝覆盖");
        string target=recover || File.Exists(recovered)?recovered:Path.Combine(dir,"mobile-notifications.dat"),tmp=Path.Combine(dir,"mobile-notifications."+Guid.NewGuid().ToString("N")+".tmp");
        using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(encrypted);f.Flush(true);}File.Move(tmp,target,true);
    }
}

internal sealed class MobileNotifications : IDisposable
{
    readonly HttpClient client;
    readonly Channel<(MobileSettings Settings,LocalNotice Notice,int Generation)> queue=Channel.CreateBounded<(MobileSettings,LocalNotice,int)>(new BoundedChannelOptions(32){FullMode=BoundedChannelFullMode.Wait,SingleReader=true});
    readonly Action<string> status;
    readonly CancellationTokenSource stopping=new();
    readonly Task worker;
    readonly Func<QqBotClient> qqFactory;
    int generation;
    internal void InvalidatePending()=>Interlocked.Increment(ref generation);
    internal MobileNotifications(Action<string> status,HttpMessageHandler? handler=null,Func<QqBotClient>? qqFactory=null)
    {
        this.status=status;this.qqFactory=qqFactory ?? (()=>new QqBotClient());client=handler==null ? new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}) : new HttpClient(handler);client.Timeout=TimeSpan.FromSeconds(10);worker=Task.Run(ProcessAsync);
    }
    internal static bool ShouldSend(LocalNotice notice) => notice.IsWarning || notice.Title is "队列已完成" or "队列已结束" or "队列已停止";
    internal void Publish(MobileSettings settings,LocalNotice notice)
    {
        if((!settings.ServerEnabled && !settings.WebhookEnabled && !settings.QqEnabled) || !ShouldSend(notice))return;
        var snapshot=Snapshot(settings);
        if(!queue.Writer.TryWrite((snapshot,notice,Volatile.Read(ref generation))))Report("通知队列已满，本次未发送");
    }
    internal void SendTest(MobileSettings settings)
    {
        var snapshot=Snapshot(settings);
        if(!snapshot.ServerEnabled && !snapshot.WebhookEnabled && !snapshot.QqEnabled){Report("请先启用至少一个渠道");return;}
        if(!queue.Writer.TryWrite((snapshot,new LocalNotice("测试通知","这是一条测试通知。",true),Volatile.Read(ref generation))))Report("通知队列已满，本次未发送");
    }
    void Report(string message){try{status(message);}catch(Exception){}}
    static MobileSettings Snapshot(MobileSettings s)=>new(){ServerEnabled=s.ServerEnabled,WebhookEnabled=s.WebhookEnabled,QqEnabled=s.QqEnabled,ServerKey=s.ServerEnabled?s.ServerKey:"",WebhookUrl=s.WebhookEnabled?s.WebhookUrl:"",Template=s.Template,QqAppId=s.QqEnabled?s.QqAppId:"",QqSecret=s.QqEnabled?s.QqSecret:"",QqOpenId=s.QqEnabled?s.QqOpenId:""};
    async Task ProcessAsync()
    {
        try{await foreach(var (settings,notice,version) in queue.Reader.ReadAllAsync(stopping.Token)) { if(version!=Volatile.Read(ref generation))continue;if(settings.ServerEnabled)await SendOne("Server酱",settings,notice,true);if(version==Volatile.Read(ref generation) && settings.WebhookEnabled)await SendOne("Webhook",settings,notice,false);if(version==Volatile.Read(ref generation) && settings.QqEnabled)await SendQq(settings,notice,version); }}catch(OperationCanceledException){}
    }
    internal static Uri ServerUri(string key)
    {
        if(Regex.IsMatch(key,@"^SCT[A-Za-z0-9]+$") && Uri.TryCreate("https://sctapi.ftqq.com/"+key+".send",UriKind.Absolute,out var turbo))return turbo;
        var m=Regex.Match(key,@"^sctp([0-9]{1,18})t[A-Za-z0-9]+$");if(m.Success && Uri.TryCreate("https://"+m.Groups[1].Value+".push.ft07.com/send/"+key+".send",UriKind.Absolute,out var sc3))return sc3;
        throw new ArgumentException("Server酱密钥格式不正确");
    }
    internal static string RenderTemplate(string template,LocalNotice notice)
    {
        var root=JsonNode.Parse(template) ?? throw new ArgumentException("模板必须是 JSON");
        JsonNode? Replace(JsonNode? node)
        {
            if(node is JsonValue v && v.TryGetValue<string>(out var text))return JsonValue.Create(text.Replace("{title}",notice.Title,StringComparison.Ordinal).Replace("{content}",notice.Message,StringComparison.Ordinal));
            if(node is JsonObject o){foreach(var key in o.Select(x=>x.Key).ToArray()){var child=o[key];if(child is JsonValue)o[key]=Replace(child);else Replace(child);} }
            else if(node is JsonArray a){for(int i=0;i<a.Count;i++){var child=a[i];if(child is JsonValue)a[i]=Replace(child);else Replace(child);}}
            return node;
        }
        if(root is not JsonObject)throw new ArgumentException("模板必须是 JSON 对象");return Replace(root)!.ToJsonString();
    }
    internal static void Validate(MobileSettings s)
    {
        if(s.ServerKey.Length>2048 || s.WebhookUrl.Length>4096 || s.Template.Length>16384)throw new ArgumentException("通知配置过长");
        if(s.ServerEnabled)_=ServerUri(s.ServerKey);
        if(s.QqEnabled && (!QqBotClient.ValidId(s.QqAppId,s.QqOpenId) || string.IsNullOrWhiteSpace(s.QqSecret) || s.QqSecret.Length>1024))throw new ArgumentException("请先绑定 QQ 机器人");
        if(s.WebhookEnabled){if(!Uri.TryCreate(s.WebhookUrl,UriKind.Absolute,out var uri) || uri.Scheme!=Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))throw new ArgumentException("Webhook 地址必须是 HTTPS 地址");_ = RenderTemplate(s.Template,new("测试","测试"));}
    }
    async Task SendQq(MobileSettings settings,LocalNotice notice,int version)
    {
        void Current(string message){if(version==Volatile.Read(ref generation))Report(message);}
        try{using var qq=qqFactory();await qq.SendAsync(new(settings.QqAppId,settings.QqSecret,settings.QqOpenId),notice,stopping.Token);Current("QQ：服务已接受（手机接收情况需自行确认）");}
        catch(OperationCanceledException){if(!stopping.IsCancellationRequested)Current("QQ：请求超时");}
        catch(Exception e) when(e is HttpRequestException or IOException or JsonException or ArgumentException or InvalidOperationException or CryptographicException){Current("QQ：发送失败，请检查网络及机器人主动消息权限");}
    }
    async Task SendOne(string channel,MobileSettings settings,LocalNotice notice,bool server)
    {
        try
        {
            Uri uri;HttpContent content;
            if(server){uri=ServerUri(settings.ServerKey);content=new FormUrlEncodedContent(new Dictionary<string,string>{{"title",notice.Title},{"desp",notice.Message}});}
            else {if(!Uri.TryCreate(settings.WebhookUrl,UriKind.Absolute,out uri!) || uri.Scheme!=Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))throw new ArgumentException();content=new StringContent(RenderTemplate(settings.Template,notice),Encoding.UTF8,"application/json");}
            using(content)using(var response=await client.PostAsync(uri,content,stopping.Token))
            {
                if(!response.IsSuccessStatusCode){Report(channel+"：服务拒绝请求");return;}
                if(server){using var body=JsonDocument.Parse(await response.Content.ReadAsStreamAsync(stopping.Token));if(!body.RootElement.TryGetProperty("code",out var code) || code.ValueKind!=JsonValueKind.Number || !code.TryGetInt32(out int result) || result!=0){Report(channel+"：服务未接受请求");return;}}
                Report(server ? "Server酱：服务已接受（手机接收情况需自行确认）" : "Webhook：HTTP 请求成功（业务结果及手机接收情况需自行确认）");
            }
        }
        catch(OperationCanceledException){if(!stopping.IsCancellationRequested)Report(channel+"：请求超时");}
        catch(Exception e) when(e is HttpRequestException or IOException or JsonException or ArgumentException or InvalidOperationException){Report(channel+"：发送失败或配置无效");}
    }
    public void Dispose(){queue.Writer.TryComplete();stopping.Cancel();client.Dispose();_ = worker.ContinueWith(_=>stopping.Dispose(),TaskScheduler.Default);}
}
