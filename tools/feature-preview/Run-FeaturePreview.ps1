[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PreviewDirectory, [switch]$AutoClose)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'feature-preview.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '../ui-acceptance/acceptance-process-helpers.psm1') -Force

function Wait-FeaturePreviewFile {
  param([Parameter(Mandatory = $true)][string]$Path, [int]$TimeoutSeconds = 45)
  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
      try { return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
    }
    Start-Sleep -Milliseconds 250
  } while ((Get-Date) -lt $deadline)
  throw "预览在线验收未在 $TimeoutSeconds 秒内形成结果：$Path"
}

$preview = $null; $stdout = $null; $stderr = $null; $vite = $null; $verification = $null
$viteTree = $null; $electronTree = $null; $startedAt = (Get-Date).ToString('o'); $succeeded = $false
try {
  $PreviewDirectory = $PreviewDirectory.Trim().TrimEnd('"')
  $manifest = Get-Content -LiteralPath (Join-Path $PreviewDirectory '预览.json') -Raw -Encoding UTF8 | ConvertFrom-Json
  $root = [IO.Path]::GetFullPath([string]$manifest.root)
  $preview = Assert-FeaturePreviewDirectory -Root $root -PreviewDirectory $PreviewDirectory
  $stdout = Join-Path $preview '运行日志.log'; $stderr = Join-Path $preview '错误日志.log'
  $sourceState = Join-Path $preview '源码状态.json'; $onlineEvidence = Join-Path $preview '在线验收.json'
  $vitePort = Get-FeaturePreviewFreeTcpPort
  $node = (Get-Command 'node.exe' -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
  $viteScript = Join-Path $root 'node_modules/vite/bin/vite.js'
  $verificationScript = Join-Path $root 'tools/ui-acceptance/run-feature-preview.cjs'
  if (-not (Test-Path -LiteralPath $viteScript -PathType Leaf)) { throw "缺少 Vite 启动文件：$viteScript" }
  if (-not (Test-Path -LiteralPath $verificationScript -PathType Leaf)) { throw "缺少 Playwright 预览验收器：$verificationScript" }

  $gitHead = (& git -C $root rev-parse HEAD 2>$null | Select-Object -First 1)
  $gitStatus = @(& git -C $root status --short 2>$null)
  Write-FeaturePreviewUtf8 -Path $sourceState -Content ([ordered]@{ capturedAt = $startedAt; root = $root; gitHead = $gitHead; gitStatus = $gitStatus } | ConvertTo-Json -Depth 5)
  Write-FeaturePreviewUtf8 -Path $stdout -Content "[$startedAt] 启动隔离 Vite 与 Playwright Electron 预览。"
  Write-FeaturePreviewUtf8 -Path $stderr -Content ''

  $previous = @{}
  foreach ($name in 'RUNNER_USER_DATA_DIR', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP', 'NO_PROXY', 'no_proxy') { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
  try {
    $env:RUNNER_USER_DATA_DIR = Join-Path $preview 'UserData'; $env:APPDATA = Join-Path $preview 'appdata'; $env:LOCALAPPDATA = Join-Path $preview 'localappdata'; $env:TEMP = Join-Path $preview 'temp'; $env:TMP = $env:TEMP; $env:NO_PROXY = '127.0.0.1,localhost'; $env:no_proxy = $env:NO_PROXY
    $vite = Start-Process -FilePath $node -ArgumentList @($viteScript, '--host', '127.0.0.1', '--port', [string]$vitePort, '--strictPort', '--config', 'frontend/vite.config.ts') -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $viteTree = Get-AcceptanceProcessTree -RootProcessId $vite.Id
    if ($viteTree.status -ne 'checked') { throw "无法核验 Vite 进程树：$($viteTree.error)" }
    $verificationArguments = @($verificationScript, '--root', $root, '--preview-directory', $preview, '--vite-url', "http://127.0.0.1:$vitePort")
    if ($AutoClose) { $verificationArguments += '--auto-close' }
    $verification = Start-Process -FilePath $node -ArgumentList $verificationArguments -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput (Join-Path $preview '在线验收.log') -RedirectStandardError (Join-Path $preview '在线验收标准错误.log') -PassThru
  } finally { foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') } }

  $online = Wait-FeaturePreviewFile -Path $onlineEvidence
  if ($online.status -ne 'online') { throw "预览在线验收失败：$($online.error)" }
  $electronTree = Get-AcceptanceProcessTree -RootProcessId ([int]$online.electronProcessId)
  if ($electronTree.status -ne 'checked') { throw "无法核验 Electron 进程树：$($electronTree.error)" }
  $verification.WaitForExit(); $verification.Refresh()
  $finalOnline = Get-Content -LiteralPath $onlineEvidence -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($finalOnline.status -ne 'closed') { throw '预览窗口未通过在线验收或未正常关闭。' }
  $electronResidual = @(Wait-AcceptanceProcessExit -ProcessIds $electronTree.processIds -TimeoutMilliseconds 10000)
  if ($electronResidual.Count) { throw ('Electron 关闭后仍有受管进程：' + ($electronResidual -join ', ')) }
  Set-FeaturePreviewResult -PreviewDirectory $preview -Result @{ status = 'closed'; startedAt = $startedAt; endedAt = (Get-Date).ToString('o'); vitePort = $vitePort; userDataDirectory = (Join-Path $preview 'UserData'); onlineAcceptance = 'passed'; sourceState = (Split-Path -Leaf $sourceState) }
  $succeeded = $true
} catch {
  if ($preview -and (Test-Path -LiteralPath $preview)) {
    $runnerError = Join-Path $preview '运行器错误.log'
    Write-FeaturePreviewUtf8 -Path $runnerError -Content $_.Exception.ToString()
    Set-FeaturePreviewResult -PreviewDirectory $preview -Result @{ status = 'failed'; startedAt = $startedAt; endedAt = (Get-Date).ToString('o'); error = $_.Exception.Message }
  }
  Show-FeaturePreviewFailure -Message ("预览无法通过在线验收。`r`n$($_.Exception.Message)`r`n请查看：$runnerError")
} finally {
  $viteResidual = @()
  if ($viteTree -and $viteTree.status -eq 'checked') {
    $viteResidual = @(Wait-AcceptanceProcessExit -ProcessIds $viteTree.processIds -TimeoutMilliseconds 1000)
    if ($viteResidual.Count) {
      $viteTree = Get-AcceptanceProcessTree -RootProcessId $vite.Id
      if ($viteTree.status -ne 'checked') { throw "无法重新核验 Vite 进程树：$($viteTree.error)" }
      $viteResidual = @(Stop-AcceptanceProcessTree -RootProcessId $vite.Id -ProcessIds $viteTree.processIds)
    }
  }
  if ($preview) {
    $treeStatus = if (($viteTree -and $viteTree.status -eq 'checked') -and ($electronTree -and $electronTree.status -eq 'checked')) { 'checked' } else { 'unknown' }
    $tracked = @(); if ($viteTree) { $tracked += $viteTree.processIds }; if ($electronTree) { $tracked += $electronTree.processIds }
    $shutdown = New-AcceptanceShutdownEvidence -CloseRequestStatus 'requested' -ProcessTreeStatus $treeStatus -TrackedProcessIds $tracked -ResidualBeforeFallback $viteResidual -FallbackUsed ($viteResidual.Count -gt 0) -ResidualProcessIds $viteResidual -Error $null
    Write-FeaturePreviewUtf8 -Path (Join-Path $preview '进程收尾.json') -Content ($shutdown | ConvertTo-Json -Depth 5)
  }
}
if (-not $succeeded) { exit 1 }
