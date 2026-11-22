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
        var buttons=new WrapPanel{Margin=new Thickness(0,12,0,0)};body.Children.Add(buttons);
        var save=new Button{Name="SaveMobileNoticeInput",Content="保存",Margin=new Thickness(0,0,8,8),Padding=new Thickness(12,6,12,6)};buttons.Children.Add(save);
        var test=new Button{Name="TestMobileNoticeInput",Content="发送测试",Margin=new Thickness(0,0,8,8),Padding=new Thickness(12,6,12,6)};buttons.Children.Add(test);
        var close=new Button{Content="关闭",Padding=new Thickness(12,6,12,6)};buttons.Children.Add(close);body.Children.Add(result);result.Text=lastStatus;
        bool Save()
        {
            try
            {
                var next=new MobileSettings{ServerEnabled=server.IsChecked==true,WebhookEnabled=webhook.IsChecked==true,ServerKey=string.IsNullOrEmpty(key.Password)?this.settings.ServerKey:key.Password.Trim(),WebhookUrl=string.IsNullOrEmpty(url.Password)?this.settings.WebhookUrl:url.Password.Trim(),Template=template.Text};
                MobileNotifications.Validate(next);MobileSettingsStore.Save(directory,next,recover);recover=false;this.settings=next;apply(next);key.Clear();url.Clear();result.Text="配置已保存；凭证不会显示。";return true;
            }
            catch(Exception e) when(e is ArgumentException or System.Text.Json.JsonException or System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException){result.Text="配置无效或保存失败，请检查渠道配置及数据目录。";return false;}
        }
        save.Click+=(_,_)=>Save();test.Click+=(_,_)=>{if(Save()){result.Text="正在发送测试；请稍候。";sender.SendTest(this.settings);}};close.Click+=(_,_)=>Close();
    }
    internal void SetStatus(string value)=>result.Text=value;
}
