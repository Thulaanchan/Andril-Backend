# M2 Phase 3B controlled integration — 23 November 2026

Task: `M2-P3B-INTEGRATION-03`, with home-desktop override
`M2-P3B-INTEGRATION-03-GIT-HOME-R01`.
Acceptance definitions: the M2-supplied M6 decision
`AND-M2-P3B-M6-FINAL-CLEARANCE-20261123`, supplied directly in this chat.
Conditional approval authorizes disposable, synthetic, non-production integration.
Final production security acceptance remains pending.

## Delivery and baseline

- Checkout: `C:\Users\94752\Downloads\Andril-backend (2)\Andril-backend`.
- Git root is that checkout, with an ordinary `.git` directory and one worktree.
- Remote: `https://github.com/Thulaanchan/Andril-Backend.git`.
- Branch: `Member-2`; initial worktree/index clean; no active Git operation.
- Initial HEAD: `4cee90f9a03a58c5f138d27da467aaaa7009e266`.
- Live initial Member-2: `2eee3aabe18306c7bc3a4e926caafab0efa1e30f`.
- Live initial Develop: `69ab2e7969253d0141b5c63221fc3fb0ad89bab1`.
- Comparison merge base: `a3c21614d40fcccb59586ba8afc585e57a76e89a`.
- `b73e291ef2e658a57acca11e50b59b49a00e0f0b`: PRESENT, unpublished, preserved.
- `ddeabfb8c2d654b3a3e87adee55337d44f35c23d`: PRESENT, unpublished, preserved;
  its parent is exactly `b73e291`, whose parent is exactly `2eee3aa`.
- Existing merge `4cee90f` is preserved. The initial 186-commit outgoing ancestry
  includes previously merged Develop history; it is not 186 new M2 changes.
- Read-only live Git queries and fetch succeeded outside the network-restricted
  sandbox. No reset/rebase/cherry-pick/merge/pull was performed.
- The GitHub open-PR query returned no Member-2 → Develop PR at preflight.
- Final live Member-2 remained `2eee3aabe18306c7bc3a4e926caafab0efa1e30f`.
  Develop advanced independently to `12fa505d57408bd40988860d93f2a6f9f9737e7d`
  through M5 PR #290. A read-only fetch confirmed that the comparison merge base
  remains `a3c21614d40fcccb59586ba8afc585e57a76e89a`. No synchronization/merge
  into the local Member-2 branch was performed.

Publication is blocked by the existing licensed PhotoProcessor CI gate. No push,
PR, hosted database change or deployment is authorized while that gate is blocked.
The local commit SHA is recorded in the final delivery message and `git log`.

## Changed-file inventory

| Area | Files and purpose |
|---|---|
| Common lifecycle | `ModuleLifecycle.cs`: internal persistence materialization and friend-assembly access; existing domain validation/coordinator reused |
| Accounts persistence | `AccountsDbContext.cs`, `ModuleLifecycleConfiguration.cs`, new `ModuleLifecycleJournalConfiguration.cs`: lifecycle, receipt and audit model |
| Transaction adapter | New `ControlledModuleLifecycleStore.cs`: internal, explicitly opted-in disposable-local writer and evidence reads |
| Accounts migrations | `20261123113056_AddControlledModuleLifecycle.cs`, its designer and Accounts snapshot |
| Integration project | OpenAPI generator interceptor namespace in `Andril.IntegrationTests.csproj` |
| Existing M2 tests | `ModuleLifecycleMappingTests.cs`, `PrivateProfilePhotoPersistenceTests.cs`: include the authorized model and permit additive migrations |
| Controlled integration | `Lifecycle/ControlledLifecycleDatabase.cs`, `ControlledLifecycleHost.cs`, `Phase3BAcceptanceTests.cs` |
| CI inventory | `scripts/ci/member-02-required-db-tests.json`: all 24 lifecycle methods required in the PostgreSQL gate; 92 existing merged M3 PostgreSQL facts added to the explicit offline-only skip inventory after source verification |
| Explicitly authorized M4 test exception | One migration-count assertion in `ChatProfileForeignKeyCompatibilityTests.cs`; all existing composite-key checks retained |
| Report | This document |

