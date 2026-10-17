param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$source = (Resolve-Path -LiteralPath $Executable).Path
if ($source -notmatch ('^' + [regex]::Escape($root) + '[\\/]portable-validation-v[0-9]+[\\/]electron[\\/][^\\/]+-portable\.exe$')) { throw '仅允许工作区内独立验证包目录的 portable EXE。' }
$runId = [guid]::NewGuid().ToString('N')
$runPath = Join-Path $root "build/ui-acceptance/$runId"
if (Test-Path -LiteralPath $runPath) { throw '隔离目录已经存在。' }
New-Item -ItemType Directory -Path $runPath | Out-Null
$copy = Join-Path $runPath 'runner.exe'
Copy-Item -LiteralPath $source -Destination $copy
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$copyHash = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash
if ($sourceHash -ne $copyHash) { throw '验证包副本哈希不一致。' }
@"
源文件：$source
SHA-256：$sourceHash
隔离目录：$runPath
"@ | Set-Content -Encoding utf8 (Join-Path $runPath 'source.txt')
New-Item -ItemType Directory -Path (Join-Path $runPath 'appdata'),(Join-Path $runPath 'localappdata'),(Join-Path $runPath 'temp') | Out-Null
$oldAppData = $env:APPDATA; $oldLocalAppData = $env:LOCALAPPDATA; $oldNoProxy = $env:NO_PROXY; $oldNoProxyLower = $env:no_proxy; $oldHealth = $env:RUNNER_HEALTH_CHECK; $oldTemp = $env:TEMP; $oldTmp = $env:TMP
$env:APPDATA = Join-Path $runPath 'appdata'; $env:LOCALAPPDATA = Join-Path $runPath 'localappdata'
$env:TEMP = Join-Path $runPath 'temp'; $env:TMP = $env:TEMP
$env:NO_PROXY = '127.0.0.1,localhost'; $env:no_proxy = '127.0.0.1,localhost'; $env:RUNNER_HEALTH_CHECK = '0'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$cli = Join-Path $PSScriptRoot 'node_modules/@playwright/cli/playwright-cli.js'
$scenario = Join-Path $PSScriptRoot 'plan-scenario.js'
$session = "runner-$runId"
$process = $null; $attached = $false; $success = $false
Push-Location $runPath
try {
  $process = Start-Process -FilePath $copy -ArgumentList "--remote-debugging-port=$port" -WindowStyle Hidden -PassThru
  $ready = $false
  for ($i=0; $i -lt 60; $i++) {
    $json = & curl.exe --noproxy '*' --silent --max-time 1 "http://127.0.0.1:$port/json/version" 2>$null
    if ($LASTEXITCODE -eq 0 -and ($json -join '') -match 'Electron/') { $ready = $true; break }
    Start-Sleep -Milliseconds 500
  }
  if (-not $ready) { throw '隔离 Electron 调试端口未就绪。' }
  $attach = & node $cli "-s=$session" attach "--cdp=http://127.0.0.1:$port" 2>&1 | Out-String
  if ($attach -notmatch 'attached to') { throw "自动化连接失败：$attach" }
  $attached = $true
  & node $cli "-s=$session" snapshot | Out-File -Encoding utf8 'initial-snapshot.txt'
  $result = & node $cli "-s=$session" run-code "--filename=$scenario" 2>&1 | Out-String
  $result | Out-File -Encoding utf8 'result.txt'
  & node $cli "-s=$session" screenshot --filename=final.png | Out-File -Encoding utf8 'screenshot.txt'
  if ($result -notmatch '(?s)### Result\s+"PASS:') { throw "验收未通过，详情见 $runPath/result.txt`n$result" }
  $success = $true
  Write-Output $result
  Write-Output "Evidence=$runPath"
} finally {
  if ($attached) { & node $cli "-s=$session" run-code 'async page => { await page.close() }' 2>&1 | Out-Null; & node $cli "-s=$session" detach 2>&1 | Out-Null }
  if ($process -and -not $process.HasExited) { if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } }
  Pop-Location
  $env:APPDATA=$oldAppData; $env:LOCALAPPDATA=$oldLocalAppData; $env:NO_PROXY=$oldNoProxy; $env:no_proxy=$oldNoProxyLower; $env:RUNNER_HEALTH_CHECK=$oldHealth; $env:TEMP=$oldTemp; $env:TMP=$oldTmp
}
