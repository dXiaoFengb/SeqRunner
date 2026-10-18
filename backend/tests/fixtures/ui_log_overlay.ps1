Add-Type -AssemblyName PresentationFramework
$window = [System.Windows.Window]::new()
$window.Title = 'Runner overlay fixture'
$window.Width = 300
$window.Height = 160
$window.Left = -2000
$window.Top = -2000
$log = [System.Windows.Controls.TextBlock]::new()
[System.Windows.Automation.AutomationProperties]::SetAutomationId($log, 'gameLogOverlayBody')
$log.Text = "baseline`n"
$window.Content = $log
$timer = [System.Windows.Threading.DispatcherTimer]::new()
$timer.Interval = [TimeSpan]::FromSeconds(3)
$timer.Add_Tick({ $log.Text = "baseline`nnew line`n"; $timer.Stop() })
$timer.Start()
$window.ShowDialog() | Out-Null
