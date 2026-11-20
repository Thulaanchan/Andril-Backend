Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Verify-Member02TestResults.ps1"

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
    Write-Host 'Synthetic verifier checks passed.'
} finally {
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
}
