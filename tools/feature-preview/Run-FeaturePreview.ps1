[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PreviewDirectory)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'feature-preview.psm1') -Force
$PreviewDirectory = $PreviewDirectory.Trim().TrimEnd('"')
$manifestPath = Join-Path $PreviewDirectory 'preview.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$root = [IO.Path]::GetFullPath([string]$manifest.root)
$preview = Assert-FeaturePreviewDirectory -Root $root -PreviewDirectory $PreviewDirectory
$stdout = Join-Path $preview 'preview.log'
$stderr = Join-Path $preview 'preview-error.log'
$startedAt = (Get-Date).ToString('o')

$previous = @{}
foreach ($name in 'RUNNER_USER_DATA_DIR', 'APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP') { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
  $env:RUNNER_USER_DATA_DIR = Join-Path $preview 'UserData'
  $env:APPDATA = Join-Path $preview 'appdata'
  $env:LOCALAPPDATA = Join-Path $preview 'localappdata'
  $env:TEMP = Join-Path $preview 'temp'
  $env:TMP = $env:TEMP
  "[$startedAt] 启动隔离功能预览。" | Set-Content -LiteralPath $stdout -Encoding utf8NoBOM
  $process = Start-Process -FilePath 'npm.cmd' -ArgumentList @('run', 'dev') -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
} catch {
  Set-FeaturePreviewResult -PreviewDirectory $preview -Result @{ status = 'failed_to_start'; startedAt = $startedAt; endedAt = (Get-Date).ToString('o'); error = $_.Exception.Message }
  throw
} finally {
  foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}

$process.WaitForExit()
$endedAt = (Get-Date).ToString('o')
$logContents = if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -Raw -Encoding utf8 } else { '' }
$electronClosedNormally = $logContents -match 'electron \. exited with code 0'
$electronInterrupted = $logContents -match 'electron \. exited with code 4294967295'
$status = if ($process.ExitCode -eq 0 -or $electronClosedNormally) { 'closed' } elseif ($electronInterrupted) { 'interrupted' } else { 'failed' }
Set-FeaturePreviewResult -PreviewDirectory $preview -Result @{
  status = $status
  startedAt = $startedAt
  endedAt = $endedAt
  processId = $process.Id
  npmExitCode = $process.ExitCode
  electronClosedNormally = $electronClosedNormally
  userDataDirectory = (Join-Path $preview 'UserData')
}