No production startup, API controllers, hosted configuration, M4 implementation,
M5 implementation, private-profile identity rules or protected Home contract changed.

## Migration and transaction design

The Accounts migration uses the existing
`andril_identity.__EFMigrationsHistory` arrangement. It adds only:

- `andril_identity.account_module_lifecycles`, keyed by `(account_id,module)`;
- `andril_identity.account_module_lifecycle_receipts`, keyed by
  `(account_id,module,request_key)`;
- `andril_identity.account_module_lifecycle_audit`, keyed by
  `(account_id,module,revision)`, with a unique scoped request reference.

New foreign keys use RESTRICT. Membership references the actual Accounts key;
receipt references membership; audit references the scoped receipt. Checks constrain
canonical modules, active/inactive states, positive revisions, valid transition
metadata, intent hashes, nonempty request/correlation identifiers and finite times.
There is no legacy backfill or dependency on private-profile completion. Existing
starter selection and independent private profiles are preserved.

The writer uses READ COMMITTED plus a transaction-scoped advisory lock derived from
the verified subject. The account lock is acquired before any lifecycle/receipt
access, including absent first rows. All modules for that account serialize in one
deterministic order. Hash collisions can add contention but cannot grant access.
The primary key/ON CONFLICT protects first insertion; reactivation also checks the
previous state and revision in its UPDATE predicate and requires exactly one row.

Lifecycle, receipt and changed-state audit writes share one transaction. No-op
requests can add a receipt without a second transition/audit; exact receipt replays
write nothing. Domain validation runs before replay success, so replay cannot
bypass current denials. First activation time is never updated. Runtime grants
exclude that column. The coordinator receives PostgreSQL `clock_timestamp()` UTC
instants; changed state and receipt timestamps come from that database sample.
Table defaults also use database time. No application wall clock supplies a
lifecycle transition timestamp.

Failure before commit rolls back the transaction. A commit exception becomes an
unknown outcome, never a false success. The existing coordinator performs a fresh
scoped, authorized receipt read and does not issue a second write. Tests inject a
lost acknowledgement after a real database commit and verify durable reconciliation;
this is not evidence of a live TCP partition experiment. Missing receipts retain
the unknown result and map to safe test-host 503.

## Disposable PostgreSQL security

PostgreSQL 17.12 portable Windows binaries were obtained through the official EDB
download page and used only in a temporary loopback cluster on port 55439.
No service installation, hosted Supabase connection or production migration ran.
Each fixture validates loopback maintenance database `postgres`, creates a fresh
`andril_m2_test_<UUID-N>` database, then drops that exact database and its roles.

Migrations execute as a dedicated database-owner migration role, not as the
runtime role. Runtime is LOGIN/NOINHERIT/NOSUPERUSER/NOBYPASSRLS/NOCREATEDB/
NOCREATEROLE, receives selected source reads and lifecycle SELECT/INSERT plus
UPDATE of state/revision/state-change time, and cannot read migration history,
create/alter objects, delete journals, update audits/receipts, or alter first time.

The migration revokes PUBLIC table access and enables/FORCEs RLS on all three
new tables. Account/module/subject context is parameterized and transaction-local;
rollback/commit and pool reset clear it. Tests use a one-connection pool to check
reuse, and independent pooled connections to test real write concurrency.

GUC values alone are not authentication. Disposable source-table policies bind
each fixture's runtime role to its one synthetic account/subject, in addition to
checking transaction context. The lifecycle policies verify that source binding.
Direct SQL cannot escape by replacing account, module or all subject context.
No runtime role can change those policies. This account binding and its grants
exist only in the fixture. The migration makes no runtime grants. A general
shared production runtime identity/authorization deployment is not approved or
demonstrated by these tests. No security-definer function is used.

## Source-to-evidence and freshness matrix

