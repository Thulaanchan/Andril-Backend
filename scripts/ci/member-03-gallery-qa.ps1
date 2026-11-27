param([Parameter(Mandatory)][string]$ServiceId)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_OS -cne 'Linux' -or $env:RUNNER_ARCH -cne 'X64' -or
    $ServiceId -cnotmatch '^[a-f0-9]{64}$') { throw 'Requires the dedicated Linux/amd64 Gallery Actions service.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$out = Join-Path $env:RUNNER_TEMP 'member03-gallery-qa'
$raw = Join-Path $env:RUNNER_TEMP ('member03-gallery-private-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $out, $raw
$utf8 = [Text.UTF8Encoding]::new($false,$true)
. (Join-Path $PSScriptRoot 'Verify-Member02TestResults.ps1')
$summary = [ordered]@{ accepted=$false; stage='checkout' }
$role = 'andril_m3_gallery_ci_' + [guid]::NewGuid().ToString('N')
$serviceVerified = $false
$roleCreated = $false
$password = $null
$baseline = $null
$project = Join-Path $repo 'tests/Andril.IntegrationTests/Andril.IntegrationTests.csproj'
$indexDigest = 'sha256:837bed18782820fe967067cb5328b02b0d9992c1be9a37ee0c9c492cfc03af3b'
$platformDigest = 'sha256:8d174ddfb292f26b6ec8d97f55d944ccab547fa8bfe9e4fd3c24afdc819ee423'

function Run-Checked([string]$Command, [string[]]$Arguments) {
    $lines = @(& $Command @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "A tool failed during $($summary.stage)." }
    $lines
}

function Service-Sql([string]$Sql) {
    $rows = @($Sql | & docker exec -i --user postgres $ServiceId psql -X -qAt -v ON_ERROR_STOP=1 -U postgres -d postgres 2>$null)
    if ($LASTEXITCODE -ne 0) { throw "A service probe failed during $($summary.stage)." }
    $rows
}

function Read-Catalog {
    Service-Sql "SELECT 'database:'||datname FROM pg_database UNION ALL SELECT 'role:'||rolname FROM pg_roles ORDER BY 1"
}

function Confirm-Checkout {
    $head = Run-Checked git @('-C',$repo,'rev-parse','HEAD')
    $tree = Run-Checked git @('-C',$repo,'rev-parse','HEAD^{tree}')
    if ($head -cne $env:GITHUB_SHA -or $head -cnotmatch '^[a-f0-9]{40}$' -or $tree -cnotmatch '^[a-f0-9]{40}$') { throw 'Wrong checkout.' }
    if ($env:GITHUB_EVENT_NAME -ceq 'push') {
        if ($env:GITHUB_REF -cne 'refs/heads/Member-3') { throw 'Wrong candidate ref.' }
    } elseif ($env:GITHUB_EVENT_NAME -ceq 'pull_request') {
        $event = Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json
        if ($event.pull_request.head.repo.full_name -cne $env:GITHUB_REPOSITORY -or $event.pull_request.head.ref -cne 'Member-3' -or
            $event.pull_request.base.ref -cne 'Develop' -or $env:GITHUB_REF -cnotmatch '^refs/pull/[1-9][0-9]*/merge$') {
            throw 'Wrong combined-tree ref.'
        }
        $parents = (Run-Checked git @('-C',$repo,'show','-s','--format=%P','HEAD')) -csplit ' '
        if ($parents.Count -ne 2 -or $parents[0] -cne $event.pull_request.base.sha -or $parents[1] -cne $event.pull_request.head.sha) {
            throw 'Combined tree parents differ from the event.'
        }
        $summary['base'] = $parents[0]
        $summary['candidate'] = $parents[1]
    } else { throw 'Unsupported event.' }
    if (@(Run-Checked git @('-C',$repo,'status','--porcelain')).Count) { throw 'Checkout contains changes.' }
    $summary['commit'] = $head
    $summary['tree'] = $tree
}

function Confirm-Service {
    $image = "docker.io/library/postgres@"
    $index = (Run-Checked docker @('buildx','imagetools','inspect','--raw',($image+$indexDigest)) | Out-String) | ConvertFrom-Json
    $linux = @($index.manifests | Where-Object { $_.platform.os -ceq 'linux' -and $_.platform.architecture -ceq 'amd64' })
    if ($linux.Count -ne 1 -or $linux[0].digest -cne $platformDigest) { throw 'Wrong PostgreSQL platform manifest.' }
    $platform = (Run-Checked docker @('buildx','imagetools','inspect','--raw',($image+$platformDigest)) | Out-String) | ConvertFrom-Json
    $running = Run-Checked docker @('inspect','--type','container','--format','{{.Image}}',$ServiceId)
    if ($running -cnotmatch '^sha256:[a-f0-9]{64}$' -or $running -cne $platform.config.digest -or
        (Run-Checked docker @('image','inspect','--format','{{.Os}}/{{.Architecture}}',$running)) -cne 'linux/amd64') {
        throw 'Running service image differs from the approved PostgreSQL image.'
    }
    if ((Service-Sql "SELECT current_setting('server_version_num')||'|'||current_setting('server_encoding')") -cne '170012|UTF8') {
        throw 'Incorrect service version or encoding.'
    }
    $summary['postgres'] = [ordered]@{ server='17.12 UTF8 linux/amd64'; imageIndex=$indexDigest; platform=$platformDigest; config=$running }
}

$trxNamespace = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
function Read-Trx([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 16MB
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $doc = [Xml.XmlDocument]::new()
        $doc.XmlResolver = $null
        $doc.Load($reader)
        return ,$doc
    } finally { $reader.Dispose() }
}
function Get-Ns([xml]$Document) {
    $ns = [Xml.XmlNamespaceManager]::new($Document.NameTable)
    $ns.AddNamespace('t', $trxNamespace)
    return ,$ns
}
$guidPattern = '^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$'
$namePattern = '^[A-Za-z_]\w*(\.[A-Za-z_]\w*)+$'
$timePattern = '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$'
$outcome = '^(Passed|Failed|NotExecuted|Aborted|Error|Timeout|Inconclusive|NotRunnable|Disconnected|Warning|Completed|InProgress|Pending|PassedButRunAborted|Cancelled|Canceled)$'
$counterNames = 'total','executed','passed','failed','error','timeout','aborted','inconclusive','passedButRunAborted','notRunnable','notExecuted','disconnected','warning','completed','inProgress','pending','cancelled','canceled','skipped'
$counterRules = @{}
foreach ($key in $counterNames) { $counterRules[$key] = '^\d{1,9}$' }
$rules = @{
    TestRun=@{ id=$guidPattern }
    Times=@{ creation=$timePattern; queuing=$timePattern; start=$timePattern; finish=$timePattern }
    Results=@{}
    UnitTestResult=@{ executionId=$guidPattern; testId=$guidPattern; testName=$namePattern; outcome=$outcome;
        startTime=$timePattern; endTime=$timePattern; duration='^(\d+\.)?\d{2}:\d{2}:\d{2}(\.\d{1,7})?$';
        testListId=$guidPattern; testType=$guidPattern }
    TestDefinitions=@{}; UnitTest=@{ id=$guidPattern; name=$namePattern }
    TestMethod=@{ className=$namePattern; name='^[A-Za-z_]\w*$' }
    Execution=@{ id=$guidPattern }
    TestEntries=@{}; TestEntry=@{ testId=$guidPattern; executionId=$guidPattern; testListId=$guidPattern }
    ResultSummary=@{ outcome=$outcome }; Counters=$counterRules
}
$children = @{
    TestRun=@('Times','Results','TestDefinitions','TestEntries','ResultSummary')
    Results=@('UnitTestResult'); TestDefinitions=@('UnitTest'); UnitTest=@('Execution','TestMethod')
    TestEntries=@('TestEntry'); ResultSummary=@('Counters')
}
function Copy-TrxNode([Xml.XmlElement]$Node, [xml]$Destination) {
    $tag = $Node.LocalName
    $copy = $Destination.CreateElement($tag, $trxNamespace)
    foreach ($attribute in $Node.Attributes) {
        $key = $attribute.Name
        if ($rules[$tag].ContainsKey($key)) {
            if ($attribute.Value -cnotmatch $rules[$tag][$key]) { throw 'TRX contains an invalid evidence field.' }
            $copy.SetAttribute($key, $attribute.Value)
        }
    }
    foreach ($child in $Node.ChildNodes) {
        if ($child -is [Xml.XmlElement] -and $child.NamespaceURI -ceq $trxNamespace -and
            $children[$tag] -ccontains $child.LocalName) {
            $null = $copy.AppendChild((Copy-TrxNode $child $Destination))
        }
    }
    return ,$copy
}
function Save-SanitizedTrx([string]$InputPath, [string]$OutputPath) {
    $original = Read-Trx $InputPath
    $originalRoot = $original.SelectSingleNode('/t:TestRun', (Get-Ns $original))
    if (!$originalRoot) { throw 'The original TRX format is unsupported.' }
    $clean = [Xml.XmlDocument]::new()
    $null = $clean.AppendChild((Copy-TrxNode $originalRoot $clean))
    $serialized = $clean.OuterXml
    if ($serialized -match '(?i)(password\s*=|pwd\s*=|host\s*=|connectionstring|PGPASSWORD|ANDRIL_M[23]_TEST_POSTGRES|-----BEGIN |[A-Z]:\\)') {
        throw 'Confidentiality validation rejected the sanitized TRX.'
    }
    $password = $script:password
    if ($password -and $password.Length -ge 8 -and $serialized.Contains($password)) {
        throw 'Confidentiality validation found fixture credentials in the sanitized TRX.'
    }
    [IO.File]::WriteAllText($OutputPath, $serialized, $utf8)
    if ([IO.File]::ReadAllText($OutputPath, $utf8) -cne $serialized) { throw 'Sanitized TRX changed on disk.' }
    return ,(Read-Trx $OutputPath)
}
function Verify-Results([xml]$Document, [string[]]$Targets) {
    $ns = Get-Ns $Document
    $defs = @{}
    $entries = @{}
    $ids = [Collections.Generic.HashSet[guid]]::new()
    foreach ($def in $Document.SelectNodes('/t:TestRun/t:TestDefinitions/t:UnitTest', $ns)) {
        $method = $def.SelectSingleNode('t:TestMethod', $ns)
        $execution = $def.SelectSingleNode('t:Execution', $ns)
        $id = $def.GetAttribute('id')
        if ($id -cnotmatch $guidPattern -or !$method -or !$execution -or $defs.ContainsKey($id)) { throw 'Invalid or duplicate TRX definition.' }
        $defs[$id] = @(($method.GetAttribute('className') + '.' + $method.GetAttribute('name')), $execution.GetAttribute('id'))
    }
    foreach ($entry in $Document.SelectNodes('/t:TestRun/t:TestEntries/t:TestEntry', $ns)) {
        $id = $entry.GetAttribute('executionId')
        if ($id -cnotmatch $guidPattern -or $entries.ContainsKey($id)) { throw 'Invalid or duplicate TRX entry.' }
        $entries[$id] = $entry.GetAttribute('testId')
    }
    $rows = @($Document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns))
    $passed = 0
    $skipped = 0
    $actual = @(foreach ($result in $rows) {
        $id = $result.GetAttribute('testId')
        $executionId = $result.GetAttribute('executionId')
        $state = $result.GetAttribute('outcome')
        if ($state -ceq 'Passed') { $passed++ } elseif ($state -ceq 'NotExecuted') { $skipped++ }
        if (!$defs.ContainsKey($id) -or !$entries.ContainsKey($executionId) -or $entries[$executionId] -cne $id -or
            $defs[$id][1] -cne $executionId -or !$ids.Add([guid]$executionId) -or [guid]$executionId -eq [guid]::Empty -or
            $result.GetAttribute('testName') -cne $defs[$id][0] -or !$result.GetAttribute('duration') -or
            !$result.GetAttribute('startTime') -or !$result.GetAttribute('endTime')) {
            throw 'TRX execution association, identity, or timing is invalid.'
        }
        if ($state -cne 'Passed' -and $Targets -ccontains $defs[$id][0]) { Write-Host "  $state $($defs[$id][0])" }
        $defs[$id][0]
    })
    $failed = $rows.Count - $passed - $skipped
    Write-Host "TRX outcomes: total $($rows.Count), passed $passed, failed/other $failed, skipped $skipped."
    $actual | Sort-Object -CaseSensitive | Set-Content -LiteralPath (Join-Path $out 'executed-identities.txt') -Encoding UTF8
    $resultSummary = $Document.SelectSingleNode('/t:TestRun/t:ResultSummary', $ns)
    $counters = if ($resultSummary) { $resultSummary.SelectSingleNode('t:Counters', $ns) }
    $mismatches = if ($actual.Count) { @(Compare-Object $Targets $actual -CaseSensitive).Count } else { $Targets.Count }
    $summary['total'] = $rows.Count
    $summary['passed'] = $passed
    $summary['failed'] = $failed
    $summary['skipped'] = $skipped
    $summary['uniqueExecutionIds'] = $ids.Count
    $summary['identityMismatches'] = $mismatches
    if (!$counters -or $resultSummary.GetAttribute('outcome') -cnotin 'Passed','Completed' -or
        $defs.Count -ne 82 -or $entries.Count -ne 82 -or $ids.Count -ne 82 -or
        $rows.Count -ne 82 -or $passed -ne 82 -or $failed -or $skipped -or $mismatches) {
        throw 'Gallery identity, result summary, or outcome reconciliation failed.'
    }
    foreach ($key in 'total','executed','passed') {
        if ($counters.GetAttribute($key) -cne '82') { throw "Unexpected TRX counter: $key." }
    }
    foreach ($key in $counterNames | Where-Object { $_ -cnotin 'total','executed','passed' }) {
        if ($counters.HasAttribute($key) -and $counters.GetAttribute($key) -cne '0') { throw "Unexpected TRX counter: $key." }
    }
    foreach ($key in 'failed','aborted','notExecuted') {
        if (!$counters.HasAttribute($key)) { throw "Missing TRX counter: $key." }
    }
    Write-Host 'Verified all 82 identities and unique execution IDs with zero mismatches.'
}


try {
    Confirm-Checkout
    $summary.stage = 'inventory'
    $inventory = Join-Path $PSScriptRoot 'member-03-gallery-required-db-tests.txt'
    $targets = [IO.File]::ReadAllLines($inventory,$utf8)
    $summary['inventorySha256'] = (Get-FileHash -LiteralPath $inventory).Hash
    if ($targets.Count -ne 82 -or @($targets | Sort-Object -Unique).Count -ne 82 -or
        $summary.inventorySha256 -cne '4A74B033879461327E2BFB1E9EC402E457274DE0EA813AC7CBC5FD4C13477C92' -or
        @($targets | Where-Object { $_ -cnotmatch '^Andril\.IntegrationTests\.Database\.GalleryPersistenceTests\.[A-Za-z_]\w*$' }).Count) {
        throw 'Gallery inventory differs from the approved candidate proposal.'
    }
    Copy-Item -LiteralPath $inventory -Destination (Join-Path $out 'expected-identities.txt')
    $summary.stage = 'restore'
    $null = Run-Checked dotnet @('restore',$project,'--configfile',(Join-Path $PSScriptRoot 'NuGet.Member02.config'),
        '--packages',(Join-Path $raw 'packages'),'--no-http-cache','--force','-p:NuGetAudit=true','-p:NuGetAuditMode=all',
        '-p:NuGetAuditLevel=low','-warnAsError:NU1900,NU1903,NU1904,NU1905')
    $summary.stage = 'release-build'
    $null = Run-Checked dotnet @('build',$project,'-c','Release','--no-restore')

    foreach ($name in 'PGPASSWORD','ANDRIL_M3_TEST_POSTGRES','ANDRIL_M2_TEST_POSTGRES','ANDRIL_M2_TEST_POSTGRES_ALLOW_ROLES') {
        if (Test-Path "Env:$name") { throw 'An inherited database credential variable is present.' }
    }
    $summary.stage = 'fixture'
    Confirm-Service
    $serviceVerified = $true
    $baseline = @(Read-Catalog)
    if ($baseline.Count -eq 0 -or @($baseline | Where-Object { $_ -cnotmatch '^(database:(postgres|template0|template1)|role:(postgres|pg_[a-z_]+))$' }).Count) {
        throw 'The service does not have a clean disposable baseline.'
    }
    $baseline | Set-Content (Join-Path $out 'catalog-before.txt') -Encoding utf8
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $null = Service-Sql "CREATE ROLE $role LOGIN CREATEDB NOCREATEROLE NOSUPERUSER NOREPLICATION NOBYPASSRLS PASSWORD '$password'"
    $roleCreated = $true
    $flags = Service-Sql "SELECT rolcanlogin::text||'|'||rolcreatedb::text||'|'||rolcreaterole::text||'|'||rolsuper::text||'|'||rolreplication::text||'|'||rolbypassrls::text FROM pg_roles WHERE rolname='$role'"
    if ($flags -cne 'true|true|false|false|false|false') { throw 'Gallery fixture login flags differ.' }
    $summary['fixtureRoleFlags'] = $flags

    $summary.stage = 'gallery-relational'
    $pidValue = Run-Checked docker @('inspect','--type','container','--format','{{.State.Pid}}',$ServiceId)
    $uid = Run-Checked id @('-u')
    $gid = Run-Checked id @('-g')
    if (@($pidValue,$uid,$gid | Where-Object { $_ -cnotmatch '^[1-9][0-9]*$' }).Count) { throw 'Invalid namespace or runner identity.' }
    $dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $namespace = @('-n','--preserve-env=ANDRIL_M3_TEST_POSTGRES,HOME,DOTNET_ROOT','nsenter','--target',$pidValue,'--net','--',
        'setpriv',"--reuid=$uid","--regid=$gid",'--clear-groups','--inh-caps=-all','--ambient-caps=-all','--bounding-set=-all','--no-new-privs','--')
    $filter = ($targets | ForEach-Object { "FullyQualifiedName=$_" }) -join '|'
    $env:ANDRIL_M3_TEST_POSTGRES = "Host=127.0.0.1;Port=5432;Database=postgres;Username=$role;Password=$password;SSL Mode=Disable;Pooling=false;Include Error Detail=false"
    try {
        & sudo @namespace $dotnet test $project -c Release --no-build --no-restore --filter $filter --results-directory $raw --logger 'trx;LogFileName=gallery.trx' --blame-hang-timeout 8m --blame-hang-dump-type none *> $null
        $summary['testExitCode'] = $LASTEXITCODE
    } finally { Remove-Item Env:ANDRIL_M3_TEST_POSTGRES -ErrorAction SilentlyContinue }
    $sourceTrx = Join-Path $raw 'gallery.trx'
    $destTrx = Join-Path $out 'gallery-sanitized.trx'
    $clean = Save-SanitizedTrx $sourceTrx $destTrx
    $summary['sanitizedTrxSha256'] = (Get-FileHash -LiteralPath $destTrx).Hash
    $trxReport = Read-QaTrx $destTrx
    $findings = @(Test-QaExecutions $trxReport (Get-QaExpectedCases $targets) Relational)
    Verify-Results $clean $targets
    if ($findings.Count -or $summary.testExitCode -ne 0) { throw 'Exact Gallery PostgreSQL verification failed.' }
    Confirm-Checkout
    $summary['testsVerified'] = $true
} finally {
    try {
        if ($serviceVerified -and $null -ne $baseline) {
            $observed = @(Read-Catalog)
            $databaseLeaks = @($observed | Where-Object { $_ -clike 'database:andril_m3_gallery_test_*' })
            # Recover only Gallery-owned disposable databases identified by the exact fixture naming contract.
            foreach ($entry in $databaseLeaks) {
                if ($entry -cnotmatch '^database:(andril_m3_gallery_test_[a-f0-9]{32})$') { throw 'Unexpected Gallery database name.' }
                $name = $Matches[1]
                if ((Service-Sql "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname='$name'") -cne $role) {
                    throw 'Unexpected Gallery database owner.'
                }
                $null = Service-Sql "DROP DATABASE $name WITH (FORCE)"
            }
            if ($roleCreated) { $null = Service-Sql "DROP ROLE $role" }
            $after = @(Read-Catalog)
            $after | Set-Content (Join-Path $out 'catalog-after.txt') -Encoding utf8
            $summary['catalogMatches'] = !@(Compare-Object $baseline $after -CaseSensitive).Count
            $summary['requiredLeakRecovery'] = $databaseLeaks.Count
            if (!$summary.catalogMatches -or $databaseLeaks.Count) { throw 'Fixture test cleanup was not clean.' }
            $summary['residualDatabases'] = 0
            $summary['residualTemporaryRoles'] = 0
        }
        if ((Test-Path Env:ANDRIL_M3_TEST_POSTGRES) -or (Test-Path Env:PGPASSWORD)) { throw 'Unexpected credential variables remain.' }
        $summary['credentialCleanupVerified'] = $true
        if ($summary.Contains('testsVerified') -and $summary.testsVerified -and $summary.catalogMatches) { $summary.accepted = $true }
    } finally {
        $password = $null
        $summary | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'summary.json') -Encoding utf8
        $manifest = Join-Path $out 'evidence-SHA256SUMS.txt'
        Get-ChildItem -LiteralPath $out -File | Sort-Object Name | ForEach-Object {
            '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash, $_.Name
        } | Set-Content -LiteralPath $manifest -Encoding utf8
        foreach ($entry in [IO.File]::ReadAllLines($manifest,$utf8)) {
            $sum,$name = $entry -split '  ',2
            if ((Get-FileHash -LiteralPath (Join-Path $out $name)).Hash -cne $sum) { throw 'Evidence hash verification failed.' }
        }
    }
}
if (!$summary.accepted) { throw 'Gallery PostgreSQL acceptance did not complete.' }
Write-Host "Verified 82 Gallery PostgreSQL executions on $($summary.commit), tree $($summary.tree)."
