Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$source = @('Andril.API','Andril.Shared','Andril.Common','Andril.Learning','Andril.Hobbies','Andril.Social','Andril.Groups','Andril.Communication','Andril.Economy','Andril.AI','Andril.Persistence')
$tests = @('Andril.UnitTests','Andril.IntegrationTests','Andril.SecurityTests','Andril.ArchitectureTests')
$expected = @('Andril.slnx','global.json','Directory.Build.props','.editorconfig','.gitignore','.env.example','README.md','.github/CODEOWNERS','.github/workflows/backend-ci.yml','src/Andril.API/Program.cs','src/Andril.API/appsettings.json','src/Andril.API/appsettings.Development.json','src/Andril.API/Properties/launchSettings.json','contracts/openapi/andril-api.v1.yaml')
foreach ($name in $source) { $expected += "src/$name/$name.csproj" }
foreach ($name in $tests) { $expected += "tests/$name/$name.csproj" }
$missing = @($expected | Where-Object { -not (Test-Path -LiteralPath (Join-Path $repo $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    $missing | ForEach-Object { Write-Error "Missing: $_" }
    throw 'Structure check failed'
}
Write-Host "Structure check passed: $($source.Count) source projects, $($tests.Count) test project definitions and configuration files present." -ForegroundColor Green