| Prerequisite | Actual reader or existing interface | Observed freshness | Remaining limitation |
|---|---|---|---|
| Authenticated subject | ASP.NET JwtBearer validates signature, issuer, audience and lifetime; subject comes from verified principal | Per HTTP request | Synthetic signed test tokens only; no hosted identity contact |
| Confirmed identity | Existing `ISupabaseUserVerifier`/`SupabaseUserVerifier`; `IOnboardingIdentity` carries confirmed evidence | Real verifier parses a synthetic upstream `/user` reply per request | Write-time external revocation consistency is not accepted |
| Initialized account | Actual `AccountsDbContext.Accounts` query by verified subject, constrained by source RLS | Inside lifecycle transaction | No invented initialized flag; missing account denies |
| Standing/restrictions | `IControlledLifecycleAuthority` boundary | Read inside transaction | No authoritative lifecycle standing reader exists in Account's four fields; absent provider returns Unavailable; Satisfied/Denied are synthetic test evidence only |
| Current Terms/Privacy | Existing `IPolicyCatalogProvider` (`ConfiguredPolicyCatalog` is the application implementation); actual `PolicyRecords` rows | Catalog and receipts evaluated during transaction, versions and receipt times checked | Test host injects explicitly synthetic catalog versions; no accepted cross-source publication lock |
| Age | Actual `Demographics` row plus existing `AgeEligibilityPolicy`, evaluated at PostgreSQL UTC date | Row read inside transaction | Demographic self-report, not new identity proof; final write-time consistency pending |
| Social opt-in | Explicit HTTP intent plus the authority's current scoped opt-in evidence | Evaluated for each request and replay | Existing starter opt-in timestamp is historical and is not treated as a current revocation source; synthetic current authority in tests only |
| Consent withdrawal | No lifecycle-authoritative withdrawal reader found in existing account-policy model | No assertion of freshness | Missing authority is Unavailable; no withdrawal field or expiry duration invented |
| Account/module revocation | Authority boundary with observed/current versions and explicit denial | Synthetic version mismatch/denial tests inside transaction | No production version generator or synchronization contract invented |
| Private profile/Home permissions | Existing domain and `PrivateModuleAccess` guard | Existing protected boundary | Lifecycle does not create a profile or grant Home/discovery/matching/Chat/call/group permissions |

READ COMMITTED plus the account lock protects lifecycle writers. It does not
establish the pending multi-source authorization freshness/revocation contract.
Only the test assembly composes executable integration. Missing authority defaults
to Unavailable, never Satisfied. There is no production lifecycle DI registration.

## Isolated host and HTTP/OpenAPI

`ControlledLifecycleHost` lives entirely in the integration-test assembly. It uses
ASP.NET Core TestServer, explicit opt-in, real bearer middleware, scoped DI for
the real coordinator/store, and the real identity-source parser. Synthetic
identity upstream, standing and revocation sources are confined to that assembly.
The store additionally rejects non-loopback or non-random disposable database names.

Candidate activation/reactivation POST routes validate the canonical module,
nonempty UUID Idempotency-Key, explicit Social intent and positive bounded expected
reactivation revision. Unknown body properties, including account ID injection,
are rejected. Account identity is resolved on the server. Responses contain only
module, state, revision and state-change time, with Cache-Control no-store.
Safe Problem Details, per-subject rate limiting, signed-token denial, captured
ASP.NET logs and generated OpenAPI are exercised by the HTTP tests.
OpenAPI declares bearer security, canonical modules, the required UUID header,
request/result schemas and 200/400/401/403/404/409/429/503 response classes.
Not every declared error class needs an executable route branch; production
consent-denial/ambiguity mappings remain unapproved.

The test Home boundary calls the existing `PrivateModuleAccess` guard even with
synthetic completed-profile evidence and still denies. Production routes remain
unregistered. No public API contract or production endpoint was added.

## M6 acceptance evidence

Each QA row is a distinct `QAxx_...` method in `Phase3BAcceptanceTests`. All
database-dependent cases use real PostgreSQL; HTTP rows use the actual ASP.NET
pipeline. Authorization sources that have no existing production contract are
explicitly synthetic and do not establish production acceptance.

