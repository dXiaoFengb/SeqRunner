$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'validation-health-helpers.psm1') -Force

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$versionText = Get-Content -Raw -LiteralPath (Join-Path $root 'VERSION.md')
$match = [regex]::Match($versionText, '(?m)^\s*-\s*开发提交版本：\s*v(\d+\.\d+\.\d+)\s*$')
if (-not $match.Success) { throw 'VERSION.md 中未找到格式正确的开发提交版本。' }
$version = $match.Groups[1].Value
$workPath = Join-Path $root "产物/工作/v$version"
$verifiedPath = Join-Path $root "产物/验证/v$version"
if ($env:RUNNER_VALIDATION_SUFFIX) { throw '验证包已改用开发版本目录，不再支持 RUNNER_VALIDATION_SUFFIX。' }
if (-not (Test-Path -LiteralPath $workPath -PathType Container)) { throw '验证工作目录不存在。' }
if (Test-Path -LiteralPath $verifiedPath) { throw '验证包目录已存在，不允许覆盖。' }

$executables = @(Get-ChildItem -LiteralPath (Join-Path $workPath 'electron') -File -Filter '*-portable.exe')
if ($executables.Count -ne 1) { throw '验证工作目录中必须恰好有一个 portable EXE。' }
$executable = $executables[0].FullName
$beforeHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
$evidence = Join-Path $root ("build/health-validation-v{0}-{1}" -f $version, [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence | Out-Null
New-Item -ItemType Directory -Path (Join-Path $evidence 'appdata'),(Join-Path $evidence 'localappdata'),(Join-Path $evidence 'temp') | Out-Null

$oldAppData = $env:APPDATA; $oldLocalAppData = $env:LOCALAPPDATA; $oldTemp = $env:TEMP; $oldTmp = $env:TMP
$oldNoProxy = $env:NO_PROXY; $oldNoProxyLower = $env:no_proxy; $oldHealth = $env:RUNNER_HEALTH_CHECK
$process = $null; $stopwatch = [Diagnostics.Stopwatch]::StartNew()
$trackedProcessIds = [System.Collections.Generic.HashSet[int]]::new()
$processTreeStatus = 'checked'; $processTreeError = $null; $exitCode = $null; $failure = $null

function Update-TrackedProcessTree {
    param([int]$RootProcessId)
    $snapshot = Get-ValidationProcessTree -RootProcessIds @($RootProcessId)
    if ($snapshot.status -ne 'checked') {
        $script:processTreeStatus = 'unknown'
        $script:processTreeError = $snapshot.error
        return
    }
    foreach ($processId in $snapshot.processIds) { [void]$script:trackedProcessIds.Add([int]$processId) }
}

try {
    $env:APPDATA = Join-Path $evidence 'appdata'
    $env:LOCALAPPDATA = Join-Path $evidence 'localappdata'
    $env:TEMP = Join-Path $evidence 'temp'; $env:TMP = $env:TEMP
    $env:NO_PROXY = '127.0.0.1,localhost'; $env:no_proxy = $env:NO_PROXY
    $env:RUNNER_HEALTH_CHECK = '1'
    $process = Start-Process -FilePath $executable -WorkingDirectory $evidence -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Update-TrackedProcessTree -RootProcessId $process.Id
        Start-Sleep -Milliseconds 100
        $process.Refresh()
    }
    if (-not $process.HasExited) {
        & taskkill.exe /PID $process.Id /T /F | Out-Null
        $process.WaitForExit(5000) | Out-Null
        throw '隐藏健康检查在 60 秒内未退出。'
    }
    $exitCode = [int]$process.ExitCode
    Update-TrackedProcessTree -RootProcessId $process.Id
    if ($processTreeStatus -ne 'checked') { throw "无法确认健康检查进程树：$processTreeError" }
    $residualProcessIds = @($trackedProcessIds | Where-Object {
        $candidate = Get-Process -Id $_ -ErrorAction SilentlyContinue
        $null -ne $candidate
    } | Sort-Object)
    if ($residualProcessIds.Count -gt 0) { throw ('隐藏健康检查留下进程：' + ($residualProcessIds -join ', ')) }
    if ($exitCode -ne 0) { throw "隐藏健康检查退出码：$exitCode" }
    $afterHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    if ($afterHash -ne $beforeHash) { throw '隐藏健康检查改变了验证包 EXE。' }
} catch {
    $failure = $_.Exception
} finally {
    $stopwatch.Stop()
    if ($process -and $null -eq $exitCode -and $process.HasExited) { $exitCode = [int]$process.ExitCode }
    if ($process -and $processTreeStatus -eq 'checked') { Update-TrackedProcessTree -RootProcessId $process.Id }
    $residualProcessIds = @($trackedProcessIds | Where-Object {
        $candidate = Get-Process -Id $_ -ErrorAction SilentlyContinue
        $null -ne $candidate
    } | Sort-Object)
    if ($failure -or $processTreeStatus -ne 'checked' -or $residualProcessIds.Count -gt 0) {
        if (-not $failure -and $processTreeStatus -ne 'checked') { $failure = [InvalidOperationException]::new("无法确认健康检查进程树：$processTreeError") }
        if (-not $failure -and $residualProcessIds.Count -gt 0) { $failure = [InvalidOperationException]::new('隐藏健康检查留下进程：' + ($residualProcessIds -join ', ')) }
        $result = New-ValidationHealthResult -Status failed -Version $version -Executable $executable -ExecutableSha256 $beforeHash -ExitCode $exitCode -DurationMs $stopwatch.ElapsedMilliseconds -Evidence $evidence -ProcessTreeStatus $processTreeStatus -ResidualProcessIds $residualProcessIds -ProcessTreeError $processTreeError -Error $failure.Message
    } else {
        $result = New-ValidationHealthResult -Status passed -Version $version -Executable $executable -ExecutableSha256 $beforeHash -ExitCode $exitCode -DurationMs $stopwatch.ElapsedMilliseconds -Evidence $evidence -ProcessTreeStatus $processTreeStatus -ResidualProcessIds $residualProcessIds -ProcessTreeError $processTreeError
        $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $workPath '.validation-health.json') -Encoding utf8NoBOM
    }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8NoBOM
    $env:APPDATA=$oldAppData; $env:LOCALAPPDATA=$oldLocalAppData; $env:TEMP=$oldTemp; $env:TMP=$oldTmp
    $env:NO_PROXY=$oldNoProxy; $env:no_proxy=$oldNoProxyLower; $env:RUNNER_HEALTH_CHECK=$oldHealth
}
if ($failure) { throw $failure }
