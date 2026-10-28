[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$BatchId,
  [Parameter(Mandatory = $true)][string[]]$Check,
  [string]$Root = (Join-Path $PSScriptRoot '../..')
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'feature-preview.psm1') -Force
$defaultRoot = Join-Path $PSScriptRoot '../..'
if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
  # powershell.exe -File 会把多余的逗号分隔值当作下一个位置参数；避免把检查项误写成根目录。
  $Check += $Root
  $Root = $defaultRoot
}
$layout = New-FeaturePreviewLayout -Root $Root -BatchId $BatchId -Checks $Check
$launcher = New-FeaturePreviewLauncher -PreviewDirectory $layout.PreviewDirectory
$batchLauncher = New-FeaturePreviewBatchLauncher -BatchDirectory $layout.BatchDirectory
Set-FeaturePreviewCurrentPointer -Layout $layout -Launcher $launcher
Update-FeaturePreviewHistoryNotes -BatchDirectory $layout.BatchDirectory
Write-Output "BatchDirectory=$($layout.BatchDirectory)"
Write-Output "PreviewDirectory=$($layout.PreviewDirectory)"
Write-Output "BatchLauncher=$batchLauncher"
Write-Output "Launcher=$launcher"
