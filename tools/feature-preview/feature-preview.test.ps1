$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot 'feature-preview.psm1'
Import-Module $module -Force
$root = Join-Path ([IO.Path]::GetTempPath()) ("runner-preview-test-" + [guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Path $root | Out-Null
  $layout = New-FeaturePreviewLayout -Root $root -BatchId '日志/预览' -Checks @('打开日志页', '确认受控状态')
  if (-not $layout.PreviewDirectory.StartsWith((Join-Path $root '产物/预览'))) { throw '预览目录没有创建在产物/预览内。' }
  foreach ($name in 'preview.json', '本次功能检查说明.md', 'UserData', 'appdata', 'localappdata', 'temp') {
    if (-not (Test-Path -LiteralPath (Join-Path $layout.PreviewDirectory $name))) { throw "缺少预览文件或目录：$name" }
  }
  $launcher = New-FeaturePreviewLauncher -Root $root -PreviewDirectory $layout.PreviewDirectory
  if (-not (Test-Path -LiteralPath $launcher)) { throw '未生成用户启动器。' }
  $launcherLines = Get-Content -LiteralPath $launcher -Encoding ascii
  if ($launcherLines.Count -ne 4 -or $launcherLines[2] -notmatch 'pwsh\.exe' -or $launcherLines[2] -notmatch 'Run-FeaturePreview\.ps1' -or $launcherLines[2] -notmatch 'PreviewDirectory "%~dp0\."') { throw '用户启动器没有生成为完整的单行隐藏启动命令。' }
  if (Test-Path -LiteralPath (Join-Path $root '产物/工作')) { throw '预览不应创建产物/工作。' }
  if (Test-Path -LiteralPath (Join-Path $root '产物/验证')) { throw '预览不应创建产物/验证。' }
  Write-Output 'Feature preview layout test passed.'
} finally {
  if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
