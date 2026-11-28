param([switch]$Connect)
$ErrorActionPreference = 'Stop'
if (-not $Connect) { throw 'Explicit -Connect required to contact the LiveKit sandbox.' }
foreach ($name in @('ANDRIL_LiveKitSandbox__ApiEndpoint','ANDRIL_LiveKitSandbox__RtcEndpoint','ANDRIL_LiveKitSandbox__ApiKey','ANDRIL_LiveKitSandbox__ApiSecret')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "Missing required environment setting: $name" }
}
$previousRun = $env:ANDRIL_RUN_LIVEKIT_SANDBOX
$previousEnabled = $env:ANDRIL_LiveKitSandbox__Enabled
try {
    $env:ANDRIL_RUN_LIVEKIT_SANDBOX = 'true'
    $env:ANDRIL_LiveKitSandbox__Enabled = 'true'
    $project = Join-Path $PSScriptRoot '../../tests/Andril.IntegrationTests/Andril.IntegrationTests.csproj'
    dotnet test $project --no-restore --filter 'FullyQualifiedName~LiveKitSandboxTests'
    if ($LASTEXITCODE -ne 0) { throw 'LiveKit sandbox verification failed; inspect sandbox for an orphan test room if cleanup was unavailable.' }
}
finally {
    $env:ANDRIL_RUN_LIVEKIT_SANDBOX = $previousRun
    $env:ANDRIL_LiveKitSandbox__Enabled = $previousEnabled
}
