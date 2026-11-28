[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Member02GitleaksQa.ps1"

$testRoot = [IO.Directory]::CreateTempSubdirectory('andril-gitleaks-test-').FullName
try {
    $manifest = Join-Path $testRoot 'manifest.json'
    $report = Join-Path $testRoot 'report.json'
    $commit = '0123456789abcdef0123456789abcdef01234567'
    $path = 'tests/Synthetic.cs'
    $rule = 'generic-api-key'
    $fingerprint = '{0}:{1}:{2}:3' -f $commit, $path, $rule
    $entry = [ordered]@{
        commit = $commit; introducingCommit = $commit; path = $path; rule = $rule; line = 3
        endLine = 3; startColumn = 2; endColumn = 6; fingerprint = $fingerprint
        classification = 'synthetic-fixture'; evidence = 'Controlled verifier test fixture.'
        decision = 'AND-M5-M2-M6-FIX03-FINAL-AUTH-20261128'
    }
    function Write-TestArray($Items, $Path) {
        ConvertTo-Json -InputObject @($Items) | Set-Content -LiteralPath $Path -Encoding utf8
    }
    function Assert-Result($Condition, [string]$Message) {
        if (!$Condition) { throw $Message }
    }
    $finding = @{
        Commit = $commit; File = $path; RuleID = $rule; StartLine = 3; EndLine = 3
        StartColumn = 2; EndColumn = 6; Fingerprint = $fingerprint; Secret = 'synthetic placeholder'
    }
    Write-TestArray @($entry) $manifest
    $dispositions = Read-QaHistoryDispositions $manifest
    Write-TestArray @($finding) $report
    $result = Get-QaGitleaksResult git 42 $report $dispositions
    Assert-Result ($result.status -ceq 'passed' -and $result.count -eq 1 -and $result.acceptedHistory -eq 1) 'Exact history match did not pass.'
    Assert-Result (!(ConvertTo-Json -InputObject $result -Depth 4).Contains($finding.Secret)) 'Scanner report emitted an unapproved field.'
    Assert-Result ((Get-QaGitleaksResult dir 42 $report $dispositions).status -ceq 'findings') 'A tracked-source finding was accepted.'
    Assert-Result ((Get-QaGitleaksResult git 2 $report $dispositions).status -ceq 'scanner or report failure') 'A scanner error was accepted.'
    Assert-Result ((Get-QaGitleaksResult git 0 $report $dispositions).status -ceq 'scanner or report failure') 'An inconsistent exit code was accepted.'
    Assert-Result ((Get-QaGitleaksResult git 42 $report $null).status -ceq 'invalid historical dispositions') 'A missing manifest was accepted.'

    $finding.EndColumn = 7
    Write-TestArray @($finding) $report
    $result = Get-QaGitleaksResult git 42 $report $dispositions
    Assert-Result ($result.status -ceq 'findings' -and $result.unresolvedHistory -eq 1) 'A changed source span was accepted.'

    $finding.EndColumn = 6
    $finding.Commit = $commit.Replace('0123','abcd')
    $finding.Fingerprint = '{0}:{1}:{2}:3' -f $finding.Commit, $path, $rule
    Write-TestArray @($finding) $report
    Assert-Result ((Get-QaGitleaksResult git 42 $report $dispositions).unresolvedHistory -eq 1) 'A new occurrence was accepted.'

    $finding.Commit = $commit
    $finding.Fingerprint = $fingerprint
    Write-TestArray @($finding, $finding) $report
    Assert-Result ((Get-QaGitleaksResult git 42 $report $dispositions).unresolvedHistory -eq 1) 'A duplicate scan occurrence was accepted.'

    Write-TestArray @() $report
    Assert-Result ((Get-QaGitleaksResult dir 0 $report $dispositions).status -ceq 'passed') 'A valid empty tracked-source report did not pass.'
    Assert-Result ((Get-QaGitleaksResult git 0 $report $dispositions).missingDispositions -eq 1) 'A stale disposition was not detected.'
    Set-Content -LiteralPath $report -Value '{}' -Encoding utf8
    Assert-Result ((Get-QaGitleaksResult git 0 $report $dispositions).status -ceq 'scanner or report failure') 'A malformed scanner report was accepted.'
    Assert-Result ((Get-QaGitleaksResult git 0 (Join-Path $testRoot 'missing.json') $dispositions).status -ceq 'scanner or report failure') 'A missing scanner report was accepted.'

    $entry.evidence = ''
    Write-TestArray @($entry) $manifest
    $invalid = $false
    try { $null = Read-QaHistoryDispositions $manifest } catch { $invalid = $true }
    Assert-Result $invalid 'A disposition without evidence was accepted.'

    $entry.evidence = 'Controlled verifier test fixture.'
    Write-TestArray @($entry, $entry) $manifest
    $invalid = $false
    try { $null = Read-QaHistoryDispositions $manifest } catch { $invalid = $true }
    Assert-Result $invalid 'A duplicate disposition was accepted.'
    Write-Host 'Gitleaks disposition verifier tests passed.'
} finally {
    Remove-Item -LiteralPath $report, $manifest -ErrorAction SilentlyContinue
    [IO.Directory]::Delete($testRoot)
}
