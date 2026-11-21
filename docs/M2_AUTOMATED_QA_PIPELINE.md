# Member-02 automated QA

[ANDRIL Member-02 QA](../.github/workflows/member-02-qa.yml) implements Option A and M2-QA-D01 through D14. It verifies a Member-2 commit and publishes reports. Automated QA carries no deployment authorization.

## Triggers and ownership

A push to Member-2 starts the workflow. Manual dispatch checks the exact Member-2 branch ref before checking out source. GitHub requires the workflow definition on the repository's default branch to register manual dispatch, and the person dispatching needs repository write access. The Member-2 push trigger can run after publication without default-branch registration. [GitHub manual dispatch](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow)

Coordinate publication, required-check decisions and ownership with M6 Lucina. The team-wide workflow continues to run on push and pull request, so a Member-2 push overlaps on restore, Release compilation and offline tests. The PostgreSQL job restores and builds the integration project graph on its separate runner. Each commit gets its own run. The .NET jobs allow 35 minutes for restore, instrumentation and tests. The scan and report jobs allow 15 and 5 minutes. Review the account's runner-minute and artifact-storage allowances, organization Actions policy and first-run durations with M6. [GitHub Actions billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions)

## Jobs and gates

| Job | Required work |
| --- | --- |
| Offline | Fresh audited solution restore, Release build with existing analyzers, synthetic verifier tests, all four offline test projects and validated coverage. |
| PostgreSQL | Pinned PostgreSQL 17 service, integration project restore and build, local database and bootstrap-role probes, and all manifest-listed M2 relational cases. |
| Secrets | Verify the Gitleaks binary archive checksum and scan the commit ancestry reachable from HEAD and the tracked checkout. |
| Report | Publish each gate's result and fail if any required job failed, skipped or was cancelled. |

All jobs use GitHub-hosted Ubuntu 24.04. The .NET jobs use [global.json](../global.json), whose SDK floor is 10.0.400 with latest-feature roll-forward. The selected SDK and runner platform appear in the summary. Workflow permissions are limited to contents read, and the report job has an empty permission set. Checkout credentials are not persisted. Official checkout, SDK setup and artifact upload actions have full commit pins with version comments.

The [CI NuGet configuration](../scripts/ci/NuGet.Member02.config) selects NuGet.org for packages and audit metadata. Restore uses a fresh job-local package directory and bypasses HTTP cache. It audits direct and transitive dependencies down to low severity. NU1901 and NU1902 are reportable warnings. NU1900, NU1903, NU1904 and NU1905 fail the gate for audit retrieval failure, high and critical vulnerabilities, and an audit source without vulnerability data. SDK or restore failure also fails the job. Review advisory evidence before changing shared dependencies. The Release build includes the existing compiler and analyzers, including xUnit. Its report includes severity, diagnostic ID, project and file/line. The baseline includes two M3 xUnit2031 warnings. Compiler and analyzer checks have a narrower scope than comprehensive SAST. [NuGet audit diagnostics](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages)

## Disposable database verification

