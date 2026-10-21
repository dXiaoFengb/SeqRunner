Import-Module (Join-Path $PSScriptRoot 'validation-health-helpers.psm1') -Force

$passed = New-ValidationHealthResult -Status passed -Version '1.2.3' -Executable 'fixture.exe' -ExecutableSha256 'ABC' -ExitCode 0 -DurationMs 12 -Evidence 'build/test' -ProcessTreeStatus checked
if ($passed.exitCode -ne 0 -or $passed.processTreeStatus -ne 'checked' -or $passed.residualProcessIds.Count -ne 0) { throw '成功结果缺少健康检查字段。' }
$failed = New-ValidationHealthResult -Status failed -Version '1.2.3' -Executable 'fixture.exe' -ExecutableSha256 'ABC' -ExitCode $null -DurationMs 12 -Evidence 'build/test' -ProcessTreeStatus unknown -ProcessTreeError 'denied' -Error 'timeout'
if ($failed.status -ne 'failed' -or $null -ne $failed.exitCode -or $failed.processTreeStatus -ne 'unknown' -or $failed.error -ne 'timeout') { throw '失败结果没有保留完整状态。' }
$tree = Get-ValidationProcessTree -RootProcessIds @($PID)
if ($tree.status -notin @('checked', 'unknown')) { throw '进程树检查状态无效。' }
if ($tree.status -eq 'unknown' -and -not $tree.error) { throw '未知进程树状态必须记录原因。' }
Write-Output 'validation health helper tests: PASS'
