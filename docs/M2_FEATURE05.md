# M2 Feature 05 — Screen 09 policy agreement and acknowledgment

Implemented only for the existing authenticated account owner. Screen 04 registration
and Features 01–04 do not create policy receipts. No starter-module activation or
overall onboarding completion is implemented here.

## Configuration contract and outstanding publishing decisions

The backend requires these configuration keys (environment variable separators may
use `__` instead of `:`):

| Prefix | Required fields |
| --- | --- |
| `AccountPolicies:Terms` | `Version`, `Url`, `Language` |
| `AccountPolicies:Privacy` | `Version`, `Url`, `Language` |
| `AccountPolicies:CommunityGuidelines` | `Version`, `Url`, `Language` |

No default versions, URLs, or legal text are supplied. The document owner must
provide reviewed English documents, exact immutable version identifiers, and
publicly accessible HTTPS URLs for those exact versions. `Language` must currently
be `en`; reviewed translations require a later explicit contract extension.
Versions are exact, nonblank strings, at most 200 characters, without surrounding
whitespace or control characters. URLs must be absolute HTTPS without credentials.
Document availability is an operational publishing responsibility: the API validates
metadata but does not fetch URLs or claim to verify that a user read a document.

All three documents must be configured, including Community Guidelines, or both
policy operations fail closed with 503. Community Guidelines are accessible but
have no required checkbox and no receipt action. Changing only their version does
not invalidate Terms/Privacy completion.

Published versions must never be reused for changed text. Deploy metadata consistently
across API instances; one scoped catalog is captured per request. Concurrent requests
already in flight may finish against the catalog they read. Feature 06 must re-read
current `RequiredActionsComplete` before allowing continuation; this flag is not
an enduring authorization grant. No Feature 06 code is included.

## API usage

Both routes require the existing Supabase user Bearer token and an initialized account.
All policy responses use `Cache-Control: no-store`, including 401 and validation errors.

1. Call `GET /api/v1/account/policies/status`. It returns `documents.terms`,
   `documents.privacy`, and `documents.communityGuidelines`, each containing `version`,
   `url`, and `language`, plus current completion flags and owner-only receipt history.
   Reading does not record acceptance.
2. On Screen 09, expose all three links before submission. Only after the user
   explicitly agrees to Terms and acknowledges Privacy, call
   `POST /api/v1/account/policies/acknowledgments` with the exact versions returned.

Request shape below uses placeholders, not real document versions:

```json
{
  "actions": [
    { "action": "terms_agreed", "version": "<documents.terms.version>" },
    { "action": "privacy_acknowledged", "version": "<documents.privacy.version>" }
  ]
}
```

Successful POST returns the same status shape as GET, with `termsAgreed`,
`privacyAcknowledged`, and `requiredActionsComplete` true, and independent receipts
containing `action`, `version`, and server-assigned `recordedAtUtc`. The two receipts
may share a timestamp because the actions were submitted together. No client
timestamp, account ID, screen assertion or completion flag is accepted in the body.
Query parameters cannot select another account.

| Status | Meaning |
| --- | --- |
| 200 | Read or atomic acknowledgment succeeded, including identical retries |
| 400 | Missing/duplicate/invalid actions, malformed versions or unknown JSON fields |
| 401 | Missing or invalid authenticated owner identity |
| 404 | Authenticated account has not been initialized |
| 409 | Submitted version differs from the current configured document; retrieve and review again |
| 503 | Policy metadata or account storage unavailable; no completion inferred |

Exactly one Terms and one Privacy action are required per POST. Invalid submissions
write neither. Historical receipts alone do not satisfy a new current version.

## Persistence and migration

Additive migration: `20261115000300_AddAccountPolicyRecords`.

`andril_identity.account_policy_records` has a composite primary key
`(account_id, action, version)`, an account foreign key, allowed-action and version
checks, and a finite UTC timestamp. Account deletion cascades under the existing
account ownership convention; policy-version changes never delete history.

Writes use one transaction and `ON CONFLICT DO NOTHING`, in deterministic action
order. Repeated/concurrent duplicates cannot replace the original timestamps.
No update or delete API exists for receipts.

RLS is enabled without a client allow policy. All PUBLIC privileges are revoked,
as are privileges for Supabase `anon` and `authenticated` roles when present.
This follows the private server-side account persistence model: the trusted backend
database role must have appropriate access (table owner or approved RLS bypass role),
and must never be exposed to clients. Role provisioning is a deployment decision,
not performed by this feature. Existing migrations are preserved.

