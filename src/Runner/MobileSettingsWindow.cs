using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;

namespace Runner;

internal sealed class MobileSettingsWindow : Window
{
    readonly string directory;
    readonly MobileNotifications sender;
    readonly Action<MobileSettings> apply;
    readonly CheckBox server=new(){Name="ServerNoticeEnabledInput",Content="启用 Server酱"}, webhook=new(){Name="WebhookNoticeEnabledInput",Content="启用 Webhook"};
    readonly PasswordBox key=new(){Name="ServerNoticeKeyInput"},url=new(){Name="WebhookNoticeUrlInput"};
    readonly TextBox template=new(){Name="WebhookNoticeTemplateInput",AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=90};
    readonly TextBlock result=new(){Name="MobileNoticeResult",TextWrapping=TextWrapping.Wrap};
    readonly CheckBox qq=new(){Name="QqNoticeEnabledInput",Content="启用 QQ 官方机器人"};
    readonly TextBlock qqStatus=new(){Name="QqNoticeStatus",TextWrapping=TextWrapping.Wrap};
    readonly System.Windows.Controls.Image qr=new(){Name="QqNoticeQr",Width=200,Height=200,HorizontalAlignment=System.Windows.HorizontalAlignment.Left,Visibility=Visibility.Collapsed};
    readonly Button bind=new(){Name="QqNoticeBindInput",Content="扫码绑定 QQ",Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,8,8)};
    CancellationTokenSource? binding;
    MobileSettings settings;
    bool recover;
    internal MobileSettingsWindow(string directory,MobileSettings settings,MobileNotifications sender,Action<MobileSettings> apply,string lastStatus,bool recover)
    {
        this.directory=directory;this.settings=settings;this.sender=sender;this.apply=apply;this.recover=recover;
        Title="手机通知";Width=620;Height=600;MinWidth=480;MinHeight=400;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Margin=new Thickness(18)};Content=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        void Add(string text){body.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,4)});}
        Add("默认仅发送异常、需人工处理及队列结束。手机通知依赖网络和第三方服务，服务接受不代表手机已收到。");
        server.IsChecked=settings.ServerEnabled;webhook.IsChecked=settings.WebhookEnabled;body.Children.Add(server);
        Add("Server酱 SendKey（留空保留已保存值，不会显示已有密钥）");body.Children.Add(key);
        body.Children.Add(webhook);Add("Webhook HTTPS 地址（留空保留已保存值，不会显示已有地址）");body.Children.Add(url);
        Add("Webhook JSON 模板：字符串中的 {title}、{content} 会被替换。");template.Text=settings.Template;body.Children.Add(template);
        qq.IsChecked=settings.QqEnabled;body.Children.Add(qq);Add("扫码可能需要注册 QQ 开放平台或创建机器人；请由本人在手机确认。主动消息还需要平台权限。绑定不会自动启用通知；解绑前已发出的请求可能仍被平台处理。");body.Children.Add(qqStatus);body.Children.Add(qr);
        var qqButtons=new WrapPanel();body.Children.Add(qqButtons);qqButtons.Children.Add(bind);
        var cancel=new Button{Name="QqNoticeCancelInput",Content="取消扫码",Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,8,8)};qqButtons.Children.Add(cancel);
        var unbind=new Button{Name="QqNoticeUnbindInput",Content="解绑 QQ",Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,8,8)};qqButtons.Children.Add(unbind);
        void ShowBinding()=>qqStatus.Text=string.IsNullOrEmpty(this.settings.QqSecret)?"QQ：未绑定":this.settings.QqEnabled?"QQ：已绑定并启用":"QQ：已绑定，尚未启用";
        ShowBinding();
        var buttons=new WrapPanel{Margin=new Thickness(0,12,0,0)};body.Children.Add(buttons);
        var save=new Button{Name="SaveMobileNoticeInput",Content="保存",Margin=new Thickness(0,0,8,8),Padding=new Thickness(12,6,12,6)};buttons.Children.Add(save);
        var test=new Button{Name="TestMobileNoticeInput",Content="发送测试",Margin=new Thickness(0,0,8,8),Padding=new Thickness(12,6,12,6)};buttons.Children.Add(test);
        var close=new Button{Content="关闭",Padding=new Thickness(12,6,12,6)};buttons.Children.Add(close);body.Children.Add(result);result.Text=lastStatus;
        bool Save()
        {
            try
            {
                var next=new MobileSettings{ServerEnabled=server.IsChecked==true,WebhookEnabled=webhook.IsChecked==true,QqEnabled=qq.IsChecked==true,ServerKey=string.IsNullOrEmpty(key.Password)?this.settings.ServerKey:key.Password.Trim(),WebhookUrl=string.IsNullOrEmpty(url.Password)?this.settings.WebhookUrl:url.Password.Trim(),Template=template.Text,QqAppId=this.settings.QqAppId,QqSecret=this.settings.QqSecret,QqOpenId=this.settings.QqOpenId};
                MobileNotifications.Validate(next);MobileSettingsStore.Save(directory,next,recover);recover=false;this.settings=next;apply(next);ShowBinding();key.Clear();url.Clear();result.Text="配置已保存；凭证不会显示。";return true;
            }
            catch(Exception e) when(e is ArgumentException or System.Text.Json.JsonException or System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException){result.Text="配置无效或保存失败，请检查渠道配置及数据目录。";return false;}
        }
        save.Click+=(_,_)=>Save();test.Click+=(_,_)=>{if(Save()){result.Text="正在发送测试；请稍候。";sender.SendTest(this.settings);}};close.Click+=(_,_)=>Close();
        bind.Click+=async (_,_)=>
        {
            if(binding!=null)return;var cts=new CancellationTokenSource(TimeSpan.FromMinutes(5));binding=cts;bind.IsEnabled=false;qqStatus.Text="QQ：正在获取扫码信息";
            try
            {
                using var client=new QqBotClient();using var session=await client.CreateAsync(cts.Token);cts.Token.ThrowIfCancellationRequested();
                using var generator=new QRCoder.QRCodeGenerator();using var data=generator.CreateQrCode(session.Url,QRCoder.QRCodeGenerator.ECCLevel.Q);using var code=new QRCoder.PngByteQRCode(data);byte[] png=code.GetGraphic(5);
                var bitmap=new System.Windows.Media.Imaging.BitmapImage();bitmap.BeginInit();bitmap.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;using(var memory=new System.IO.MemoryStream(png)){bitmap.StreamSource=memory;bitmap.EndInit();}bitmap.Freeze();qr.Source=bitmap;qr.Visibility=Visibility.Visible;qqStatus.Text="QQ：等待扫码；请在手机 QQ 确认";
                while(true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2),cts.Token);var item=await client.PollAsync(session,cts.Token);cts.Token.ThrowIfCancellationRequested();
                    if(item.Status==3){qqStatus.Text="QQ：二维码已过期，请重新扫码";break;}if(item.Status!=2)continue;
                    var credentials=item.Credentials!;var previous=this.settings;var next=new MobileSettings{ServerEnabled=previous.ServerEnabled,WebhookEnabled=previous.WebhookEnabled,ServerKey=previous.ServerKey,WebhookUrl=previous.WebhookUrl,Template=previous.Template,QqEnabled=false,QqAppId=credentials.AppId,QqSecret=credentials.Secret,QqOpenId=credentials.OpenId};
                    MobileSettingsStore.Save(directory,next,recover);recover=false;this.settings=next;apply(next);qq.IsChecked=false;qqStatus.Text="QQ：绑定成功，请启用并保存";break;
                }
            }
            catch(OperationCanceledException){qqStatus.Text=cts.IsCancellationRequested?"QQ：扫码已取消或超时":"QQ：请求超时，请重试";}
            catch(Exception e) when(e is System.Net.Http.HttpRequestException or System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException or FormatException){qqStatus.Text="QQ：绑定失败，请检查网络或稍后重试";}
            finally{qr.Source=null;qr.Visibility=Visibility.Collapsed;binding=null;bind.IsEnabled=true;cts.Dispose();}
        };
        cancel.Click+=(_,_)=>binding?.Cancel();
        unbind.Click+=(_,_)=>{binding?.Cancel();try{var previous=this.settings;var next=new MobileSettings{ServerEnabled=previous.ServerEnabled,WebhookEnabled=previous.WebhookEnabled,ServerKey=previous.ServerKey,WebhookUrl=previous.WebhookUrl,Template=previous.Template};MobileSettingsStore.Save(directory,next,recover);recover=false;this.settings=next;apply(next);qq.IsChecked=false;qqStatus.Text="QQ：已解绑";result.Text="";}catch(Exception e) when(e is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException){qqStatus.Text="QQ：解绑记录保存失败，原设置保留";}};
        Closed+=(_,_)=>binding?.Cancel();
    }
    internal void SetStatus(string value)=>result.Text=value;
}
