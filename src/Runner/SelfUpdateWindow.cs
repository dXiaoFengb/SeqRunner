using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Runner;

internal sealed class SelfUpdateWindow : Window
{
    readonly TextBlock status = new() { Name = "UpdateStatus", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    internal SelfUpdateWindow(Store store, SelfUpdateCoordinator coordinator, Func<Action<string>, CancellationToken, Task>? simulation = null)
    {
        Title = "软件更新"; Width = 560; Height = 420; MinWidth = 440; MinHeight = 350; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new Thickness(18) }; Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var version = typeof(SelfUpdateWindow).Assembly.GetName().Version;
        body.Children.Add(new TextBlock { Text = $"当前开发版本：{version?.Major}.{version?.Minor}.{version?.Build}", Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(new TextBlock { Text = "首版发布前仅保留更新通道；尚未配置可信发布公钥，不能检查或安装正式更新。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var startup = new System.Windows.Controls.CheckBox { Name = "UpdateStartupInput", Content = "软件启动时检查更新", IsChecked = store.UpdateCheckOnStartup, Margin = new Thickness(0, 4, 0, 4) };
        var scheduled = new System.Windows.Controls.CheckBox { Name = "UpdateScheduledInput", Content = "每天定时检查更新", IsChecked = store.UpdateCheckScheduled, Margin = new Thickness(0, 4, 0, 4) };
        var time = new System.Windows.Controls.TextBox { Name = "UpdateTimeInput", Text = store.UpdateCheckTime, Width = 90, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        body.Children.Add(startup); body.Children.Add(scheduled); body.Children.Add(new TextBlock { Text = "检查时间（24 小时制，如 09:00）" }); body.Children.Add(time);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; body.Children.Add(buttons);
        var save = new System.Windows.Controls.Button { Name = "UpdateSaveInput", Content = "保存设置", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8) };
        var check = new System.Windows.Controls.Button { Name = "UpdateCheckInput", Content = "检查更新", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8) };
        var close = new System.Windows.Controls.Button { Content = "关闭", Padding = new Thickness(12, 6, 12, 6) };
        buttons.Children.Add(save); buttons.Children.Add(check);
        var simulationCancellation = new CancellationTokenSource();
        if (simulation != null)
        {
            var simulate = new System.Windows.Controls.Button { Name = "UpdateSimulationInput", Content = "运行隔离更新测试", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8) };
            buttons.Children.Add(simulate);
            simulate.Click += async (_, _) => { simulate.IsEnabled = false; try { await simulation(value => status.Text = value, simulationCancellation.Token); } catch (Exception) { status.Text = "隔离测试无法启动；正式程序没有更新"; } finally { simulate.IsEnabled = true; } };
        }
        buttons.Children.Add(close); body.Children.Add(status); RefreshStatus(coordinator.Status);
        Closed += (_, _) => simulationCancellation.Cancel();
        save.Click += (_, _) =>
        {
            if (!TimeSpan.TryParseExact(time.Text, @"hh\:mm", CultureInfo.InvariantCulture, out _)) { status.Text = "请输入有效的检查时间，例如 09:00"; return; }
            bool oldStartup = store.UpdateCheckOnStartup, oldScheduled = store.UpdateCheckScheduled; string oldTime = store.UpdateCheckTime;
            store.UpdateCheckOnStartup = startup.IsChecked == true; store.UpdateCheckScheduled = scheduled.IsChecked == true; store.UpdateCheckTime = time.Text;
            try { store.Save(); status.Text = "更新设置已保存；未配置可信公钥时不会发起检查。"; }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { store.UpdateCheckOnStartup = oldStartup; store.UpdateCheckScheduled = oldScheduled; store.UpdateCheckTime = oldTime; status.Text = "更新设置保存失败，已保留原设置。"; }
        };
        check.Click += async (_, _) => await coordinator.CheckAsync(); close.Click += (_, _) => Close();
    }
    internal void RefreshStatus(string text) => status.Text = text;
}
