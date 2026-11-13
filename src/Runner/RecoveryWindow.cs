using System.Windows;
using System.Windows.Controls;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace Runner;

public sealed class RecoveryWindow : Window
{
    readonly Store store;
    readonly RunnerEngine engine;
    readonly CompletionCoordinator completion;
    readonly Action changed;
    readonly ListBox items = new() { MinHeight=110, Margin=new Thickness(0,8,0,8) };
    readonly TextBox details = new() { IsReadOnly=true, TextWrapping=TextWrapping.Wrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, MinHeight=150 };
    public RecoveryWindow(Store store, RunnerEngine engine, CompletionCoordinator completion, Action changed)
    {
        this.store=store; this.engine=engine; this.completion=completion; this.changed=changed;
        Title="待核实事项"; Width=720; Height=520; MinWidth=540; MinHeight=400; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var layout=new DockPanel { Margin=new Thickness(14) }; Content=layout;
        var header=new TextBlock { Text="选择一条事项，阅读详情并在人工检查后逐条确认。确认不会将任务判为成功，也不会重跑任务或重新发出系统操作。", TextWrapping=TextWrapping.Wrap };
        DockPanel.SetDock(header,Dock.Top); layout.Children.Add(header);
        var controls=new StackPanel { Orientation=System.Windows.Controls.Orientation.Horizontal, Margin=new Thickness(0,10,0,0) };
        var confirm=new Button { Content="确认已检查所选事项", Padding=new Thickness(12,6,12,6), Margin=new Thickness(0,0,8,0) }; confirm.Click+=Confirm;
        var close=new Button { Content="关闭", Padding=new Thickness(12,6,12,6) }; close.Click+=(_,_)=>Close(); controls.Children.Add(confirm); controls.Children.Add(close); DockPanel.SetDock(controls,Dock.Bottom); layout.Children.Add(controls);
        DockPanel.SetDock(items,Dock.Top); layout.Children.Add(items); layout.Children.Add(details); items.SelectionChanged+=(_,_)=>ShowDetails(); Reload();
    }
    void Reload() { items.ItemsSource=null; items.ItemsSource=Recovery.Pending(store); if(items.Items.Count>0) items.SelectedIndex=0; else details.Text="没有待核实事项。"; }
    void ShowDetails()
    {
        if(items.SelectedItem is not RunRecord item) return;
        string guidance=Recovery.IsSystemAction(item) ? "请检查系统操作是否已经发生；本工具无法确认系统最终状态。不要仅凭此记录重新发出操作。" : "请检查相关程序及其可能遗留的子进程是否仍在运行，确认业务状态；本工具不会接管或结束身份不明的进程。";
        details.Text=$"来源：{item.Trigger}\n计划：{(string.IsNullOrWhiteSpace(item.PlanName) ? "未记录" : item.PlanName)}\n开始时间：{item.Started:yyyy-MM-dd HH:mm:ss zzz}\n状态：{item.State}\n\n{guidance}\n\n已有记录：\n"+string.Join("\n",item.Events);
    }
    void Confirm(object sender, RoutedEventArgs e)
    {
        if(items.SelectedItem is not RunRecord item) return;
        if(MessageBox.Show("你已按提示检查所选事项，确认解除这一条阻塞吗？", "确认人工核实", MessageBoxButton.YesNo, MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
        try { engine.AcknowledgeRecovery(item); completion.RecoveryAcknowledged(); changed(); Reload(); }
        catch(Exception ex) { MessageBox.Show("核实结果保存失败，仍禁止启动："+ex.Message); Reload(); }
    }
}
