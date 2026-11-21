Set-StrictMode -Version Latest

function Import-QaXml([string]$Path, [string]$Kind) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing $Kind report." }
    if ((Get-Item -LiteralPath $Path).Length -gt 32MB) { throw "Oversized $Kind report." }
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = 'Prohibit'
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 32MB
    $settings.MaxCharactersFromEntities = 1024
    try {
        $reader = [System.Xml.XmlReader]::Create($Path, $settings)
        try {
            $xml = [System.Xml.XmlDocument]::new()
            $xml.XmlResolver = $null
            $xml.Load($reader)
            return $xml
        } finally { $reader.Dispose() }
    } catch { throw "Invalid $Kind XML." }
}

function Read-QaTrx([string]$Path) {
    $xml = Import-QaXml $Path TRX
    $ns = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $definitions = @{}
    foreach ($def in $xml.SelectNodes('/t:TestRun/t:TestDefinitions/t:UnitTest', $ns)) {
        $method = $def.SelectSingleNode('t:TestMethod', $ns)
        $id = $def.GetAttribute('id')
        if (!$method -or !$id -or $definitions.ContainsKey($id)) { throw 'Invalid TRX test definition.' }
        $name = ($method.GetAttribute('name') -split '\(', 2)[0]
        $qualified = "$($method.GetAttribute('className')).$name"
        if ($qualified.Length -gt 512 -or $qualified -cnotmatch '^[A-Za-z_][A-Za-z0-9_]*(?:[.+][A-Za-z_][A-Za-z0-9_]*)+$') { throw 'Invalid TRX method identifier.' }
        $definitions[$id] = $qualified
    }
    $executions = [System.Collections.Generic.HashSet[string]]::new()
    $tests = @(
        foreach ($result in $xml.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns)) {
            $id = $result.GetAttribute('testId')
            $execution = $result.GetAttribute('executionId')
            $display = $result.GetAttribute('testName')
            $outcome = $result.GetAttribute('outcome')
            if ($outcome -cnotin 'Passed', 'Failed', 'NotExecuted', 'Aborted', 'Error', 'Timeout', 'Inconclusive', 'NotRunnable', 'Disconnected', 'Warning', 'Completed', 'InProgress', 'Pending') {
                throw 'Invalid TRX outcome.'
            }
            if (!$definitions.ContainsKey($id) -or !$execution -or !$executions.Add($execution) -or !$display -or !$outcome) {
                throw 'Invalid or repeated TRX execution identity.'
            }
            [pscustomobject]@{ Method = $definitions[$id]; Display = $display; Outcome = $outcome; Node = $result }
        }
    )
    $counters = $xml.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters', $ns)
    if (!$tests.Count -or !$counters -or $counters.GetAttribute('total') -ne "$($tests.Count)") {
        throw 'Empty or inconsistent TRX results.'
    }
    $passed = @($tests.Where({ $_.Outcome -ceq 'Passed' })).Count
    $skipped = @($tests.Where({ $_.Outcome -ceq 'NotExecuted' })).Count
    if ($counters.GetAttribute('passed') -ne "$passed") { throw 'Inconsistent TRX pass count.' }
    [pscustomobject]@{ Xml = $xml; Namespace = $ns; Tests = $tests; Passed = $passed; Skipped = $skipped
        Total = $tests.Count; Unsuccessful = $tests.Count - $passed - $skipped }
}

function Get-QaExpectedCases($Entries) {
    $expected = [System.Collections.Generic.Dictionary[string, string[]]]::new([StringComparer]::Ordinal)
    foreach ($entry in $Entries) {
        if ($entry -is [string]) { $method = $entry; $cases = @($entry) }
        else { $method = $entry.method; $cases = @($entry.cases) }
        if ($method -cnotmatch '^[\w.]+\.[\w]+$' -or !$cases.Count -or $expected.ContainsKey($method) -or
            @($cases | Where-Object { $_ -cne $method -and !$_.StartsWith("$method(", [StringComparison]::Ordinal) }).Count) {
            throw 'Invalid or repeated expected test identity.'
        }
        # Multiple theory rows may have the same display name. Require the exact multiplicity.
        $expected.Add($method, [string[]]$cases)
    }
    return ,$expected
}

