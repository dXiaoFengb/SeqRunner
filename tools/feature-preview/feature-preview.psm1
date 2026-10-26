Set-StrictMode -Version Latest

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

function Assert-FeaturePreviewDirectory {
  param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$PreviewDirectory
  )
  $previewRoot = Get-PreviewRoot -Root $Root
  $candidate = [IO.Path]::GetFullPath($PreviewDirectory)
  $prefix = $previewRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '预览目录必须位于产物/预览内。' }
  $manifest = Join-Path $candidate 'preview.json'
  if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw '预览目录缺少 preview.json，拒绝启动。' }
  return $candidate
}

function New-FeaturePreviewLayout {
  param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$BatchId,
    [Parameter(Mandatory = $true)][string[]]$Checks
  )
  if (-not $Checks.Count -or @($Checks | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count) { throw '至少提供一条非空的预览检查项。' }
  $absoluteRoot = [IO.Path]::GetFullPath($Root)
  $runId = "$(Get-Date -Format 'yyyy-MM-dd_HH-mm-ss-fff')-$(New-Guid)"
  $preview = Join-Path (Join-Path (Get-PreviewRoot -Root $absoluteRoot) (ConvertTo-PreviewPathSegment $BatchId)) $runId
  if (Test-Path -LiteralPath $preview) { throw "预览目录已存在：$preview" }
  $null = New-Item -ItemType Directory -Path $preview -ErrorAction Stop
  foreach ($name in 'UserData', 'appdata', 'localappdata', 'temp') {
    $null = New-Item -ItemType Directory -Path (Join-Path $preview $name) -ErrorAction Stop
  }
  $manifest = [ordered]@{
    schemaVersion = 1
    batchId = $BatchId
    createdAt = (Get-Date).ToString('o')
    root = $absoluteRoot
    previewDirectory = $preview
    checks = @($Checks)
    status = 'prepared'
  }
  $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $preview 'preview.json') -Encoding utf8NoBOM
  @(
    '# 本次功能检查说明',
    '',
    "- 功能批次：$BatchId",
    '- 启动方式：双击同目录的 `开始本次检查.cmd`；会打开正常的软件窗口。',
    '- 数据边界：本次只使用同目录的 `UserData/`、`appdata/`、`localappdata/` 与 `temp/`，不读取或修改项目根目录的 `data/`。',
    '- 验收结论：此预览只证明当前源码在隔离开发态的功能表现；它不代表 EXE 已打包或正式验收。',
    '',
    '## 检查项',
    ''
  ) + @(for ($index = 0; $index -lt $Checks.Count; $index++) { "$($index + 1). $($Checks[$index])" }) | Set-Content -LiteralPath (Join-Path $preview '本次功能检查说明.md') -Encoding utf8NoBOM
  return [pscustomobject]@{ PreviewDirectory = $preview; Manifest = $manifest }
}

function New-FeaturePreviewLauncher {
  param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$PreviewDirectory
  )
  # 预览目录和项目根目录都可能含中文。CMD 文件保持 ASCII，仅以 %~dp0
  # 传递实际路径，避免 CMD 的文件编码破坏中文绝对路径。
  $runnerRelative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($PreviewDirectory), [IO.Path]::GetFullPath((Join-Path $Root 'tools/feature-preview/Run-FeaturePreview.ps1'))).Replace('/', '\\')
  $command = 'start "" /b pwsh.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -PreviewDirectory "%~dp0."' -f ("%~dp0$runnerRelative")
  $contents = @(
    '@echo off',
    'setlocal',
    $command,
    'exit /b 0'
  )
  $target = Join-Path $PreviewDirectory '开始本次检查.cmd'
  $contents | Set-Content -LiteralPath $target -Encoding ascii
  return $target
}

function Set-FeaturePreviewResult {
  param(
    [Parameter(Mandatory = $true)][string]$PreviewDirectory,
    [Parameter(Mandatory = $true)][hashtable]$Result
  )
  $Result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PreviewDirectory 'result.json') -Encoding utf8NoBOM
}

Export-ModuleMember -Function ConvertTo-PreviewPathSegment, Get-PreviewRoot, Assert-FeaturePreviewDirectory, New-FeaturePreviewLayout, New-FeaturePreviewLauncher, Set-FeaturePreviewResult
