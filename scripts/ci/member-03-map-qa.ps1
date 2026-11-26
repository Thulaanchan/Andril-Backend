[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ServiceId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $repo 'tests/Andril.IntegrationTests/Andril.IntegrationTests.csproj'
$inventoryPath = Join-Path $repo 'scripts/ci/member-03-map-required-db-tests.json'
$sharedInventoryPath = 'scripts/ci/member-02-required-db-tests.json'
$inventoryTrigger = 'ec2216e543beba548f1e10aa7a135622c7750c73'
$privateMediaIdentity = 'Andril.IntegrationTests.Database.MapAttractionLifecycleMigrationAcceptanceTests.PrivateMediaMigration_EnforcesScopedUniquenessFinalizationAndCleanup'
$approvedDigest = 'sha256:837bed18782820fe967067cb5328b02b0d9992c1be9a37ee0c9c492cfc03af3b'
$approvedPlatformDigest = 'sha256:8d174ddfb292f26b6ec8d97f55d944ccab547fa8bfe9e4fd3c24afdc819ee423'
$runRoot = Join-Path $env:RUNNER_TEMP 'member03-map-qa'
$rawRoot = Join-Path $env:RUNNER_TEMP 'member03-map-qa-private'
$runId = '{0}-{1}' -f $env:GITHUB_RUN_ID, $env:GITHUB_RUN_ATTEMPT
$raw = Join-Path $rawRoot $runId
$safe = Join-Path $runRoot $runId
$summary = Join-Path $safe 'summary.txt'
$fixtureRole = 'andril_m3_map37b_' + [guid]::NewGuid().ToString('N')
$fixturePassword = $null
$firstFailure = $null
$cleanupFailure = $null
$runCode = 0
$roleCreated = $false
$serviceReady = $false

if ($env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_REF -cne 'refs/heads/Member-3') {
    throw 'M3 Map verification may run only in the authorized Member-3 GitHub Actions job.'
}
if ($ServiceId -notmatch '^[A-Fa-f0-9-]{20,64}$') { throw 'Invalid disposable PostgreSQL service identifier.' }
if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { throw 'GitHub runner temporary directory is unavailable.' }

$null = New-Item -ItemType Directory -Path $safe -Force
$null = New-Item -ItemType Directory -Path $raw -Force

function Add-Summary([string] $Text) {
    Add-Content -LiteralPath $summary -Value $Text -Encoding utf8
    Write-Host $Text
}

function Protect-Text([string] $Text) {
    if ($fixturePassword) { $Text = $Text.Replace($fixturePassword, '[REDACTED]') }
    $Text = $Text -replace '(?i)(Password\s*=\s*)[^;\s]+', '$1[REDACTED]'
    $Text = $Text.Replace('disposable_ci_postgres', '[REDACTED]')
    return $Text
}

function Save-SafeLog([string] $Source, [string] $Name) {
    $destination = Join-Path $safe $Name
    $content = if (Test-Path -LiteralPath $Source -PathType Leaf) {
        Get-Content -LiteralPath $Source -Raw
    } else { '' }
    [IO.File]::WriteAllText($destination, (Protect-Text $content), [Text.UTF8Encoding]::new($false))
}

function Invoke-Native([string] $Label, [string] $Executable, [string[]] $Arguments, [switch] $AllowFailure) {
    $stdout = Join-Path $raw "$Label.stdout.log"
    $stderr = Join-Path $raw "$Label.stderr.log"
    & $Executable @Arguments 1> $stdout 2> $stderr
    $code = $LASTEXITCODE
    Set-Content -LiteralPath (Join-Path $safe "$Label.exitcode") -Value $code -NoNewline
    Save-SafeLog $stdout "$Label.stdout.log"
    Save-SafeLog $stderr "$Label.stderr.log"
    Add-Summary "$Label EXIT_CODE=$code"
    if ($code -ne 0 -and -not $AllowFailure) { throw "$Label failed with exit code $code." }
    return $stdout
}

function Invoke-ServiceSql([string] $Label, [string] $Sql, [switch] $AllowFailure) {
    $args = @('exec', '--user', 'postgres', $ServiceId, 'psql', '-X', '-v', 'ON_ERROR_STOP=1', '-U', 'postgres', '-d', 'postgres', '-A', '-t', '-c', $Sql)
    return Invoke-Native $Label 'docker' $args -AllowFailure:$AllowFailure
}

function Confirm-ServiceImage {
    if ($env:RUNNER_OS -cne 'Linux' -or $env:RUNNER_ARCH -cne 'X64') {
        throw 'The approved PostgreSQL service requires a Linux/amd64 runner.'
    }
    $repository = 'docker.io/library/postgres'
    $indexRef = "$repository@${approvedDigest}"
    $platformRef = "$repository@${approvedPlatformDigest}"
    $indexFile = Invoke-Native 'postgres-image-index' 'docker' @('buildx', 'imagetools', 'inspect', '--raw', $indexRef)
    $index = Get-Content -LiteralPath $indexFile -Raw | ConvertFrom-Json
    $matching = @($index.manifests | Where-Object {
        $_.platform.os -ceq 'linux' -and $_.platform.architecture -ceq 'amd64'
    })
    if ($matching.Count -ne 1 -or $matching[0].digest -cne $approvedPlatformDigest) {
        throw 'The approved image index does not resolve to the expected Linux/amd64 manifest.'
    }
    $manifestFile = Invoke-Native 'postgres-platform-manifest' 'docker' @('buildx', 'imagetools', 'inspect', '--raw', $platformRef)
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    $containerFile = Invoke-Native 'postgres-container-image' 'docker' @('inspect', '--type', 'container', '--format', '{{.Image}}', $ServiceId)
    $actualId = (Get-Content -LiteralPath $containerFile -Raw).Trim()
    if ($actualId -cnotmatch '^sha256:[a-f0-9]{64}$' -or $actualId -cne $manifest.config.digest) {
        throw 'The running PostgreSQL container does not use the approved platform image configuration.'
    }
    $imageFile = Invoke-Native 'postgres-image-platform' 'docker' @('image', 'inspect', '--format', '{{.Os}}/{{.Architecture}}', $actualId)
    if ((Get-Content -LiteralPath $imageFile -Raw).Trim() -cne 'linux/amd64') {
        throw 'Docker does not report Linux/amd64 for the running PostgreSQL image.'
    }
    Add-Summary "POSTGRES_IMAGE_INDEX=$approvedDigest; PLATFORM=linux/amd64; PLATFORM_MANIFEST=$approvedPlatformDigest; IMAGE_CONFIG=$actualId"
}

function Get-Identities([string] $Path) {
    return ,([string[]] (Get-Content -LiteralPath $Path | ForEach-Object {
        if ($_ -match '^\s+(Andril\.IntegrationTests\.[^\r\n]+)$') { $Matches[1].Trim() }
    }))
}

function Get-ExpectedEntries([string[]] $Identities) {
    $groups = @($Identities | Group-Object { ($_ -split '\(', 2)[0] })
    return ,([object[]] @($groups | ForEach-Object {
        [pscustomobject] @{ method = $_.Name; cases = [string[]] $_.Group }
    }))
}

function Save-IdentityList([string] $Name, [string[]] $Items) {
    Set-Content -LiteralPath (Join-Path $safe $Name) -Value $Items -Encoding utf8
}

function Verify-Trx([string] $Label, [string] $TrxPath, [string[]] $Expected, [int] $ExpectedLiveDatabaseCount) {
    . (Join-Path $PSScriptRoot 'Verify-Member02TestResults.ps1')
    $report = Read-QaTrx $TrxPath
    Export-QaTrx $report (Join-Path $safe "$Label.trx")
    $expectedCases = Get-QaExpectedCases (Get-ExpectedEntries $Expected)
    $issues = @(Test-QaExecutions $report $expectedCases Relational)
    $failed = @($report.Tests | Where-Object { $_.Outcome -cin @('Failed','Error','Timeout','Aborted','Inconclusive','NotRunnable','Disconnected','Warning','Completed','InProgress','Pending') }).Count
    $cancelled = @($report.Tests | Where-Object { $_.Outcome -cin @('Aborted','InProgress','Pending') }).Count
    if ($report.Total -ne $Expected.Count -or $report.Passed -ne $Expected.Count -or
        $report.Skipped -ne 0 -or $report.Unsuccessful -ne 0 -or $failed -ne 0 -or
        $cancelled -ne 0 -or $issues.Count -ne 0) {
        $failurePath = Join-Path $safe "$Label-failures.json"
        Export-QaFailureDiagnostics $report $failurePath
        Save-IdentityList "$Label-inventory-issues.txt" $issues
        Add-Summary "$Label TOTAL=$($report.Total) PASSED=$($report.Passed) FAILED=$failed SKIPPED=$($report.Skipped) INVENTORY_ISSUES=$($issues.Count)"
        throw "$Label TRX identity or outcome reconciliation failed."
    }
    $actual = [string[]] @($report.Tests | ForEach-Object { $_.Display })
    Save-IdentityList "$Label-expected-identities.txt" $Expected
    Save-IdentityList "$Label-executed-identities.txt" $actual
    Add-Summary "$Label EXPECTED=$($Expected.Count) PASSED=$($report.Passed) FAILED=$failed SKIPPED=$($report.Skipped) CANCELLED=$cancelled UNEXPECTED=0"
    Add-Summary "$Label LIVE_DATABASE_IDENTITIES=$ExpectedLiveDatabaseCount"
}

function Invoke-TestPartition([string] $Label, [string] $Filter, [string[]] $Expected, [int] $LiveDatabaseCount) {
    $results = Join-Path $raw "$Label-results"
    $null = New-Item -ItemType Directory -Path $results -Force
    $trxName = "$Label.trx"
    $args = @('test', $project, '--configuration', 'Release', '--no-build', '--no-restore',
        '--filter', $Filter, '--logger', "trx;LogFileName=$trxName", '--results-directory', $results,
        '--blame-hang-timeout', '8m', '--blame-hang-dump-type', 'none')
    # Enter only the verified service's network namespace. Drop elevation before
    # running tests, which must connect to PostgreSQL's actual loopback listener.
    $namespaceArgs = @('-n', '--preserve-env=ANDRIL_M3_TEST_POSTGRES,HOME,DOTNET_ROOT',
        'nsenter', '--target', $servicePid, '--net', '--',
        'setpriv', "--reuid=$runnerUid", "--regid=$runnerGid", '--clear-groups',
        '--inh-caps=-all', '--ambient-caps=-all', '--bounding-set=-all',
        '--no-new-privs', '--', 'env', 'LANG=en_US.UTF-8', 'LC_ALL=en_US.UTF-8', $dotnetPath)
    $null = Invoke-Native "$Label-tests" 'sudo' ($namespaceArgs + $args) -AllowFailure
    $exitCode = [int] (Get-Content -LiteralPath (Join-Path $safe "$Label-tests.exitcode") -Raw)
    $trx = Join-Path $results $trxName
    if (!(Test-Path -LiteralPath $trx -PathType Leaf)) { throw "$Label did not produce TRX evidence." }
    Verify-Trx $Label $trx $Expected $LiveDatabaseCount
    if ($exitCode -ne 0) { throw "$Label dotnet test failed with exit code $exitCode." }
}

function Verify-InventoryAndM2([object] $Inventory) {
    if ($Inventory.lifecycleCases.Count -ne 42 -or $Inventory.lifecycleDatabase.Count -ne 37 -or
        $Inventory.lifecycleOffline.Count -ne 5 -or $Inventory.remainingMap.Count -ne 148 -or
        $Inventory.expected.livePostgreSql -ne 185 -or $Inventory.expected.offline -ne 5 -or
        $Inventory.expected.total -ne 190 -or $Inventory.socialTestsIncluded -ne $false) {
        throw 'M3 identity inventory does not match the approved 37/5/148 partition counts.'
    }
    $all = @($Inventory.lifecycleCases) + @($Inventory.remainingMap)
    if (@($all | Sort-Object -Unique).Count -ne 190 -or @($all | Where-Object { $_ -match 'Social' }).Count) {
        throw 'M3 identity inventory contains duplicates or Social tests.'
    }
    if ($privateMediaIdentity -cnotin $Inventory.lifecycleDatabase -or
        @($Inventory.lifecycleDatabase | Where-Object { $_ -notin $Inventory.lifecycleCases }).Count -or
        @($Inventory.lifecycleOffline | Where-Object { $_ -notin $Inventory.lifecycleCases }).Count -or
        @($Inventory.lifecycleDatabase | Where-Object { $_ -in $Inventory.lifecycleOffline }).Count) {
        throw 'Lifecycle database/offline identity partitions are inconsistent.'
    }

    $m2File = Join-Path $repo $sharedInventoryPath
    $currentM2 = Get-Content -LiteralPath $m2File -Raw | ConvertFrom-Json
    if ($currentM2.requiredM2.Count -ne 88) { throw 'M2 required identity count changed.' }
    & git merge-base --is-ancestor $inventoryTrigger HEAD
    if ($LASTEXITCODE -ne 0) { throw 'The approved inventory trigger is not in the checkout history.' }
    $triggerRaw = @(& git show "${inventoryTrigger}:$sharedInventoryPath")
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the trigger M2 inventory.' }
    $triggerM2 = ($triggerRaw -join "`n") | ConvertFrom-Json
    $expectedCurrentSkips = @($triggerM2.offlineOnlySkips) + $privateMediaIdentity
    if (($currentM2.requiredM2 -join "`n") -cne ($triggerM2.requiredM2 -join "`n") -or
        ($currentM2.offlineOnlySkips -join "`n") -cne ($expectedCurrentSkips -join "`n")) {
        throw 'The checkout differs from the approved trigger inventory plus the R05 private-media identity.'
    }
    $parentRaw = @(& git show "${inventoryTrigger}^:$sharedInventoryPath")
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the inventory trigger parent.' }
    $parentM2 = ($parentRaw -join "`n") | ConvertFrom-Json
    $oldIds = [string[]] $parentM2.requiredM2
    $newIds = [string[]] $currentM2.requiredM2
    if ($oldIds.Count -ne 88 -or $newIds.Count -ne 88) { throw 'M2 required identity count differs from 88.' }
    for ($i = 0; $i -lt 88; $i++) { if ($oldIds[$i] -cne $newIds[$i]) { throw "M2 required identity order differs at index $i." } }

    $approvedAdditions = [string[]] @(
        'Andril.IntegrationTests.Database.PostgresSocialQuestionnaireReadAdapterTests.LivePostgres_GetLatestByProfileIdAsync_ReturnsMostRecentResponse',
        'Andril.IntegrationTests.Database.PostgresSocialQuestionnaireReadAdapterTests.LivePostgres_ListByProfileIdAsync_ReturnsChronologicalDescendingResponsesUpToMaxItems',
        'Andril.IntegrationTests.Database.MapAttractionLifecycleMigrationAcceptanceTests.MigratedSchema_PreservesAttractionsAndRequiresExplicitStateWithIntegrity'
    )
    $oldSkips = [string[]] $parentM2.offlineOnlySkips
    $newSkips = [string[]] $triggerM2.offlineOnlySkips
    $added = @($newSkips | Where-Object { $_ -notin $oldSkips })
    $removed = @($oldSkips | Where-Object { $_ -notin $newSkips })
    if ($added.Count -ne 3 -or $removed.Count -ne 0 -or
        @($approvedAdditions | Where-Object { $_ -notin $added }).Count -ne 0) {
        throw 'Shared M2 offline skip inventory differs from its three approved identities.'
    }
    Add-Summary 'M2_REQUIRED_IDENTITIES=88; ORDER_PRESERVED=True'
    Add-Summary 'SHARED_OFFLINE_SKIP_ADDITIONS=3; EXACT_IDENTITIES_VERIFIED=True'
    Add-Summary 'R05_SHARED_OFFLINE_SKIP_ADDITIONS=1; EXACT_IDENTITY_VERIFIED=True'
}

function Invoke-Cleanup {
    if (!$serviceReady) { Add-Summary 'CLEANUP_SERVICE_READY=False'; return }
    try {
        $dbFile = Invoke-ServiceSql 'cleanup-list-databases' "SELECT datname FROM pg_database WHERE datname NOT IN ('postgres','template0','template1') ORDER BY datname"
        $databases = @(Get-Content -LiteralPath $dbFile | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $n = 0
        foreach ($database in $databases) {
            if ($database -notmatch '^[A-Za-z0-9_]+$') { throw 'Unexpected database identifier; cleanup stopped safely.' }
            $n++
            $quoted = '"' + $database.Replace('"', '""') + '"'
            $null = Invoke-ServiceSql "cleanup-drop-database-$n" "DROP DATABASE IF EXISTS $quoted WITH (FORCE)"
        }
        $roleFile = Invoke-ServiceSql 'cleanup-list-roles' "SELECT rolname FROM pg_roles WHERE rolname <> 'postgres' AND rolname !~ '^pg_' ORDER BY rolname"
        $roles = @(Get-Content -LiteralPath $roleFile | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $n = 0
        foreach ($role in $roles) {
            if ($role -notmatch '^[A-Za-z0-9_]+$') { throw 'Unexpected role identifier; cleanup stopped safely.' }
            $n++
            $quoted = '"' + $role.Replace('"', '""') + '"'
            $null = Invoke-ServiceSql "cleanup-drop-owned-$n" "DROP OWNED BY $quoted"
            $null = Invoke-ServiceSql "cleanup-drop-role-$n" "DROP ROLE $quoted"
        }
        $dbRemainFile = Invoke-ServiceSql 'cleanup-verify-databases' "SELECT count(*) FROM pg_database WHERE datname NOT IN ('postgres','template0','template1')"
        $roleRemainFile = Invoke-ServiceSql 'cleanup-verify-roles' "SELECT count(*) FROM pg_roles WHERE rolname <> 'postgres' AND rolname !~ '^pg_'"
        $dbRemain = (Get-Content -LiteralPath $dbRemainFile -Raw).Trim()
        $roleRemain = (Get-Content -LiteralPath $roleRemainFile -Raw).Trim()
        Add-Summary "CLEANUP_REMAINING_DATABASES=$dbRemain; ROLES=$roleRemain"
        if ($dbRemain -cne '0' -or $roleRemain -cne '0') { throw 'Disposable database/role cleanup verification was nonzero.' }
        $script:roleCreated = $false
    } catch {
        $script:cleanupFailure = 'Disposable service cleanup or catalog verification failed.'
        Add-Summary 'CLEANUP_VERIFICATION=FAIL'
    }
}

try {
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -cne $env:GITHUB_SHA) { throw 'Checkout SHA differs from the triggered GitHub Actions commit.' }
    Add-Summary "COMMIT=$commit"
    Confirm-ServiceImage
    $serviceReady = $true
    $inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
    Verify-InventoryAndM2 $inventory
    $lifecycleFilter = 'FullyQualifiedName~MapAttractionLifecycleMigrationAcceptanceTests|FullyQualifiedName~MapAttractionLifecyclePersistenceTests|FullyQualifiedName~MapAttractionLifecycleTransactionPrototypeTests'
    $mapFilter = '(FullyQualifiedName~Andril.IntegrationTests.Database.Map|FullyQualifiedName~Andril.IntegrationTests.Database.HobbiesMap)&FullyQualifiedName!~MapAttractionLifecycle'
    Save-IdentityList 'lifecycle-expected-identities.txt' ([string[]] $inventory.lifecycleCases)
    Save-IdentityList 'remaining-map-expected-identities.txt' ([string[]] $inventory.remainingMap)

    $packages = Join-Path $env:RUNNER_TEMP 'member03-map-packages'
    $null = Invoke-Native 'restore' 'dotnet' @('restore', $project, '--configfile', (Join-Path $repo 'scripts/ci/NuGet.Member02.config'),
        '--packages', $packages, '--no-http-cache', '--force', '-p:NuGetAudit=true', '-p:NuGetAuditMode=all',
        '-p:NuGetAuditLevel=low', '-warnAsError:NU1900,NU1903,NU1904,NU1905', '-clp:NoSummary')
    $null = Invoke-Native 'release-build' 'dotnet' @('build', $project, '--configuration', 'Release', '--no-restore', '-clp:NoSummary')

    $probeFile = Invoke-ServiceSql 'verify-postgres-service' "SELECT current_setting('server_version_num') || '|' || current_setting('server_encoding')"
    $server = (Get-Content -LiteralPath $probeFile -Raw).Trim()
    if ($server -cne '170012|UTF8') { throw 'Disposable PostgreSQL server identity/version/encoding check failed.' }
    Add-Summary "POSTGRES_SERVICE=$server"

    $fixturePassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $createRoleSql = "CREATE ROLE $fixtureRole LOGIN CREATEDB CREATEROLE NOSUPERUSER NOBYPASSRLS PASSWORD '$fixturePassword';"
    $createOut = Join-Path $raw 'create-fixture-role.stdout.log'
    $createErr = Join-Path $raw 'create-fixture-role.stderr.log'
    $createRoleSql | & docker exec -i --user postgres $ServiceId psql -X -v ON_ERROR_STOP=1 -U postgres -d postgres 1> $createOut 2> $createErr
    $createCode = $LASTEXITCODE
    Set-Content -LiteralPath (Join-Path $safe 'create-fixture-role.exitcode') -Value $createCode -NoNewline
    Save-SafeLog $createOut 'create-fixture-role.stdout.log'
    Save-SafeLog $createErr 'create-fixture-role.stderr.log'
    if ($createCode -ne 0) { throw 'Could not provision the restricted disposable fixture login.' }
    $roleCreated = $true
    $flagsFile = Invoke-ServiceSql 'verify-fixture-role-flags' "SELECT rolname||'|'||rolcreatedb||'|'||rolcreaterole||'|'||rolsuper||'|'||rolbypassrls FROM pg_roles WHERE rolname='$fixtureRole'"
    $flags = (Get-Content -LiteralPath $flagsFile -Raw).Trim()
    if ($flags -cne "$fixtureRole|true|true|false|false") { throw 'Fixture login privilege verification failed.' }
    Add-Summary 'FIXTURE_ROLE=CREATEDB,CREATEROLE,NOSUPERUSER,NOBYPASSRLS'

    $pidFile = Invoke-Native 'postgres-network-namespace' 'docker' @(
        'inspect', '--type', 'container', '--format', '{{.State.Pid}}', $ServiceId)
    $servicePid = (Get-Content -LiteralPath $pidFile -Raw).Trim()
    $runnerUid = ((& id -u) -join '').Trim()
    $runnerGid = ((& id -g) -join '').Trim()
    if ($servicePid -cnotmatch '^[1-9][0-9]*$' -or $runnerUid -cnotmatch '^[1-9][0-9]*$' -or
        $runnerGid -cnotmatch '^[1-9][0-9]*$') {
        throw 'The PostgreSQL namespace or unprivileged runner identity is invalid.'
    }
    $dotnetPath = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    Add-Summary 'POSTGRES_TEST_ENDPOINT=127.0.0.1:5432; TEST_PROCESS=unprivileged runner in service network namespace'
    $env:ANDRIL_M3_TEST_POSTGRES = "Host=127.0.0.1;Port=5432;Database=postgres;Username=$fixtureRole;Password=$fixturePassword;SSL Mode=Disable;Pooling=false;Include Error Detail=false;Timeout=10;Command Timeout=15"
    Remove-Item Env:ANDRIL_M2_TEST_POSTGRES, Env:ANDRIL_M2_TEST_POSTGRES_ALLOW_ROLES -ErrorAction SilentlyContinue
    Invoke-TestPartition 'lifecycle' $lifecycleFilter ([string[]] $inventory.lifecycleCases) 37
    Invoke-TestPartition 'remaining-map' $mapFilter ([string[]] $inventory.remainingMap) 148
    Add-Summary 'TOTAL_EXPECTED=190; LIVE_DATABASE=185; OFFLINE=5; ALL_PASSED=True'
} catch {
    $runCode = 1
    $firstFailure = $_.Exception.Message
    Add-Summary 'FIRST_FAILURE=Recorded; subsequent test partition will not run.'
    Add-Summary ("FAILURE_DETAIL=" + (Protect-Text $firstFailure))
    Write-Error 'MAP-37B CI verification failed; see sanitized summary and artifacts.' -ErrorAction Continue
} finally {
    Remove-Item Env:ANDRIL_M3_TEST_POSTGRES, Env:ANDRIL_M2_TEST_POSTGRES,
        Env:ANDRIL_M2_TEST_POSTGRES_ALLOW_ROLES, Env:PGPASSWORD -ErrorAction SilentlyContinue
    Invoke-Cleanup
    if ($fixturePassword) { $fixturePassword = $null }
    if ($cleanupFailure) { $runCode = 1 }
    if ($firstFailure) { Add-Summary 'OVERALL=FAILED' }
    elseif ($cleanupFailure) { Add-Summary 'OVERALL=FAILED_CLEANUP' }
    else { Add-Summary 'OVERALL=PASS' }
    Set-Content -LiteralPath (Join-Path $safe 'overall.exitcode') -Value $runCode -NoNewline
}

if ($runCode -ne 0) { exit $runCode }
