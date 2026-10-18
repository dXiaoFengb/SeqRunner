Add-Type -AssemblyName PresentationFramework
$window = [System.Windows.Window]::new()
$window.Title = 'Runner UI log fixture'
$window.Width = 300
$window.Height = 160
$window.Left = -2000
$window.Top = -2000
$window.ShowInTaskbar = $true
$panel = [System.Windows.Controls.GroupBox]::new()
[System.Windows.Automation.AutomationProperties]::SetAutomationId($panel, 'logInterface')
$log = [System.Windows.Controls.TextBox]::new()
$log.IsReadOnly = $true
$log.Text = "baseline`n"
$panel.Content = $log
$window.Content = $panel
$timer = [System.Windows.Threading.DispatcherTimer]::new()
$timer.Interval = [TimeSpan]::FromSeconds(5)
$timer.Add_Tick({ $log.AppendText("new line`n"); $timer.Stop() })
$timer.Start()
$window.ShowDialog() | Out-Null
