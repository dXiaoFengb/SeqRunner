Import-Module (Join-Path $PSScriptRoot 'acceptance-process-helpers.psm1') -Force
$ErrorActionPreference = 'Stop'

$self = Get-AcceptanceProcessTree -RootProcessId 100 -ProcessSnapshot @(
  [pscustomobject]@{ ProcessId = 100; ParentProcessId = 1 },
  [pscustomobject]@{ ProcessId = 101; ParentProcessId = 100 },
  [pscustomobject]@{ ProcessId = 102; ParentProcessId = 101 },
  [pscustomobject]@{ ProcessId = 200; ParentProcessId = 1 }
)
if ($self.status -ne 'checked' -or @($self.processIds | Sort-Object) -join ',' -ne '100,101,102') { throw '进程快照未被解析为精确的受控进程树。' }
if ((Wait-AcceptanceProcessExit -ProcessIds @() -TimeoutMilliseconds 1).Count -ne 0) { throw '空进程集合不应留下残留 PID。' }

$pidFile = Join-Path $env:TEMP ('runner-acceptance-child-' + [guid]::NewGuid().ToString('N') + '.txt')
$escapedPidFile = $pidFile.Replace("'", "''")
$childCommand = "`$PID | Set-Content -LiteralPath '$escapedPidFile' -Encoding ascii; Start-Sleep -Seconds 30"
$encodedChildCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCommand))
$escapedPowerShell = (Join-Path $PSHOME 'pwsh.exe').Replace("'", "''")
$rootCommand = "Start-Process -FilePath '$escapedPowerShell' -ArgumentList @('-NoProfile','-EncodedCommand','$encodedChildCommand') -WindowStyle Hidden; exit"
$root = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList @('-NoProfile', '-Command', $rootCommand) -WindowStyle Hidden -PassThru
try {
    for ($attempt = 0; $attempt -lt 30 -and -not (Test-Path -LiteralPath $pidFile); $attempt++) { Start-Sleep -Milliseconds 100 }
    if (-not (Test-Path -LiteralPath $pidFile)) { throw '受控延后子进程未写入 PID。' }
    $delayedChildId = [int](Get-Content -LiteralPath $pidFile -Raw)
    Wait-Process -Id $root.Id -Timeout 5 -ErrorAction SilentlyContinue
    $residual = @(Stop-AcceptanceProcessTree -RootProcessId $root.Id -ProcessIds @($root.Id, $delayedChildId))
    if ($residual.Count -ne 0) { throw ('主进程已退出时的受控延后子进程仍残留：' + ($residual -join ', ')) }
} finally {
    if (Get-Process -Id $root.Id -ErrorAction SilentlyContinue) { Stop-Process -Id $root.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $pidFile) { Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue }
}

$evidence = New-AcceptanceShutdownEvidence -CloseRequestStatus requested -ProcessTreeStatus checked -TrackedProcessIds @($PID) -ResidualBeforeFallback @() -FallbackUsed $false -ResidualProcessIds @() -Error $null
$json = $evidence | ConvertTo-Json -Depth 4
if ($json -notmatch '"fallbackUsed"\s*:\s*false' -or $json -notmatch '"residualProcessIds"\s*:\s*\[\s*\]') { throw '关闭证据必须明确记录无残留与未兜底状态。' }
Write-Output 'acceptance process helper tests: PASS'
