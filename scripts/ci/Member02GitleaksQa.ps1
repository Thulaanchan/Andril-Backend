function Get-QaSha256([string]$Value) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($Value))).ToLowerInvariant()
}

function Test-QaPositiveInteger($Value) {
    ($Value -is [int] -or $Value -is [long]) -and $Value -gt 0 -and $Value -le [int]::MaxValue
}

function Read-QaHistoryDispositions([string]$Path) {
    $decision = 'AND-M5-M2-M6-FIX03-FINAL-AUTH-20261128'
    $records = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -NoEnumerate
    if ($records -isnot [array] -or $records.Count -eq 0) { throw 'Invalid historical disposition manifest.' }
    $entries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $fields = @('commit', 'introducingCommit', 'rule', 'path', 'line', 'endLine',
        'startColumn', 'endColumn', 'fingerprint', 'classification', 'evidence', 'decision')
    foreach ($record in $records) {
        if ($record -isnot [Collections.IDictionary] -or $record.Count -ne $fields.Count -or
            @($record.Keys | Where-Object { $_ -cnotin $fields }).Count -ne 0 -or
            $record.commit -isnot [string] -or $record.commit -cnotmatch '^[0-9a-f]{40}$' -or
            $record.introducingCommit -isnot [string] -or $record.introducingCommit -cnotmatch '^[0-9a-f]{40}$' -or
            $record.rule -isnot [string] -or $record.rule -cnotmatch '^[a-z0-9-]+$' -or
            $record.path -isnot [string] -or $record.path -cnotmatch '^(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+$' -or
            $record.path -match '(?:^|/)\.\.?(?:/|$)' -or
            !(Test-QaPositiveInteger $record.line) -or !(Test-QaPositiveInteger $record.endLine) -or
            !(Test-QaPositiveInteger $record.startColumn) -or !(Test-QaPositiveInteger $record.endColumn) -or
            $record.endLine -lt $record.line -or
            ($record.endLine -eq $record.line -and $record.endColumn -lt $record.startColumn) -or
            $record.fingerprint -isnot [string] -or
            $record.fingerprint -cne ('{0}:{1}:{2}:{3}' -f $record.commit, $record.path, $record.rule, $record.line) -or
            $record.classification -isnot [string] -or $record.classification -cnotin 'synthetic-fixture', 'false-positive' -or
            $record.evidence -isnot [string] -or [string]::IsNullOrWhiteSpace($record.evidence) -or
            $record.decision -isnot [string] -or $record.decision -cne $decision -or
            !$entries.TryAdd($record.fingerprint, $record)) {
            throw 'Invalid or duplicate historical disposition.'
        }
    }
    return ,$entries
}

function Get-QaGitleaksResult([string]$Mode, [int]$ExitCode, [string]$Report, $Dispositions) {
    $findings = [Collections.Generic.List[object]]::new()
    $accepted = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $reportValid = $false
    try {
        $records = Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json -AsHashtable -NoEnumerate
        if ($records -isnot [array]) { throw 'Invalid scanner report.' }
        foreach ($record in $records) {
            if ($record -isnot [Collections.IDictionary] -or
                $record.RuleID -isnot [string] -or $record.RuleID -cnotmatch '^[a-z0-9-]+$' -or
                $record.File -isnot [string] -or [string]::IsNullOrWhiteSpace($record.File) -or
                !(Test-QaPositiveInteger $record.StartLine) -or !(Test-QaPositiveInteger $record.EndLine) -or
                !(Test-QaPositiveInteger $record.StartColumn) -or !(Test-QaPositiveInteger $record.EndColumn) -or
                $record.EndLine -lt $record.StartLine -or
                ($record.EndLine -eq $record.StartLine -and $record.EndColumn -lt $record.StartColumn) -or
                ($Mode -eq 'git' -and ($record.Commit -isnot [string] -or $record.Commit -cnotmatch '^[0-9a-f]{40}$' -or
                    $record.Fingerprint -isnot [string] -or
                    $record.Fingerprint -cne ('{0}:{1}:{2}:{3}' -f $record.Commit, $record.File, $record.RuleID, $record.StartLine)))) {
                throw 'Invalid scanner finding.'
            }

            $disposition = 'current-source'
            if ($Mode -eq 'git') {
                $disposition = 'unresolved'
                if ($null -ne $Dispositions -and $Dispositions.ContainsKey($record.Fingerprint)) {
                    $entry = $Dispositions[$record.Fingerprint]
                    if ($entry.line -eq $record.StartLine -and $entry.endLine -eq $record.EndLine -and
                        $entry.startColumn -eq $record.StartColumn -and $entry.endColumn -eq $record.EndColumn -and
                        $accepted.Add($record.Fingerprint)) {
                        $disposition = $entry.classification
                    }
                }
            }
            $findings.Add([ordered]@{
                rule = $record.RuleID
                commit = if ($Mode -eq 'git') { $record.Commit } else { $null }
                line = $record.StartLine
                endLine = $record.EndLine
                startColumn = $record.StartColumn
                endColumn = $record.EndColumn
                pathSha256 = Get-QaSha256 $record.File
                fingerprintSha256 = if ($Mode -eq 'git') { Get-QaSha256 $record.Fingerprint } else { $null }
                disposition = $disposition
            })
        }
        $reportValid = $true
    } catch {
        # Scanner fields and parser errors can contain source values. Do not forward either.
    }
    $unresolved = if ($Mode -eq 'git') { $findings.Count - $accepted.Count } else { 0 }
    $missing = if ($Mode -eq 'git' -and $null -ne $Dispositions) { $Dispositions.Count - $accepted.Count } else { 0 }
    $status = if (!$reportValid -or ($ExitCode -eq 0 -and $findings.Count -ne 0) -or
        ($ExitCode -eq 42 -and $findings.Count -eq 0) -or $ExitCode -notin 0, 42) { 'scanner or report failure' }
        elseif ($Mode -eq 'git' -and $null -eq $Dispositions) { 'invalid historical dispositions' }
        elseif ($unresolved -gt 0 -or $missing -gt 0 -or ($Mode -eq 'dir' -and $findings.Count -gt 0)) { 'findings' }
        else { 'passed' }
    [ordered]@{
        mode = $Mode
        status = $status
        exitCode = $ExitCode
        count = $findings.Count
        acceptedHistory = $accepted.Count
        unresolvedHistory = $unresolved
        missingDispositions = $missing
        currentSource = if ($Mode -eq 'dir') { $findings.Count } else { 0 }
        findings = $findings.ToArray()
    }
}
