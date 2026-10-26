[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$BatchId,
  [Parameter(Mandatory = $true)][string[]]$Check,
  [string]$Root = (Join-Path $PSScriptRoot '../..')
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'feature-preview.psm1') -Force
$layout = New-FeaturePreviewLayout -Root $Root -BatchId $BatchId -Checks $Check
$launcher = New-FeaturePreviewLauncher -Root ([IO.Path]::GetFullPath($Root)) -PreviewDirectory $layout.PreviewDirectory
Write-Output "PreviewDirectory=$($layout.PreviewDirectory)"
Write-Output "Launcher=$launcher"
