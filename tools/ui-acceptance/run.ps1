param([Parameter(Mandatory=$true)][string]$Executable, [ValidateSet('plan','log')][string]$Scenario = 'plan')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$source = (Resolve-Path -LiteralPath $Executable).Path
if ($source -notmatch ('^' + [regex]::Escape($root) + '[\\/]产物[\\/]验证[\\/]v\d+\.\d+\.\d+[\\/]electron[\\/][^\\/]+-portable\.exe$')) { throw '仅允许工作区内已最终化验证包目录的 portable EXE。' }
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
$scenarioPath = Join-Path $PSScriptRoot "$Scenario-scenario.js"
if ($Scenario -eq 'log') {
  $dataPath = Join-Path $runPath 'data'
  New-Item -ItemType Directory -Path $dataPath | Out-Null
  @{ schema_version=3; queues=@(@{id='fixture-script';name='受控 MAA';task_ids=@('fixture-maa');time_slots=@()}); tasks=@(@{id='fixture-maa';name='受控 MAA';executable='C:\fixture\MAA.exe';preset_id='maa';preset_evidence='受控夹具';success_log_rules=@(@{mode='keyword';pattern='成功'});failure_log_rules=@(@{mode='keyword';pattern='失败'});maa_adapter=@{installation_directory='C:\fixture'} }); plans=@(); fired_slots=@(); startup_fired_dates=@{} } | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 (Join-Path $dataPath 'config.json')
  $today = (Get-Date).Date
  $runs = @(
    @{ id='today'; queue_id='fixture-1'; queue_name='今日验收记录'; trigger_source='manual'; state='completed'; started_at=$today.AddHours(12).ToString('s'); items=@(); scheduler_skips=@('验收计划跳过') },
    @{ id='old'; queue_id='fixture-2'; queue_name='旧验收记录'; trigger_source='manual'; state='completed'; started_at=$today.AddDays(-40).AddHours(12).ToString('s'); ended_at=$today.AddDays(-40).AddHours(13).ToString('s'); run_log_id='9718cc60-639e-48c9-8875-069541d28b52'; run_log_status='已采集'; items=@(@{task_id='task';task_name='验收任务';state='failed';error_summary='受控失败';log_summary=@('受控摘要');detail_log_id='f6e372af-0e2c-4fd7-9434-f229c3a1643f';detail_log_status='已采集';ui_log_id='69220a50-b013-4b4c-8109-850c9c62daca';ui_log_status='已采集界面日志'}) }
  )
  @{ schema_version=3; runs=$runs } | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 (Join-Path $dataPath 'history.json')
  $details = Join-Path $dataPath 'detail_logs'
  New-Item -ItemType Directory -Path $details | Out-Null
  '{"captured_at":"2026-01-01T00:00:00+00:00","text":"[2026-01-01 12:00:00.000][INF][Bootstrapper] <2> MaaAssistantArknights GUI started\n受控详细内容"}' | Set-Content -Encoding utf8NoBOM (Join-Path $details 'f6e372af-0e2c-4fd7-9434-f229c3a1643f.jsonl')
  '{"captured_at":"2026-01-01T00:00:00+00:00","text":"受控界面日志"}' | Set-Content -Encoding utf8NoBOM (Join-Path $details '69220a50-b013-4b4c-8109-850c9c62daca.jsonl')
  '{"captured_at":"2026-01-01T00:00:00+00:00","text":"受控本轮日志：总耗时 12 秒"}' | Set-Content -Encoding utf8NoBOM (Join-Path $details '9718cc60-639e-48c9-8875-069541d28b52.jsonl')
}
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
  $result = & node $cli "-s=$session" run-code "--filename=$scenarioPath" 2>&1 | Out-String
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
