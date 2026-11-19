# M2 Feature 06 — One starter module (Screen 10)

Feature 06 adds an account-owner-only starter membership for exactly one of
`learning`, `hobbies`, or `social`. The working tree was verified clean on local
`Member-2` before changes. No module profile or public identity is created.

## API contract

Both operations use `/api/v1/account/starter-module` and require the existing
Supabase user Bearer token. The account is resolved from `ICurrentUser`, never
from a caller-supplied account ID. All responses have `Cache-Control: no-store`,
including authentication, model-binding and business-rule errors.

### GET — read eligibility and selection status

Returns:

- `demographicsComplete`: valid stored DOB and gender exist.
- `requiredPoliciesComplete`: existing receipts match current configured Terms
  and Privacy versions.
- `modules`: three entries with `module`, `ageEligible`, `requiresSocialOptIn`
  and `canSelect`.
- `membership`: null before selection; otherwise `module`, `activatedAtUtc`
  and `socialOptIn`.
- `nextStep`: `complete_demographics`, `review_policies`, `age_ineligible`,
  `select_starter`, or `starter_selected`.

Age uses the server's current UTC calendar date and the existing Feature 04
calendar policy: Learning/Hobbies require 16+, Social requires 18+.
`canSelect` requires demographics, current policies, age eligibility and no
existing starter. For Social it remains conditional on explicit opt-in in POST.
All `canSelect` values are false once a starter exists.

No account IDs, Supabase subjects, DOB, gender, policy history, module profile IDs
or cross-module identity links are returned. GET does not write anything.
Status/navigation hints are not module-access authorization or overall onboarding
completion; module profile provisioning remains separate.

### POST — select the starter

Learning:

```json
{ "module": "learning" }
```

Hobbies:

```json
{ "module": "hobbies" }
```

Social, only following an explicit user opt-in:

```json
{ "module": "social", "socialOptIn": true }
```

Social requires an explicit JSON boolean `true`. For Learning/Hobbies, opt-in must
be omitted, null or false; true is rejected rather than recorded for another module.
Only one exact lowercase module identifier is accepted. Arrays, multiple selection
fields, duplicate JSON properties (including differently cased duplicates), and
unknown fields are rejected. No client timestamp, age, account ID or policy flag
is accepted. Query parameters cannot select another account.

The response has the same shape as GET. An identical valid retry returns 200 with
the original membership and opt-in timestamps. Current age and policy prerequisites
are revalidated on retries: after a policy update, acknowledge that policy before
retrying. Failure does not delete or replace the existing membership. A different
starter choice always conflicts; switching and deactivation are outside this feature.

| Status | Meaning |
| --- | --- |
| 200 | Read/selection/retry succeeded |
| 400 | Invalid/multiple module, malformed body, unknown/duplicate fields, or invalid Social opt-in |
| 401 | Missing/invalid authenticated identity |
| 403 | Not age-eligible for the selected module |
| 404 | Account has not been initialized |
| 409 | Missing demographics/current policy receipts, or another starter already selected |
| 503 | Required policy metadata or account storage unavailable |

## Persistence, concurrency and privacy

New additive migration: `20261115000400_AddStarterMemberships`.

The private `andril_identity.account_starter_memberships` table contains:

| Column | Purpose |
| --- | --- |
| `account_id` | Primary key and account foreign key; one starter row per account |
| `module` | Constrained to learning/hobbies/social |
| `activated_at_utc` | Finite server-generated UTC selection timestamp |
| `social_opt_in_at_utc` | Required and equal to activation timestamp only for Social; null otherwise |

The Social membership and opt-in evidence are one atomic row, so a duplicate
request cannot create additional consent evidence. There is no membership update,
delete or profile-creation path. The foreign key follows the existing account
cascade-deletion convention.

POST uses a Read Committed transaction, locks the account row first to serialize
starter selections, then locks the demographics row to synchronize with existing
Feature 04 edits. Business checks run in C# after obtaining these locks, against
the stored demographics and policy receipts. The UTC evaluation time is captured
after waiting for locks. Missing prerequisites abort without inserting anything.
Policy receipts are append-only through the existing API.

The primary key and `ON CONFLICT DO NOTHING` provide a final duplicate guard.
The repository checks the saved module before committing, returning a conflict
for a different winner. Original timestamps are never updated. GET uses a Repeatable
Read transaction so its database-derived fields come from one consistent snapshot.

RLS is enabled without client allow policies. PUBLIC access is revoked, together
with privileges for Supabase `anon` and `authenticated` when present. The trusted
server database role must be provisioned consistently with Features 02–05; credentials
are never exposed. The database table itself is not a cross-module identity API.

