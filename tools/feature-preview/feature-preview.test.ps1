$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot 'feature-preview.psm1'
Import-Module $module -Force
$root = Join-Path ([IO.Path]::GetTempPath()) ('runner-preview-test-' + [guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Path $root | Out-Null
  $first = New-FeaturePreviewLayout -Root $root -BatchId '日志/预览' -Checks @('打开日志页')
  $second = New-FeaturePreviewLayout -Root $root -BatchId '日志/预览' -Checks @('打开计划页')
  if ($first.PreviewDirectory -notmatch '运行[\\/]\d{8}-\d{4}-01$' -or $second.PreviewDirectory -notmatch '运行[\\/]\d{8}-\d{4}-02$') { throw '预览目录未使用短时间戳和递增序号。' }
  $launcher = New-FeaturePreviewLauncher -PreviewDirectory $second.PreviewDirectory
  $batchLauncher = New-FeaturePreviewBatchLauncher -BatchDirectory $second.BatchDirectory
  Set-FeaturePreviewCurrentPointer -Layout $second -Launcher $launcher
  $port = Get-FeaturePreviewFreeTcpPort
  if ($port -lt 1 -or $port -gt 65535) { throw '未能获取有效的隔离 Vite 端口。' }
  $node = (Get-Command 'node.exe' -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
  if ([string]::IsNullOrWhiteSpace($node) -or -not (Test-Path -LiteralPath $node -PathType Leaf)) { throw '预览运行未解析到单个 node.exe 路径。' }
  $emptyLog = Join-Path $second.PreviewDirectory '错误日志.log'
  Write-FeaturePreviewUtf8 -Path $emptyLog -Content ''
  if ([IO.File]::ReadAllText($emptyLog) -ne '') { throw '错误日志初始化必须允许空内容。' }
  foreach ($name in '预览.json', '检查说明.md', 'UserData', 'appdata', 'localappdata', 'temp', '启动预览.cmd') {
    if (-not (Test-Path -LiteralPath (Join-Path $second.PreviewDirectory $name))) { throw "缺少预览文件或目录：$name" }
  }
  foreach ($name in '打开最新预览.cmd', '当前预览.json', '当前预览.md') {
    if (-not (Test-Path -LiteralPath (Join-Path $second.BatchDirectory $name))) { throw "缺少批次入口文件：$name" }
  }
  $runCommand = Get-Content -LiteralPath $launcher -Encoding ASCII
  $batchCommand = Get-Content -LiteralPath $batchLauncher -Encoding ASCII
  if ($runCommand.Count -ne 4 -or $runCommand[2] -notmatch 'powershell\.exe' -or $runCommand[2] -notmatch 'Run-FeaturePreview\.ps1') { throw '运行目录启动器不是完整的 Windows PowerShell 命令。' }
  if ($batchCommand.Count -ne 4 -or $batchCommand[2] -notmatch 'powershell\.exe' -or $batchCommand[2] -notmatch 'Start-LatestFeaturePreview\.ps1') { throw '批次入口不是完整的 Windows PowerShell 命令。' }
  if (Test-Path -LiteralPath (Join-Path $root '产物/工作')) { throw '预览不应创建产物/工作。' }
  if (Test-Path -LiteralPath (Join-Path $root '产物/验证')) { throw '预览不应创建产物/验证。' }
  Write-Output 'Feature preview layout test passed.'
} finally {
  if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
