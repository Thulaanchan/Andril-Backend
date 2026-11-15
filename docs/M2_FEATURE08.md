# Feature 08: Onboarding progress and resume

`GET /api/v1/onboarding/status` requires a valid Supabase bearer JWT and live,
matching confirmed identity evidence from the existing Feature 07 verifier.
All responses are `Cache-Control: no-store`. No request body or account ID is
accepted. Query values cannot select an account or override progress.

The endpoint is read-only: it does not initialize an account, accept a policy,
select a starter, or create a profile. Flutter calls the existing mutation
endpoints and refreshes this status after login, restart and successful writes.
No new table, migration, package, secret or configuration setting is introduced.

| Result | Meaning and action |
| --- | --- |
| 401 | Missing/invalid JWT, revoked or mismatched live identity. Authenticate again. |
| 403 + `nextStep: verify_identity` | Supabase reports an unconfirmed email identity. Complete verification and retry. |
| `initialize_account` | Confirmed subject has no ANDRIL account. Use existing account initialization. |
| `complete_demographics` | Stored DOB/gender missing or invalid. Use existing eligibility submission. |
| `age_ineligible` | Under 16, or the previously selected module is no longer age-eligible. No module-home authorization. |
| `review_policies` | Current Terms agreement or Privacy acknowledgment is missing/outdated. |
| `select_starter` | Eligible account has current receipts but no starter membership. Existing Social selection still requires explicit opt-in and age 18+. |
| `profile_setup` | Starter prerequisites exist, but private profile completion cannot yet be established. |
| 429 | Shared account/onboarding per-subject local rate limit; honor Retry-After. |
| 503 | Live verification, policy configuration, database or trusted progress dependency unavailable. Do not infer completion; retry when available. |

Successful response example:

```json
{
  "nextStep": "profile_setup",
  "progress": {
    "identityVerified": true,
    "accountInitialized": true,
    "demographicsComplete": true,
    "currentPoliciesAccepted": true,
    "starterModuleSelected": true,
    "privateProfileComplete": false
  },
  "selectedModule": "learning",
  "blockingDependency": "private_profile_infrastructure",
  "onboardingComplete": false,
  "moduleHomeAuthorized": false
}
```

New confirmed users receive `initialize_account`, only identityVerified true,
all other progress booleans false, and null selectedModule/blockingDependency.
Progress indicators describe independently observed facts, not cumulative
authorization. For example an existing membership remains selected when a newer
policy requires review. Private DOB, gender, account/Supabase IDs, receipt details
and module identity IDs are never returned.

Evaluation reuses StarterModuleService and its repository's repeatable-read
snapshot, UTC age policy and current policy catalog. Current Terms/Privacy are
checked on every request; changes can send a user back to review. Missing policy
configuration fails closed for initialized accounts, even when demographics are
incomplete. The existing catalog also requires Community Guidelines configuration;
this endpoint does not require a Community Guidelines acknowledgment.
An uninitialized account can be reported before consulting the policy catalog.

Private-profile infrastructure is an explicit dependency. Gallery ownership and
Social questionnaire answers do not prove completion. Until an authoritative,
account-and-module-scoped profile contract exists, privateProfileComplete,
onboardingComplete and moduleHomeAuthorized are always false. No module-home
nextStep is emitted. This endpoint is a navigation hint, never a capability to
access a module; future module endpoints must enforce their own authorization.
Concurrent updates may become visible on the next request; reads do not lock
the entire onboarding flow or persist a stale last-screen/completion flag.

Tests cover decision order, policy/DOB regressions, restarted and concurrent
reads, identity failures, ownership isolation, safe errors, no-store, rate limits
and absence of profile authorization. The real PostgreSQL test uses the existing
explicit loopback disposable-database fixture and is skipped when
ANDRIL_M2_TEST_POSTGRES is absent. Synthetic HTTP identity evidence in security
tests makes no external Supabase requests.

## Validation in this checkout

SDK: 10.0.404. A temporary writable DOTNET_CLI_HOME was used. Restore used
the local NuGet cache with an empty package-source configuration and auditing
disabled; no package or SDK versions were changed.

```powershell
dotnet restore Andril.slnx --configfile $offline -p:NuGetAudit=false -m:1
dotnet build Andril.slnx --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test Andril.slnx --no-build --no-restore -m:1
```

Restore exited 0. Build exited 0 with zero warnings and zero errors. The normal
test command exited 1: all four test assemblies aborted because VSTest could
not connect to the test-host process (15-second connection timeout). No normal
runner pass is claimed.

A temporary in-process reflection harness invoked the compiled Fact/Theory
methods, including their inline data, across all four test assemblies. It
reported 358 successful cases, zero failures, and 20 explicitly skipped local
PostgreSQL cases. This includes all 24 new Feature 08 unit/security cases; its
one new PostgreSQL persistence case was skipped. This supplemental execution
does not replace a successful VSTest run in a working test environment.
No production database, Supabase service or Git remote was contacted.

## Changed files

- `src/Andril.Common/Accounts/Onboarding/OnboardingContracts.cs` (new)
- `src/Andril.Common/Accounts/Onboarding/OnboardingStatusService.cs` (new)
- `src/Andril.API/Authentication/OnboardingIdentity.cs` (new)
- `src/Andril.API/Controllers/Common/OnboardingController.cs` (new)
- `src/Andril.API/Middleware/OnboardingExceptionHandler.cs` (new)
- `src/Andril.API/Middleware/ConfirmedAccountIdentityMiddleware.cs`
- `src/Andril.API/Extensions/AccountServiceCollectionExtensions.cs`
- `src/Andril.API/Extensions/SupabaseIdentityServiceCollectionExtensions.cs`
- `src/Andril.API/Program.cs`
- `contracts/openapi/andril-api.v1.yaml`
- `tests/Andril.UnitTests/Common/OnboardingStatusTests.cs` (new)
- `tests/Andril.SecurityTests/Authentication/OnboardingEndpointTests.cs` (new)
- `tests/Andril.IntegrationTests/Database/OnboardingResumePersistenceTests.cs` (new)
- `docs/M2_FEATURE08.md` (new)
