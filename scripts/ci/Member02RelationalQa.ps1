function Assert-QaRelationalPartitions($Required, $Partitions) {
    $assigned = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($partition in $Partitions.Values) {
        if (!$partition.Count) { throw 'A relational partition is empty.' }
        foreach ($method in $partition.Keys) {
            if (!$Required.ContainsKey($method) -or !$assigned.Add($method) -or
                (Compare-Object @($Required[$method] | Sort-Object) @($partition[$method] | Sort-Object) -CaseSensitive)) {
                throw 'Relational partition identity or case multiplicity differs from the mandatory inventory.'
            }
        }
    }
    if ($assigned.Count -ne $Required.Count) { throw 'A mandatory relational method is unassigned.' }
}

function Get-QaRelationalPartitions($Required) {
    $partitions = [ordered]@{
        'non-lifecycle' = [Collections.Generic.Dictionary[string, string[]]]::new([StringComparer]::Ordinal)
        lifecycle = [Collections.Generic.Dictionary[string, string[]]]::new([StringComparer]::Ordinal)
    }
    foreach ($method in $Required.Keys) {
        $group = if ($method.StartsWith('Andril.IntegrationTests.Lifecycle.', [StringComparison]::Ordinal)) {
            'lifecycle'
        } elseif ($method.StartsWith('Andril.IntegrationTests.Database.', [StringComparison]::Ordinal)) {
            'non-lifecycle'
        } else { throw 'A mandatory method has no approved relational partition.' }
        $partitions[$group].Add($method, $Required[$method])
    }
    Assert-QaRelationalPartitions $Required $partitions
    # Additional authorized regressions extend the same manifest and preserve both baseline groups.
    if ($partitions['non-lifecycle'].Count -lt 37 -or $partitions.lifecycle.Count -lt 51) {
        throw 'A relational partition is smaller than its approved baseline.'
    }
    return $partitions
}

function Write-QaPartitionReport([string]$Output, $State) {
    $target = Join-Path $Output "relational-$($State.partition).json"
    $temporary = "$target.tmp"
    $State | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding utf8
    [IO.File]::Move($temporary, $target, $true)
}

function New-QaPartitionReport([string]$Partition, $Expected) {
    [ordered]@{
        partition = $Partition
        outcome = 'Not executed'
        reason = 'Not started'
        startedUtc = $null
        endedUtc = $null
        elapsedSeconds = $null
        exitCode = $null
        counts = $null
        missingCount = $null
        unexpectedCount = $null
        reportAvailable = $false
        expected = @($Expected.Keys | Sort-Object -CaseSensitive | ForEach-Object {
            [ordered]@{ method = $_; count = $Expected[$_].Count }
        })
        results = @()
        missing = @()
    }
}

function Write-QaRelationalEvent([string]$Output, [string]$Partition, [string]$Event, $Data = $null) {
    $record = [ordered]@{ utc = [DateTimeOffset]::UtcNow.ToString('o'); partition = $Partition; event = $Event; data = $Data }
    $line = $record | ConvertTo-Json -Depth 6 -Compress
    Add-Content -LiteralPath (Join-Path $Output 'relational-progress.jsonl') -Value $line -Encoding utf8
    Write-Host $line
}

function Start-QaDiagnosticProcess([string]$Program, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Program)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($arg in $Arguments) { $start.ArgumentList.Add($arg) }
    [Diagnostics.Process]::Start($start)
}

function Stop-QaDiagnosticProcess([Diagnostics.Process]$Process) {
    if ($Process.HasExited) { return $true }
    try { $Process.Kill($true) }
    catch [InvalidOperationException] { if (!$Process.HasExited) { return $false } }
    catch { return $false }
    return $Process.WaitForExit(8000)
}

