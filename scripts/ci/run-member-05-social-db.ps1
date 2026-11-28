param(
    [string] $Dotnet = 'dotnet',
    [string] $ResultsDirectory = (Join-Path ([IO.Path]::GetTempPath()) ("andril-social-results-" + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:ANDRIL_M2_TEST_POSTGRES)) {
    throw 'The Member-05 acceptance gate requires ANDRIL_M2_TEST_POSTGRES for an isolated loopback PostgreSQL instance.'
}

$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $repository 'tests/Andril.IntegrationTests/Andril.IntegrationTests.csproj'
$socialManifest = Join-Path $PSScriptRoot 'member-05-social-required-db-tests.txt'
$groupsManifest = Join-Path $PSScriptRoot 'member-05-groups-required-db-tests.txt'
$social = @(Get-Content -LiteralPath $socialManifest | Where-Object { $_ -ne '' })
$groups = @(Get-Content -LiteralPath $groupsManifest | Where-Object { $_ -ne '' })
$functionalManifest = Join-Path $PSScriptRoot 'member-05-final-functional-required-db-tests.txt'
$functional = @(Get-Content -LiteralPath $functionalManifest | Where-Object { $_ -ne '' })
$runtimeManifest = Join-Path $PSScriptRoot 'member-05-runtime-required-db-tests.txt'
$runtime = @(Get-Content -LiteralPath $runtimeManifest | Where-Object { $_ -ne '' })
$learningManifest = Join-Path $PSScriptRoot 'member-01-learning-eligibility-required-db-tests.txt'
$learning = @(Get-Content -LiteralPath $learningManifest | Where-Object { $_ -ne '' })
$cvManifest = Join-Path $PSScriptRoot 'member-01-learning-private-cv-required-db-tests.txt'
$cv = @(Get-Content -LiteralPath $cvManifest | Where-Object { $_ -ne '' })
$expected = $social + $groups + $functional + $runtime + $learning + $cv
if ($social.Count -ne 52 -or $groups.Count -ne 27 -or $functional.Count -ne 13 -or $runtime.Count -eq 0 -or $learning.Count -ne 11 -or $cv.Count -ne 8 -or @($expected | Sort-Object -Unique).Count -ne $expected.Count -or
    @($expected | Where-Object { $_ -cnotmatch '^Andril\.IntegrationTests\.Database\.[A-Za-z0-9_]+\.[A-Za-z0-9_]+$' }).Count -ne 0) {
    throw 'The Member-05 acceptance manifests are incomplete, invalid, or contain duplicates.'
}

$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$name = 'member-05-social-db.trx'
$trx = Join-Path $ResultsDirectory $name
if (Test-Path -LiteralPath $trx) { throw 'Use a results directory without a prior Member-05 acceptance report.' }
$filter = ($expected | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
& $Dotnet test $project -c Release --filter $filter --logger "trx;LogFileName=$name" --results-directory $ResultsDirectory --verbosity minimal
$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) { exit $exitCode }

[xml]$result = Get-Content -Raw -LiteralPath $trx
$observed = @($result.TestRun.Results.UnitTestResult)
$counters = $result.TestRun.ResultSummary.Counters
$mismatch = Compare-Object $expected @($observed | ForEach-Object testName) -CaseSensitive
if ($mismatch -or $observed.Count -ne $expected.Count -or [int]$counters.total -ne $expected.Count -or
    [int]$counters.executed -ne $expected.Count -or [int]$counters.passed -ne $expected.Count -or
    [int]$counters.notExecuted -ne 0 -or @($observed | Where-Object outcome -cne 'Passed').Count -ne 0) {
    $mismatch | Format-Table -AutoSize
    throw 'The Member-05 acceptance gate requires a passing result for every exact manifest entry, with zero skips.'
}
Write-Output "Verified $($social.Count) exact Social PostgreSQL tests with zero skips. Results $trx"
Write-Output "Verified $($groups.Count) exact Shared Groups PostgreSQL tests with zero skips. Results $trx"
Write-Output "Verified $($functional.Count) exact Final Functional PostgreSQL tests with zero skips. Results $trx"
Write-Output "Verified $($runtime.Count) exact Runtime Integration PostgreSQL tests with zero skips. Results $trx"
