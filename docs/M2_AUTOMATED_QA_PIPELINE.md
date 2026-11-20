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

For a failed audit, use the diagnostic code to distinguish data access failures from package findings. For test failures, start with the suite and method ID in the TRX and reproduce through the normal harness. For relational failures, distinguish service startup or readiness from incomplete selection, schema failures and permission failures. The first published run establishes Linux compatibility, actual service behavior, account policy compatibility and hosted artifact handling.

## First execution

After owner review and M6 coordination, publish the approved QA commit on Member-2 and open its automatically triggered run. Verify the SHA, each required job, the NuGet audit, four offline Cobertura reports, all manifest-listed M2 passes including the restricted runtime case, and the four artifact groups. Record the actual outcomes before proposing repository setting changes. Once manual dispatch is registered, select Member-2 when dispatching.