function Invoke-QaBoundedProcess([string]$Program, [string[]]$Arguments,
    [Diagnostics.Stopwatch]$Budget, [TimeSpan]$Deadline, [scriptblock]$OnLine, [scriptblock]$OnTick) {
    $process = Start-QaDiagnosticProcess $Program $Arguments
    $termination = 'Not needed'
    $timedOut = $false
    $exitCode = $null
    try {
        $stdout = $process.StandardOutput.ReadLineAsync()
        $stderr = $process.StandardError.ReadLineAsync()
        while ($null -ne $stdout -or $null -ne $stderr -or !$process.HasExited) {
            if ($Budget.Elapsed -ge $Deadline) {
                $timedOut = $true
                break
            }
            if ($null -ne $stdout -and $stdout.IsCompleted) {
                $line = $stdout.GetAwaiter().GetResult()
                $stdout = if ($null -ne $line) { $process.StandardOutput.ReadLineAsync() } else { $null }
                if ($null -ne $line -and $OnLine) { & $OnLine $line }
            }
            if ($null -ne $stderr -and $stderr.IsCompleted) {
                $line = $stderr.GetAwaiter().GetResult()
                $stderr = if ($null -ne $line) { $process.StandardError.ReadLineAsync() } else { $null }
            }
            if ($OnTick -and !$process.HasExited) { & $OnTick }
            if (!($stdout -and $stdout.IsCompleted) -and !($stderr -and $stderr.IsCompleted)) {
                Start-Sleep -Milliseconds 30
            }
        }
    } finally {
        if (!$process.HasExited) {
            $termination = if (Stop-QaDiagnosticProcess $process) { 'Process tree stopped' } else { 'Process tree stop unconfirmed' }
        } elseif ($timedOut -and ($null -ne $stdout -or $null -ne $stderr)) {
            $termination = 'Process exited with output streams still open'
        }
        if ($process.HasExited) { $exitCode = $process.ExitCode }
        $process.Dispose()
    }
    [pscustomobject]@{ timedOut = $timedOut; exitCode = $exitCode; termination = $termination }
}

function Get-QaRelationalProgress([string]$Line, $Expected) {
    # VSTest detailed console logger emits completion lines. Names and theory arguments are never echoed.
    if ($Line -cmatch '^\s*(Passed|Failed|Skipped) ([\w.]+)(?:\(.*\))?(?: \[[^\r\n\]]+\])?\s*$' -and
        $Expected.ContainsKey($Matches[2])) {
        [ordered]@{ method = $Matches[2]; outcome = $Matches[1] }
    }
}

function Get-QaRelationalSequence([string]$Directory, $Expected) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter 'Sequence_*.xml' -File -Recurse) {
        try {
            # VSTest can retain both the original sequence and a byte-identical attachment copy.
            if ($file.Length -gt 32MB -or !$seen.Add((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)) { continue }
            $xml = Import-QaXml $file.FullName 'VSTest sequence'
            foreach ($entry in $xml.SelectNodes('/TestSequence/Test')) {
                $method = ($entry.GetAttribute('Name') -split '\(', 2)[0]
                $completed = $entry.GetAttribute('Completed')
                if ($Expected.ContainsKey($method) -and $completed -cin 'True', 'False', 'true', 'false') {
                    [ordered]@{ method = $method; completed = ($completed -ieq 'true') }
                }
            }
        } catch { continue }
    }
}

function Get-QaPostgresSnapshot([string]$Container, [switch]$Cleanup) {
    # Read through the existing disposable service admin socket. Never request query text, role names, or database names.
    $sql = if ($Cleanup) {
        @'
SELECT (SELECT count(*) FROM pg_database WHERE datname ~ '^andril_m2_(test|qa)_[0-9a-f]{32}$'),
       (SELECT count(*) FROM pg_roles WHERE rolname ~ '^(m2_migrate_|m2_runtime_|andril_m2_qa_role_)[0-9a-f]{32}$');
'@
    } else {
        @'
WITH sessions AS (
    SELECT pid, wait_event_type, CASE WHEN wait_event_type = 'Lock' THEN pg_blocking_pids(pid)
        ELSE ARRAY[]::int[] END AS blockers
    FROM pg_stat_activity WHERE datname ~ '^andril_m2_test_[0-9a-f]{32}$'
)
SELECT a.pid, coalesce(a.wait_event_type, '-'), coalesce(l.locktype, '-'),
       coalesce(l.mode, '-'), coalesce(l.granted::text, '-'), array_to_string(a.blockers, ',')
FROM sessions a LEFT JOIN pg_locks l ON l.pid = a.pid
    AND (NOT l.granted OR l.locktype IN ('advisory', 'transactionid', 'tuple'))
WHERE a.wait_event_type IS NOT NULL OR a.pid IN (SELECT unnest(blockers) FROM sessions)
ORDER BY a.pid, l.locktype, l.mode LIMIT 64;
'@
    }
    $rows = [Collections.Generic.List[string]]::new()
    $capture = { param($line) if ($rows.Count -lt 64 -and $line.Length -le 256) { $rows.Add($line) } }
    $result = Invoke-QaBoundedProcess docker @('exec', '--env', 'PGOPTIONS=-c statement_timeout=2000', $Container,
        'psql', '-X', '-w', '-A', '-t', '-q', '-F', '|', '-U', 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1',
        '-c', $sql) ([Diagnostics.Stopwatch]::StartNew()) ([TimeSpan]::FromSeconds(4)) $capture
    if ($result.timedOut -or $result.exitCode -ne 0) { return @{ status = 'Unavailable' } }
    if ($Cleanup) {
        if ($rows.Count -ne 1 -or $rows[0] -cnotmatch '^([0-9]{1,8})\|([0-9]{1,8})$') {
            return @{ status = 'Unavailable'; serviceTeardown = 'GitHub Actions' }
        }
        return @{ status = 'Observed'; remainingDisposableDatabases = [int]$Matches[1]
            remainingDisposableRoles = [int]$Matches[2]; serviceTeardown = 'GitHub Actions' }
    }
    $safe = @(foreach ($row in $rows) {
        if ($row -cmatch '^([0-9]+)\|([A-Za-z-]+)\|([a-z_ -]+)\|([A-Za-z-]+)\|(true|false|-)\|([0-9,]*)$') {
            [ordered]@{ pid = $Matches[1]; waitCategory = $Matches[2]; lockType = $Matches[3]; mode = $Matches[4]
                granted = $Matches[5]; blockingPids = $Matches[6] }
        }
    })
    return @{ status = 'Observed'; waits = $safe }
}

