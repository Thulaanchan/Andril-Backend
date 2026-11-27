param(
    [string] $Dotnet = 'dotnet',
    [string] $ResultsDirectory = (Join-Path ([IO.Path]::GetTempPath()) ("andril-social-results-" + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:ANDRIL_M2_TEST_POSTGRES)) {
    throw 'The Social acceptance gate requires ANDRIL_M2_TEST_POSTGRES for an isolated loopback PostgreSQL instance.'
}

$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $repository 'tests/Andril.IntegrationTests/Andril.IntegrationTests.csproj'
$manifest = Join-Path $PSScriptRoot 'member-05-social-required-db-tests.txt'
$expected = @(Get-Content -LiteralPath $manifest | Where-Object { $_ -ne '' })
if ($expected.Count -eq 0 -or @($expected | Sort-Object -Unique).Count -ne $expected.Count -or
    @($expected | Where-Object { $_ -cnotmatch '^Andril\.IntegrationTests\.Database\.[A-Za-z0-9_]+\.[A-Za-z0-9_]+$' }).Count -ne 0) {
    throw 'The Social acceptance test manifest is empty, invalid, or contains duplicates.'
}

$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$name = 'member-05-social-db.trx'
$trx = Join-Path $ResultsDirectory $name
if (Test-Path -LiteralPath $trx) { throw 'Use a results directory without a prior Social acceptance report.' }
$filter = ($expected | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
& $Dotnet test $project -c Release --filter $filter --logger "trx;LogFileName=$name" --results-directory $ResultsDirectory --verbosity minimal
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) { exit $exitCode }

[xml]$result = Get-Content -Raw -LiteralPath $trx
$observed = @($result.TestRun.Results.UnitTestResult)
$counters = $result.TestRun.ResultSummary.Counters
$mismatch = Compare-Object $expected @($observed | ForEach-Object testName) -CaseSensitive
if ($mismatch -or $observed.Count -ne $expected.Count -or [int]$counters.passed -ne $expected.Count -or
    [int]$counters.notExecuted -ne 0 -or @($observed | Where-Object outcome -cne 'Passed').Count -ne 0) {
    $mismatch | Format-Table -AutoSize
    throw 'The Social acceptance gate requires a passing result for every exact manifest entry, with zero skips.'
}
Write-Output "Verified $($expected.Count) exact Social PostgreSQL tests with zero skips. Results $trx"
