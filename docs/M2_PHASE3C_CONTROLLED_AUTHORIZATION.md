# Phase 3C controlled lifecycle authorization

Scope approved by AND-M5-P3C-CONSOLIDATED-RESPONSE-20261124 and
AND-M6-M2-P3C-CONSOLIDATED-FINAL-CLEARANCE-20261124. This composition uses a
disposable loopback database and test-owned authorities. Production controllers,
starter checks, Home authorization, OpenAPI and dependency injection retain their
existing behavior.

## M2 boundary

`LifecycleProfileAuthorization` evaluates `LifecycleProfileEvidence` and
`PrivateProfilePurposeEvidence` against the verified actor, exact account,
canonical module, current membership revision and owned profile identity.
`IControlledProfileAuthority.ReadAsync` supplies operation-specific standing,
permission, freshness and expiry from controlled evidence. Missing or
unavailable evidence denies the operation. The operation names apply to private
profile creation, reading, editing, photo reservation, metadata and buffered
content reads, and photo finalization. No positive result is cached between
requests. `ModuleNavigationService.ReadOwnStatusAsync` returns navigation
information and conveys no permission.

`ControlledModuleLifecycleStore.WithProfileAsync` takes the same transaction
advisory lock as lifecycle writes using
`pg_advisory_xact_lock(hashtextextended(subject, 0))`. Its read-committed transaction
enters the account fence, loads the exact module membership and current
evidence, resolves the owned profile identity, authorizes the purpose, then
locks the protected profile row. The transaction stays open through the
protected database operation and a second authority and revision check, until
commit or rollback. Read delivery uses a bounded in-process callback under the
fence after the final check. A caller must deliver a buffered response at its
authorized boundary and obtain new evidence for a subsequent request.

Photo finalization uses `PrivateProfilePhotoStore` in that same transaction,
with the authorized profile/module, exact upload and active lease, and
verification against the stored byte length and hash. Prepared files may remain
in private storage after a denied finalization. The controlled path does not
publish an external upload transport, provider URLs, or ongoing streams.
Previously delivered bytes cannot be recalled. Account recovery, recovery
preview and media deletion are outside the controlled API. Provider session
revocation still needs its own authoritative source and timing evidence.

## Disposable PostgreSQL privileges

| Data | Controlled runtime operations |
|---|---|
| Accounts, demographics, Terms and Privacy receipts | Subject/account-scoped SELECT |
| Module lifecycle | Scoped SELECT and INSERT, UPDATE state/revision/state-change time |
| Lifecycle receipts | Scoped SELECT and INSERT, no update or delete |
| Lifecycle audit | Scoped INSERT. Inspection uses separate fixture ownership. |
| Private module profiles | Scoped SELECT and INSERT, UPDATE display name, bio and visibility |
| Private profile photos | Scoped SELECT and INSERT, UPDATE lease/state/current-photo and related metadata needed for reservation and finalization |

The fixture gives its dedicated runtime role no ownership, role creation, DDL,
`BYPASSRLS`, or migration-history access. Profile/photo policies also require the
transaction-local account/module context. The migration admits durable authorized
no-transition receipts. A never-activated result creates no membership row.
Nonzero receipt revisions retain the membership foreign key through a generated
nullable column. Downgrade validates the old rule and fails transactionally if a
no-transition receipt would be invalid. No data is erased or backfilled.

## Downstream consumption

A downstream owner should request a fresh M2 decision for the verified actor,
destination module and exact operation, then enforce the feature's independent
policy inside its own protected boundary. The controlled contract is internal.
There is no shared production DI binding or feature grant through lifecycle
HTTP routes.

| Owner | Enforcement and required tests |
|---|---|
| M1 | Check Learning setup, Home and resource-specific permission at read release or write commit. Test incomplete setup, inactive access and deactivation racing a write. |
| M3 | Check Hobbies ownership, privacy and effective permission before Gallery/Map release, submission, delayed media or rewards. Preserve earned rewards. Pending Map awards remain gated. Test delayed actions and cross-module scope. |
| M4 | Check conversation, participation, block and mutual match in addition to lifecycle. Revalidate notification dispatch and retries, call admission, reconnect and renewal. Provider-session termination and timing remain M4/M6 work. |
| M5 | Check both participants where applicable, current privacy, blocks, relationships and group roles. Preserve historical records. Test absence from discovery while inactive and continued denial of revoked roles after reactivation. |
| M6 | Use independent administrative authorization and audit-access policy. Verify the actual restricted role, RLS, connection-pool reuse and coordinated transaction races before acceptance. |

D5 must execute the exact M2 PostgreSQL inventory in its supported restricted-role
environment, reconcile the M3 offline inventory separately, and verify the
licensed PhotoProcessor. D6 publication and merge require evidence review and
separate authorization.
