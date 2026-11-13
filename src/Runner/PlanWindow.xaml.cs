using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;

namespace Runner;

public partial class PlanWindow : Window
{
    readonly Store store;
    readonly RunnerEngine engine;
    readonly Action changed;
    ObservableCollection<SchedulePlan> plans;
    bool loading;
    static string Json(object value) => JsonSerializer.Serialize(value);
    static List<SchedulePlan> Clone(IEnumerable<SchedulePlan> value) => JsonSerializer.Deserialize<List<SchedulePlan>>(Json(value))!;
    SchedulePlan? Current => PlansList.SelectedItem as SchedulePlan;
    bool Dirty => Json(plans) != Json(store.Plans);
    public PlanWindow(Store store, RunnerEngine engine, Action changed)
    {
        InitializeComponent(); this.store=store; this.engine=engine; this.changed=changed;
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            SlotDays.Children.Add(new CheckBox { Content="周" + "日一二三四五六"[(int)day], Tag=day, IsChecked=true, Margin=new Thickness(3,0,0,0) });
            ItemDays.Children.Add(new CheckBox { Content="周" + "日一二三四五六"[(int)day], Tag=day, IsChecked=true, Margin=new Thickness(3,0,0,0) });
        }
        plans = new(Clone(store.Plans)); PlansList.ItemsSource=plans; TaskChoices.ItemsSource=store.Tasks; Details.IsEnabled=false;
    }
    static List<DayOfWeek> Days(StackPanel panel) => panel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (DayOfWeek)c.Tag).ToList();
    static void SetDays(StackPanel panel, IEnumerable<DayOfWeek> days) { var selected=days.ToHashSet(); foreach (var c in panel.Children.OfType<CheckBox>()) c.IsChecked=selected.Contains((DayOfWeek)c.Tag); }
    void SelectPlan(object sender, SelectionChangedEventArgs e)
    {
        loading=true; var p=Current; Details.IsEnabled=p!=null; PlanName.Text=p?.Name ?? ""; Enabled.IsChecked=p?.Enabled ?? false;
        SlotsList.ItemsSource=p?.Slots; ItemsList.ItemsSource=p?.Items; loading=false;
    }
    void NameChanged(object sender, TextChangedEventArgs e) { if (!loading && Current is { } p) { p.Name=PlanName.Text; PlansList.Items.Refresh(); } }
    void EnabledChanged(object sender, RoutedEventArgs e) { if (!loading && Current is { } p) { p.Enabled=Enabled.IsChecked==true; PlansList.Items.Refresh(); } }
    void NewPlan(object sender, RoutedEventArgs e) { var p=new SchedulePlan(); plans.Add(p); PlansList.SelectedItem=p; }
    void DeletePlan(object sender, RoutedEventArgs e) { if (Current is { } p && MessageBox.Show("删除该计划？保存后生效。", "删除计划", MessageBoxButton.YesNo)==MessageBoxResult.Yes) plans.Remove(p); }
    void MovePlan(int delta) { int i=PlansList.SelectedIndex, j=i+delta; if(i>=0 && j>=0 && j<plans.Count) { plans.Move(i,j); PlansList.SelectedIndex=j; } }
    void PlanUp(object sender, RoutedEventArgs e)=>MovePlan(-1);
    void PlanDown(object sender, RoutedEventArgs e)=>MovePlan(1);
    void SelectSlot(object sender, SelectionChangedEventArgs e) { if (SlotsList.SelectedItem is ScheduleSlot s) { SlotTime.Text=s.Time; SlotEnabled.IsChecked=s.Enabled; SetDays(SlotDays,s.Days); } }
    bool ReadSlot(out string time, out List<DayOfWeek> days)
    {
        time=SlotTime.Text.Trim(); days=Days(SlotDays);
        if (!TimeSpan.TryParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture, out var t) || t.TotalHours>=24 || days.Count==0) { MessageBox.Show("请输入 00:00 至 23:59 的时间并至少选择一个星期。"); return false; }
        return true;
    }
    void RefreshSlots(ScheduleSlot? selected=null) { SlotsList.ItemsSource=null; SlotsList.ItemsSource=Current?.Slots; SlotsList.SelectedItem=selected; }
    void AddSlot(object sender, RoutedEventArgs e) { if (Current is { } p && ReadSlot(out var time,out var days)) { var s=new ScheduleSlot { Time=time,Days=days,Enabled=SlotEnabled.IsChecked==true }; p.Slots.Add(s); RefreshSlots(s); } }
    void UpdateSlot(object sender, RoutedEventArgs e) { if (SlotsList.SelectedItem is ScheduleSlot s && ReadSlot(out var time,out var days)) { s.Time=time; s.Days=days; s.Enabled=SlotEnabled.IsChecked==true; RefreshSlots(s); } }
    void DeleteSlot(object sender, RoutedEventArgs e) { if (Current is { } p && SlotsList.SelectedItem is ScheduleSlot s) { p.Slots.Remove(s); RefreshSlots(); } }
    void SelectItem(object sender, SelectionChangedEventArgs e) { if (ItemsList.SelectedItem is ScheduleItem i) { TaskChoices.SelectedItem=store.Tasks.FirstOrDefault(t=>t.Id==i.TaskId); SetDays(ItemDays,i.Days); } }
    void RefreshItems(ScheduleItem? selected=null) { ItemsList.ItemsSource=null; ItemsList.ItemsSource=Current?.Items; ItemsList.SelectedItem=selected; }
    void AddItem(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p || TaskChoices.SelectedItem is not TaskSpec t) { MessageBox.Show("请先选择计划和已保存任务。"); return; }
        var days=Days(ItemDays); if(days.Count==0) { MessageBox.Show("至少选择一个运行星期。"); return; }
        if(p.Items.Any(i=>i.TaskId==t.Id)) { MessageBox.Show("该任务已在计划中。"); return; }
        var item=new ScheduleItem { TaskId=t.Id,TaskName=t.Name,Days=days }; p.Items.Add(item); RefreshItems(item);
    }
    void UpdateItem(object sender, RoutedEventArgs e) { if(ItemsList.SelectedItem is ScheduleItem i) { var days=Days(ItemDays); if(days.Count==0) { MessageBox.Show("至少选择一个运行星期。"); return; } i.Days=days; RefreshItems(i); } }
    void DeleteItem(object sender, RoutedEventArgs e) { if(Current is { } p && ItemsList.SelectedItem is ScheduleItem i) { p.Items.Remove(i); RefreshItems(); } }
    void MoveItem(int delta) { if(Current is not { } p) return; int i=ItemsList.SelectedIndex,j=i+delta; if(i>=0 && j>=0 && j<p.Items.Count) { var item=p.Items[i]; p.Items.RemoveAt(i); p.Items.Insert(j,item); RefreshItems(item); } }
    void ItemUp(object sender, RoutedEventArgs e)=>MoveItem(-1);
    void ItemDown(object sender, RoutedEventArgs e)=>MoveItem(1);
    bool SavePlans()
    {
        foreach(var p in plans)
        {
            if(string.IsNullOrWhiteSpace(p.Name)) { MessageBox.Show("计划名称不能为空。"); return false; }
            if(p.Enabled && (p.Items.Count==0 || p.Slots.Count==0)) { MessageBox.Show("启用的计划需要至少一个任务和时间点。"); return false; }
            if(p.Items.Any(i=>i.Days.Count==0 || !store.Tasks.Any(t=>t.Id==i.TaskId)) || p.Slots.Any(s=>s.Days.Count==0)) { MessageBox.Show("计划存在未保存或已删除的任务，或缺少星期。"); return false; }
        }
        var old=store.Plans; var copy=Clone(plans); var now=DateTimeOffset.UtcNow;
        foreach(var p in copy)
        {
            var before=old.FirstOrDefault(o=>o.Id==p.Id);
            if(before==null || (!before.Enabled && p.Enabled) || Json(before.Slots)!=Json(p.Slots)) p.EffectiveFrom=now;
            foreach(var item in p.Items) item.TaskName=store.Tasks.First(t=>t.Id==item.TaskId).Name;
        }
        store.Plans=copy;
        try { store.Save(); }
        catch(Exception e) { store.Plans=old; MessageBox.Show("计划保存失败："+e.Message); return false; }
        plans=new(Clone(store.Plans)); PlansList.ItemsSource=plans; changed(); MessageBox.Show("计划已保存。"); return true;
    }
    void Save(object sender, RoutedEventArgs e)=>SavePlans();
    async void RunPlan(object sender, RoutedEventArgs e)
    {
        if(Dirty) { MessageBox.Show("请先保存计划，再手动运行。"); return; }
        if(Current is not { } selected) { MessageBox.Show("请先选择计划。"); return; }
        if(engine.Running || engine.NeedsRecovery) { MessageBox.Show("当前有运行或待核实的中断，不能启动。"); return; }
        var p=store.Plans.FirstOrDefault(x=>x.Id==selected.Id); if(p==null) return;
        var tasks=p.Items.Where(i=>i.Days.Contains(DateTime.Now.DayOfWeek)).Select(i=>store.Tasks.FirstOrDefault(t=>t.Id==i.TaskId)).ToList();
        if(tasks.Count==0 || tasks.Any(t=>t==null)) { MessageBox.Show("今天没有可运行的任务，或计划引用的任务不存在。"); return; }
        var run=new RunRecord { Trigger="手动计划", PlanName=p.Name, State="运行中" }; run.Events.Add("已保存手动计划运行意图"); store.History.Add(run);
        try { store.Save(); } catch(Exception ex) { store.History.Remove(run); MessageBox.Show("运行记录保存失败，未启动："+ex.Message); return; }
        await engine.StartTasks(tasks.Select(t=>t!.Copy()).ToList(),run);
    }
    void CloseWindow(object sender, RoutedEventArgs e)=>Close();
    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!Dirty) return;
        var result=MessageBox.Show("计划有未保存修改。是：保存；否：放弃；取消：返回。", "保存计划", MessageBoxButton.YesNoCancel);
        if(result==MessageBoxResult.Cancel || (result==MessageBoxResult.Yes && !SavePlans())) e.Cancel=true;
    }
}
