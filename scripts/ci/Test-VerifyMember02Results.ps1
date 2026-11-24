Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Verify-Member02TestResults.ps1"
. "$PSScriptRoot/Member02RelationalQa.ps1"

$file = [IO.Path]::GetTempFileName()
$fact = 'Qa.Synthetic.Fact'
$theory = 'Qa.Synthetic.Theory'
$row = "$theory(value: 1)"
$factSet = Get-QaExpectedCases @($fact)
$both = Get-QaExpectedCases @($fact, @{ method = $theory; cases = @($row, $row) })

function Set-Trx($Rows) {
    $defs = @(
        foreach ($name in 'Fact', 'Theory') {
            "<UnitTest id='$name'><TestMethod className='Qa.Synthetic' name='$name' /></UnitTest>"
        }
    ) -join ''
    $passed = 0
    $results = @(
        foreach ($item in $Rows) {
            if ($item[2] -eq 'Passed') { $passed++ }
            "<UnitTestResult testId='$($item[0])' testName='$($item[1])' outcome='$($item[2])' executionId='$($item[3])' />"
        }
    ) -join ''
    Set-Content -LiteralPath $file -Value @"
<TestRun xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010'>
  <TestDefinitions>$defs</TestDefinitions>
  <Results>$results</Results>
  <ResultSummary outcome='Completed'><Counters total='$($Rows.Count)' passed='$passed' /></ResultSummary>
</TestRun>
"@
}

function Assert-Trx([bool]$ShouldPass, $Expected, [string]$Mode = 'Relational') {
    try { $passed = @(Test-QaExecutions (Read-QaTrx $file) $Expected $Mode).Count -eq 0 }
    catch { $passed = $false }
    if ($passed -ne $ShouldPass) { throw 'Unexpected result in synthetic TRX verification.' }
}

