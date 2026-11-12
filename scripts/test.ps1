Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repo
try {
    dotnet test .\Andril.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Test command failed' }
    Write-Warning 'The ZIP contains test project definitions only; add meaningful security/unit/integration test cases before claiming coverage.'
} finally { Pop-Location }
