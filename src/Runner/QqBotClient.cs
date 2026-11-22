using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Runner;

// Protocol adapted from Tencent Connect qqbot-agent-sdk 1.2.2 (MIT); see third-party notices.
internal sealed record QqCredentials(string AppId,string Secret,string OpenId);
internal sealed record QqBindingResult(int Status,QqCredentials? Credentials=null);
internal sealed class QqBindingSession : IDisposable
{
    internal string TaskId { get; }
    internal byte[] Key { get; }
    internal string Url => "https://q.qq.com/qqbot/openclaw/connect.html?task_id="+Uri.EscapeDataString(TaskId)+"&_wv=2";
    internal QqBindingSession(string taskId,byte[] key){TaskId=taskId;Key=key;}
    public void Dispose()=>CryptographicOperations.ZeroMemory(Key);
}
internal sealed class QqBotClient : IDisposable
{
    readonly HttpClient http;
    internal QqBotClient(HttpMessageHandler? handler=null)
    {
        http=handler==null?new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}):new HttpClient(handler);
        http.Timeout=TimeSpan.FromSeconds(10);http.DefaultRequestHeaders.Accept.ParseAdd("application/json");http.DefaultRequestHeaders.UserAgent.ParseAdd("QQBotAdapter/1.2.2 (SequentialRunnerV3/1.19; Windows)");
    }
    async Task<JsonDocument> Post(string url,object body,CancellationToken token,string? bearer=null)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,url){Content=JsonContent.Create(body)};
        if(bearer!=null)request.Headers.TryAddWithoutValidation("Authorization","QQBot "+bearer);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
        using var stream=await response.Content.ReadAsStreamAsync(token);using var bytes=new MemoryStream();var buffer=new byte[4096];int read;
        while((read=await stream.ReadAsync(buffer,token))>0){if(bytes.Length+read>65536)throw new InvalidDataException("QQ 响应过大");bytes.Write(buffer,0,read);}
        return JsonDocument.Parse(bytes.ToArray());
    }
    static JsonElement Data(JsonDocument doc)
    {
        if(!doc.RootElement.TryGetProperty("retcode",out var ret) || ret.ValueKind!=JsonValueKind.Number || ret.GetInt32()!=0 || !doc.RootElement.TryGetProperty("data",out var data) || data.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("QQ 绑定请求未成功");return data;
    }
    static string String(JsonElement obj,string key)
    {
        if(!obj.TryGetProperty(key,out var value))return "";return value.ValueKind is JsonValueKind.String or JsonValueKind.Number?value.ToString():"";
    }
    internal async Task<QqBindingSession> CreateAsync(CancellationToken token)
    {
        byte[] key=RandomNumberGenerator.GetBytes(32);try{using var doc=await Post("https://q.qq.com/lite/create_bind_task",new{key=Convert.ToBase64String(key)},token);string id=String(Data(doc),"task_id");if(string.IsNullOrWhiteSpace(id) || id.Length>512)throw new InvalidDataException("QQ 绑定任务无效");return new(id,key);}catch{CryptographicOperations.ZeroMemory(key);throw;}
    }
    internal async Task<QqBindingResult> PollAsync(QqBindingSession session,CancellationToken token)
    {
        using var doc=await Post("https://q.qq.com/lite/poll_bind_result",new{task_id=session.TaskId},token);var data=Data(doc);
        if(!data.TryGetProperty("status",out var status) || status.ValueKind!=JsonValueKind.Number || !status.TryGetInt32(out int code) || code<0 || code>3)throw new InvalidDataException("QQ 绑定状态不明");
        if(code!=2)return new(code);string id=String(data,"bot_appid"),openid=String(data,"user_openid"),encoded=String(data,"bot_encrypt_secret");
        if(!ValidId(id,openid) || string.IsNullOrEmpty(encoded) || encoded.Length>4096)throw new InvalidDataException("QQ 绑定信息不完整");
        byte[] packed=Convert.FromBase64String(encoded);if(packed.Length<29)throw new InvalidDataException("QQ 凭证无效");byte[] plain=new byte[packed.Length-28];
        try{using var aes=new AesGcm(session.Key,16);aes.Decrypt(packed.AsSpan(0,12),packed.AsSpan(12,plain.Length),packed.AsSpan(12+plain.Length,16),plain);string secret=Encoding.UTF8.GetString(plain);if(string.IsNullOrWhiteSpace(secret) || secret.Length>1024)throw new InvalidDataException("QQ 凭证无效");return new(2,new(id,secret,openid));}finally{CryptographicOperations.ZeroMemory(plain);}
    }
    internal static bool ValidId(string id,string openid)=>id.Length is >0 and <=32 && id.All(char.IsAsciiDigit) && openid.Length is >0 and <=256 && openid.All(c=>char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    internal async Task SendAsync(QqCredentials credentials,LocalNotice notice,CancellationToken token)
    {
        if(!ValidId(credentials.AppId,credentials.OpenId) || string.IsNullOrWhiteSpace(credentials.Secret) || credentials.Secret.Length>1024)throw new InvalidDataException("QQ 绑定信息无效");
        using var auth=await Post("https://bots.qq.com/app/getAppAccessToken",new{appId=credentials.AppId,clientSecret=credentials.Secret},token);string access=String(auth.RootElement,"access_token");
        if(string.IsNullOrEmpty(access) || access.Length>4096 || access.Any(char.IsControl))throw new InvalidDataException("QQ 授权不可用");
        string text=notice.Title+"\n"+notice.Message;if(text.Length>1000)text=text[..1000];
        using var response=await Post("https://api.sgroup.qq.com/v2/users/"+Uri.EscapeDataString(credentials.OpenId)+"/messages",new{content=text,msg_type=0},token,access);
        if(string.IsNullOrEmpty(String(response.RootElement,"id")))throw new InvalidDataException("QQ 未确认请求");
    }
    public void Dispose()=>http.Dispose();
}