function Test-QaExecutions($Report, $Expected, [ValidateSet('Offline', 'Relational')][string]$Mode) {
    $found = @{}
    foreach ($group in ($Report.Tests | Where-Object { $Expected.ContainsKey($_.Method) } | Group-Object -Property Method -CaseSensitive)) {
        $found[$group.Name] = $group.Group
    }
    foreach ($method in ($Expected.Keys | Sort-Object)) {
        $actual = $found[$method]
        if (!$actual) { "Missing expected test $method"; continue }
        $wantedNames = @($Expected[$method] | Sort-Object -CaseSensitive)
        $actualNames = @($actual.Display | Sort-Object -CaseSensitive)
        if (Compare-Object $wantedNames $actualNames -CaseSensitive) {
            "Unexpected case identity or multiplicity for $method"
        }
        $wanted = if ($Mode -eq 'Relational') { 'Passed' } else { 'NotExecuted' }
        if ($actual.Where({ $_.Outcome -cne $wanted }).Count) { "Expected $wanted for every case of $method" }
    }
    foreach ($group in ($Report.Tests | Where-Object { !$Expected.ContainsKey($_.Method) -and
        ($Mode -eq 'Relational' -or $_.Outcome -cne 'Passed') } | Group-Object -Property Method -CaseSensitive)) {
        "Unexpected test or outcome for $($group.Name)"
    }
    $outcome = $Report.Xml.SelectSingleNode('/t:TestRun/t:ResultSummary', $Report.Namespace).GetAttribute('outcome')
    if ($outcome -cnotin 'Passed', 'Completed') { 'VSTest reported an unsuccessful run.' }
}

function Get-QaDuration($Test) {
    $duration = [TimeSpan]::Zero
    if ([TimeSpan]::TryParseExact($Test.Node.GetAttribute('duration'), 'c',
        [Globalization.CultureInfo]::InvariantCulture, [ref]$duration) -and
        $duration -ge [TimeSpan]::Zero -and $duration.TotalDays -le 1) { return $duration.ToString('c') }
    return $null
}

