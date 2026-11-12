Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repo
try {
    dotnet restore .\Andril.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build .\Andril.slnx --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
} finally { Pop-Location }
