[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Restore', 'Build', 'Offline', 'ProcessorRestore', 'Processor', 'Relational', 'Secrets', 'Summarize')]
    [string]$Phase,
    [string]$OutputDirectory,
    [string]$ServiceId,
    [switch]$IntegrationOnly,
    [string]$ProcessorLicenseFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. "$PSScriptRoot/Verify-Member02TestResults.ps1"
. "$PSScriptRoot/Member02RelationalQa.ps1"
. "$PSScriptRoot/Member02GitleaksQa.ps1"

$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$integration = 'tests/Andril.IntegrationTests/Andril.IntegrationTests.csproj'
$processorTests = 'tests/Andril.ProfilePhotoProcessor.Tests/Andril.ProfilePhotoProcessor.Tests.csproj'
$buildTarget = if ($IntegrationOnly) { $integration } else { 'Andril.slnx' }
if (!$OutputDirectory) {
    if ($env:QA_NAME -cnotin 'member02-offline', 'member02-postgres', 'member02-secrets') { throw 'Invalid QA report directory.' }
    $OutputDirectory = Join-Path $env:RUNNER_TEMP $env:QA_NAME
}
$out = [IO.Path]::GetFullPath($OutputDirectory)
$raw = Join-Path ([IO.Path]::GetTempPath()) ("andril-qa-" + [guid]::NewGuid().ToString('N'))
Assert-QaReportSeparation $out $raw
$null = New-Item -ItemType Directory -Path $out -Force
$details = Join-Path $out 'details.md'

function Add-Detail([string]$Line) {
    Add-Content -LiteralPath $details -Value $Line -Encoding utf8
    Write-Host $Line
}

function Invoke-CheckedDotnet([string[]]$Arguments, [string]$Gate, [switch]$Audit) {
    $lines = @(& dotnet @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $code = $LASTEXITCODE
    $diagnostics = @(
        foreach ($line in $lines) {
            if ($line -match '^(?:(?<file>.+?)\((?<line>\d+)(?:,\d+)*\)\s*:\s*|(?<origin>.+?)\s*:\s*)?(?<severity>warning|error)\s+(?<id>[A-Z]+\d{4,5}):\s*(?<message>.+?)(?:\s+\[(?<project>[^\[\]]+\.csproj)(?:::[^\]]+)?\])?\s*$') {
                $d = $Matches.Clone()
                $file = if ($d.ContainsKey('file')) { [IO.Path]::GetFileName($d.file) } else { '-' }
                $project = if ($d.ContainsKey('project')) { [IO.Path]::GetFileName($d.project) }
                    elseif ($d.ContainsKey('origin') -and $d.origin -like '*.csproj') { [IO.Path]::GetFileName($d.origin) }
                    else { '-' }
                if ($file -notmatch '^[\w.-]+$') { $file = '-' }
                if ($project -notmatch '^[\w.-]+$') { $project = '-' }
                if ($d.ContainsKey('line')) { $file += ":$($d.line)" }
                $finding = ''
                if ($d.id -in 'NU1901', 'NU1902', 'NU1903', 'NU1904' -and
                    $d.message -match "'([\w.-]+)'\s+([\w.+-]+).*?(https://github\.com/advisories/GHSA-[a-z0-9-]+|https://nvd\.nist\.gov/vuln/detail/CVE-[0-9-]+)") {
                    $finding = "$($Matches[1]) $($Matches[2]) $($Matches[3])"
                }
                "$($d.severity) $($d.id) | $project | $file | $finding".TrimEnd()
            }
        }
    )
    $diagnostics = @($diagnostics | Sort-Object -Unique)
    $warnings = @($diagnostics.Where({ $_ -like 'warning *' })).Count
    $errors = @($diagnostics.Where({ $_ -like 'error *' })).Count
    $status = if ($code -eq 0) { 'passed' } else { 'failed' }
    Add-Detail "- $Gate $status (exit $code, $warnings distinct warnings, $errors distinct errors)."
    @("Severity and ID | Project | File and line | Advisory evidence"; $diagnostics) |
        Set-Content -LiteralPath (Join-Path $out "$Gate.txt") -Encoding utf8
    if ($code -ne 0 -or ($Audit -and
        @($diagnostics.Where({ $_ -match ' (NU1900|NU1903|NU1904|NU1905) \|' })).Count)) {
        throw "$Gate failed. See the sanitized diagnostics."
    }
}

function Invoke-QaRestore([string]$Project, [string]$Gate, [string]$PackageCache) {
    $settings = @(& git ls-files --cached --others --exclude-standard -- '*.csproj' '*.props' '*.targets' '*.config')
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect repository audit settings.' }
    $bypass = Select-String -LiteralPath $settings -Pattern 'NuGetAuditSuppress|<NuGetAudit>\s*false|<(NoWarn|WarningsNotAsErrors)>[^<]*NU190[0345]|RestoreAuditSources'
    if ($bypass) { throw 'Review repository settings that override the required NuGet audit.' }
    # Separate from the safe report directory. This package cache lives until the job ends.
    $packages = Join-Path $env:RUNNER_TEMP $PackageCache
    Invoke-CheckedDotnet @('restore', $Project, '--configfile', "$PSScriptRoot/NuGet.Member02.config",
        '--packages', $packages, '--no-http-cache', '--force', '-p:NuGetAudit=true', '-p:NuGetAuditMode=all',
        '-p:NuGetAuditLevel=low', '-warnAsError:NU1900,NU1903,NU1904,NU1905', '-clp:NoSummary') $Gate -Audit
}

function Invoke-QaTests([string]$Suite, [string]$Project, $Expected, [switch]$Coverage) {
    $resultDir = Join-Path $raw $Suite
    $null = New-Item -ItemType Directory -Path $resultDir -Force
    $arguments = @('test', $Project, '--configuration', 'Release', '--no-build', '--no-restore',
        '--logger', "trx;LogFileName=$Suite.trx", '--results-directory', $resultDir)
    if ($Coverage) { $arguments += @('--collect', 'Code Coverage;Format=cobertura') }
    & dotnet @arguments 2>&1 | Out-Null
    $exitCode = $LASTEXITCODE
    $errors = [System.Collections.Generic.List[string]]::new()
    $counts = 'TRX unavailable'
    try {
        $report = Read-QaTrx (Join-Path $resultDir "$Suite.trx")
        $counts = "$($report.Passed) passed, $($report.Unsuccessful) unsuccessful, $($report.Skipped) skipped"
        $errors.AddRange([string[]]@(Test-QaExecutions $report $Expected Offline))
        if ($Suite -eq 'offline-integration') {
            foreach ($name in 'AccountPersistenceTests.MigrationMatchesModel_AndScriptOnlyTargetsAccountSchema',
                'PrivateProfilePhotoPersistenceTests.MigrationIsAdditiveScopedAndMatchesSnapshot') {
                if (@($report.Tests.Where({ $_.Method -ceq "Andril.IntegrationTests.Database.$name" -and $_.Outcome -ceq 'Passed' })).Count -ne 1) {
                    $errors.Add("Missing successful migration check $name")
                }
            }
        }
        $failures = Export-QaFailureDiagnostics $report (Join-Path $out "$Suite-failures.json")
        Add-Detail "- $Suite failed tests: $($failures.Count). Safe report: $Suite-failures.json (suite report artifact)."
        foreach ($failure in $failures) {
            $classification = if ($failure.exceptionType) { $failure.exceptionType } else { 'unavailable' }
            $sqlState = if ($failure.sqlState) { $failure.sqlState } else { 'unavailable' }
            $constraint = if ($failure.constraintName) { $failure.constraintName } else { 'unavailable' }
            $location = if ($failure.sourceFile) { "$($failure.sourceFile):$($failure.sourceLine)" } else { 'unavailable' }
            Add-Detail "- Failed: $($failure.testName); category: $($failure.diagnosticCategory); exception: $classification; SQLSTATE: $sqlState; constraint: $constraint; source: $location."
        }
        Export-QaTrx $report (Join-Path $out "$Suite.trx")
    } catch {
        $errors.Add("Could not verify or sanitize $Suite TRX. Diagnostic information unavailable.")
        # Invalid reports remain a gate failure; no claims about their test inventory are possible.
        ConvertTo-Json -InputObject ([ordered]@{ schemaVersion = 1; failedCount = $null;
            diagnosticCategory = 'ReportUnavailable'; failures = @() }) -Depth 3 |
            Set-Content -LiteralPath (Join-Path $out "$Suite-failures.json") -Encoding utf8
        Add-Detail "- $Suite failure diagnostics unavailable. Safe report: $Suite-failures.json (suite report artifact)."
    }
    if ($exitCode -ne 0) { $errors.Add("dotnet test exited $exitCode") }
    $coverageResult = 'not requested'
    if ($Coverage) {
        try {
            # VSTest may retain the original and a byte-identical TRX attachment copy.
            $reports = @(Get-ChildItem -LiteralPath $resultDir -Recurse -Filter '*.cobertura.xml' -File |
                Get-FileHash -Algorithm SHA256 | Sort-Object -Property Hash -Unique)
            if ($reports.Count -ne 1) { throw "Expected one Cobertura report and found $($reports.Count)." }
            $coverageResult = Read-QaCoverage $reports[0].Path
            Copy-Item -LiteralPath $reports[0].Path -Destination (Join-Path $out "$Suite.cobertura.xml")
        } catch {
            $coverageResult = 'missing or invalid'
            $reason = if ($_.Exception.Message -match '^(Expected one Cobertura report and found \d+\.|Invalid Cobertura (report|XML|lines counters|branches counters)\.)$') {
                $_.Exception.Message
            } else { "Report processing raised $($_.Exception.GetType().Name)." }
            $errors.Add("Coverage $coverageResult. $reason")
        }
    }
    $status = if ($errors.Count) { 'failed' } else { 'passed' }
    Add-Detail "- $Suite $status ($counts). Coverage $coverageResult."
    foreach ($issue in $errors) { Write-Host $issue }
    return ($errors.Count -eq 0)
}

function Invoke-OfflineQa($Inventory) {
    Remove-Item Env:ANDRIL_M2_TEST_POSTGRES, Env:ANDRIL_M3_TEST_POSTGRES, Env:ANDRIL_M2_TEST_POSTGRES_ALLOW_ROLES, Env:ANDRIL_RUN_LIVEKIT_SANDBOX -ErrorAction SilentlyContinue
    $suites = [ordered]@{
        unit = 'tests/Andril.UnitTests/Andril.UnitTests.csproj'
        security = 'tests/Andril.SecurityTests/Andril.SecurityTests.csproj'
        architecture = 'tests/Andril.ArchitectureTests/Andril.ArchitectureTests.csproj'
        'offline-integration' = $integration
    }
    $failed = @(
        foreach ($suite in $suites.Keys) {
            $entries = if ($suite -eq 'offline-integration') { @($Inventory.requiredM2) + @($Inventory.offlineOnlySkips) } else { @() }
            if (!(Invoke-QaTests $suite $suites[$suite] (Get-QaExpectedCases $entries) -Coverage)) { $suite }
        }
    )
    Add-Detail "- Offline skip inventory covers $($Inventory.requiredM2.Count) M2 and $($Inventory.offlineOnlySkips.Count) other environment-dependent methods."
    if ($failed.Count) { throw "Offline verification failed for $($failed -join ', ')." }
}

function Invoke-ProcessorQa {
    $secret = $env:SIXLABORS_LICENSE_KEY
    Remove-Item Env:SIXLABORS_LICENSE_KEY -ErrorAction SilentlyContinue
    $license = $ProcessorLicenseFile
    $materializedLicense = $null
    $stage = 'license setup'
    try {
        if ($env:GITHUB_ACTIONS -eq 'true' -and $license) { throw 'CI requires the step secret.' }
        if ($license) {
            $license = (Get-Item -LiteralPath $license -ErrorAction Stop).FullName
            if (!(Test-Path -LiteralPath $license -PathType Leaf)) { throw 'External license unavailable.' }
            Assert-QaReportSeparation $repo $license
            Assert-QaReportSeparation $out $license
        } else {
            if ([string]::IsNullOrWhiteSpace($secret) -or !$IsLinux) { throw 'Linux and a license secret are required.' }
            Assert-QaReportSeparation $repo $raw
            $ownerDirectory = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute
            $null = [IO.Directory]::CreateDirectory($raw, $ownerDirectory)
            $license = Join-Path $raw 'sixlabors.lic'
            $materializedLicense = $license
            $fileOptions = [IO.FileStreamOptions]::new()
            $fileOptions.Mode = 'CreateNew'
            $fileOptions.Access = 'Write'
            $fileOptions.UnixCreateMode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite
            $writer = [IO.StreamWriter]::new($license, [Text.UTF8Encoding]::new($false), $fileOptions)
            try { $writer.Write($secret) } finally { $writer.Dispose() }
        }
        $secret = $null

        $stage = 'licensed Release build'
        try {
            & dotnet build $processorTests --configuration Release --no-restore --no-incremental "-p:SixLaborsLicenseFile=$license" `
                '-p:RunAnalyzersDuringBuild=true' '-clp:NoSummary' *> $null
            if ($LASTEXITCODE -ne 0) { throw 'Build or license validation failed.' }
        } finally {
            if ($materializedLicense) { Remove-Item -LiteralPath $materializedLicense -Force }
        }
        Add-Detail '- Photo processor licensed Release build passed.'

        $stage = 'required tests'
        if (!(Invoke-QaTests 'photo-processor' $processorTests (Get-QaExpectedCases @()))) {
            throw 'Test verification failed.'
        }
    } catch {
        Add-Detail "- Photo processor failed during $stage."
        throw "Photo processor QA failed during $stage. See the safe offline summary."
    } finally {
        $secret = $null
    }
}

function Invoke-QaPsql([string]$Database, [string]$Sql) {
    $arguments = @('--no-psqlrc', '--no-password', '--host', '127.0.0.1', '--port', '55432',
        '--username', 'andril_qa_fixture', '--dbname', $Database, '--no-align', '--tuples-only', '--quiet', '--set', 'ON_ERROR_STOP=1')
    $reply = @($Sql | & psql @arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'Disposable PostgreSQL readiness or permission probe failed.' }
    return ($reply -join [Environment]::NewLine).Trim()
}

function Invoke-RelationalQa($Inventory) {
    if ($env:GITHUB_ACTIONS -ne 'true' -or $ServiceId -cnotmatch '^[0-9a-f]{64}$') {
        throw 'The relational gate requires the isolated GitHub Actions job service.'
    }
    Remove-Item Env:ANDRIL_M3_TEST_POSTGRES, Env:ANDRIL_M2_TEST_POSTGRES, Env:ANDRIL_M2_TEST_POSTGRES_ALLOW_ROLES -ErrorAction SilentlyContinue
    $port = & docker port $ServiceId '5432/tcp' 2>&1
    if ($LASTEXITCODE -ne 0 -or "$port" -cne '127.0.0.1:55432') { throw 'Unexpected PostgreSQL service port binding.' }
    $budget = [Diagnostics.Stopwatch]::StartNew()
    $deadline = [TimeSpan]::FromMinutes(27)
    $expected = Get-QaExpectedCases $Inventory.requiredM2
    $partitions = Get-QaRelationalPartitions $expected
    $states = [ordered]@{}
    foreach ($partition in $partitions.Keys) {
        $states[$partition] = New-QaPartitionReport $partition $partitions[$partition]
        Write-QaPartitionReport $out $states[$partition]
    }
    $probesPassed = $false
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    try {
        "SET statement_timeout = '15s'; CREATE ROLE andril_qa_fixture LOGIN CREATEDB CREATEROLE NOSUPERUSER NOBYPASSRLS PASSWORD '$password';" |
            & docker exec -i $ServiceId psql -X -U postgres -d postgres -v ON_ERROR_STOP=1 *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Could not provision the restricted disposable fixture bootstrap role.' }
        $env:PGPASSWORD = $password
        $env:PGCONNECT_TIMEOUT = '5'
        $env:PGSSLMODE = 'disable'
        $env:PGOPTIONS = '-c statement_timeout=15000'
        $version = Invoke-QaPsql postgres @'
SELECT current_setting('server_version_num') FROM pg_roles
WHERE rolname = current_user AND current_user = 'andril_qa_fixture' AND current_database() = 'postgres'
  AND inet_server_port() = 5432 AND rolcreatedb AND rolcreaterole AND NOT rolsuper AND NOT rolbypassrls;
'@
        if ($version -cne '170012') { throw 'Unexpected server version or fixture role privileges.' }
        $suffix = [guid]::NewGuid().ToString('N')
        $db = "andril_m2_qa_$suffix"
        $role = "andril_m2_qa_role_$suffix"
        $null = Invoke-QaPsql postgres "CREATE DATABASE $db;"
        try {
            $null = Invoke-QaPsql $db @"
BEGIN;
CREATE ROLE $role NOLOGIN NOINHERIT;
GRANT $role TO CURRENT_USER;
CREATE SCHEMA role_probe;
GRANT USAGE, CREATE ON SCHEMA role_probe TO $role;
CREATE TABLE role_probe.probe (id integer);
ALTER TABLE role_probe.probe OWNER TO $role;
SET ROLE $role;
SELECT count(*) FROM role_probe.probe;
RESET ROLE;
DROP OWNED BY $role;
DROP ROLE $role;
COMMIT;
"@
        } finally {
            $null = Invoke-QaPsql postgres "DROP DATABASE $db WITH (FORCE);"
        }
        Remove-Item Env:PGPASSWORD, Env:PGSSLMODE, Env:PGCONNECT_TIMEOUT, Env:PGOPTIONS
        $probesPassed = $true
        Add-Detail '- PostgreSQL 17.12 loopback, maintenance database, database create/drop and role probes passed.'
        Add-Detail "- Relational selection includes $($expected.Count) M2 methods and excludes $($Inventory.offlineOnlySkips.Count) other environment-dependent methods."
        $env:ANDRIL_M2_TEST_POSTGRES = "Host=127.0.0.1;Port=55432;Database=postgres;Username=andril_qa_fixture;Password=$password;SSL Mode=Disable;Timeout=5"
        $env:ANDRIL_M2_TEST_POSTGRES_ALLOW_ROLES = '1'
        foreach ($partition in $partitions.Keys) {
            $state = $states[$partition]
            if ($budget.Elapsed -ge $deadline) {
                $state.reason = 'Shared relational execution budget exhausted before launch'
                Write-QaPartitionReport $out $state
                break
            }
            $result = Invoke-QaRelationalPartition $integration $state $partitions[$partition] $raw $out $ServiceId $budget $deadline
            if ($result.timedOut -or $result.termination -eq 'Process tree stop unconfirmed') { break }
        }
        if (@($states.Values | Where-Object outcome -cne 'Passed').Count) { throw 'M2 relational verification incomplete or unsuccessful.' }
    } finally {
        Remove-Item Env:PGPASSWORD, Env:PGSSLMODE, Env:PGCONNECT_TIMEOUT, Env:PGOPTIONS, Env:ANDRIL_M2_TEST_POSTGRES,
            Env:ANDRIL_M2_TEST_POSTGRES_ALLOW_ROLES -ErrorAction SilentlyContinue
        if ($probesPassed) {
            $cleanup = Get-QaPostgresSnapshot $ServiceId -Cleanup
            Write-QaRelationalEvent $out 'relational' 'Fixture cleanup observation' $cleanup
        } else {
            Write-QaRelationalEvent $out 'relational' 'Fixture cleanup observation' @{
                status = 'Unavailable'; serviceTeardown = 'GitHub Actions'
            }
        }
    }
}

function Invoke-GitleaksQa {
    $version = '8.30.1'
    $asset = "gitleaks_$($version)_linux_x64.tar.gz"
    $archive = Join-Path $raw $asset
    $hash = '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb'
    try {
        $null = New-Item -ItemType Directory -Path $raw
        Invoke-WebRequest -Uri "https://github.com/gitleaks/gitleaks/releases/download/v$version/$asset" -OutFile $archive -TimeoutSec 60
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $hash) { throw 'Gitleaks checksum mismatch.' }
        & tar -xzf $archive -C $raw gitleaks
        $scanner = Join-Path $raw 'gitleaks'
        if ($LASTEXITCODE -ne 0 -or (& $scanner version) -cne $version -or $LASTEXITCODE -ne 0) {
            throw 'Gitleaks installation or version check failed.'
        }
        if ((& git rev-parse --is-shallow-repository) -cne 'false' -or $LASTEXITCODE -ne 0) {
            throw 'The checked-out history must be complete.'
        }
        $source = Join-Path $raw 'tracked-source'
        $null = New-Item -ItemType Directory -Path $source
        & git checkout-index --all "--prefix=$source/"
        if ($LASTEXITCODE -ne 0) { throw 'Could not export the tracked checkout for scanning.' }
        $dispositions = $null
        try {
            $dispositions = Read-QaHistoryDispositions "$PSScriptRoot/member-02-gitleaks-dispositions.json"
        } catch {
            Add-Detail '- Historical disposition evidence is missing or invalid.'
        }
        $failed = $false
        foreach ($mode in 'git', 'dir') {
            $report = Join-Path $raw "$mode.json"
            $arguments = @($mode, '--redact=100', '--exit-code=42', '--no-banner', '--no-color',
                '--report-format=json', "--report-path=$report", '--timeout=300')
            if ($mode -eq 'git') { $arguments += @('--log-opts=--full-history --diff-merges=first-parent HEAD', $repo) }
            else { $arguments += $source }
            & $scanner @arguments *> $null
            $code = $LASTEXITCODE
            $result = Get-QaGitleaksResult $mode $code $report $dispositions
            $result | ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath (Join-Path $out "gitleaks-$mode.json") -Encoding utf8
            Add-Detail ("- Gitleaks {0} {1}. Raw findings: {2}. Accepted history: {3}. Unresolved history: {4}. Missing dispositions: {5}. Current source: {6}." -f
                $mode, $result.status, $result.count, $result.acceptedHistory, $result.unresolvedHistory,
                $result.missingDispositions, $result.currentSource)
            $failed = $failed -or ($result.status -ne 'passed')
        }
        & "$PSScriptRoot/Test-Member02GitleaksQa.ps1"
        if ($failed) { throw 'Secret scanning failed. See the redacted reports.' }
    } catch {
        Add-Detail '- Gitleaks gate failed.'
        throw 'Gitleaks verification did not complete successfully.'
    }
}

Push-Location $repo
try {
    $inventory = Get-Content -LiteralPath "$PSScriptRoot/member-02-required-db-tests.json" -Raw | ConvertFrom-Json
    switch ($Phase) {
        'Restore' {
            $sdk = & dotnet --version
            if ($LASTEXITCODE -ne 0) { throw 'Could not select the repository .NET SDK.' }
            Add-Detail "- SDK $sdk, net10.0, $([Runtime.InteropServices.RuntimeInformation]::OSDescription)."
            Invoke-QaRestore $buildTarget 'NuGet audit' 'member02-nuget-packages'
        }
        'Build' { Invoke-CheckedDotnet @('build', $buildTarget, '--configuration', 'Release', '--no-restore',
            '-p:RunAnalyzersDuringBuild=true', '-clp:NoSummary') 'Release build' }
        'Offline' { Invoke-OfflineQa $inventory }
        'ProcessorRestore' { Invoke-QaRestore $processorTests 'Photo processor NuGet audit' 'member02-photo-nuget-packages' }
        'Processor' { Invoke-ProcessorQa }
        'Relational' { Invoke-RelationalQa $inventory }
        'Secrets' { Invoke-GitleaksQa }
        'Summarize' {
            if ($env:QA_NAME -ceq 'member02-postgres') {
                Complete-QaInterruptedPartitions $out $env:QA_RELATIONAL_OUTCOME
                foreach ($partition in 'non-lifecycle', 'lifecycle') {
                    $path = Join-Path $out "relational-$partition.json"
                    if (Test-Path -LiteralPath $path) {
                        $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                        $counts = if ($result.reportAvailable) {
                            "$($result.counts.passed) passed, $($result.counts.failed) failed, $($result.counts.skipped) skipped, $($result.counts.cancelled) cancelled, $($result.missingCount) missing"
                        } else { 'no completed TRX' }
                        Add-Detail "- Relational $partition $($result.outcome) ($counts)."
                    } else { Add-Detail "- Relational $partition evidence unavailable." }
                }
                if ($env:QA_RELATIONAL_OUTCOME -cne 'success') {
                    Add-Detail '- Relational evidence may be incomplete. Missing results confer no acceptance.'
                }
            }
            $body = if (Test-Path -LiteralPath $details) { Get-Content -LiteralPath $details -Raw } else { '- No gate reports available.' }
            Set-Content -LiteralPath $details -Value $body -Encoding utf8
            $delimiter = [guid]::NewGuid().ToString('N')
            Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value (@("details<<$delimiter", $body, $delimiter) -join [Environment]::NewLine)
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $body
        }
    }
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $raw) {
        $resolved = (Resolve-Path -LiteralPath $raw).Path
        $parent = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path.TrimEnd('\', '/')
        if ([IO.Path]::GetDirectoryName($resolved) -eq $parent -and [IO.Path]::GetFileName($resolved) -cmatch '^andril-qa-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
