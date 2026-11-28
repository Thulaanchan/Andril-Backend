# Phase 3A account-owner lifecycle and media removal

M2 authority: `AND-M2-PHASE03A-FINAL-CLOSURE-DECISIONS-20261128`, decisions
A01–A05, B01–B06, D13–D19, E01–E07 and G01–G07. This records M2 scope;
it does not grant M6 administrative permissions or approve another owner's
production consumer. The original 15 deferred business records remain unavailable.
They are unrelated to the 15 Learning offline skips reconciled previously.

## Storage

The Accounts forward migration adds `account_owner_states`,
`account_owner_receipts` and `account_owner_audit`. It preserves published
migrations and all existing account/module, financial and media rows. There is
no Active backfill: establishment requires a confirmed identity, resolved
account and explicit `MayEstablishActive` from the current authority. Missing
authority never becomes Satisfied. Historical restrictions are not reclassified.

`PostgresAccountOwnerLifecycleStore` reuses the established subject advisory
transaction fence. It resolves the owner, sets transaction-local subject/account
scope, reads current revision and operation-specific authority, applies the
existing coordinator, and atomically writes state, receipt and audit. It checks
authority again before commit. The authority implementation must coordinate its
own revocation writers with this fence; merely returning version identifiers is
not a distributed consistency guarantee. Unknown commits return no success and
are not automatically retried.

All three tables enable and force RLS. No client/runtime grant is introduced by
the migration. Restricted runtime provisioning and its authoritative evidence
source require operator/security review. The database rejects revision/time
regression and updates to Deleted. Foreign keys restrict physical account removal.
Downgrade refuses to erase retained lifecycle data transactionally.

Deactivation/reactivation leave profiles, memberships, balances and preferences
untouched. Account Active is a necessary condition, not a module/resource grant.
Suspended self-actions and Deleted restoration remain denied. No administrative
permission names or suspension-release procedure were invented.

## Explicit API composition

`AddAccountOwnerLifecycle` requires an explicit scoped authority factory.
`MapAccountOwnerLifecycle` supplies the following candidate contracts:

| Method | Path | Request | Response |
| --- | --- | --- | --- |
| GET | `/api/v1/account/lifecycle` | None | State, revision, changedAtUtc |
| POST | `/api/v1/account/lifecycle/deactivate` | requestKey UUID, expectedRevision | Same minimal projection |
| POST | `/api/v1/account/lifecycle/reactivate` | requestKey UUID, expectedRevision | Same minimal projection |
| POST | `/api/v1/account/lifecycle/delete` | requestKey UUID, expectedRevision, confirmDeletion=true | Same minimal projection |

All routes require authenticated, confirmed identity. Unknown body properties,
including client account identifiers, are rejected. Deletion requires explicit
confirmation. Safe Problem Details distinguish 400, 401, 403, 409 and 503 without
private source errors. Apply the application's existing account no-store behavior
to binding/authentication failures as well as normal responses, and complete the
reviewed rate-limit/OpenAPI/activation contract before enabling shared routes.

The shared application startup intentionally calls neither composition method.
The signed-bearer PostgreSQL test host is the executable integration evidence;
its confirmed-identity/standing sources are explicitly synthetic.

`ActiveAccountOwnerFilter` checks stored state on every opted-in protected request.
The test retains the same valid signed token through deactivation, reactivation
and deletion and verifies denial/restoration/terminal denial. The filter does
not terminate provider sessions or revoke already admitted calls/streams. Each
consumer must participate in the approved read-release/write-commit revocation
protocol before project-wide access enforcement is claimed. No cross-owner
endpoint was changed or implicitly activated.

The narrow owner status/reactivation routes do not grant normal module access
to a deactivated account. Reactivation leaves independent moderation, consent,
module eligibility and permissions intact.

## Deletion and retention

Deleted is a durable terminal access state with no recovery window. This slice
does not physically purge accounts or cascade-delete any module/financial data.
H01 requires the actual approved retention policy; no global period was found.
Financial, security, moderation and audit obligations remain retained. Existing
AI deletion scheduling interfaces are downstream dependencies, not proof that
all external stores have been erased. No provider-session revocation is claimed.

M6 must provide actual suspension/release identifiers, verified owner-request
handling for assisted deletion, administrative authority and protected reason/
actor/outcome auditing. No administrative endpoint is implemented here.

## Private media

`IProfilePhotoService.RemoveAsync` requires confirmation and reuses the existing
authorized profile transaction. The store detaches only the specified owned
Stored photo. Replays cannot remove a newer photo. A pending replacement conflicts.
Detached object keys are retained, with no physical delete or automatic cleanup
claim. Unsettled reclaimed writes require provider/retention review and fail closed.
This deliberately separates user-visible reference removal from irreversible purge.
No new photo-removal HTTP route or additional-photo transport is activated.

Replacement and licensed decoding remain on the existing architecture. The
additional-photo metadata concurrency test checks the existing three-slot
constraint under twelve competing upserts. This is relational metadata evidence,
not acceptance of a completed additional-photo upload/finalization pipeline.

## Verification and outstanding integration

The mandatory PostgreSQL inventory retains all 106 original identities and adds
11 exact account-owner/media identities. They execute against disposable local
PostgreSQL and in CI; offline skips must match the exact manifest. Skipped database
tests are never reported as executed passes. Existing wallet/Economy, privacy,
security and architecture regressions remain mandatory.

Pending consumers: M1 Learning/CV/Portfolio/Diary/AI, M3 Hobbies/Map/Flight Points,
M4 communication/session/notification dispatch and M5 Social discovery/matching/
consent. FP-B01/FP-B02 original handoffs and the original 12-screen mapping were
not recovered. No reward ledger, matching algorithm, notification service, new
quota, retention duration, or Phase 4 functionality is introduced.

Restricted lifecycle/limited-mode and user-facing remote-session controls remain
rejected by M2. Existing operation restrictions, logout, authentication and privacy
remain. QA-B01 remains open until actual attributable independent review evidence
is reconciled; CI and a merge action are not substituted for a submitted review.
