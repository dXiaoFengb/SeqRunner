using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Runner;

public partial class MainWindow : Window
{
    readonly Store store;
    readonly RunnerEngine engine;
    readonly Forms.NotifyIcon tray;
    readonly Mutex instanceLock;
    bool exiting;
    public MainWindow()
    {
        InitializeComponent();
        string? overrideDir = Environment.GetEnvironmentVariable("RUNNER_V3_DATA_DIR");
        string dir = string.IsNullOrWhiteSpace(overrideDir) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SequentialRunnerV3") : overrideDir;
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToUpperInvariant())));
        instanceLock = new Mutex(true, "Global\\SequentialRunnerV3-" + id, out bool first);
        if (!first) { System.Windows.MessageBox.Show("该数据目录已由另一个实例使用。"); System.Windows.Application.Current.Shutdown(1); throw new InvalidOperationException("数据目录已被占用"); }
        store = new Store(dir);
        try { store.Load(); }
        catch (Exception e) { System.Windows.MessageBox.Show("数据读取失败，软件不会覆盖该文件：" + e.Message); System.Windows.Application.Current.Shutdown(1); throw; }
        TaskList.ItemsSource = store.Tasks;
        engine = new RunnerEngine(store, Refresh);
        HistoryList.ItemsSource = store.History;
        tray = new Forms.NotifyIcon { Text = "顺序化软件执行工具 V3", Icon = System.Drawing.SystemIcons.Application, Visible = true };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("打开", null, (_, _) => ShowWindow()); menu.Items.Add("退出", null, (_, _) => RequestExit()); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowWindow();
        Refresh();
        if (Environment.GetEnvironmentVariable("RUNNER_V3_SMOKE") == "1")
        {
            int seconds = int.TryParse(Environment.GetEnvironmentVariable("RUNNER_V3_SMOKE_SECONDS"), out int configured) ? Math.Clamp(configured, 2, 30) : 2;
            Loaded += (_, _) => { var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) }; timer.Tick += (_, _) => { timer.Stop(); RequestExit(); }; timer.Start(); };
        }
    }
    void Refresh() { Status.Text = engine.Status; HistoryList.Items.Refresh(); if (HistoryList.SelectedItem == null && store.History.Count > 0) HistoryList.SelectedItem = store.History[^1]; ShowHistory(); }
    void HistoryChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ShowHistory();
    void ShowHistory() { if (Events == null || HistoryList == null) return; Events.Text = string.Join(Environment.NewLine, (HistoryList.SelectedItem as RunRecord)?.Events ?? []); Events.ScrollToEnd(); }
    void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    void OnClosing(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); } }
    void RequestExit() { if (engine.Running) { System.Windows.MessageBox.Show("队列仍在运行。请先停止并等待受管程序退出。"); ShowWindow(); return; } try { store.Save(); } catch (Exception e) { System.Windows.MessageBox.Show("保存失败，软件保持打开：" + e.Message); return; } exiting = true; tray.Visible = false; tray.Dispose(); instanceLock.ReleaseMutex(); instanceLock.Dispose(); Close(); System.Windows.Application.Current.Shutdown(); }
    void Exit(object sender, RoutedEventArgs e) => RequestExit();
    void SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => Editor.DataContext = TaskList.SelectedItem;
    void Add(object sender, RoutedEventArgs e) { store.Tasks.Add(new TaskSpec()); TaskList.SelectedIndex = store.Tasks.Count - 1; }
    void Delete(object sender, RoutedEventArgs e) { if (TaskList.SelectedItem is TaskSpec t && System.Windows.MessageBox.Show("删除选中的任务配置？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes) store.Tasks.Remove(t); }
    void Move(int delta) { int i = TaskList.SelectedIndex, j = i + delta; if (i >= 0 && j >= 0 && j < store.Tasks.Count) { store.Tasks.Move(i, j); TaskList.SelectedIndex = j; } }
    void Up(object sender, RoutedEventArgs e) => Move(-1);
    void Down(object sender, RoutedEventArgs e) => Move(1);
    void Save(object sender, RoutedEventArgs e) { try { store.Save(); TaskList.Items.Refresh(); Status.Text = "配置已保存"; } catch (Exception ex) { System.Windows.MessageBox.Show("保存失败：" + ex.Message); } }
    async void Start(object sender, RoutedEventArgs e) { try { store.Save(); await engine.Start(); } catch (Exception ex) { System.Windows.MessageBox.Show("运行失败：" + ex.Message); } }
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