| ID | Evidence and tested behavior |
|---|---|
| QA-01 | HTTP: missing/expired bearer denied, no lifecycle write |
| QA-02 | HTTP + DB: actual foreign account body injection rejected; no foreign change |
| QA-03 | HTTP: invalid module, absent/empty UUID header and invalid content type rejected |
| QA-04 | Authorization + DB: persisted underage DOB denies Social |
| QA-05 | Authorization + HTTP: absent current opt-in and absent explicit Social intent deny |
| QA-06 | Authorization + DB: absent and superseded-version policy receipts deny; no fabricated time-expiry field |
| QA-07 | Authorization + HTTP: unavailable source, stale version and dependency failure fail closed |
| QA-08 | PostgreSQL: 12 independent concurrent identical initial writes yield one membership/receipt/audit |
| QA-09 | PostgreSQL: concurrent revision conflict cannot lose updates; first activation time preserved |
| QA-10 | PostgreSQL + HTTP: protected intent reuse of a UUID key conflicts; journals unchanged |
| QA-11 | PostgreSQL: injected audit constraint failure rolls back membership and receipt too |
| QA-12 | PostgreSQL + HTTP: post-commit lost-ack fault reconciles receipt; unknown outcome never returns HTTP success |
| QA-13 | PostgreSQL: Learning scope cannot write Hobbies; no implicit other-module membership |
| QA-14 | Authorization + DB: revoked standing denies both receipt replay and reactivation; no permission restoration |
| QA-15 | HTTP: active lifecycle still denied by the existing Home access guard |
| QA-16 | Restricted-role PostgreSQL: real foreign account SELECT/UPDATE/INSERT denied, including GUC replacement |
| QA-17 | Restricted-role PostgreSQL: single pooled connection clears committed/rolled-back context |
| QA-18 | Restricted-role PostgreSQL: DDL, RLS changes, migration-history reads and journal mutation denied; role flags checked |
| QA-19 | PostgreSQL + HTTP: persisted legacy starter has no lifecycle row and no Home grant |
| QA-20 | Integration: pre-cancellation, cancellation waiting for advisory lock and dependency failure cannot succeed; subsequent request recovers |
| QA-21 | HTTP + actual logging: exact minimal response, safe errors, no-store and absence of identifiers/keys/private data in captured logs |

Additional cases verify opt-in rejection, rate limiting/OpenAPI, downgrade/upgrade
preserving existing Accounts and an unrelated sentinel, successful HTTP reactivation,
and the absent-authority Unavailable default. CI's required PostgreSQL inventory
includes all 24 methods; missing execution cannot silently pass that gate.

## Verification and publication gate

Final execution counts and local commit are summarized in the delivery message.
Raw local TRX/build reports are under ignored `TestResults/Phase3B`; no credentials
or license contents are included in committed reports. Initial failures from a
noncanonical synthetic gender and premature TestClient creation were corrected.
Full offline regression also exposed two hard-coded migration-count assumptions;
the M2 assertion and the specifically authorized one-line M4 assertion were corrected.
An instrumented concurrent notification unit test hit its existing 10-second
readiness timeout while database tests ran concurrently; no M4 logic or timeout
was changed. The exact offline CI script subsequently passed when run without
the concurrent database workload. The offline inventory also lacked 92 existing M3 PostgreSQL facts
from merged history. Each was checked against its `MapCapturePostgresFact` source
attribute before being added to the offline-only inventory. M2 database tests
remain mandatory executions, not permitted skips in the relational run.

| Final independent suite | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Unit/Common, with CI coverage | 5,284 | 0 | 0 |
| Full security, with CI coverage | 826 | 0 | 0 |
| Architecture, with CI coverage | 7 | 0 | 0 |
| Offline integration, with CI coverage | 860 | 0 | 214 explicitly inventoried DB cases |
| Required M2 PostgreSQL/HTTP integration | 61 | 0 | 0 |
| Lifecycle subset of preceding row (not additive) | 24, including all 21 official QA methods | 0 | 0 |

`member-02-qa.ps1 Offline` passed its actual coverage parsing and exact TRX
execution/skip checks. Coverage line/branch counters: unit 36982/43305 and
10378/12236; security 8204/20263 and 2831/8929; architecture 176/15498 and
128/7958; offline integration 11680/26696 and 3481/11113. These are suite-specific
counters, not aggregate product coverage or production security approval.
The final relational TRX also passed the repository's exact required-case verifier.
Release solution/analyzer build, audited solution/processor restores, structure,
synthetic verifier checks, staged diff checks and Gitleaks scans passed. The
separate licensed processor build is the blocked exception described below.
The full commit ancestry and exact staged source had zero Gitleaks findings.
An incidental broader scan including ignored local artifacts found two generic
API-key matches in pre-existing `.vs/Andril.slnx/config/applicationhost.config`
provider `sessionKey` attributes. That Visual Studio configuration is ignored,
untracked, unchanged and excluded from the staged export; it is not part of this
delivery. No local provider configuration was altered.