The Docker Official Image for PostgreSQL 17.12 Bookworm is pinned by digest. Docker publishes its port on runner IPv4 loopback only. A container health check waits for the server. The job authenticates through that exact published port and verifies the server version, the maintenance database postgres, and the ability to create and force-drop a random database, create and grant a role, transfer ownership, set role and clean up. The container administrator only provisions the fixture login. The login uses a random per-job password and has CREATEDB and CREATEROLE with neither superuser nor RLS-bypass rights. Application permission assertions use the restricted runtime and client-like roles. The existing fixture drops its databases and explicitly cleans up uniquely named cluster roles. The runner discards the entire cluster after the job. [Docker Official Image provenance](https://hub.docker.com/layers/library/postgres/17.12-bookworm/images/sha256-8d174ddfb292f26b6ec8d97f55d944ccab547fa8bfe9e4fd3c24afdc819ee423)

Only the relational test step supplies ANDRIL_M2_TEST_POSTGRES and enables ANDRIL_M2_TEST_POSTGRES_ALLOW_ROLES. ANDRIL_M3_TEST_POSTGRES stays unset. The offline step clears all three variables and its job has no database service.

The [exact test manifest](../scripts/ci/member-02-required-db-tests.json) lists the 36 M2 relational facts derived from 35 LocalPostgresFact and one IsolatedRolePostgresFact declaration. It reconciles with actual offline VSTest identifiers. The 44 MapCapturePostgresFact cases listed separately are expected offline skips and excluded from the M2 database filter. The current list has no parameterized cases.

| Test class in Andril.IntegrationTests.Database | Required M2 cases |
| --- | ---: |
| AccountDemographicsPersistenceTests | 3 |
| AccountPersistenceTests | 4 |
| AccountPolicyPersistenceTests | 3 |
| LanguagePreferencePersistenceTests | 4 |
| OnboardingResumePersistenceTests | 1 |
| PrivateProfilePersistenceTests | 4 |
| PrivateProfilePhotoPersistenceTests | 12 |
| StarterModulePersistenceTests | 5 |
| **Total** | **36** |

The Accounts fixture applies all eight Accounts migrations through 20261120152336_AddPrivateProfilePhotos. The offline gate requires the Accounts and photo model checks to pass. These check snapshot consistency, migration order, additive forward and rollback SQL, relationship and index definitions, state and cleanup checks, and RLS setup. The relational cases cover rollback/reapply, constraints, ownership, uniqueness, replacement, idempotency, concurrent reservations, cleanup claims and retry scheduling. The restricted runtime case verifies upload, replacement and retrieval and denies schema edits, migration history access, metadata deletion, unauthorized account fields and client access. Hosted grants are outside this verification.

The TRX checker joins executions to test definitions using fully qualified method identifiers. Every required case must appear with the expected outcome. Invalid or missing TRX, missing cases, unexpected selections, unsuccessful results and unexpected skips fail. For a future theory, put the method and exact display names in the manifest's cases list, repeating a name for identical rows. The verifier checks multiplicity and distinct execution IDs. Update the manifest alongside new database tests and check it against the adapter's actual discovery.

## Coverage, secret findings and reports

VSTest uses the included Microsoft.CodeCoverage 17.14.1 collector and emits Cobertura XML with line, branch and assembly data per offline suite. The gate rejects missing or invalid coverage. Identical attachment copies count as one report. Suite percentages remain separate because they cover overlapping code. There is no minimum coverage percentage.

Gitleaks 8.30.1 uses a SHA-256-verified archive and scans all reachable Member-2 history, including commits in the first push, plus a copy of tracked source without Git metadata. Full redaction is enabled. Findings, unavailable tooling and scan errors fail the job. Reports retain rule IDs, commit IDs where available, line numbers and path hashes. Investigate possible synthetic matches privately with the owner before considering an exception. [Gitleaks release assets](https://github.com/gitleaks/gitleaks/releases/expanded_assets/v8.30.1)

Open **Actions → ANDRIL Member-02 QA → Summary** for the intended commit. Review the four jobs and download the available artifacts from the run page. Reports include sanitized TRX, validated coverage, audit and compiler/analyzer diagnostics, relational verification, redacted scan metadata and the consolidated summary. Retention is seven days. Raw output, parameter values and connection configuration stay outside uploaded reports. Counts are per-suite executions. The 36 M2 cases appear as offline skips and as relational executions. The 44 unrelated database cases only appear as offline skips. [Downloading workflow artifacts](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts)

For a failed audit, use the diagnostic code to distinguish data access failures from package findings. For test failures, start with the suite and method ID in the TRX and the safe failure JSON described below, then reproduce through the normal harness. For relational failures, distinguish service startup or readiness from incomplete selection, schema failures and permission failures. Historical run outcomes are evidence for that commit and environment, not fresh local verification.

## Safe failure diagnostics

Each processed suite writes `<suite>-failures.json`; the relational artifact contains `m2-postgres-failures.json`. The existing artifact-directory upload and seven-day retention publish it without a workflow change. The job details, step summary and consolidated summary list the failed count, method names, safe classification, SQLSTATE, constraint and source location. They link the report by filename within the suite artifact. No raw test output is written to Actions logs.

The version 1 envelope has `schemaVersion`, `failedCount` and `failures`. Each failed execution has these fields:

| Field | Validation / unavailable behavior |
| --- | --- |
| `testName`, `outcome` | Definition-joined method identifier, bounded to 512 ASCII identifier characters; outcome must be a recognized VSTest outcome. Parameter display data is excluded. |
| `duration` | Nonnegative invariant TimeSpan, at most one day; otherwise null. |
| `exceptionType`, `innerExceptionType` | Literal trusted framework/project type allowlist. Only an explicit exception-header format and explicit inner marker are recognized; otherwise null. |
| `assertionCategory` | Fixed categories for recognized xUnit assertion headers; otherwise null. No assertion arguments or expected/actual strings. |
| `sqlState` | Explicit PostgreSQL exception header or SqlState field, checked against the supported SQLSTATE allowlist; otherwise null. |
| `constraintName` | Explicit ConstraintName field or recognized PostgreSQL constraint-error header, checked against the photo constraint/index allowlist; otherwise null. |
| `sourceFile`, `sourceLine` | Known Git-tracked C# file under src/ or tests/, no traversal or duplicate separators, positive bounded line. An absolute path is normalized only when its prefix exactly matches this checkout; arbitrary absolute/external paths are rejected. Only the relative result is published. |
| `diagnosticCategory` | Assertion, PostgreSql, EntityFramework, KnownException, Unclassified or ExtractionUnavailable. |

Missing evidence stays null. The supported SQLSTATE list is deliberately finite; unknown codes and constraints remain unavailable until explicitly reviewed. A message mentioning a type is not evidence of an inner exception unless the explicit marker is present. These are classifications extracted from an untrusted report, not independent proof of a database failure. Multiple failed executions remain independent, including theory rows sharing the same method. A passing suite has an empty failures array. An invalid/unreadable report produces `failedCount: null`, `diagnosticCategory: ReportUnavailable`, an empty failures array and a failed QA gate.

TRX publication rebuilds a minimal XML document using validated method identities, outcomes, durations, generated IDs and recomputed counters. It does not copy raw XML nodes, messages, stacks, arbitrary attributes, timestamps, attachments or machine metadata. This keeps method/outcome/count compatibility with the existing verifier; sanitized theory display arguments remain omitted as before. Diagnostic extraction occurs before publication and does not modify the original document. Raw exception text never enters the sanitized TRX or JSON.

XML processing prohibits DTDs and external resolution, limits file size and document characters to 32 MiB, and limits each diagnostic message and stack to 64 KiB. Oversized XML fails verification; oversized diagnostics are classified ExtractionUnavailable without changing the test outcome. Raw reports live under a random private temporary directory. Published and raw directories must have disjoint trees, checked before use. The existing finally cleanup validates the exact temporary parent and generated directory name before deletion. Never upload a raw results directory or add raw report attachments to artifact paths.

Run `pwsh -NoProfile -File scripts/ci/Test-VerifyMember02Results.ps1` for existing identity/count/skip/missing-result checks, QA-DIAG-01 through QA-DIAG-20, and execution of the actual consolidated workflow gate with synthetic success, failure, cancelled, skipped and unauthorized-ref inputs. No additional packages are needed. Tests cover malicious messages, synthetic credentials, SQL parameter text, arbitrary XML attributes, unknown values, absolute/traversal paths, malformed XML, XXE, resource limits, multiple failures, sanitization round trips, extraction failure and raw/published path overlap. They use controlled temporary files and restore environment state.

## Option C investigation baseline — 2026-11-21

The repository was clean on Member-2 at `143228e3d3579f4c761d68ecdc58b216dc0f2178` before these diagnostic edits, with no staged/untracked work. Its preceding commit was `c4a5687`. The committed workflow exists. global.json requests SDK 10.0.400 with latestFeature roll-forward; the installed selection is 10.0.404. No branch changes or Git mutations were performed.

The locally available historical `member02-postgres-1.zip` was inspected without modification. Its sanitized TRX contains 36 executions: 35 passed, one failed, zero skipped. The failure is `Andril.IntegrationTests.Database.PrivateProfilePhotoPersistenceTests.DatabaseRejectsMismatchedScopeDuplicateRequestsAndMultipleCurrentOrPendingRows`. No Output element or original exception evidence survives. A round trip through the revised sanitizer retains the failure, produces one safe failure record with unavailable exception fields, and remains rejected by the relational verifier. Those counts describe the historical artifact, not a fresh relational run.

The existing LocalAccountDatabase fixture (AccountPersistenceTests.cs:124–173) checks the maintenance database postgres and a literal loopback hostname, disables pooling/error-detail inclusion, creates `andril_m2_test_<random GUID>`, applies Accounts migrations, and force-drops only that generated database, including initialization-failure cleanup. It does not accept a supplied database name for deletion. No PostgreSQL process, registered PostgreSQL service, installed PostgreSQL/Docker command, or M2 test connection was found through the bounded local checks, including unsandboxed read-only checks. The current server version, process ownership, privileges and actual disposable schema therefore could not be verified. No instance was started or initialized and no database connection was attempted.

The Release integration project built with existing restored dependencies after a sandboxed attempt exited without useful diagnostics. Running the exact requested test filter with all three database opt-ins removed in the test child selected exactly one test: zero passed, one skipped, zero failed. Exit code zero is **not reproduction or a relational pass**. M3 and the role-test opt-in stayed disabled. Reproduction with PostgreSQL remains blocked until an existing disposable loopback maintenance environment can be verified. The normal focused command is:

```powershell
dotnet test tests/Andril.IntegrationTests/Andril.IntegrationTests.csproj --configuration Release --no-build --no-restore --filter "FullyQualifiedName=Andril.IntegrationTests.Database.PrivateProfilePhotoPersistenceTests.DatabaseRejectsMismatchedScopeDuplicateRequestsAndMultipleCurrentOrPendingRows"
```

Two fresh offline checks passed without skips: AccountPersistenceTests.MigrationMatchesModel_AndScriptOnlyTargetsAccountSchema and PrivateProfilePhotoPersistenceTests.MigrationIsAdditiveScopedAndMatchesSnapshot. These establish model/snapshot and generated-migration consistency, not the installed PostgreSQL catalog or runtime constraint behavior.

### Source findings and pending root cause

Classification: **J — root cause not yet established**. No exact failed assertion, assertion exception, EF exception, PostgreSQL exception/inner type, observed SQLSTATE or observed constraint can be recovered from the historical sanitized report or the skipped local execution.

The complete failed method (PrivateProfilePhotoPersistenceTests.cs:150–195), its seed helper, photo entity, store, Accounts context, profile/photo mappings, photo migration and snapshot were inspected. A concrete candidate for later controlled verification is overlapping rejection conditions: line 156 reserves a PendingStorage row, while lines 174–175 try a second PendingStorage row for the same profile with module hobbies and expect ForeignKeyViolation (23503). The pending unique index is keyed by profile_id, so this input can also conflict with `uq_profile_photo_pending` (a 23505 uniqueness rejection). This supports a potential D/E test-data/expectation issue; it does **not** establish which assertion failed in GitHub. No conclusion about constraint evaluation order or actual SQLSTATE is asserted without execution.

The probes use raw ExecuteSqlInterpolatedAsync, not SaveChanges for the deliberately invalid inserts. ReserveAsync commits its own transaction before the first probe. Thus a retained Added entity after a failed SaveChanges or an already-aborted explicit probe transaction is not demonstrated by this source. Later probes explicitly target check, pending, current and request rejection; execution is needed to establish how far the method gets.

| Protection | Mapping / migration / snapshot source evidence | Live database evidence |
| --- | --- | --- |
| Profile/module FK | fk_profile_photo_profile_module on (profile_id, module) → private_module_profiles(id, module) | Unavailable |
| Scoped request uniqueness | uq_profile_photo_request on (profile_id, module, request_id) | Unavailable |
| Immutable object key uniqueness | uq_profile_photo_object unique on object_key | Unavailable |
| One current photo | uq_profile_photo_current, profile_id, WHERE is_current | Unavailable |
| One pending upload | uq_profile_photo_pending, profile_id, WHERE state = 'PendingStorage' | Unavailable |
| Valid state and metadata | ck_profile_photo_state, ids, module, key, hashes, media, times, late_write | Unavailable |
| Cleanup metadata | ck_profile_photo_cleanup and ix_profile_photo_cleanup | Unavailable |

The mapping declares distinct index metadata names for current and pending indexes, while the migration/snapshot produce the intended profile_id unique indexes with separate filters. The offline consistency check passed. No evidence currently establishes a production domain, mapping or migration defect; live verification remains required.

Recommended next action is to verify an existing disposable loopback PostgreSQL maintenance instance, record its version and ownership, and run this single test once with a private raw report. Inspect only that generated database's catalog and isolate the four intended scenarios if necessary. If the overlapping-violation candidate is confirmed, recommend a separately approved **test-only** correction: move the initial ReserveAsync call from line 156 to immediately after the foreign-key assertion, so that probe encounters no existing pending row. Assert SQLSTATE 23503 and the exact composite FK name, then independently arrange the pending/current/request cases so each violates only its target protection. Keep the invalid-current-PendingStorage check as a separate check-constraint probe. No correction is applied here.

Alternatives are a state-valid Stored/current foreign-key probe before any photo reservation, or a dedicated seeded profile/database for each rejection case. The former changes the probe state and needs careful review of state checks; the latter provides stronger isolation but adds setup time. Merely accepting either SQLSTATE or weakening/removing the pending index would reduce the guarantee and is not recommended. If runtime evidence identifies a different failure, revise the proposed correction from that evidence. Owner approval is required before changing existing assertions, application code, mappings or migrations; the complete 36-case M2 relational regression follows the approved correction.

Production registrations still use UnavailableProfilePhotos (AccountServiceCollectionExtensions.cs:26), whose IsApprovedAndConfigured is false (ProfilePhotos.cs:308). Photo processing, onboarding readiness and module authorization were not changed. No hosted service, user-data database, role, database constraint, application dependency, manifest case, workflow gate, trigger or retention setting was modified. No commit or push was performed. No fresh GitHub Actions run is claimed.

Local diagnostic validation passed: PowerShell syntax checks on all three scripts, the existing verifier regression cases, all QA-DIAG-01–20 scenarios, historical sanitized-artifact round-trip rejection, and actual consolidated-gate failure/cancellation/skip/ref enforcement using synthetic inputs. Existing actionlint 1.7.12 accepted the unchanged workflow; existing Gitleaks 8.30.1 found zero findings in the four authorized changed files. Both existing tool archives were checked against official release SHA-256 lists, and each executable was checked against its verified archive before use. Raw local logs, temporary report copies and synthetic probes were cleaned up outside the repository. No new workflow execution or full relational regression was performed.

## First execution

After owner review and M6 coordination, publish the approved QA commit on Member-2 and open its automatically triggered run. Verify the SHA, each required job, the NuGet audit, four offline Cobertura reports, all manifest-listed M2 passes including the restricted runtime case, and the four artifact groups. Record the actual outcomes before proposing repository setting changes. Once manual dispatch is registered, select Member-2 when dispatching.

## Standalone profile photo processor QA

The offline job requires a separate audited restore, licensed Release build and unfiltered run of the standalone processor tests. Only `Andril.ProfilePhotoProcessor` directly references ImageSharp 4.1.3. The test project uses the transitive reference. The production API still resolves the unavailable inspector.

1. The owner configured the `SIXLABORS_LICENSE_KEY` Actions repository secret. Keep its complete licensed value current and valid for `Andril.ProfilePhotoProcessor`. Only the licensed build and test step receives the secret.
2. `ProcessorRestore` restores the standalone graph with the same audited NuGet configuration and flags as the solution, with a separate job-local package cache. It does not receive the license.
3. `Processor` removes the secret from its environment before invoking any child process. On Linux it writes the value to a temporary mode-0600 file under a mode-0700 directory outside the checkout and artifact roots. The non-incremental Release build receives only the path through `SixLaborsLicenseFile`. Build output is discarded. The file is removed in a `finally` block before tests run, with outer cleanup for the private directory. Forced runner termination relies on runner teardown.
4. Tests run without build, restore or a filter. The existing verifier requires a nonempty, valid TRX and zero unsuccessful or skipped cases. Only sanitized test reports and safe summaries are published under the existing retention policy. A missing secret, license error, build failure or verification failure fails the offline job.
5. Local validation can use `ProcessorRestore` with a private `RUNNER_TEMP` and `Processor -ProcessorLicenseFile <external-path>`. The script never copies or removes that local license. GitHub runs require the step secret and reject the local override.

### Image processing and release boundaries

The standalone normalizer handles supported still 8-bit RGB and grayscale JPEG/PNG, including alpha in PNG. It interprets untagged samples as standard sRGB, accepts consistent sRGB declarations, and rejects embedded ICC, CMYK, conflicting color declarations and unsupported bit depths. It composites over white before resizing, applies EXIF orientation, and writes a fresh 8-bit JPEG at quality 85. The JPEG has no source metadata or embedded profile and assumes standard sRGB interpretation.

PNG framing and checksums and JPEG marker/scan framing are checked before decoding. ImageSharp performs pixel decoding in a restricted configuration with strict segment handling where supported. For JPEG, that setting is not a complete entropy validator, and the pinned reader can stop a damaged scan early. The normalizer rejects detected incomplete output, including non-opaque JPEG pixels, but parser regression tests do not prove rejection of all malformed codestreams. The two-slot gate and allocator limits are process-local controls. Cancellation is cooperative. Production admission, hard deadlines, OS-level worker isolation, and deployment validation remain separate prerequisites.
