Set-StrictMode -Version Latest

$script:PreviewManifestName = '预览.json'
$script:CurrentPointerName = '当前预览.json'

function Write-FeaturePreviewUtf8 {
  param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Content)
  [IO.File]::WriteAllText($Path, $Content, (New-Object Text.UTF8Encoding($true)))
}

function Get-FeaturePreviewFreeTcpPort {
  $listener = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Loopback), 0
  try {
    $listener.Start()
    return ([Net.IPEndPoint]$listener.LocalEndpoint).Port
  } finally {
    $listener.Stop()
  }
}


function ConvertTo-PreviewPathSegment {
  param([Parameter(Mandatory = $true)][string]$Value)
  $safe = $Value.Trim() -replace '[<>:"/\\|?*\x00-\x1F]', '_'
  $safe = $safe.Trim('.', ' ')
  if ([string]::IsNullOrWhiteSpace($safe)) { throw '功能批次标识不能在路径清理后为空。' }
  return $safe
}

function Get-PreviewRoot {
  param([Parameter(Mandatory = $true)][string]$Root)
  return [IO.Path]::GetFullPath((Join-Path $Root '产物/预览'))
}

function Get-FeaturePreviewBatchDirectory {
  param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$BatchId)
  return Join-Path (Get-PreviewRoot -Root $Root) (ConvertTo-PreviewPathSegment $BatchId)
}

function Assert-FeaturePreviewDirectory {
  param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$PreviewDirectory)
  $previewRoot = Get-PreviewRoot -Root $Root
  $candidate = [IO.Path]::GetFullPath($PreviewDirectory)
  $prefix = $previewRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '预览目录必须位于产物/预览内。' }
  $manifest = Join-Path $candidate $script:PreviewManifestName
  if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "预览目录缺少 $script:PreviewManifestName，拒绝启动。" }
  return $candidate
}

function Get-NextFeaturePreviewDirectory {
  param([Parameter(Mandatory = $true)][string]$BatchDirectory)
  $runs = Join-Path $BatchDirectory '运行'
  $null = New-Item -ItemType Directory -Path $runs -Force
  $stamp = Get-Date -Format 'yyyyMMdd-HHmm'
  for ($sequence = 1; $sequence -le 99; $sequence++) {
    $candidate = Join-Path $runs ('{0}-{1:d2}' -f $stamp, $sequence)
    if (-not (Test-Path -LiteralPath $candidate)) { return $candidate }
  }
  throw '同一分钟内预览运行编号已达到 99，拒绝覆盖已有证据。'
}

function New-FeaturePreviewLayout {
  param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$BatchId,
    [Parameter(Mandatory = $true)][string[]]$Checks
  )
  if (-not $Checks.Count -or @($Checks | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count) { throw '至少提供一条非空的预览检查项。' }
  $absoluteRoot = [IO.Path]::GetFullPath($Root)
  $batchDirectory = Get-FeaturePreviewBatchDirectory -Root $absoluteRoot -BatchId $BatchId
  $preview = Get-NextFeaturePreviewDirectory -BatchDirectory $batchDirectory
  $null = New-Item -ItemType Directory -Path $preview -ErrorAction Stop
  foreach ($name in 'UserData', 'appdata', 'localappdata', 'temp') { $null = New-Item -ItemType Directory -Path (Join-Path $preview $name) -ErrorAction Stop }
  $manifest = [ordered]@{
    schemaVersion = 2
    batchId = $BatchId
    createdAt = (Get-Date).ToString('o')
    root = $absoluteRoot
    batchDirectory = $batchDirectory
    previewDirectory = $preview
    checks = @($Checks)
    status = 'prepared'
  }
  Write-FeaturePreviewUtf8 -Path (Join-Path $preview $script:PreviewManifestName) -Content ($manifest | ConvertTo-Json -Depth 5)
  $lines = @(
    '# 检查说明', '', "- 功能批次：$BatchId", '- 启动方式：优先双击批次根目录的 `打开最新预览.cmd`。', '- 数据边界：只使用本目录的 `UserData/`、`appdata/`、`localappdata/` 与 `temp/`，不读取或修改项目根目录的 `data/`。', '- 验收结论：此预览不代表 EXE 已打包或正式人工验收。', '', '## 检查项', ''
  ) + @(for ($index = 0; $index -lt $Checks.Count; $index++) { "$($index + 1). $($Checks[$index])" })
  Write-FeaturePreviewUtf8 -Path (Join-Path $preview '检查说明.md') -Content ($lines -join [Environment]::NewLine)
  return [pscustomobject]@{ PreviewDirectory = $preview; BatchDirectory = $batchDirectory; Manifest = $manifest }
}

