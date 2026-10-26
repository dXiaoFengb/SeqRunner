function Get-NativeProcessSnapshot {
    if ($null -eq ('AcceptanceProcessNative' -as [type])) {
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AcceptanceProcessNative {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PROCESSENTRY32 {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);
}
'@
    }
    $snapshot = [AcceptanceProcessNative]::CreateToolhelp32Snapshot(0x00000002, 0)
    if ($snapshot -eq [IntPtr]::new(-1)) { throw '无法创建 Windows 进程快照。' }
    try {
        $entry = [AcceptanceProcessNative+PROCESSENTRY32]::new()
        $entry.dwSize = [Runtime.InteropServices.Marshal]::SizeOf($entry)
        if (-not [AcceptanceProcessNative]::Process32First($snapshot, [ref]$entry)) { throw '无法读取 Windows 进程快照。' }
        do {
            [pscustomobject]@{ ProcessId = [int]$entry.th32ProcessID; ParentProcessId = [int]$entry.th32ParentProcessID }
            $entry.dwSize = [Runtime.InteropServices.Marshal]::SizeOf($entry)
        } while ([AcceptanceProcessNative]::Process32Next($snapshot, [ref]$entry))
    } finally {
        [void][AcceptanceProcessNative]::CloseHandle($snapshot)
    }
}

function Get-AcceptanceProcessTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$RootProcessId,
        [AllowNull()][object[]]$ProcessSnapshot = $null
    )

    try {
        $children = @{}
        $processes = if ($null -eq $ProcessSnapshot) {
            @(Get-NativeProcessSnapshot)
        } else {
            @($ProcessSnapshot)
        }
        foreach ($process in $processes) {
            $parent = [int]$process.ParentProcessId
            if (-not $children.ContainsKey($parent)) { $children[$parent] = @() }
            $children[$parent] += [int]$process.ProcessId
        }
        $known = [System.Collections.Generic.HashSet[int]]::new()
        $pending = [System.Collections.Generic.Queue[int]]::new()
        $known.Add($RootProcessId) | Out-Null; $pending.Enqueue($RootProcessId)
        while ($pending.Count -gt 0) {
            $parent = $pending.Dequeue()
            if (-not $children.ContainsKey($parent)) { continue }
            foreach ($child in $children[$parent]) { if ($known.Add($child)) { $pending.Enqueue($child) } }
        }
        return [pscustomobject]@{ status = 'checked'; processIds = @($known | Sort-Object); error = $null }
    } catch {
        return [pscustomobject]@{ status = 'unknown'; processIds = @(); error = $_.Exception.Message }
    }
}

function Wait-AcceptanceProcessExit {
    [CmdletBinding()]
    param([AllowEmptyCollection()][int[]]$ProcessIds = @(), [int]$TimeoutMilliseconds = 10000)

    $deadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $remaining = @($ProcessIds | Where-Object { $_ -gt 0 } | ForEach-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue } | Select-Object -ExpandProperty Id -Unique)
        if (-not $remaining.Count) { return @() }
        Start-Sleep -Milliseconds 100
    } while ($deadline.ElapsedMilliseconds -lt $TimeoutMilliseconds)
    return @($remaining | Sort-Object -Unique)
}

function Stop-AcceptanceProcessTree {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$RootProcessId, [Parameter(Mandatory)][int[]]$ProcessIds)

    $recordedProcessIds = @($ProcessIds | Where-Object { $_ -gt 0 } | Sort-Object -Unique)
    if ($recordedProcessIds -notcontains $RootProcessId) { throw '隔离验收主进程不在已记录的进程树中；拒绝执行兜底终止。' }
    $rootAlive = Get-Process -Id $RootProcessId -ErrorAction SilentlyContinue
    if ($rootAlive) {
        $currentTree = Get-AcceptanceProcessTree -RootProcessId $RootProcessId
        if ($currentTree.status -ne 'checked') { throw '无法重新核验隔离验收进程树；拒绝执行兜底终止。' }
        $unexpected = @($currentTree.processIds | Where-Object { $_ -notin $recordedProcessIds })
        if ($unexpected.Count -gt 0) { throw ('隔离验收进程树出现未记录 PID；拒绝执行兜底终止：' + ($unexpected -join ', ')) }
        & taskkill.exe /PID $RootProcessId /T /F | Out-Null
    }
    foreach ($processId in $recordedProcessIds | Where-Object { $_ -ne $RootProcessId }) {
        if (Get-Process -Id $processId -ErrorAction SilentlyContinue) {
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        }
    }
    return @(Wait-AcceptanceProcessExit -ProcessIds $recordedProcessIds -TimeoutMilliseconds 5000)
}

function New-AcceptanceShutdownEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('requested', 'not_requested', 'failed')][string]$CloseRequestStatus,
        [Parameter(Mandatory)][ValidateSet('checked', 'unknown')][string]$ProcessTreeStatus,
        [Parameter(Mandatory)][AllowEmptyCollection()][int[]]$TrackedProcessIds,
        [Parameter(Mandatory)][AllowEmptyCollection()][int[]]$ResidualBeforeFallback,
        [Parameter(Mandatory)][bool]$FallbackUsed,
        [Parameter(Mandatory)][AllowEmptyCollection()][int[]]$ResidualProcessIds,
        [AllowNull()][string]$Error
    )

    $result = [ordered]@{
        closeRequestStatus = $CloseRequestStatus
        processTreeStatus = $ProcessTreeStatus
        trackedProcessIds = @($TrackedProcessIds | Where-Object { $_ -gt 0 } | Sort-Object -Unique)
        residualBeforeFallback = @($ResidualBeforeFallback | Where-Object { $_ -gt 0 } | Sort-Object -Unique)
        fallbackUsed = $FallbackUsed
        residualProcessIds = @($ResidualProcessIds | Where-Object { $_ -gt 0 } | Sort-Object -Unique)
    }
    if ($Error) { $result.error = $Error }
    return [pscustomobject]$result
}

Export-ModuleMember -Function Get-AcceptanceProcessTree, Wait-AcceptanceProcessExit, Stop-AcceptanceProcessTree, New-AcceptanceShutdownEvidence
