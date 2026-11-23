# Feature 09 — private module profiles and completion boundaries

## Current profile behavior

This section describes the current contract. The earlier foundation report below
records the original scope and verification. Canonical module identifiers remain
`learning`, `hobbies` and `social` (Dating).

Draft profiles now have independently scoped display name, bio and private
visibility. No values are copied between modules. `PUT /api/v1/account/profiles/{module}`
replaces these draft fields after existing owner, verified identity, membership,
age, policy and Social opt-in checks. POST `{}` still creates an identity root;
GET includes the draft fields. Exact PUT retries preserve identity and creation time.

Example PUT body:

```json
{"displayName":"  Alice  ","bio":"My private draft","visibility":"private"}
```

The stored name is `Alice`; canonical comparison is `alice`. Only ASCII A–Z is
case-folded. Unicode letters are not folded or normalized. Both approved rules
are enforced: UNIQUE(module, canonical_display_name) and
UNIQUE(account_id, canonical_display_name). Different accounts can reuse a name
in different modules. The current validator requires 3–30 graphemes and rejects
reserved names. The 16 KiB PUT transport limit is a separate resource guard.
Only private visibility is supported. Clients must render bio/name as plain text.

Additive migration `20261116000200_AddPrivateProfileFields` adds nullable draft
fields, private visibility, generated canonical name with C collation, exact
Unicode-whitespace trim check and both unique indexes. Database-generated
canonical values cannot be overridden by clients. Existing shell rows remain
valid with null names. Repository prechecks give safe 409 conflicts; database
constraints close concurrent claim races.

### PHOTO-01 boundary

The implementation includes scoped upload, status, current-photo and content
endpoints, a durable PostgreSQL photo repository, and a backend-only Supabase
Storage adapter. Migration `20261120152336_AddPrivateProfilePhotos` adds photo
metadata and its composite profile/module foreign key. Uploads are keyed by a
profile-scoped request UUID, and private content is read through the owner-scoped
API. Status responses omit storage keys, public URLs and other module identities.

* POST `/api/v1/account/profiles/{module}/photos/{requestId}` accepts
  `application/octet-stream` with a nonempty, profile-scoped request UUID.
* GET `/api/v1/account/profiles/{module}/photos/{uploadId}` reads scoped status.
* GET `/api/v1/account/profiles/{module}/photos/current` reads the current status.
* GET `/api/v1/account/profiles/{module}/photos/{uploadId}/content` returns private JPEG content.

Offline tests with synthetic processing and Storage cover scope isolation,
retries, publication and cleanup. Disposable PostgreSQL tests cover persistence
and proposed restricted grants. This evidence leaves hosted Storage, live image
processing and deployed permissions unverified. The shipped API wiring uses
an unavailable image inspector and the production feature remains inactive.
Deployment requires separately approved processing, private Storage, runtime
permissions and cleanup verification. See the [photo deployment notes](M2_PHASE02A_PHOTO_DEPLOYMENT.md).

### Onboarding and protected access

The owner-scoped completion read rechecks confirmed account identity, current
Terms and Privacy receipts, eligible starter membership and, for Social, age
18+ and explicit opt-in. Under `AND-M2-SOC-ONB-DEC-20261121-01`, a valid,
persisted and canonically unique name on the owned Social profile completes
private-profile setup. Biography, photo, interests, preferences and personality
questionnaire are optional for this gate. Decision `AND-M2-SOC-ONB-DEC-20261121-02`
also allows overall onboarding completion. See the [Feature 08 status contract](M2_FEATURE08.md).

The profile DTO still describes a draft. Clients use onboarding status for
completion, including the legacy `profile_setup` next step after Social setup
completes. Learning and Hobbies shells await authoritative module-owned evidence.
`IPrivateModuleAccess` remains a separate fail-closed boundary for protected entry,
and onboarding status keeps `moduleHomeAuthorized` false.

### Historical continuation files

The 19 foundation files listed below remain present. Four additional files:

* `src/Andril.Common/Accounts/Profiles/ProfilePhotos.cs`
* `src/Andril.API/Controllers/Common/ProfilePhotosController.cs`
* `src/Andril.Persistence/Migrations/Accounts/20261116000200_AddPrivateProfileFields.cs`
* `tests/Andril.UnitTests/Common/ProfilePhotoTests.cs`

Extended foundation files: PrivateModuleProfile.cs, ProfileContracts.cs,
PrivateProfileService.cs, PrivateProfileRepository.cs, PrivateModuleProfileConfiguration.cs,
AccountsDbContextModelSnapshot.cs, PrivateProfilesController.cs,
PrivateProfileExceptionHandler.cs, AccountServiceCollectionExtensions.cs,
PrivateProfilePersistenceTests.cs, PrivateProfileEndpointTests.cs, OpenAPI and this report.
All other foundation files remain intact. Final status: 7 tracked modifications,
16 untracked files (23 total), all uncommitted on Member-2 at unchanged HEAD
`66d40f0bef0a15e5eb49568eb106e504401350ff`.

## Historical foundation report (November 2026)

