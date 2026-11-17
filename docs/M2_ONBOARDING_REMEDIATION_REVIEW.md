# M2 onboarding remediation: integration proposal and verification gates

This document is a proposal for human review, not an approved production contract.
No module-profile implementation or completion rule is introduced by it.

## Observed verification results

SDK 10.0.404 was selected under global.json's 10.0.400/latestFeature policy.
The solution build with --no-restore -m:1 -p:UseSharedCompilation=false exited 0,
with zero warnings and errors. All four individual test projects aborted during
test-host communication. Diagnostic traces show the host process launches and then
SocketException 10013: "An attempt was made to access a socket in a way forbidden
by its access permissions." This establishes a socket-permission blocker in this
execution environment; it does not identify the exact OS/sandbox enforcement rule.
No firewall, sandbox, registry, privilege or application-code workaround was applied.
Temporary writable CLI/log directories worked, and cached dependencies built.
The trace also contains test-platform extension-loader warnings for
Microsoft.Bcl.AsyncInterfaces and Microsoft.VisualStudio.TestPlatform.Fakes; their
causal role is unproven. Do not change packages based on those warnings alone.

No fresh test passes, assertion failures or runner-reported skips were established.
The 20 opt-in PostgreSQL methods were not executed; no approved target is configured.
Migration source ordering and fixture isolation were inspected, but migrations,
transactions, concurrency and role isolation were not verified against a database.
Full E2E remains blocked by both infrastructure and missing approved profile evidence.
Only this M2 review document was added; no production source or dependencies changed.

## Proposed profile evidence boundary — approval pending

M2 resolves the account from the verified Supabase subject. An endpoint's approved
module policy supplies exactly learning, hobbies or social. Never use a client
account ID, profile ID, provider metadata or completion flag as ownership evidence.

Proposed internal contract (illustrative, not registered in DI):

```csharp
public interface IPrivateModuleProfileEvidence
{
    Task<ProfileEvidenceResult> ReadAsync(
        Guid resolvedAccountId, AndrilModule targetModule, CancellationToken cancellationToken);
}
public enum AndrilModule { Learning, Hobbies, Social }
public enum ProfileEvidenceState { Missing, Incomplete, Complete, Unavailable }
public sealed record ProfileEvidenceResult(
    Guid AccountId, AndrilModule Module, Guid? PrivateProfileId,
    ProfileEvidenceState State);
```

M1, M3 and M5 must approve and supply their own authoritative adapters. Each must
resolve persisted account-to-profile ownership and evaluate its approved required
profile fields. Gallery actor context, gallery ownership and questionnaire answers
alone are insufficient. Missing/unavailable adapters must never synthesize Complete.
Profile IDs remain internal to the selected module and must not enter the public
onboarding response. M2 verifies echoed account/module and a nonempty profile ID
before using Complete evidence; returned evidence must originate from trusted storage.

M2 separately evaluates live confirmed identity, account existence, current UTC age,
current Terms/Privacy, selected module membership and Social opt-in. The profile
adapter does not decide those M2 rules. Authorization requires all checks to succeed.
Current starter membership is the only activation source in scope; later switching
and deactivation require their separately approved contracts.

Proposed policy: explicit module resource + authenticated owner + current M2
prerequisites + matching complete profile evidence. Apply at module endpoints only
with their owners' integration approval. Do not change the global fallback to require
completed onboarding: doing so would block the APIs needed to complete onboarding.
Health stays anonymous. Missing evidence denies; unavailable evidence never grants.
Agree on 403 versus safe 503 handling for dependency failures before implementation.
Do not cache a permanent completion grant; policy/eligibility/profile changes must
be observed, and sensitive module operations must revalidate authorization.

Approval decisions: required fields per module; ownership mapping and uniqueness;
deleted/suspended profile semantics; consistency/revocation guarantees; module endpoint
resource binding; error contract; internal profile ID handling. Once approved, likely
M2 integration files are OnboardingContracts.cs, OnboardingStatusService.cs and narrow
API authorization/DI additions. Teammate endpoint changes remain with their owners.

Required policy tests: missing/invalid identity, foreign account/module evidence,
missing/incomplete/unavailable profile, invalid profile ID, missing membership,
underage access, outdated consent, missing Social opt-in, successful matching evidence,
progress regression, and public health. No success test can prove real adapter behavior
until the owners' implementations and approved disposable database are available.

## Language decision — approval pending

Approved Feature 03 behavior remains: en/ta/si only; English fallback has
isPersisted=false; first sync preserves an existing preference; Settings can update it.
The requirement to save the selected language does not unambiguously establish a
mandatory completion gate. Feature 08's approved sequence omitted such a gate.

Option A permits completion with the unsaved English fallback. Option B requires
a saved account language before completion. Recommend B for deterministic account
state across devices, while retaining the fallback and first-sync behavior. Product
approval must specify gate placement and treatment of existing accounts. Do not
silently persist English or overwrite a pre-login device choice. No behavior changed.

## Disposable PostgreSQL approval gate

No ANDRIL_M2_TEST_POSTGRES target was configured during this review. Do not automatically
create a database. Proposed setup is a separately approved dedicated local PostgreSQL
instance with no production/shared data, no tunnels, and a test-only role. Existing
tests require a loopback maintenance database named postgres and CREATE DATABASE.
They create random andril_m2_test_<uuid> databases, apply account migrations there,
then DROP only their generated database. Approve that lifecycle explicitly first.

Existing migrations are ordered accounts -> language -> demographics -> receipts ->
starter. Source inspection is not proof of executed constraints, rollback or RLS.
Existing tests use one privileged fixture connection: catalog checks do not prove
real client-role isolation. Add/run role-specific denial tests in the approved
instance, covering anon/authenticated and the intended backend runtime role, including
schema/table privileges, RLS behavior and backend access. Never provision test roles
on a shared instance merely to run these checks.

## Developer-run verification, without elevation

Run in a normal authorized local PowerShell session from the repository. This is a
manual environment comparison, not a request to disable security controls. Keep the
database variable empty until the disposable target and lifecycle are approved.

```powershell
if ((git branch --show-current).Trim() -ne 'Member-2') { throw 'Wrong branch' }
$checkRoot = Join-Path $env:TEMP ('andril-m2-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $checkRoot | Out-Null
$env:DOTNET_CLI_HOME = $checkRoot
$env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:ANDRIL_M2_TEST_POSTGRES = ''
$offline = Join-Path $checkRoot 'NuGet.Config'
Set-Content $offline '<configuration><packageSources><clear /></packageSources></configuration>'
dotnet --version
dotnet restore Andril.slnx --configfile $offline -p:NuGetAudit=false -m:1
if ($LASTEXITCODE -ne 0) { throw 'Offline restore failed' }
dotnet build Andril.slnx --no-restore -m:1 -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
foreach ($suite in @('Unit', 'Security', 'Integration', 'Architecture')) {
    dotnet test "tests/Andril.${suite}Tests/Andril.${suite}Tests.csproj" --no-build --no-restore --logger trx --results-directory $checkRoot --diag (Join-Path $checkRoot "$suite.log")
    Write-Output "${suite}_EXIT=$LASTEXITCODE"
}
git status --short
git diff --check
```

Review diagnostics locally before sharing; never share tokens, credentials or private
configuration. A successful simulated HTTP test is not real Supabase/Flutter E2E.
Full E2E remains blocked until approved profile adapters, module endpoint policies,
disposable PostgreSQL and explicitly authorized nonproduction identities/devices exist.