No database migrations were applied during this implementation. Live PostgreSQL
tests are opt-in via the existing disposable-local-database fixture; they create and
drop random local test databases and apply migrations, so they were disabled for this
task. They cover duplicate concurrency, history, constraints, RLS catalog state,
and rollback after a second insert fails. Their presence is not evidence of a pass.

## Changed files

New implementation files:

- `src/Andril.Common/Accounts/Policies/PolicyContracts.cs`
- `src/Andril.Common/Accounts/Policies/AccountPolicyRecord.cs`
- `src/Andril.Common/Accounts/Policies/AccountPolicyService.cs`
- `src/Andril.API/Configuration/ConfiguredPolicyCatalog.cs`
- `src/Andril.API/Controllers/Common/AccountPolicyController.cs`
- `src/Andril.API/Middleware/PolicyExceptionHandler.cs`
- `src/Andril.Persistence/Configurations/AccountPolicyRecordConfiguration.cs`
- `src/Andril.Persistence/Repositories/Common/AccountPolicyRepository.cs`
- `src/Andril.Persistence/Migrations/Accounts/20261115000300_AddAccountPolicyRecords.cs`

Required shared changes:

- `src/Andril.API/Extensions/AccountServiceCollectionExtensions.cs` — policy DI and exception handling.
- `src/Andril.API/Program.cs` — policy-route no-store headers before authentication.
- `src/Andril.Persistence/DbContexts/AccountsDbContext.cs` — policy entity registration.
- `src/Andril.Persistence/Migrations/Accounts/AccountsDbContextModelSnapshot.cs` — additive model snapshot.
- `contracts/openapi/andril-api.v1.yaml` — policy schemas and operations.

New tests and documentation:

- `tests/Andril.UnitTests/Common/AccountPolicyTests.cs`
- `tests/Andril.SecurityTests/Authentication/AccountPolicyEndpointTests.cs`
- `tests/Andril.IntegrationTests/Database/AccountPolicyPersistenceTests.cs`
- `docs/M2_FEATURE05.md`

Other members' feature files and previous migration files are unchanged.

## Validation results — 15 November 2026

- Offline restore succeeded for all 15 projects, using only the existing local
  NuGet package cache as the source and disabling NuGet audit networking.
- Solution build succeeded. An initial xUnit analyzer warning in the new tests
  was fixed; the final test run rebuilt the changed test projects without warnings.
- Full test suite: **192 passed, 0 failed, 14 skipped**.
  - Unit: 88 passed.
  - Security/API: 98 passed.
  - Integration: 5 passed, 14 skipped (live PostgreSQL).
  - Architecture: 1 passed (existing discovery smoke test, not architectural proof).
- Feature 05 specifically: **35 passed, 3 skipped**. Its three skipped tests cover
  live PostgreSQL concurrency/history, constraints/RLS, and transaction rollback.
- Previous Feature 01–04 tests were retained and ran in the full suite; their
  live database tests remain skipped.
- `git diff --check` passed. The migration snapshot matches the EF model, and the
  new migration's upgrade/downgrade SQL was generated and checked without connecting
  to a database or applying migrations.
- OpenAPI changes were reviewed; automated YAML parsing was unavailable because
  the local Python environment has no YAML parser. No package was downloaded.

Validation commands used single-worker MSBuild (`-m:1`) and disabled the workload
resolver for this local environment. Restore source was the existing
`$env:USERPROFILE/.nuget/packages`; `NuGetAudit=false`. Tests used `--no-restore`,
`-p:UseSharedCompilation=false`, `--logger trx`, and an empty
`ANDRIL_M2_TEST_POSTGRES` for this process only. The restricted runner stalled, so
the successful test run used the approved unrestricted local execution context.

Final local state: branch `Member-2`; 5 modified tracked files and 13 new files,
all unstaged. No GitHub/remote commands, branch changes, commits, pushes, PRs,
merges, production database connections, or migration application were performed.

Before operational use, supply the approved document metadata, review database
role provisioning, and validate the migration and skipped tests in a separately
authorized disposable PostgreSQL environment. No legal publishing decisions were
invented, and missing configuration intentionally keeps policy completion unavailable.
