# B5-P07 — Private Learning CV runtime

## P08 architecture reconciliation

Commit `99fe4f0` introduced the Learning foundation architecture tests as
"TEMPORARY pending-activation gates, not permanent prohibitions" and explicitly
directed revision with approved M2/Learning activation and authorization/API tests.
The user's B5-P07 request explicitly authorizes M2-backed Learning access, shared
serializable document coordination and independently authorized production wiring;
B5-P08 authorizes reconciling that temporary gate while preserving security.
This is the approval evidence for the private CV scope only, not new approval of
any AI, multi-party, retention or XP business policy.

The revised contract still discovers actual Program registrations and requires
no Learning service/persistence activation with default configuration. An exact
CV-contract allowlist replaces only the obsolete blanket IL-reference ban. Explicit
enablement permits one scoped CV repository, preserves the real M2 service and
repository, leaves generic module access denied, and adds no global Learning
DbContext registration. Real route metadata must be absent when disabled and
must match the six private, authenticated routes when enabled. P07's real PostgreSQL
tests retain serializable lifecycle coordination, FORCE RLS, restricted grants,
rollback and private release verification. No architectural layer or authority
check is removed; the other foundation restrictions remain unchanged.

The existing owner-only CV decisions (LRN-05, DEC-M1-04 and
AND-M1-B410-CV-FIELDS-20261127) authorize private owner creation, editing,
preview and export. This delivery introduces no general profile, peer-disclosure,
AI-processing or retention permission.

## Shared transaction mechanism

M2's explicit `WithSerializableLearningProfileAsync` callback reuses the real
confirmed server subject, account lookup, starter membership, age eligibility and
current Terms/Privacy validation. Unsupported implementations return pending by
default. Existing M2 callbacks retain their original isolation and behavior.
The Learning callback additionally locks the existing active M2 lifecycle row
under its unchanged FORCE RLS policy. Membership and policy records are locked
through release. It neither creates profiles nor treats setup completion as an
additional CV prerequisite.

Private CV persistence shares that Accounts connection and serializable transaction.
It acquires the existing subject fence before the snapshot/account locks; it adds
no lock namespace. Account, membership/lifecycle and document locks follow their
existing ordering. No external provider is called under these locks.
The document savepoint switches to a restricted CV role, verifies active RLS and
rejects superuser, BYPASSRLS, database/role creation and table-owner membership.
It restores session context before the M2 transaction completes. Cancellation and
preparation failure roll back; uncertain commit yields CommitUnknown with no CV
payload and no automatic replay. A failed session-fence release discards the pooled
connection. The existing isolated constructor and its local database guard remain.

## Explicit runtime composition

`Learning:PrivateCv:Enabled` defaults off. Explicit enablement requires
`Learning:PrivateCv:RuntimeRole` and the real scoped Accounts/profile services.
The deployment helper `LearningCvRuntimePermissions.GrantSql` grants only private
CV read/insert/content-column updates and existing owner helpers. There is no
identity-table grant, ownership update, delete, migration-on-startup or hosted
migration. Existing Learning migrations are required before enablement; absent
schema/authority fails closed. No deployment configuration is changed here.

Protected `/api/learning/cvs` routes provide creation, current-owner listing and
contact-consent changes alongside the existing structured edit, private preview
and PDF routes. Confirmed identity is enforced before binding; unknown ownership
fields and malformed JSON are rejected locally. Responses are not cacheable.
Existing structured projection, imported/unclassified withholding, current consent,
font handling and PDF rendering remain unchanged.

## Remaining owner deliverables

- M1 Product/M5/M6: purpose/resource/relationship-specific privacy and blocking
  decisions for exchanges, Pacts, mentorship, Circles and Connections. The
  Classroom-bound eligibility source does not supply these decisions or consent.
- M1/M2/M5/M6: authoritative AI-processing-policy ingestion, AI-specific disclosure
  evidence and concrete provider transport/revocation guarantees. Existing denied
  adapters remain denied.
- M2/M6 and the retention policy owner: authenticated maintenance worker grants,
  approved retention/deletion eligibility and external-store coverage. No retention
  interval or owner-status shortcut is introduced.
- Product/M1/M3/M6: XP event, amount, cap and threshold publication remains
  unconfigured. No awards are enabled.

These holds do not prevent the independently authorized private CV delivery and
are not requests to repeat general implementation approval.