Continuation validation: offline local-cache restore passed; final build has zero
warnings/errors after correcting one xUnit analyzer warning. Standard solution
runner: Unit 198 passed, Security 197 passed, Integration 37 passed / 24 skipped,
Architecture 1 passed. Total **433 passed, 0 failed, 24 skipped**. Skips are all
PostgreSQL execution tests (20 pre-F09, 4 F09); no disposable server was configured
and database execution was explicitly disabled to honor the no-migrations rule.
Migration model/script tests passed offline; real uniqueness races, constraints and
RLS role permissions remain unverified. The mocks do not prove real face detection,
image safety, durable media storage or human review. `git diff --check` passed.
No dependency packages downloaded, remote operations, commits or deployments.

The following sections record the original foundation scope, pending decisions and validation. Use the current sections above for Social completion, field updates and photo status.

### Scope and approved decisions

Canonical account identifiers remain `learning`, `hobbies`, `social`. There is no
approved Dating rename in this checkout. No other member's contracts were renamed.
Learning has no profile field contract. Hobbies exposes a profile-access reader
interface for gallery ownership, not profile completion. Social questionnaire
completion/skipping is not a private-profile completion contract. AI's access-reader
interface requires private ownership and permissions but supplies no required fields.

No approved profile fields, display-name uniqueness scope/normalization, photo
ownership contract, interests vocabulary, privacy settings, connections model or
per-module completion requirements were found. These decisions are **PENDING**.
No names, photos, interests, privacy settings or connections are stored/copied by
this change; no public media URL is accepted. Their eventual persisted fields must
be scoped to the independent profile ID, never defaulted from another module.

The implemented foundation is deliberately incomplete. It must not be represented
as completed profiles or completed onboarding. Field update endpoints and
display-name uniqueness constraints cannot safely be implemented until approved
rules are provided. There is no production completion bypass or configurable flag.

### Architecture and persistence

`PrivateModuleProfile` is a private identity root with an independent random UUID,
internal account FK, canonical module and immutable UTC creation timestamp.
`AccountsDbContext.PrivateProfiles` maps it in `andril_identity`.
Unique `(account_id,module)` prevents duplicates; the schema can represent three
independent roots for an account, but current APIs can create ONLY the single
selected starter module. Later module activation remains outside this feature.
There is no cross-module listing or lookup by client-supplied profile/account UUID.

Migration `20261116000100_AddPrivateModuleProfiles` is additive. It creates only
`andril_identity.private_module_profiles`, its PK, unique owner/module index,
account cascade FK, module/nonempty-ID/finite-timestamp constraints, enables RLS
and revokes PUBLIC/anon/authenticated table access. Earlier migrations are unchanged.
No migration is applied by startup or by this implementation session.

As with previous M2 tables, backend runtime role/grants require deployment review.
There are no direct-client RLS policies or grants. Table owners/BYPASSRLS roles are
not tenant-isolated by RLS. Service ownership checks are required. Role enforcement
has not been verified against PostgreSQL here.

Reads use a repeatable-read snapshot. Creation locks the account then demographics
row, matching F06 lock order, revalidates membership, current policies and age,
then inserts with conflict handling. Identical retries retain ID/timestamp.
Membership is checked in both repository and service; the DB FK enforces account
ownership, not membership eligibility. A privileged SQL writer must not be treated
as a client API. No partial user field updates exist yet.

### APIs

Both routes require a valid JWT AND the existing fresh Supabase confirmed-user
check. They inherit account no-store responses and per-subject throttling.

* `GET /api/v1/account/profiles/{module}` — read the owned eligible identity root.
* `POST /api/v1/account/profiles/{module}` — idempotent creation; body exactly `{}`.

Example (synthetic values, not real user data):

```http
POST /api/v1/account/profiles/learning
Authorization: Bearer <Supabase access token>
Content-Type: application/json

{}
```

```json
{
  "profileId": "2a11a38a-c2bf-48ce-b921-cdc275da92e4",
  "module": "learning",
  "createdAtUtc": "2026-11-16T00:00:00+00:00",
  "isComplete": false,
  "blockingDependency": "approved_profile_contract"
}
```

The response never includes the internal account UUID, Supabase subject or another
module identity. Unknown fields, identity overrides, client completion flags and
unapproved field values receive 400. Invalid JWT: 401. Unconfirmed identity,
missing/wrong membership, demographics, current policies or age eligibility: 403.
Missing account/profile: 404. Throttling: 429. Missing policy configuration, identity
dependency or store: 503. POST body limit: 1024 bytes. PATCH/PUT are unsupported.

### F08 integration and protected access

Before F09: F08 always reported `private_profile_infrastructure` at profile_setup.
After F09: the production DI wiring uses an owner-scoped completion reader that
reads persisted identity roots and revalidates current prerequisites:

* Missing root → `profile_setup`, dependency `private_profile_missing`.
* Existing root → `profile_setup`, dependency `approved_profile_contract`.
* Unavailable storage/configuration or changed prerequisites → fail closed.
* Missing reader (legacy construction) → `private_profile_infrastructure`.

