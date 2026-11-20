Set-StrictMode -Version Latest

function Import-QaXml([string]$Path, [string]$Kind) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing $Kind report." }
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = 'Prohibit'
    $settings.XmlResolver = $null
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
        if ($qualified -cnotmatch '^[\p{L}_][\p{L}\p{N}_.+''\x60]*$') { throw 'Invalid TRX method identifier.' }
        $definitions[$id] = $qualified
    }
    $executions = [System.Collections.Generic.HashSet[string]]::new()
    $tests = @(
        foreach ($result in $xml.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns)) {
            $id = $result.GetAttribute('testId')
            $execution = $result.GetAttribute('executionId')
            $display = $result.GetAttribute('testName')
            $outcome = $result.GetAttribute('outcome')
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

function Export-QaTrx($Report, [string]$Path) {
    # Preserve result and definition IDs, timestamps, outcome and method identity. Drop diagnostics,
    # attachments, environment/machine details and parameter display data from the uploaded copy.
    $xml = $Report.Xml
    foreach ($node in @($xml.SelectNodes('//*[local-name()="Output" or local-name()="CollectorDataEntries" or local-name()="ResultFiles" or local-name()="RunInfos" or local-name()="TestSettings" or local-name()="StdOut" or local-name()="StdErr"]'))) {
        $null = $node.ParentNode.RemoveChild($node)
    }
    foreach ($result in $Report.Tests) {
        $result.Node.SetAttribute('testName', $result.Method)
        $result.Node.RemoveAttribute('computerName')
    }
    foreach ($def in $xml.SelectNodes('//t:UnitTest', $Report.Namespace)) {
        $method = $def.SelectSingleNode('t:TestMethod', $Report.Namespace)
        $name = ($method.GetAttribute('name') -split '\(', 2)[0]
        $method.SetAttribute('name', $name)
        $def.SetAttribute('name', $name)
        $def.RemoveAttribute('storage')
        $method.RemoveAttribute('codeBase')
    }
    $xml.DocumentElement.RemoveAttribute('runUser')
    $xml.DocumentElement.SetAttribute('name', 'ANDRIL QA')
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