function Complete-QaPartition($State, $Expected, [string]$Raw, [string]$Output, $Run) {
    $State.endedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $State.elapsedSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse($State.startedUtc)).TotalSeconds, 3)
    $State.exitCode = $Run.exitCode
    $State.reportAvailable = $false
    $State.counts = $null
    $State.unexpectedCount = $null
    $State.results = @()
    $sequence = @(Get-QaRelationalSequence $Raw $Expected)
    $incomplete = @($sequence | Where-Object { !$_.completed })
    if ($sequence.Count) {
        Write-QaRelationalEvent $Output $State.partition 'VSTest blame sequence' @{
            lastStarted = $sequence[-1].method; incomplete = @($incomplete | ForEach-Object { $_.method })
        }
    }
    $State.outcome = 'Failed'
    $State.reason = 'Result verification failed'
    $report = $null
    $valid = $false
    try { $report = Read-QaTrx (Join-Path $Raw 'results.trx') } catch { }
    if ($null -ne $report) {
        $State.reportAvailable = $true
        $aborted = @($report.Tests | Where-Object Outcome -CEQ 'Aborted').Count
        $State.counts = [ordered]@{ passed = $report.Passed; failed = $report.Unsuccessful - $aborted
            skipped = $report.Skipped; cancelled = $aborted }
        $known = @($report.Tests | Where-Object { $Expected.ContainsKey($_.Method) })
        $State.unexpectedCount = $report.Total - $known.Count
        $State.results = @(foreach ($test in $known) {
            $status = switch -CaseSensitive ($test.Outcome) {
                'Passed' { 'Passed' }
                'NotExecuted' { 'Skipped' }
                'Aborted' { 'Cancelled' }
                default { 'Failed' }
            }
            [ordered]@{ method = $test.Method; outcome = $status; duration = Get-QaDuration $test }
        })
        $valid = @(Test-QaExecutions $report $Expected Relational).Count -eq 0
        if ($State.unexpectedCount -eq 0) {
            Export-QaTrx $report (Join-Path $Output "relational-$($State.partition).trx")
            $null = Export-QaFailureDiagnostics $report (Join-Path $Output "relational-$($State.partition)-failures.json")
        }
    }
    $State.missing = @(foreach ($method in ($Expected.Keys | Sort-Object -CaseSensitive)) {
        $actual = @(if ($report) { $report.Tests | Where-Object { $_.Method -ceq $method } | ForEach-Object Display })
        $wanted = @($Expected[$method])
        $missing = @(
            if (!$actual.Count) { $wanted }
            else { Compare-Object ($wanted | Sort-Object) ($actual | Sort-Object) -CaseSensitive |
                Where-Object SideIndicator -eq '<=' | ForEach-Object InputObject }
        )
        if ($missing.Count) {
            [ordered]@{ method = $method; outcome = 'Missing result'; count = $missing.Count }
        }
    })
    $State.missingCount = 0
    foreach ($missingMethod in $State.missing) { $State.missingCount += $missingMethod.count }
    if ($Run.timedOut -or $incomplete.Count) {
        $State.outcome = 'Cancelled'
        $State.reason = if ($Run.timedOut) { 'Shared relational execution deadline' } else { 'VSTest aborted with incomplete test sequence' }
    } elseif (!$State.reportAvailable) {
        $State.outcome = 'Missing result'
        $State.reason = 'Completed TRX unavailable or invalid'
    } elseif ($Run.exitCode -eq 0 -and $valid) {
        $State.outcome = 'Passed'
        $State.reason = 'Exact result verification passed'
    }
    Write-QaPartitionReport $Output $State
}

