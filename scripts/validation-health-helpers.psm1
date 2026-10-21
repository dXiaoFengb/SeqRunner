function Get-ValidationProcessTree {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int[]]$RootProcessIds)

    try {
        $processes = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)
        $childrenByParent = @{}
        foreach ($process in $processes) {
            $parentId = [int]$process.ParentProcessId
            if (-not $childrenByParent.ContainsKey($parentId)) { $childrenByParent[$parentId] = @() }
            $childrenByParent[$parentId] += [int]$process.ProcessId
        }
        $known = [System.Collections.Generic.HashSet[int]]::new()
        $pending = [System.Collections.Generic.Queue[int]]::new()
        foreach ($rootId in $RootProcessIds) {
            if ($known.Add($rootId)) { $pending.Enqueue($rootId) }
        }
        while ($pending.Count -gt 0) {
            $parentId = $pending.Dequeue()
            foreach ($childId in @($childrenByParent[$parentId])) {
                if ($known.Add($childId)) { $pending.Enqueue($childId) }
            }
        }
        return [pscustomobject]@{ status = 'checked'; processIds = @($known | Sort-Object); error = $null }
    } catch {
        return [pscustomobject]@{ status = 'unknown'; processIds = @(); error = $_.Exception.Message }
    }
}

function New-ValidationHealthResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('passed', 'failed')][string]$Status,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$ExecutableSha256,
        [AllowNull()][Nullable[int]]$ExitCode,
        [Parameter(Mandatory)][long]$DurationMs,
        [Parameter(Mandatory)][string]$Evidence,
        [Parameter(Mandatory)][ValidateSet('checked', 'unknown')][string]$ProcessTreeStatus,
        [string[]]$ResidualProcessIds = @(),
        [AllowNull()][string]$ProcessTreeError,
        [AllowNull()][string]$Error
    )
    $result = [ordered]@{
        status = $Status; version = $Version; executable = $Executable; executableSha256 = $ExecutableSha256
        exitCode = $ExitCode; durationMs = $DurationMs; evidence = $Evidence
        processTreeStatus = $ProcessTreeStatus; residualProcessIds = @($ResidualProcessIds)
        processTreeError = $ProcessTreeError
    }
    if ($Error) { $result.error = $Error }
    return $result
}

Export-ModuleMember -Function Get-ValidationProcessTree, New-ValidationHealthResult