function Get-QaFailureDiagnostics($Report) {
    # Only literal allowlist members are returned. Message/stack text never becomes output.
    $types = @('Xunit.Sdk.EqualException', 'Xunit.Sdk.ThrowsException', 'Xunit.Sdk.TrueException',
        'Xunit.Sdk.FalseException', 'Xunit.Sdk.SingleException', 'Xunit.Sdk.ContainsException',
        'Xunit.Sdk.NotNullException', 'Xunit.Sdk.NullException', 'Xunit.Sdk.IsTypeException',
        'Microsoft.EntityFrameworkCore.DbUpdateException', 'Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException',
        'Npgsql.PostgresException', 'Npgsql.NpgsqlException', 'System.InvalidOperationException',
        'System.TimeoutException', 'Andril.Common.Accounts.Profiles.PhotoDependencyException',
        'Andril.Common.Accounts.Profiles.PhotoUploadConflictException')
    $constraints = @('fk_profile_photo_profile_module', 'pk_private_profile_photos',
        'uq_profile_photo_request', 'uq_profile_photo_object', 'uq_profile_photo_current', 'uq_profile_photo_pending',
        'ck_profile_photo_ids', 'ck_profile_photo_module', 'ck_profile_photo_key', 'ck_profile_photo_hashes',
        'ck_profile_photo_media', 'ck_profile_photo_state', 'ck_profile_photo_cleanup',
        'ck_profile_photo_late_write', 'ck_profile_photo_times')
    $states = @('23502', '23503', '23505', '23514', '25P02', '42501', '40001', '40P01', '42P01', '42703', '57014', '53300', '08006')
    $assertions = @{ 'Equal' = 'Equality'; 'Throws' = 'ExceptionType'; 'True' = 'Boolean'; 'False' = 'Boolean';
        'Single' = 'Cardinality'; 'Contains' = 'Contains'; 'NotNull' = 'Nullability'; 'Null' = 'Nullability'; 'IsType' = 'Type' }
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $sourceFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $tracked = @(& git -C $repo ls-files -- 'src/*.cs' 'tests/*.cs' 2>$null)
    if ($LASTEXITCODE -eq 0) { foreach ($path in $tracked) { $null = $sourceFiles.Add($path) } }
    foreach ($test in $Report.Tests.Where({ $_.Outcome -cnotin 'Passed', 'NotExecuted' })) {
        $safe = [ordered]@{ testName = $test.Method; outcome = $test.Outcome; duration = (Get-QaDuration $test)
            exceptionType = $null; innerExceptionType = $null; assertionCategory = $null; sqlState = $null
            constraintName = $null; sourceFile = $null; sourceLine = $null; diagnosticCategory = 'Unclassified' }
        try {
            $message = $test.Node.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $Report.Namespace)
            $stack = $test.Node.SelectSingleNode('t:Output/t:ErrorInfo/t:StackTrace', $Report.Namespace)
            $text = if ($message) { $message.InnerText } else { '' }
            $trace = if ($stack) { $stack.InnerText } else { '' }
            if ($text.Length -gt 65536 -or $trace.Length -gt 65536) { throw 'Diagnostic size limit.' }
            foreach ($line in ($text -split '\r?\n')) {
                if ($line -cmatch '^\s*(?<inner>[-]+>\s*|[-]{2,}\s+)?(?<type>[A-Za-z][A-Za-z0-9_.]+Exception)(?: \(0x[0-9A-Fa-f]{8}\))?(?:\s*:|\s*$)') {
                    $type = $Matches.type
                    $inner = $Matches.ContainsKey('inner')
                    if ($type -cin $types) {
                        if (!$inner -and !$safe.exceptionType) { $safe.exceptionType = $type }
                        elseif ($inner -and !$safe.innerExceptionType) { $safe.innerExceptionType = $type }
                    }
                }
                if ($line -cmatch '^Assert\.(Equal|Throws|True|False|Single|Contains|NotNull|Null|IsType)\(\) Failure(?:[: ].*)?$') {
                    $safe.assertionCategory = $assertions[$Matches[1]]
                }
                if ($line -cmatch '^\s*(?:SqlState:\s*|(?:[-]+>\s*|[-]{2,}\s+)?Npgsql\.PostgresException(?:\s*\([^)]{1,16}\))?\s*:\s*)([0-9A-Z]{5})(?:\s*:.*)?\s*$') {
                    if ($Matches[1] -cin $states) { $safe.sqlState = $Matches[1] }
                }
                if ($line -cmatch '^\s*ConstraintName:\s*([a-z_]+)\s*$') {
                    if ($Matches[1] -cin $constraints) { $safe.constraintName = $Matches[1] }
                }
                elseif ($line -cmatch '^\s*(?:[-]+>\s*|[-]{2,}\s+)?Npgsql\.PostgresException\s*:\s*[0-9A-Z]{5}: .* constraint "([a-z_]+)"\s*$') {
                    if ($Matches[1] -cin $constraints) { $safe.constraintName = $Matches[1] }
                }
            }
            foreach ($line in ($trace -split '\r?\n')) {
                # Only the exact known checkout prefix may be converted to a relative path.
                if ($line -cmatch '^\s*(?:at [A-Za-z0-9_.+`<>]+\([^\r\n]*\) in )?(.+\.cs):line ([1-9][0-9]{0,6})\s*$') {
                    $path = $Matches[1].Replace('\', '/'); $number = [int]$Matches[2]
                    $prefix = $repo.Replace('\', '/').TrimEnd('/') + '/'
                    if ($path.StartsWith($prefix, [StringComparison]::Ordinal)) { $path = $path.Substring($prefix.Length) }
                    if ($path -cmatch '^(?:tests|src)/[A-Za-z0-9_./-]+\.cs$' -and
                        $path -cnotmatch '(?:^|/)\.{1,2}(?:/|$)|//' -and
                        $sourceFiles.Contains($path)) {
                        $safe.sourceFile = $path; $safe.sourceLine = $number; break
                    }
                }
            }
            if ($safe.assertionCategory -or $safe.exceptionType -clike 'Xunit.Sdk.*') { $safe.diagnosticCategory = 'Assertion' }
            elseif ($safe.sqlState -or $safe.exceptionType -ceq 'Npgsql.PostgresException' -or $safe.innerExceptionType -ceq 'Npgsql.PostgresException') { $safe.diagnosticCategory = 'PostgreSql' }
            elseif ($safe.exceptionType -clike 'Microsoft.EntityFrameworkCore.*') { $safe.diagnosticCategory = 'EntityFramework' }
            elseif ($safe.exceptionType) { $safe.diagnosticCategory = 'KnownException' }
        } catch {
            # Do not publish exception messages, even for failures inside the extractor.
            $safe.diagnosticCategory = 'ExtractionUnavailable'
        }
        [pscustomobject]$safe
    }
}

function Assert-QaReportSeparation([string]$OutputPath, [string]$RawPath) {
    $output = [IO.Path]::GetFullPath($OutputPath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $private = [IO.Path]::GetFullPath($RawPath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($private.StartsWith($output, [StringComparison]::OrdinalIgnoreCase) -or
        $output.StartsWith($private, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Raw diagnostics and published reports must have separate directory trees.'
    }
}

function Export-QaFailureDiagnostics($Report, [string]$Path) {
    $failures = @(Get-QaFailureDiagnostics $Report)
    ConvertTo-Json -InputObject ([ordered]@{ schemaVersion = 1; failedCount = $failures.Count; failures = $failures }) -Depth 5 |
        Set-Content -LiteralPath $Path -Encoding utf8
    return ,$failures
}

function Export-QaTrx($Report, [string]$Path) {
    # Rebuild a minimal compatible TRX, rather than trying to remove every possible raw field.
    $xml = [xml]'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" name="ANDRIL QA" />'
    $xml.DocumentElement.SetAttribute('id', [guid]::NewGuid().ToString())
    $uri = $xml.DocumentElement.NamespaceURI
    $defs = $xml.CreateElement('TestDefinitions', $uri)
    $results = $xml.CreateElement('Results', $uri)
    $null = $xml.DocumentElement.AppendChild($defs)
    $null = $xml.DocumentElement.AppendChild($results)
    $ids = @{}
    foreach ($test in $Report.Tests) {
        if (!$ids.ContainsKey($test.Method)) {
            $id = [guid]::NewGuid().ToString(); $ids[$test.Method] = $id
            $def = $xml.CreateElement('UnitTest', $uri); $def.SetAttribute('id', $id)
            $method = $xml.CreateElement('TestMethod', $uri)
            $split = $test.Method.LastIndexOf('.')
            $method.SetAttribute('className', $test.Method.Substring(0, $split))
            $method.SetAttribute('name', $test.Method.Substring($split + 1))
            $def.SetAttribute('name', $test.Method.Substring($split + 1))
            $null = $def.AppendChild($method); $null = $defs.AppendChild($def)
        }
        $node = $xml.CreateElement('UnitTestResult', $uri)
        $node.SetAttribute('testId', $ids[$test.Method]); $node.SetAttribute('executionId', [guid]::NewGuid().ToString())
        $node.SetAttribute('testName', $test.Method); $node.SetAttribute('outcome', $test.Outcome)
        $duration = Get-QaDuration $test
        if ($duration) { $node.SetAttribute('duration', $duration) }
        $null = $results.AppendChild($node)
    }
    $summary = $xml.CreateElement('ResultSummary', $uri)
    $original = $Report.Xml.SelectSingleNode('/t:TestRun/t:ResultSummary', $Report.Namespace).GetAttribute('outcome')
    $summary.SetAttribute('outcome', $(if ($Report.Unsuccessful -or $original -cnotin 'Passed', 'Completed') { 'Failed' } else { 'Completed' }))
    $counts = $xml.CreateElement('Counters', $uri)
    $counts.SetAttribute('total', "$($Report.Total)"); $counts.SetAttribute('passed', "$($Report.Passed)")
    $counts.SetAttribute('executed', "$($Report.Total - $Report.Skipped)")
    foreach ($outcome in 'Failed', 'Error', 'Timeout', 'Aborted', 'Inconclusive', 'NotRunnable', 'Disconnected', 'Warning', 'Completed', 'InProgress', 'Pending') {
        $attribute = $outcome.Substring(0, 1).ToLowerInvariant() + $outcome.Substring(1)
        $counts.SetAttribute($attribute, "$(@($Report.Tests.Where({ $_.Outcome -ceq $outcome })).Count)")
    }
    $counts.SetAttribute('notExecuted', "$($Report.Skipped)")
    $null = $summary.AppendChild($counts); $null = $xml.DocumentElement.AppendChild($summary)
    $xml.Save($Path)
}

function Read-QaCoverage([string]$Path) {
    $xml = Import-QaXml $Path Cobertura
    $root = $xml.DocumentElement
    if ($root.LocalName -ne 'coverage' -or !$root.SelectNodes('packages/package').Count) { throw 'Invalid Cobertura report.' }
    $values = @{}
    foreach ($kind in 'lines', 'branches') {
        $covered = 0L
        $valid = 0L
        if (![long]::TryParse($root.GetAttribute("$kind-covered"), [ref]$covered) -or
            ![long]::TryParse($root.GetAttribute("$kind-valid"), [ref]$valid) -or
            $covered -lt 0 -or $valid -lt $covered -or ($kind -eq 'lines' -and $valid -eq 0)) {
            throw "Invalid Cobertura $kind counters."
        }
        $values[$kind] = "$covered/$valid"
    }
    return "lines $($values.lines), branches $($values.branches)"
}
