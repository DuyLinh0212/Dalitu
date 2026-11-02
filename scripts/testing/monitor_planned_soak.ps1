param([Parameter(Mandatory)][int]$ApiProcessId)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$evidence = Join-Path $repo 'docs\testing\runs\2026-10-23-planned'
$parentRun = Get-Content (Join-Path $evidence 'performance.json') -Raw | ConvertFrom-Json
$taskDatabase = $parentRun.database
if ($taskDatabase -notmatch '^hutube_planned_[a-f0-9]{32}$') { throw 'Expected an isolated test database.' }
$samples = [System.Collections.Generic.List[object]]::new()
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(10)
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    $apiProcess = Get-Process -Id $ApiProcessId -ErrorAction SilentlyContinue
    if (-not $apiProcess) { break }
    $connections = & 'D:\PostgreSQL\18\bin\psql.exe' -h 127.0.0.1 -p 55441 -U hutube_test -d postgres -X -At -c "SELECT count(*) FROM pg_stat_activity WHERE datname='$taskDatabase';"
    $samples.Add([pscustomobject]@{
        utc = [DateTimeOffset]::UtcNow.ToString('o')
        workingSetBytes = $apiProcess.WorkingSet64
        privateBytes = $apiProcess.PrivateMemorySize64
        totalCpuSeconds = $apiProcess.TotalProcessorTime.TotalSeconds
        handles = $apiProcess.HandleCount
        threads = $apiProcess.Threads.Count
        databaseConnections = [int]$connections
    })
    Start-Sleep -Seconds 5
}
if ($samples.Count -lt 2) { throw 'Insufficient samples; the soak process was not running.' }
$first = $samples[0]
$last = $samples[$samples.Count - 1]
$growth = ($last.workingSetBytes - $first.workingSetBytes) / $first.workingSetBytes
[pscustomobject]@{
    id = 'B-PERF.07'
    parentRunId = $parentRun.runId
    status = $(if ($growth -gt 0.10) { 'Fail' } else { 'Pass' })
    actual = 'Kestrel working set/private bytes/CPU/handles/threads and PostgreSQL connections sampled every five seconds after warmup. Load-stage duration is in performance.json.'
    observedSeconds = ([DateTimeOffset]::Parse($last.utc) - [DateTimeOffset]::Parse($first.utc)).TotalSeconds
    workingSetGrowthFraction = $growth
    samples = $samples
} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence 'soak-resources.json') -Encoding utf8