function Test-DiagnosticScenarios {
    $json = [IO.Path]::GetTempFileName()
    $sanitized = [IO.Path]::GetTempFileName()
    $source = 'tests/Andril.IntegrationTests/Database/PrivateProfilePhotoPersistenceTests.cs'
    function Check([bool]$Condition, [string]$Id) {
        if (!$Condition) { throw "Diagnostic regression failed: $Id" }
        Write-Host "$Id passed."
    }
    function Set-Diagnostic([string]$Message = '', [string]$Stack = '', [switch]$Passing, [switch]$Multiple) {
        $outcome = if ($Passing) { 'Passed' } else { 'Failed' }
        $rows = ,@('Fact', $fact, $outcome, '1')
        if ($Multiple) { $rows += ,@('Theory', $row, 'Failed', '2') }
        Set-Trx $rows
        $xml = Import-QaXml $file TRX
        foreach ($result in $xml.SelectNodes('//*[local-name()="UnitTestResult"]')) {
            $result.SetAttribute('duration', '00:00:01.2340000')
            $result.SetAttribute('unexpected', 'SYNTHETIC_PRIVATE_MARKER')
            $output = $xml.CreateElement('Output', $xml.DocumentElement.NamespaceURI)
            $error = $xml.CreateElement('ErrorInfo', $xml.DocumentElement.NamespaceURI)
            foreach ($pair in @(@('Message', $Message), @('StackTrace', $Stack))) {
                $node = $xml.CreateElement($pair[0], $xml.DocumentElement.NamespaceURI)
                $node.InnerText = $pair[1]; $null = $error.AppendChild($node)
            }
            $null = $output.AppendChild($error); $null = $result.AppendChild($output)
        }
        $xml.Save($file)
        return Read-QaTrx $file
    }
    try {
        $report = Set-Diagnostic -Passing -Message 'Npgsql.PostgresException: 23505: synthetic'
        Check (@(Get-QaFailureDiagnostics $report).Count -eq 0) 'QA-DIAG-01'
        $report = Set-Diagnostic
        $d = @(Export-QaFailureDiagnostics $report $json)
        Check ($d.Count -eq 1 -and $d[0].testName -ceq $fact -and $d[0].outcome -ceq 'Failed' -and
            $d[0].duration -ceq '00:00:01.2340000') 'QA-DIAG-02'
        $report = Set-Diagnostic -Message "Microsoft.EntityFrameworkCore.DbUpdateException: synthetic`n ---> Npgsql.PostgresException (0x80004005): 23505: synthetic"
        $d = @(Get-QaFailureDiagnostics $report)[0]
        Check ($d.exceptionType -ceq 'Microsoft.EntityFrameworkCore.DbUpdateException' -and
            $d.innerExceptionType -ceq 'Npgsql.PostgresException') 'QA-DIAG-03'
        Check ($d.sqlState -ceq '23505') 'QA-DIAG-04'
        $report = Set-Diagnostic -Message "Npgsql.PostgresException: 23505: synthetic`nConstraintName: uq_profile_photo_pending" -Stack "at Andril.Test.Run() in ${source}:line 178"
        $d = @(Get-QaFailureDiagnostics $report)[0]
        Check ($d.constraintName -ceq 'uq_profile_photo_pending') 'QA-DIAG-05'
        Check ($d.sourceFile -ceq $source -and $d.sourceLine -eq 178) 'QA-DIAG-06'
        $d = @(Get-QaFailureDiagnostics (Set-Diagnostic))[0]
        Check ($null -eq $d.exceptionType -and $null -eq $d.innerExceptionType -and $null -eq $d.sqlState -and
            $null -eq $d.constraintName -and $null -eq $d.sourceFile -and $d.diagnosticCategory -ceq 'Unclassified') 'QA-DIAG-07'
        $secrets = "Host=private.invalid;Database=private;Username=private;Password=SYNTHETIC_PASSWORD`nAuthorization: Bearer SYNTHETIC_TOKEN`nParameters: @p0='SYNTHETIC_SQL_PARAMETER'`n::error::SYNTHETIC_PRIVATE_MARKER"
        $report = Set-Diagnostic -Message $secrets -Stack 'C:/Users/synthetic/private.cs:line 12'
        $null = Export-QaFailureDiagnostics $report $json
        Export-QaTrx $report $sanitized
        $published = (Get-Content $json -Raw) + (Get-Content $sanitized -Raw)
        Check ($published -cnotmatch 'Host=|private.invalid|Username=|Database=') 'QA-DIAG-08'
        Check ($published -cnotmatch 'SYNTHETIC_PASSWORD|SYNTHETIC_TOKEN|Authorization:|Password=') 'QA-DIAG-09'
        Check ($published -cnotmatch 'SYNTHETIC_SQL_PARAMETER|Parameters:|@p0') 'QA-DIAG-10'
        foreach ($path in @("C:/repo/${source}:line 5", "/home/runner/${source}:line 5", "tests/../${source}:line 5", "tests//${source}:line 5", 'tests/unknown.cs:line 5')) {
            $d = @(Get-QaFailureDiagnostics (Set-Diagnostic -Stack $path))[0]
            Check ($null -eq $d.sourceFile) 'QA-DIAG-11'
        }
        $knownPath = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))) $source
        $d = @(Get-QaFailureDiagnostics (Set-Diagnostic -Stack "at Andril.Test.Run() in ${knownPath}:line 178"))[0]
        Check ($d.sourceFile -ceq $source -and $d.sourceLine -eq 178) 'QA-DIAG-06'
        $report = Set-Diagnostic -Message "Unknown.EvilException: synthetic`nSqlState: ZZZZZ`nConstraintName: uq_untrusted`nAssert.Evil() Failure: injected"
        $d = @(Get-QaFailureDiagnostics $report)[0]
        Check ($null -eq $d.exceptionType -and $null -eq $d.sqlState -and $null -eq $d.constraintName -and $null -eq $d.assertionCategory) 'QA-DIAG-12'
        Set-Content $file '<unfinished'
        $rejected = $false; try { $null = Read-QaTrx $file } catch { $rejected = $true }
        Check $rejected 'QA-DIAG-13'
        Set-Content $file '<!DOCTYPE TestRun [<!ENTITY leak SYSTEM "file:///synthetic-private">]><TestRun>&leak;</TestRun>'
        $rejected = $false; try { $null = Read-QaTrx $file } catch { $rejected = $true }
        Check $rejected 'QA-DIAG-14'
        $stream = [IO.File]::OpenWrite($file)
        try { $stream.SetLength(32MB + 1) } finally { $stream.Dispose() }
        $rejected = $false; try { $null = Read-QaTrx $file } catch { $rejected = $true }
        Check $rejected 'QA-DIAG-15'
        $report = Set-Diagnostic -Multiple -Message 'Assert.Equal() Failure: synthetic'
        $d = @(Get-QaFailureDiagnostics $report)
        Check ($d.Count -eq 2 -and $d[0].testName -cne $d[1].testName -and $d[0].assertionCategory -ceq 'Equality') 'QA-DIAG-16'
        $report = Set-Diagnostic -Message ('x' * 65537)
        $d = @(Get-QaFailureDiagnostics $report)[0]
        Export-QaTrx $report $sanitized
        Check ($d.diagnosticCategory -ceq 'ExtractionUnavailable' -and
            @(Test-QaExecutions (Read-QaTrx $sanitized) $factSet Relational).Count -gt 0) 'QA-DIAG-17'
        $report = Set-Diagnostic -Message $secrets
        Export-QaTrx $report $sanitized
        $safeXml = Get-Content $sanitized -Raw
        Check ($safeXml -cnotmatch 'SYNTHETIC_|Output|StackTrace|ErrorInfo|unexpected|C:/Users') 'QA-DIAG-18'
        Set-Trx (,@('Fact', $fact, 'NotExecuted', '1'))
        Check (@(Test-QaExecutions (Read-QaTrx $file) $factSet Relational).Count -gt 0 -and
            @(Test-QaExecutions (Read-QaTrx $file) $both Relational).Count -gt 0) 'QA-DIAG-19'
        $temp = [IO.Path]::GetTempPath()
        Assert-QaReportSeparation (Join-Path $temp 'published') (Join-Path $temp 'private')
        foreach ($paths in @(@($temp, (Join-Path $temp 'private')), @((Join-Path $temp 'private/published'), (Join-Path $temp 'private')))) {
            $rejected = $false; try { Assert-QaReportSeparation $paths[0] $paths[1] } catch { $rejected = $true }
            Check $rejected 'QA-DIAG-20'
        }
    } finally {
        Remove-Item -LiteralPath $json, $sanitized -ErrorAction SilentlyContinue
    }
}

