[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$BatchDirectory, [switch]$AutoClose)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'feature-preview.psm1') -Force
try {
  $batch = [IO.Path]::GetFullPath($BatchDirectory.Trim().TrimEnd('"'))
  $pointerPath = Join-Path $batch '当前预览.json'
  if (-not (Test-Path -LiteralPath $pointerPath -PathType Leaf)) { throw '未找到当前预览指针。请先生成新的功能预览。' }
  $pointer = Get-Content -LiteralPath $pointerPath -Raw -Encoding UTF8 | ConvertFrom-Json
  $seed = [IO.Path]::GetFullPath([string]$pointer.previewDirectory)
  $prefix = $batch.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  if (-not $seed.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '当前预览指针不属于本功能批次，拒绝启动。' }
  $manifest = Get-Content -LiteralPath (Join-Path $seed '预览.json') -Raw -Encoding UTF8 | ConvertFrom-Json
  $layout = New-FeaturePreviewLayout -Root ([string]$manifest.root) -BatchId ([string]$manifest.batchId) -Checks @($manifest.checks)
  $launcher = New-FeaturePreviewLauncher -PreviewDirectory $layout.PreviewDirectory
  Set-FeaturePreviewCurrentPointer -Layout $layout -Launcher $launcher
  Update-FeaturePreviewHistoryNotes -BatchDirectory $layout.BatchDirectory
  & (Join-Path $PSScriptRoot 'Run-FeaturePreview.ps1') -PreviewDirectory $layout.PreviewDirectory -AutoClose:$AutoClose
} catch {
  Show-FeaturePreviewFailure -Message ("无法启动当前预览。`r`n$($_.Exception.Message)")
  exit 1
}