Policy metadata reuses the Feature 05 scoped catalog, including fail-closed behavior
when required document metadata is missing. A request uses one configured catalog;
deploy policy metadata consistently across API instances. No document values or
legal text were invented.

## Validation — 15 November 2026

- Offline restore succeeded for all 15 projects using the existing local NuGet
  package cache as the only source, with NuGet audit networking disabled.
- Solution build succeeded with zero warnings and zero errors. The final test run
  rebuilt the request-validation fix successfully.
- Full final suite: **264 passed, 0 failed, 19 skipped**.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Unit | 118 | 0 | 0 |
| Security/API | 139 | 0 | 0 |
| Integration | 6 | 0 | 19 |
| Architecture | 1 | 0 | 0 |

Feature 06 specifically: **72 passed, 5 skipped** (30 unit, 41 security/API,
1 offline migration/model check). Existing tests were retained. The existing
architecture test is a discovery smoke test, not comprehensive architecture proof.

New checks cover ages 15/16/17/18 across all modules, UTC birthday handling,
missing/outdated policies, missing demographics, explicit Social opt-in, duplicate
and changed requests, current prerequisites on retries, ownership, privacy-safe
responses, no-store headers and invalid configuration/storage.

Five opt-in PostgreSQL tests were added for concurrent identical requests,
competing choices, database constraints/RLS, account isolation/current policies,
and a concurrent DOB correction. **They were skipped, not passed.** The test process
left `ANDRIL_M2_TEST_POSTGRES` empty; no database connection or migration deployment
was performed. Live concurrency/constraints still require disposable PostgreSQL
validation. The EF snapshot and additive upgrade/downgrade SQL passed offline checks.

Commands used the existing environment workaround `MSBuildEnableWorkloadResolver=false`,
single-worker MSBuild (`-m:1`), and a CLI home under ignored API `obj/feature06-cli`:

```powershell
dotnet restore Andril.slnx --source "$env:USERPROFILE/.nuget/packages" -p:NuGetAudit=false -p:RestoreDisableParallel=true -m:1
dotnet build Andril.slnx --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test Andril.slnx --no-restore -m:1 -p:UseSharedCompilation=false --logger trx
```

Tests used the approved unrestricted local runner because the restricted runner
had stalled in the preceding feature. No remote dependency restore was performed.
`git diff --check` passed. OpenAPI was updated and reviewed; a standalone automated
OpenAPI/YAML validator was not run.

## Changed files

New implementation files:

- `src/Andril.Common/Accounts/StarterModules/StarterModuleContracts.cs`
- `src/Andril.Common/Accounts/StarterModules/StarterMembership.cs`
- `src/Andril.Common/Accounts/StarterModules/StarterModuleService.cs`
- `src/Andril.API/Controllers/Common/StarterModuleController.cs`
- `src/Andril.API/Middleware/StarterModuleExceptionHandler.cs`
- `src/Andril.Persistence/Configurations/StarterMembershipConfiguration.cs`
- `src/Andril.Persistence/Repositories/Common/StarterModuleRepository.cs`
- `src/Andril.Persistence/Migrations/Accounts/20261115000400_AddStarterMemberships.cs`

Required shared changes:

- `src/Andril.API/Extensions/AccountServiceCollectionExtensions.cs` — starter service/repository/error-handler DI.
- `src/Andril.API/Program.cs` — include starter routes in no-store handling.
- `src/Andril.Persistence/DbContexts/AccountsDbContext.cs` — starter entity registration.
- `src/Andril.Persistence/Migrations/Accounts/AccountsDbContextModelSnapshot.cs` — additive snapshot.
- `contracts/openapi/andril-api.v1.yaml` — request/status/error contracts.

New tests and report:

- `tests/Andril.UnitTests/Common/StarterModuleTests.cs`
- `tests/Andril.SecurityTests/Authentication/StarterModuleEndpointTests.cs`
- `tests/Andril.IntegrationTests/Database/StarterModulePersistenceTests.cs`
- `docs/M2_FEATURE06.md`

## Outstanding decisions and final Git state

- No additional business-rule decision blocks the implemented starter selection.
- Operational prerequisites remain approved Feature 05 document metadata, trusted
  backend database-role provisioning, and separately authorized disposable PostgreSQL
  migration/concurrency validation before deployment.
- Mapping `starter_selected` to a future module-profile flow is intentionally left
  to that separate feature. No later activation, switching, deactivation or Flutter
  flow is implemented here.
- Branch remains `Member-2`: 5 modified tracked files, 12 new files, all unstaged.
- Other members' feature code and all previous migration files are untouched.
- No Git remote was accessed or modified. No branch switching, commits, pushes,
  pulls, merges, rebases or PR operations were performed. No production database
  access, secrets changes or migration deployment occurred.