`privateProfileComplete`, `onboardingComplete`, `moduleHomeAuthorized` remain false.
There is intentionally NO success transition without approved required fields.
`IPrivateModuleAccess` is a separate fail-closed boundary for module handlers; it
rechecks the reader and denies access while completion/privacy contracts are pending.
It is not wired into other members' controllers, gallery or AI adapters. Those
owners must integrate their own protected access checks after approval; routing
responses must never grant module access. This change does not certify those
existing endpoints as protected by F09.

### Required follow-up and ownership

Product/spec owner with M1 (Learning), M3 (Hobbies), M5 (Social): supply approved
required/optional fields, display-name uniqueness scope/case/Unicode/rename rules,
media reference ownership and privacy/connection rules, completion criteria and
any Social/Dating naming decision. M2 then implements persisted validated fields,
service validation + matching database uniqueness, update API and authoritative
completion; module owners integrate access enforcement at each protected boundary.

Acceptance requires approved complete/incomplete persisted fixtures, independent
module privacy tests, real PostgreSQL concurrency/constraints/RLS role tests and
F08 progression only when all prerequisites and module permissions pass.
Until then successful-completion and display-name uniqueness tests are blocked by
missing requirements, not passed or replaced by invented assertions.

### Validation

Offline restore/build and standard solution tests are run for this change.
New tests cover age boundaries, identity/ownership, cross-module denial, retries,
rejected fields and flags, missing/current prerequisites, store failure and F08
blocking. Migration SQL/model validation runs offline. PostgreSQL tests use the
existing `LocalPostgresFact` fixture; only an explicitly configured disposable
local server is allowed. No such server was configured during implementation.
In-memory concurrent HTTP tests are not PostgreSQL concurrency verification.
Final standard runner results (2026-11-16):

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Unit | 185 | 0 | 0 |
| Security | 197 | 0 | 0 |
| Integration | 36 | 0 | 22 |
| Architecture | 1 | 0 | 0 |
| Total | 419 | 0 | 22 |

Offline restore succeeded using only the local NuGet cache. Final solution build:
0 warnings, 0 errors. Standard commands: `dotnet build Andril.slnx --no-restore
-m:1 -p:UseSharedCompilation=false` and `dotnet test Andril.slnx --no-build
--no-restore -m:1 --logger trx`. TRX files are in each test project's ignored
TestResults directory, timestamps 10_06_56 through 10_07_02.
The initial sandbox testhost stalled and was cancelled; the normal runner completed
outside that sandbox. One first-run new test failed because its synthetic identity
handler returned the first subject for a second account; the fixture was corrected
and the complete suite rerun successfully. Production verification was unchanged.

All 22 skips are real PostgreSQL tests (20 existing, 2 new). No migrations were
applied. The 36 passing integration tests include offline SQL/model validation and
synthetic HTTP tests, not live PostgreSQL proof. Display-name uniqueness and a
successful approved profile-completion transition have no executable tests yet:
their product contracts are absent. YAML parser verification could not run because
the local Python environment lacks PyYAML; no dependency was downloaded. OpenAPI
edits were reviewed as text. `git diff --check` passed.

### Exact changed-file manifest

Created:

* `src/Andril.Common/Accounts/Profiles/PrivateModuleProfile.cs`
* `src/Andril.Common/Accounts/Profiles/ProfileContracts.cs`
* `src/Andril.Common/Accounts/Profiles/PrivateProfileService.cs`
* `src/Andril.Persistence/Configurations/PrivateModuleProfileConfiguration.cs`
* `src/Andril.Persistence/Repositories/Common/PrivateProfileRepository.cs`
* `src/Andril.Persistence/Migrations/Accounts/20261116000100_AddPrivateModuleProfiles.cs`
* `src/Andril.API/Controllers/Common/PrivateProfilesController.cs`
* `src/Andril.API/Middleware/PrivateProfileExceptionHandler.cs`
* `tests/Andril.UnitTests/Common/PrivateProfileTests.cs`
* `tests/Andril.SecurityTests/Authentication/PrivateProfileEndpointTests.cs`
* `tests/Andril.IntegrationTests/Database/PrivateProfilePersistenceTests.cs`
* `docs/M2_FEATURE09.md`

Modified:

* `src/Andril.Common/Accounts/Onboarding/OnboardingStatusService.cs`
* `src/Andril.Common/Accounts/Onboarding/OnboardingContracts.cs` (comment only)
* `src/Andril.Persistence/DbContexts/AccountsDbContext.cs`
* `src/Andril.Persistence/Migrations/Accounts/AccountsDbContextModelSnapshot.cs`
* `src/Andril.API/Extensions/AccountServiceCollectionExtensions.cs`
* `tests/Andril.SecurityTests/Authentication/OnboardingEndpointTests.cs` (new repository fixture)
* `contracts/openapi/andril-api.v1.yaml`

Baseline and final branch: `Member-2`; HEAD unchanged at
`66d40f0bef0a15e5eb49568eb106e504401350ff`. Baseline clean; delivery has 7 modified
tracked files and 12 new files, all unstaged/uncommitted. No other members' code,
previous migrations, remotes, deployments or production data were touched.