function Invoke-QaRelationalPartition([string]$Project, $State, $Expected, [string]$RawRoot, [string]$Output,
    [string]$Container, [Diagnostics.Stopwatch]$Budget, [TimeSpan]$Deadline) {
    $partition = $State.partition
    $directory = Join-Path $RawRoot $partition
    $null = New-Item -ItemType Directory -Path $directory -Force
    $State.outcome = 'Running'
    $State.reason = 'Awaiting a completed partition result'
    $State.startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Write-QaPartitionReport $Output $State
    Write-QaRelationalEvent $Output $partition 'Partition started' @{ methods = $Expected.Count }
    $filter = ($Expected.Keys | Sort-Object -CaseSensitive | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
    $progress = @{ nextSample = $Budget.Elapsed.Add([TimeSpan]::FromSeconds(30)); completed = @{}; hang = $false }
    $onLine = {
        param($message)
        $result = Get-QaRelationalProgress $message $Expected
        if ($result) {
            $method = $result.method
            $progress.completed[$method] = 1 + [int]$progress.completed[$method]
            if ($progress.completed[$method] -le $Expected[$method].Count) {
                Write-QaRelationalEvent $Output $partition 'Test completion observed' $result
            }
        }
        if ($message -match 'specified inactivity time|no activity for the specified time') { $progress.hang = $true }
    }
    $onTick = {
        if ($Budget.Elapsed -ge $progress.nextSample -and ($Deadline - $Budget.Elapsed).TotalSeconds -gt 12) {
            Write-QaRelationalEvent $Output $partition 'PostgreSQL waits' (Get-QaPostgresSnapshot $Container)
            $progress.nextSample = $Budget.Elapsed.Add([TimeSpan]::FromSeconds(30))
        }
    }
    $run = Invoke-QaBoundedProcess dotnet @('test', $Project, '--configuration', 'Release', '--no-build',
        '--no-restore', '--filter', $filter, '--logger', 'trx;LogFileName=results.trx',
        '--logger', 'console;verbosity=detailed', '--results-directory', $directory,
        '--blame-hang', '--blame-hang-timeout', '3m', '--blame-hang-dump-type', 'none') $Budget $Deadline $onLine $onTick
    Write-QaRelationalEvent $Output $partition 'Test process ended' $run
    Complete-QaPartition $State $Expected $directory $Output $run
    if ($progress.hang -and !$run.timedOut -and $State.outcome -cne 'Passed') {
        $State.outcome = 'Cancelled'
        $State.reason = 'VSTest hang timeout'
        Write-QaPartitionReport $Output $State
    }
    Write-QaRelationalEvent $Output $partition 'Partition completed' @{
        outcome = $State.outcome; reason = $State.reason; elapsedSeconds = $State.elapsedSeconds
        counts = $State.counts; missingCount = $State.missingCount
    }
    return $run
}

function Complete-QaInterruptedPartitions([string]$Output, [string]$StepOutcome) {
    foreach ($partition in 'non-lifecycle', 'lifecycle') {
        $file = Join-Path $Output "relational-$partition.json"
        if (!(Test-Path -LiteralPath $file)) { continue }
        $state = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
        if ($state.outcome -cne 'Running') { continue }
        $state.outcome = if ($StepOutcome -ceq 'cancelled') { 'Cancelled' } else { 'Missing result' }
        $state.reason = 'Relational step ended without a partition result checkpoint'
        $state.endedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $state.missing = @($state.expected | ForEach-Object {
            [ordered]@{ method = $_.method; outcome = 'Missing result'; count = $_.count }
        })
        $state.missingCount = 0
        foreach ($missingMethod in $state.missing) { $state.missingCount += $missingMethod.count }
        Write-QaPartitionReport $Output $state
    }
}