function Test-ConsolidatedFailurePropagation {
    $workflow = Get-Content -LiteralPath "$PSScriptRoot/../../.github/workflows/member-02-qa.yml" -Raw
    $pattern = '(?s)      - name: Summarize and enforce all required jobs\r?\n        run: \|\r?\n(.*?)      - name: Upload consolidated summary'
    if ($workflow -notmatch $pattern) { throw 'Could not locate the consolidated gate.' }
    $gate = [scriptblock]::Create(($Matches[1] -replace '(?m)^          ', ''))
    $names = @('GITHUB_REF', 'GITHUB_SHA', 'RUNNER_OS', 'RUNNER_ARCH', 'RUNNER_TEMP', 'GITHUB_STEP_SUMMARY',
        'OFFLINE_RESULT', 'POSTGRES_RESULT', 'SECRETS_RESULT', 'OFFLINE_DETAILS', 'POSTGRES_DETAILS', 'SECRETS_DETAILS')
    $saved = @{}; foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('andril-gate-test-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $temp
    try {
        $env:GITHUB_REF = 'refs/heads/Member-2'; $env:GITHUB_SHA = 'synthetic'
        $env:RUNNER_OS = 'synthetic'; $env:RUNNER_ARCH = 'synthetic'; $env:RUNNER_TEMP = $temp
        $env:GITHUB_STEP_SUMMARY = Join-Path $temp 'summary.md'
        $env:OFFLINE_DETAILS = ''; $env:POSTGRES_DETAILS = ''; $env:SECRETS_DETAILS = ''
        foreach ($target in @('none', 'OFFLINE_RESULT', 'POSTGRES_RESULT', 'SECRETS_RESULT')) {
            foreach ($status in @('failure', 'cancelled', 'skipped')) {
                $env:OFFLINE_RESULT = 'success'; $env:POSTGRES_RESULT = 'success'; $env:SECRETS_RESULT = 'success'
                if ($target -ne 'none') { [Environment]::SetEnvironmentVariable($target, $status) }
                $passed = $true; try { & $gate } catch { $passed = $false }
                if ($passed -ne ($target -eq 'none')) { throw 'Consolidated failure propagation regression.' }
            }
        }
        $env:GITHUB_REF = 'refs/heads/unauthorized'
        $env:OFFLINE_RESULT = 'success'; $env:POSTGRES_RESULT = 'success'; $env:SECRETS_RESULT = 'success'
        $rejected = $false; try { & $gate } catch { $rejected = $true }
        if (!$rejected) { throw 'Unauthorized ref passed the consolidated gate.' }
        Write-Host 'Consolidated gate: success, failure, cancelled, skipped and unauthorized ref checks passed.'
    } finally {
        foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        $resolved = [IO.Path]::GetFullPath($temp)
        $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
        if ([IO.Path]::GetDirectoryName($resolved) -eq $parent -and [IO.Path]::GetFileName($resolved) -cmatch '^andril-gate-test-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}

function Assert-RelationalCheck([bool]$Condition, [string]$Description) {
    if (!$Condition) { throw "Relational regression failed: $Description" }
    Write-Host "Relational check passed: $Description"
}

function Test-RelationalPartitions {
    $inventory = Get-Content -LiteralPath "$PSScriptRoot/member-02-required-db-tests.json" -Raw | ConvertFrom-Json
    $required = Get-QaExpectedCases $inventory.requiredM2
    $parts = Get-QaRelationalPartitions $required
    Assert-QaRelationalPartitions $required $parts
    Assert-RelationalCheck ($parts['non-lifecycle'].Count + $parts.lifecycle.Count -eq $required.Count) 'Manifest partitions'

    $one = Get-QaExpectedCases @($fact)
    $two = Get-QaExpectedCases @(@{ method = $theory; cases = @($row, $row) })
    Assert-QaRelationalPartitions $both @{ a = $one; b = $two }
    foreach ($invalid in @(@{ a = $one; b = $both }, @{ a = $one }, @{ a = $one; b = (Get-QaExpectedCases @($theory)) },
        @{ a = $one; b = (Get-QaExpectedCases @('Qa.Synthetic.Unexpected')) })) {
        $rejected = $false
        try { Assert-QaRelationalPartitions $both $invalid } catch { $rejected = $true }
        Assert-RelationalCheck $rejected 'Reject overlap, missing, wrong multiplicity or unexpected method'
    }
}

function Test-RelationalEvidence {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('andril-relational-test-' + [guid]::NewGuid().ToString('N'))
    $privateDir = Join-Path $temp 'private'
    $outputDir = Join-Path $temp 'output'
    $null = New-Item -ItemType Directory -Path $privateDir, $outputDir
    try {
        $reportFile = Join-Path $privateDir 'results.trx'
        Set-Trx (,@('Fact', $fact, 'Passed', '1'))
        Copy-Item -LiteralPath $file -Destination $reportFile
        $state = New-QaPartitionReport 'lifecycle' $factSet
        Assert-RelationalCheck ($state.outcome -ceq 'Not executed') 'Unstarted partition'
        $state.startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Complete-QaPartition $state $factSet $privateDir $outputDir @{ timedOut = $false; exitCode = 0 }
        Assert-RelationalCheck ($state.outcome -ceq 'Passed' -and $state.counts.passed -eq 1) 'Complete exact pass'
        Complete-QaPartition $state $factSet $privateDir $outputDir @{ timedOut = $true; exitCode = 1 }
        Assert-RelationalCheck ($state.outcome -ceq 'Cancelled' -and $state.counts.passed -eq 1) 'Cancellation overrides partial success'
        Complete-QaPartition $state $both $privateDir $outputDir @{ timedOut = $false; exitCode = 0 }
        Assert-RelationalCheck ($state.outcome -ceq 'Failed' -and $state.missingCount -eq 2) 'Missing theory results'
        foreach ($outcome in 'Failed', 'NotExecuted', 'Aborted') {
            Set-Trx (,@('Fact', $fact, $outcome, '1'))
            Copy-Item -LiteralPath $file -Destination $reportFile -Force
            Complete-QaPartition $state $factSet $privateDir $outputDir @{ timedOut = $false; exitCode = 1 }
            $wanted = switch ($outcome) { 'NotExecuted' { 'Skipped' } 'Aborted' { 'Cancelled' } default { 'Failed' } }
            Assert-RelationalCheck ($state.outcome -ceq 'Failed' -and $state.results[0].outcome -ceq $wanted) "Preserve $wanted test result"
        }
        Set-Trx @(@('Fact', $fact, 'Passed', '1'), @('Theory', 'Qa.Synthetic.SENSITIVE_DATA', 'Passed', '2'))
        Copy-Item -LiteralPath $file -Destination $reportFile -Force
        Complete-QaPartition $state $factSet $privateDir $outputDir @{ timedOut = $false; exitCode = 0 }
        $checkpoint = Get-Content (Join-Path $outputDir 'relational-lifecycle.json') -Raw
        Assert-RelationalCheck ($state.outcome -ceq 'Failed' -and $state.unexpectedCount -eq 1 -and
            $checkpoint -cnotmatch 'SENSITIVE_DATA|Qa\.Synthetic\.Theory') 'Reject and omit unexpected identity'
        Remove-Item -LiteralPath $reportFile
        Complete-QaPartition $state $factSet $privateDir $outputDir @{ timedOut = $false; exitCode = 1 }
        Assert-RelationalCheck ($state.outcome -ceq 'Missing result' -and $state.missingCount -eq 1) 'Missing completed TRX'

        Set-Content -LiteralPath (Join-Path $privateDir 'Sequence_fixture.xml') -Value @"
<TestSequence><Test Name="$fact" Completed="False" /><Test Name="Qa.Synthetic.SENSITIVE_DATA" Completed="False" /></TestSequence>
"@
        $sequence = @(Get-QaRelationalSequence $privateDir $factSet)
        Assert-RelationalCheck ($sequence.Count -eq 1 -and !$sequence[0].completed) 'Allowlisted incomplete blame sequence'
        Assert-RelationalCheck ((Get-QaRelationalProgress "  Passed $row [3 ms]" $both).method -ceq $theory) 'Allowlisted console completion'
        Assert-RelationalCheck ($null -eq (Get-QaRelationalProgress '  Passed Qa.Synthetic.SENSITIVE_DATA' $both)) 'Suppress unknown console output'

        $state = New-QaPartitionReport 'lifecycle' $factSet
        $state.outcome = 'Running'
        Write-QaPartitionReport $outputDir $state
        Complete-QaInterruptedPartitions $outputDir 'cancelled'
        $checkpoint = Get-Content (Join-Path $outputDir 'relational-lifecycle.json') -Raw | ConvertFrom-Json
        Assert-RelationalCheck ($checkpoint.outcome -ceq 'Cancelled' -and $checkpoint.missingCount -eq 1) 'External cancellation checkpoint'
    } finally {
        $resolved = (Resolve-Path -LiteralPath $temp).Path
        $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
        if ([IO.Path]::GetDirectoryName($resolved) -eq $parent -and [IO.Path]::GetFileName($resolved) -cmatch '^andril-relational-test-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}

function Test-BoundedRelationalProcess {
    $outputLines = [Collections.Generic.List[string]]::new()
    $capture = { param($line) $outputLines.Add($line) }
    $command = @('-NoProfile', '-Command', '[Console]::WriteLine("fixture status"); [Console]::Error.WriteLine("private"); exit 7')
    $completed = Invoke-QaBoundedProcess pwsh $command ([Diagnostics.Stopwatch]::StartNew()) ([TimeSpan]::FromSeconds(20)) $capture
    Assert-RelationalCheck (!$completed.timedOut -and $completed.exitCode -eq 7 -and
        $outputLines.Count -eq 1 -and $outputLines[0] -ceq 'fixture status') 'Observe exit, drain both streams, suppress stderr'
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $timeout = Invoke-QaBoundedProcess pwsh @('-NoProfile', '-Command', 'Start-Sleep -Seconds 45') $timer ([TimeSpan]::FromSeconds(1))
    Assert-RelationalCheck ($timeout.timedOut -and $timeout.termination -ceq 'Process tree stopped' -and
        $timer.Elapsed.TotalSeconds -lt 12) 'Enforce deadline and stop child process'
}

try {
    $good = @('Fact', $fact, 'Passed', '1')
    Set-Trx (,@($good))
    Assert-Trx $true $factSet
    Set-Trx (,@('Fact', $fact, 'NotExecuted', '1'))
    Assert-Trx $false $factSet
    Assert-Trx $true $factSet Offline
    Assert-Trx $false (Get-QaExpectedCases @()) Offline
    Set-Trx (,@('Fact', $fact, 'Failed', '1'))
    Assert-Trx $false $factSet
    Set-Trx (,@('Fact', $fact, 'Aborted', '1'))
    Assert-Trx $false $factSet
    Set-Trx @($good, @('Theory', $row, 'Passed', '2'))
    Assert-Trx $false $both
    Set-Trx @($good, @('Theory', $row, 'Passed', '2'), @('Theory', $row, 'Passed', '3'))
    Assert-Trx $true $both
    Set-Trx @($good, @('Theory', $row, 'Passed', '2'), @('Theory', $row, 'Passed', '2'))
    Assert-Trx $false $both
    Set-Trx @($good, @('Theory', $row, 'Passed', '2'), @('Theory', "$theory(value: 2)", 'Passed', '3'))
    Assert-Trx $false $both
    Set-Trx (,@($good))
    Assert-Trx $false $both
    Set-Content -LiteralPath $file -Value '<unfinished'
    Assert-Trx $false $factSet
    Set-Content -LiteralPath $file -Value '<TestRun/>'
    Assert-Trx $false $factSet
    Clear-Content -LiteralPath $file
    Assert-Trx $false $factSet
    Remove-Item -LiteralPath $file
    Assert-Trx $false $factSet
    Test-DiagnosticScenarios
    Test-ConsolidatedFailurePropagation
    Test-RelationalPartitions
    Test-RelationalEvidence
    Test-BoundedRelationalProcess
    Write-Host 'Synthetic verifier checks passed.'
} finally {
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
}