function New-FeaturePreviewLauncher {
  param([Parameter(Mandatory = $true)][string]$PreviewDirectory)
  $command = 'start "" /b powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0..\..\..\..\..\tools\feature-preview\Run-FeaturePreview.ps1" -PreviewDirectory "%~dp0."'
  $contents = @('@echo off', 'setlocal', $command, 'exit /b 0')
  $target = Join-Path $PreviewDirectory '启动预览.cmd'
  Set-Content -LiteralPath $target -Value $contents -Encoding ASCII
  return $target
}

function New-FeaturePreviewBatchLauncher {
  param([Parameter(Mandatory = $true)][string]$BatchDirectory)
  $command = 'start "" /b powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0..\..\..\tools\feature-preview\Start-LatestFeaturePreview.ps1" -BatchDirectory "%~dp0."'
  $contents = @('@echo off', 'setlocal', $command, 'exit /b 0')
  $target = Join-Path $BatchDirectory '打开最新预览.cmd'
  Set-Content -LiteralPath $target -Value $contents -Encoding ASCII
  return $target
}

function Set-FeaturePreviewCurrentPointer {
  param([Parameter(Mandatory = $true)]$Layout, [Parameter(Mandatory = $true)][string]$Launcher)
  $pointer = [ordered]@{ schemaVersion = 1; updatedAt = (Get-Date).ToString('o'); previewDirectory = $Layout.PreviewDirectory; launcher = $Launcher; checks = @($Layout.Manifest.checks) }
  Write-FeaturePreviewUtf8 -Path (Join-Path $Layout.BatchDirectory $script:CurrentPointerName) -Content ($pointer | ConvertTo-Json -Depth 5)
  $lines = @('# 当前预览', '', "- 当前可检查目录：$($Layout.PreviewDirectory)", "- 运行内启动器：$Launcher", '- 启动方式：返回批次根目录，双击 `打开最新预览.cmd`。每次启动都会新建一份运行证据。', '- 此文件是可更新的指针；`运行/` 中的单次证据不会被覆盖。', '', '## 本次检查项', '') + @($Layout.Manifest.checks | ForEach-Object { "- $_" })
  Write-FeaturePreviewUtf8 -Path (Join-Path $Layout.BatchDirectory '当前预览.md') -Content ($lines -join [Environment]::NewLine)
}

function Update-FeaturePreviewHistoryNotes {
  param([Parameter(Mandatory = $true)][string]$BatchDirectory)
  $legacy = @(Get-ChildItem -LiteralPath $BatchDirectory -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}_' })
  if (-not $legacy.Count) { return }
  $lines = @('# 历史预览说明', '', '以下目录是旧格式的运行证据，均已保留但不再作为用户启动入口。请使用批次根目录的 `打开最新预览.cmd`。', '')
  foreach ($directory in $legacy | Sort-Object Name) {
    $resultPath = Join-Path $directory.FullName 'result.json'
    $status = '未形成可用结果，不作为检查入口'
    if (Test-Path -LiteralPath $resultPath) {
      try {
        $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($result.status -eq 'closed') { $status = '旧格式下已验证关闭，仅供历史追溯' } else { $status = '旧格式启动未成功或被中断，不作为检查入口' }
      } catch { $status = '结果文件无法读取，不作为检查入口' }
    }
    $lines += ('- {0}：{1}。' -f $directory.Name, $status)
  }
  Write-FeaturePreviewUtf8 -Path (Join-Path $BatchDirectory '历史预览说明.md') -Content ($lines -join [Environment]::NewLine)
}

function Set-FeaturePreviewResult {
  param([Parameter(Mandatory = $true)][string]$PreviewDirectory, [Parameter(Mandatory = $true)][hashtable]$Result)
  Write-FeaturePreviewUtf8 -Path (Join-Path $PreviewDirectory '结果.json') -Content ($Result | ConvertTo-Json -Depth 5)
}

function Show-FeaturePreviewFailure {
  param([Parameter(Mandatory = $true)][string]$Message)
  try {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    [void][Windows.Forms.MessageBox]::Show($Message, '预览启动失败', [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Error)
  } catch { Write-Error $Message }
}

Export-ModuleMember -Function Write-FeaturePreviewUtf8, Get-FeaturePreviewFreeTcpPort, ConvertTo-PreviewPathSegment, Get-PreviewRoot, Get-FeaturePreviewBatchDirectory, Assert-FeaturePreviewDirectory, New-FeaturePreviewLayout, New-FeaturePreviewLauncher, New-FeaturePreviewBatchLauncher, Set-FeaturePreviewCurrentPointer, Update-FeaturePreviewHistoryNotes, Set-FeaturePreviewResult, Show-FeaturePreviewFailure