Independent checks include Release solution build, lifecycle/unit/Common tests,
full security/architecture/offline integration, all required disposable M2 database
and HTTP cases, generated OpenAPI, audited NuGet restores, structure checks,
synthetic TRX-verifier checks, git diff checks and Gitleaks 8.30.1 scans.
Earlier recompilations emitted two existing xUnit2031 warnings in unchanged M3
capture-session tests; the final incremental Release/analyzer build had zero
warnings and zero errors. Fixture cleanup verified zero remaining disposable
databases and lifecycle test roles, and the temporary cluster was stopped.

**Licensed PhotoProcessor CI gate: BLOCKED — LICENSE NOT CONFIGURED.**
`docs/M2_AUTOMATED_QA_PIPELINE.md` and `scripts/ci/member-02-qa.ps1` require
`Processor -ProcessorLicenseFile <external-path>` for local validation, passing
the existing complete license file through MSBuild `SixLaborsLicenseFile` without
copying it. GitHub uses repository secret `SIXLABORS_LICENSE_KEY` only in the
licensed build/test step. The package also supports MSBuild `SixLaborsLicenseKey`
or a discovered `sixlabors.lic`. Values must be valid for this assembly.

No process/user/machine environment setting, evaluated license MSBuild property,
documented local license file or project UserSecretsId was configured. License
contents were not displayed, copied or committed. The separate audited processor
restore succeeded; its unchanged Release licensing task failed with “No Six Labors
license found.” Processor tests could not run. No license check, project or CI
workflow was weakened. Full CI verification and publication are not claimed.

## Remaining decisions and manual PR draft

Pending: accepted write-time authorization freshness/revocation consistency;
authoritative standing/withdrawal/current-opt-in sources; receipt/audit retention;
account deletion/erasure policy; production runtime binding/grants and DI/API
activation; production failure mappings; final M5/M6 security acceptance.
There is no receipt-expiry duration or pruning workflow. Disposable database
teardown is test cleanup, not a production retention policy.

Suggested title: **M2 Phase 3B — Lifecycle Persistence and Controlled Integration**

Suggested description after the publication gate is resolved:

> Adds Accounts lifecycle, durable receipt and committed audit persistence with
> atomic scoped transitions and an isolated PostgreSQL/ASP.NET integration host.
> Includes M6 QA-01–QA-21, restricted-role/RLS/pooling checks and generated OpenAPI.
> Production lifecycle services/endpoints remain disabled; freshness, retention,
> erasure and final M5/M6 acceptance remain pending. See the verification report
> for exact results and the outstanding licensed PhotoProcessor gate.

Manual source/target: `Member-2` → `Develop` in the verified backend repository.
No PR was created or updated. No push or deployment was performed.

Existing branch-only commits since merge base `a3c2161`, followed by the new local
integration commit identified in the delivery message:

```text
c4f6eac12b9dd134d55bb4c6013b584248a6bbc5 test(m2): expand onboarding security regression coverage
fe52264b5f901786ff04b22a7f685c4ef386bf04 test(m2): protect module lifecycle authorization boundaries
7acc07809f46a4d590ca41c0082a3dcdf2e4c8dc feat(m2): add offline module lifecycle domain model
3cd6e96e84db57bf27bc0be56a42aa200b77ff00 M2: Configure development-only Swagger UI
2eee3aabe18306c7bc3a4e926caafab0efa1e30f test(m2): strengthen lifecycle authorization evidence contracts
b73e291ef2e658a57acca11e50b59b49a00e0f0b feat(m2): add offline lifecycle persistence mapping
ddeabfb8c2d654b3a3e87adee55337d44f35c23d feat(m2): add offline lifecycle transaction coordination
4cee90f9a03a58c5f138d27da467aaaa7009e266 Merge branch 'Develop' into Member-2
```

Classification: **LOCAL IMPLEMENTATION COMPLETE — PUSH BLOCKED** by the licensed
processor gate. Controlled non-production integration is verified. Final security
approval remains pending and production activation is not authorized.
