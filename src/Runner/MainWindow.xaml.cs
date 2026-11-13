using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Runner;

public partial class MainWindow : Window
{
    readonly Store store = null!;
    readonly RunnerEngine engine = null!;
    readonly Forms.NotifyIcon tray = null!;
    readonly Mutex instanceLock;
    Scheduler scheduler = null!;
    System.Windows.Threading.DispatcherTimer scheduleTimer = null!;
    EventWaitHandle? activation, activationAck;
    RegisteredWaitHandle? activationWait;
    ObservableCollection<TaskSpec> drafts = [];
    TaskSpec? selected, editor;
    bool revertingSelection;
    bool exiting;
    static string Snapshot(object? value) => JsonSerializer.Serialize(value);
    bool HasDraftChanges => System.Windows.Controls.Validation.GetHasError(TimeoutInput) || Snapshot(drafts) != Snapshot(store.Tasks) || (editor != null && Snapshot(editor) != Snapshot(selected));
    const string Notice = "本工具只串行管理自己启动的任务及可追踪的受管进程。\n\n不要手动启动会共用日志的脚本实例；共享日志可能导致完成误判。若日志可能共享，请在任务配置中勾选对应选项，软件会暂停并请你核实。\n\n由外部服务启动的进程可能无法追踪，更新或重启线索也可能无法全部识别。";
    public MainWindow()
    {
        InitializeComponent();
        string? overrideDir = Environment.GetEnvironmentVariable("RUNNER_V3_DATA_DIR");
        string dir = string.IsNullOrWhiteSpace(overrideDir) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SequentialRunnerV3") : overrideDir;
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToUpperInvariant())));
        instanceLock = new Mutex(true, "Global\\SequentialRunnerV3-" + id, out bool first);
        if (!first)
        {
            bool activated = false;
            try { using var signal = EventWaitHandle.OpenExisting("Global\\SequentialRunnerV3-Activate-" + id); using var ack = EventWaitHandle.OpenExisting("Global\\SequentialRunnerV3-Ack-" + id); signal.Set(); activated = ack.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { }
            if (!activated) System.Windows.MessageBox.Show("软件已启动，但无法联系已有窗口。请通过通知区域打开，或检查已有实例。");
            instanceLock.Dispose(); System.Windows.Application.Current.Shutdown(); return;
        }
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\SequentialRunnerV3-Activate-" + id);
        activationAck = new EventWaitHandle(false, EventResetMode.AutoReset, "Global\\SequentialRunnerV3-Ack-" + id);
        activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(() => { ShowWindow(); activationAck?.Set(); }), null, Timeout.Infinite, false);
        store = new Store(dir);
        try { store.Load(); }
        catch (Exception e) { System.Windows.MessageBox.Show("数据读取失败，软件不会覆盖该文件：" + e.Message); System.Windows.Application.Current.Shutdown(1); throw; }
        drafts = new(store.Tasks.Select(t => t.Copy()));
        TaskList.ItemsSource = drafts;
        engine = new RunnerEngine(store, Refresh);
        scheduler = new Scheduler(store, engine, Refresh);
        scheduleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        scheduleTimer.Tick += (_, _) => scheduler.Tick(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        scheduler.Tick(DateTimeOffset.UtcNow, TimeZoneInfo.Local);
        scheduleTimer.Start();
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
        HistoryList.ItemsSource = store.History;
        tray = new Forms.NotifyIcon { Text = "顺序化软件执行工具 V3", Icon = System.Drawing.SystemIcons.Application, Visible = true };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("打开", null, (_, _) => ShowWindow()); menu.Items.Add("退出", null, (_, _) => RequestExit()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowWindow();
        Refresh();
        if (!store.NoticeSeen) Loaded += (_, _) => { if (Environment.GetEnvironmentVariable("RUNNER_V3_SMOKE") != "1") ShowNoticeCore(true); };
        if (Environment.GetEnvironmentVariable("RUNNER_V3_SMOKE") == "1")
        {
            int seconds = int.TryParse(Environment.GetEnvironmentVariable("RUNNER_V3_SMOKE_SECONDS"), out int configured) ? Math.Clamp(configured, 2, 30) : 2;
            Loaded += (_, _) => { var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) }; timer.Tick += (_, _) => { timer.Stop(); RequestExit(); }; timer.Start(); };
        }
    }
    void Refresh() { Status.Text = engine.Status + (scheduler != null && !string.IsNullOrEmpty(scheduler.Status) ? "；" + scheduler.Status : ""); HistoryList.Items.Refresh(); if (HistoryList.SelectedItem == null && store.History.Count > 0) HistoryList.SelectedItem = store.History[^1]; ShowHistory(); }
    void HistoryChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ShowHistory();
    void ShowHistory() { if (Events == null || HistoryList == null) return; Events.Text = string.Join(Environment.NewLine, (HistoryList.SelectedItem as RunRecord)?.Events ?? []); Events.ScrollToEnd(); }
    void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    void OnClosing(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); } }
    void PowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e) { if (e.Mode == Microsoft.Win32.PowerModes.Resume) Dispatcher.BeginInvoke(() => { scheduler.MarkResumed(); scheduler.Tick(DateTimeOffset.UtcNow, TimeZoneInfo.Local); }); }
    void OpenPlans(object sender, RoutedEventArgs e) { var window=new PlanWindow(store,engine,Refresh) { Owner=this }; window.ShowDialog(); }
    void ShowNoticeCore(bool first)
    {
        System.Windows.MessageBox.Show(Notice, "使用注意事项", MessageBoxButton.OK, MessageBoxImage.Information);
        if (first) { store.NoticeSeen = true; try { store.Save(); } catch (Exception e) { store.NoticeSeen = false; System.Windows.MessageBox.Show("注意事项状态保存失败：" + e.Message); } }
    }
    void ShowNotice(object sender, RoutedEventArgs e) => ShowNoticeCore(false);
    bool ResolveChanges()
    {
        if (!HasDraftChanges) return true;
        var answer = System.Windows.MessageBox.Show("任务配置有未保存的修改。选择“是”保存，“否”放弃，“取消”返回。", "保存配置", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return SaveDraft();
        drafts = new(store.Tasks.Select(t => t.Copy())); selected = null; editor = null;
        revertingSelection = true; TaskList.ItemsSource = drafts; TaskList.SelectedIndex = -1; revertingSelection = false; Editor.DataContext = null;
        return true;
    }
    bool SaveDraft()
    {
        if (System.Windows.Controls.Validation.GetHasError(TimeoutInput)) { System.Windows.MessageBox.Show("最长运行时间请输入 0 到 525600 之间的整数分钟。"); return false; }
        if (editor != null && (editor.TimeoutMinutes < 0 || editor.TimeoutMinutes > 525600)) { System.Windows.MessageBox.Show("最长运行时间请输入 0 到 525600 之间的整数分钟。"); return false; }
        if (editor != null && selected != null) { int i = drafts.IndexOf(selected); if (i >= 0) { revertingSelection = true; selected = editor.Copy(); drafts[i] = selected; TaskList.SelectedItem = selected; revertingSelection = false; } }
        var ids=drafts.Select(t=>t.Id).ToHashSet();
        if (store.Plans.Any(p=>p.Items.Any(i=>!ids.Contains(i.TaskId)))) { System.Windows.MessageBox.Show("计划引用了已删除的任务，请先在定时计划中移除引用。"); return false; }
        var old = store.Tasks;
        store.Tasks = new(drafts.Select(t => t.Copy()));
        try { store.Save(); }
        catch (Exception ex) { store.Tasks = old; System.Windows.MessageBox.Show("保存失败：" + ex.Message); return false; }
        if (selected != null) { editor = selected.Copy(); Editor.DataContext = editor; }
        TaskList.Items.Refresh(); Status.Text = "配置已保存"; return true;
    }
    void RequestExit() { if (engine.Running) { System.Windows.MessageBox.Show("队列仍在运行。请先停止并等待受管程序退出。"); ShowWindow(); return; } if (!ResolveChanges()) return; try { store.Save(); } catch (Exception e) { System.Windows.MessageBox.Show("保存失败，软件保持打开：" + e.Message); return; } exiting = true; scheduleTimer.Stop(); Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged; tray.Visible = false; tray.Dispose(); activationWait?.Unregister(null); activation?.Dispose(); activationAck?.Dispose(); instanceLock.ReleaseMutex(); instanceLock.Dispose(); Close(); System.Windows.Application.Current.Shutdown(); }
    void Exit(object sender, RoutedEventArgs e) => RequestExit();
    void SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (revertingSelection) return;
        var next = TaskList.SelectedItem as TaskSpec;
        if (editor != null && (System.Windows.Controls.Validation.GetHasError(TimeoutInput) || Snapshot(editor) != Snapshot(selected)))
        {
            var answer = System.Windows.MessageBox.Show("当前任务有未保存修改。选择“是”保存，“否”放弃，“取消”返回。", "保存配置", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) { revertingSelection = true; TaskList.SelectedItem = selected; revertingSelection = false; return; }
            if (answer == MessageBoxResult.Yes && !SaveDraft()) { revertingSelection = true; TaskList.SelectedItem = selected; revertingSelection = false; return; }
        }
        selected = next; editor = selected?.Copy(); Editor.DataContext = editor;
    }
    void Add(object sender, RoutedEventArgs e) { if (!ResolveChanges()) return; drafts.Add(new TaskSpec()); TaskList.SelectedIndex = drafts.Count - 1; }
    void Delete(object sender, RoutedEventArgs e) { if (TaskList.SelectedItem is not TaskSpec t) return; var refs=store.Plans.Where(p=>p.Items.Any(i=>i.TaskId==t.Id)).Select(p=>p.Name).ToList(); if(refs.Count>0) { System.Windows.MessageBox.Show("任务被计划引用，请先移除引用："+string.Join("、",refs)); return; } if (System.Windows.MessageBox.Show("删除选中的任务配置？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { revertingSelection = true; drafts.Remove(t); selected = null; editor = null; Editor.DataContext = null; revertingSelection = false; } }
    void Move(int delta) { int i = TaskList.SelectedIndex, j = i + delta; if (i >= 0 && j >= 0 && j < drafts.Count) { revertingSelection = true; drafts.Move(i, j); TaskList.SelectedIndex = j; revertingSelection = false; } }
    void Up(object sender, RoutedEventArgs e) => Move(-1);
    void Down(object sender, RoutedEventArgs e) => Move(1);
    void Save(object sender, RoutedEventArgs e) => SaveDraft();
    async void Start(object sender, RoutedEventArgs e) { try { if (!ResolveChanges()) return; await engine.Start(); } catch (Exception ex) { System.Windows.MessageBox.Show("运行失败：" + ex.Message); } }
    void Stop(object sender, RoutedEventArgs e) => engine.Stop();
    void Confirm(object sender, RoutedEventArgs e) => engine.Confirm();
    void Continue(object sender, RoutedEventArgs e) => engine.Continue();
    void ResolveRecovery(object sender, RoutedEventArgs e)
    {
        if (!engine.NeedsRecovery) { System.Windows.MessageBox.Show("没有待核实的中断运行。"); return; }
        if (System.Windows.MessageBox.Show("请先检查上次任务及其可能遗留的程序。已人工确认安全，可以允许重新启动队列吗？", "确认中断已核实", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { engine.AcknowledgeRecovery(); } catch (Exception ex) { System.Windows.MessageBox.Show("保存确认失败，仍禁止启动：" + ex.Message); }
    }
}
