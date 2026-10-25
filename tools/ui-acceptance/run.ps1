param([Parameter(Mandatory=$true)][string]$Executable, [ValidateSet('plan','log')][string]$Scenario = 'plan')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$source = (Resolve-Path -LiteralPath $Executable).Path
if ($source -notmatch ('^' + [regex]::Escape($root) + '[\\/]产物[\\/]验证[\\/]v\d+\.\d+\.\d+[\\/][^\\/]+-portable\.exe$')) { throw '仅允许工作区内已最终化验证包根目录的 portable EXE。' }
$runId = [guid]::NewGuid().ToString('N')
$runPath = Join-Path $root "build/ui-acceptance/$runId"
if (Test-Path -LiteralPath $runPath) { throw '隔离目录已经存在。' }
New-Item -ItemType Directory -Path $runPath | Out-Null
$copy = $source
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
@"
验证包：$source
SHA-256：$sourceHash
隔离目录：$runPath
"@ | Set-Content -Encoding utf8 (Join-Path $runPath 'source.txt')
New-Item -ItemType Directory -Path (Join-Path $runPath 'appdata'),(Join-Path $runPath 'localappdata'),(Join-Path $runPath 'temp') | Out-Null
$oldAppData = $env:APPDATA; $oldLocalAppData = $env:LOCALAPPDATA; $oldNoProxy = $env:NO_PROXY; $oldNoProxyLower = $env:no_proxy; $oldHealth = $env:RUNNER_HEALTH_CHECK; $oldTemp = $env:TEMP; $oldTmp = $env:TMP; $oldRunnerUserData = $env:RUNNER_USER_DATA_DIR
$env:APPDATA = Join-Path $runPath 'appdata'; $env:LOCALAPPDATA = Join-Path $runPath 'localappdata'
$env:TEMP = Join-Path $runPath 'temp'; $env:TMP = $env:TEMP
$env:RUNNER_USER_DATA_DIR = Join-Path $runPath 'UserData'
$env:NO_PROXY = '127.0.0.1,localhost'; $env:no_proxy = '127.0.0.1,localhost'; $env:RUNNER_HEALTH_CHECK = '0'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$scenarioRunner = Join-Path $PSScriptRoot 'run-cdp-scenario.cjs'
$scenarioPath = Join-Path $PSScriptRoot "$Scenario-scenario.js"
if ($Scenario -eq 'log') {
  $dataPath = Join-Path $runPath 'UserData'
  New-Item -ItemType Directory -Path $dataPath | Out-Null
  @{ schema_version=3; queues=@(@{id='fixture-script';name='受控 MAA';task_ids=@('fixture-maa');time_slots=@()}); tasks=@(@{id='fixture-maa';name='受控 MAA';executable='C:\fixture\MAA.exe';preset_id='maa';preset_evidence='受控夹具';success_log_rules=@(@{mode='keyword';pattern='成功'});failure_log_rules=@(@{mode='keyword';pattern='失败'});maa_adapter=@{installation_directory='C:\fixture'} }); plans=@(); fired_slots=@(); startup_fired_dates=@{} } | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 (Join-Path $dataPath 'config.json')
  $today = (Get-Date).Date
  $fixtureEvent = @{timestamp='2026-01-01T00:00:00+00:00';task_id='task';task_name='验收任务';source='script';sequence=1;phase='脚本日志';severity='error';event_type='script.log.translated';message='脚本记录了错误级信息；请查看脚本内部日志确认原文。';rule_id='script.level.error'}
  $fixtureItems = @(
    @{task_id='task';task_name='验收任务';state='failed';error_summary='受控失败';log_summary=@('受控摘要');script_translation_version='script-local-rules-v1';detail_log_id='f6e372af-0e2c-4fd7-9434-f229c3a1643f';detail_log_status='已采集';ui_log_id='69220a50-b013-4b4c-8109-850c9c62daca';ui_log_status='已采集界面日志';readable_report=@{generator_version='local-rules-v2';conclusion='任务未完成';completion_basis='受控失败';duration_seconds=$null;risk_count=1;risks=@('检测到 1 条错误级脚本日志');events=@($fixtureEvent)}}
    @{task_id='task-2';task_name='受控成功任务';state='succeeded';log_summary=@('已按受控规则完成');readable_report=@{generator_version='local-rules-v1';conclusion='任务已完成';completion_basis='成功规则命中';duration_seconds=12;risk_count=0;risks=@();events=@()}}
    @{task_id='task-3';task_name='受控人工确认';state='succeeded';log_summary=@('人工确认完成');readable_report=@{generator_version='local-rules-v1';conclusion='任务已完成';completion_basis='人工确认';duration_seconds=19;risk_count=0;risks=@();events=@()}}
    @{task_id='task-4';task_name='受控停止任务';state='stopped';error_summary='用户停止';log_summary=@('已停止');readable_report=@{generator_version='local-rules-v1';conclusion='任务已停止';completion_basis='用户停止';duration_seconds=4;risk_count=0;risks=@();events=@()}}
    @{task_id='task-5';task_name='受控风险任务';state='succeeded';log_summary=@('日志包含错误级记录');readable_report=@{generator_version='local-rules-v1';conclusion='任务已完成';completion_basis='成功规则命中';duration_seconds=25;risk_count=1;risks=@('检测到错误级脚本日志，但未构成终态失败');events=@()}}
    @{task_id='task-6';task_name='受控等待任务';state='stopped';error_summary='计划跳过';log_summary=@('受控跳过');readable_report=@{generator_version='local-rules-v1';conclusion='任务未执行';completion_basis='计划跳过';duration_seconds=0;risk_count=1;risks=@('没有执行脚本');events=@()}}
  )
  $runs = @(
    @{ id='today'; queue_id='fixture-1'; queue_name='今日验收记录'; trigger_source='manual'; state='completed'; started_at=$today.AddHours(12).ToString('s'); items=@(); scheduler_skips=@('验收计划跳过') },
    @{ id='old'; queue_id='fixture-2'; queue_name='旧验收记录'; trigger_source='manual'; state='completed'; started_at=$today.AddDays(-40).AddHours(12).ToString('s'); ended_at=$today.AddDays(-40).AddHours(13).ToString('s'); run_log_id='9718cc60-639e-48c9-8875-069541d28b52'; run_log_status='已采集'; readable_report=@{generator_version='local-rules-v1';conclusion='本轮结束，存在失败或未执行项';completion_basis='请查看任务级报告';duration_seconds=3600;risk_count=1;risks=@('检测到 1 条错误级脚本日志');events=@()}; items=$fixtureItems }
  )
  @{ schema_version=3; runs=$runs } | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 (Join-Path $dataPath 'history.json')
  $details = Join-Path $dataPath 'detail_logs'
  New-Item -ItemType Directory -Path $details | Out-Null
  '{"captured_at":"2026-01-01T00:00:00+00:00","text":"[2026-01-01 12:00:00.000][INF][Bootstrapper] <2> MaaAssistantArknights GUI started\n受控详细内容"}' | Set-Content -Encoding utf8NoBOM (Join-Path $details 'f6e372af-0e2c-4fd7-9434-f229c3a1643f.jsonl')
  '{"captured_at":"2026-01-01T00:00:00+00:00","text":"受控界面日志"}' | Set-Content -Encoding utf8NoBOM (Join-Path $details '69220a50-b013-4b4c-8109-850c9c62daca.jsonl')
  '{"captured_at":"2026-01-01T00:00:00+00:00","text":"受控本轮日志：总耗时 12 秒"}' | Set-Content -Encoding utf8NoBOM (Join-Path $details '9718cc60-639e-48c9-8875-069541d28b52.jsonl')
}
$process = $null; $success = $false
Push-Location $runPath
try {
  $process = Start-Process -FilePath $copy -ArgumentList "--remote-debugging-port=$port" -WorkingDirectory (Split-Path -Parent $source) -WindowStyle Hidden -PassThru
  $ready = $false
  $debugPort = $port
  $debugPortFile = Join-Path $env:RUNNER_USER_DATA_DIR 'electron/DevToolsActivePort'
  for ($i=0; $i -lt 60; $i++) {
    $json = & curl.exe --noproxy '*' --silent --max-time 1 "http://127.0.0.1:$debugPort/json/version" 2>$null
    if ($LASTEXITCODE -eq 0 -and ($json -join '') -match 'Electron/') { $ready = $true; break }
    if (Test-Path -LiteralPath $debugPortFile) {
      $declaredPort = (Get-Content -LiteralPath $debugPortFile -TotalCount 1 -ErrorAction SilentlyContinue).Trim()
      if ($declaredPort -match '^\d+$' -and [int]$declaredPort -gt 0 -and [int]$declaredPort -le 65535) { $debugPort = [int]$declaredPort }
    }
    Start-Sleep -Milliseconds 500
  }
  if (-not $ready) { throw '隔离 Electron 调试端口未就绪。' }
  @"
请求调试端口：$port
实际调试端口：$debugPort
端口来源：$(if ($debugPort -eq $port) { '启动参数' } else { $debugPortFile })
"@ | Set-Content -Encoding utf8 'debug-endpoint.txt'
  $rendererReady = $false
  $rendererExited = $false
  $lastTargets = ''
  for ($i=0; $i -lt 180; $i++) {
    $process.Refresh()
    if ($process.HasExited) { $rendererExited = $true; break }
    $targets = & curl.exe --noproxy '*' --silent --max-time 1 "http://127.0.0.1:$debugPort/json/list" 2>$null
    if ($LASTEXITCODE -eq 0) { $lastTargets = $targets -join '' }
    if ($LASTEXITCODE -eq 0 -and ($targets -join '') -match '"type"\s*:\s*"page"') { $rendererReady = $true; break }
    Start-Sleep -Milliseconds 250
  }
  if (-not $rendererReady) {
    $lastTargets | Set-Content -Encoding utf8 'debug-targets.json'
    if ($rendererExited) { throw "隔离 Electron 在创建渲染页面前退出，退出码：$($process.ExitCode)。" }
    throw '隔离 Electron 在 45 秒内没有创建渲染页面。'
  }
  $result = & node $scenarioRunner '--cdp' "http://127.0.0.1:$debugPort" '--scenario' $Scenario 2>&1 | Out-String
  $scenarioExitCode = $LASTEXITCODE
  $result | Out-File -Encoding utf8 'result.txt'
  if ($scenarioExitCode -ne 0) { throw "验收场景执行失败，退出码：$scenarioExitCode`n$result" }
  $finalScreenshot = Join-Path $runPath 'final.png'
  $screenshot = & node $scenarioRunner '--cdp' "http://127.0.0.1:$debugPort" "--screenshot=$finalScreenshot" 2>&1 | Out-String
  $screenshotExitCode = $LASTEXITCODE
  $screenshot | Out-File -Encoding utf8 'screenshot.txt'
  if ($screenshotExitCode -ne 0 -or -not (Test-Path -LiteralPath $finalScreenshot -PathType Leaf) -or (Get-Item -LiteralPath $finalScreenshot).Length -le 0) {
    throw "验收截图未成功生成，退出码：$screenshotExitCode`n$screenshot"
  }
  if ($result -notmatch '(?s)### Result\s+"PASS:') { throw "验收未通过，详情见 $runPath/result.txt`n$result" }
  $success = $true
  Write-Output $result
  Write-Output "Evidence=$runPath"
} finally {
  if ($process -and -not $process.HasExited) { if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } }
  Pop-Location
  $env:APPDATA=$oldAppData; $env:LOCALAPPDATA=$oldLocalAppData; $env:NO_PROXY=$oldNoProxy; $env:no_proxy=$oldNoProxyLower; $env:RUNNER_HEALTH_CHECK=$oldHealth; $env:TEMP=$oldTemp; $env:TMP=$oldTmp; $env:RUNNER_USER_DATA_DIR=$oldRunnerUserData
}
