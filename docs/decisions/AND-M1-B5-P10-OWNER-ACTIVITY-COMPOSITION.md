# B5-P10 — Supported owner-purpose activity composition

PR #641 was verified merged with both builds and required PostgreSQL CI successful;
Member-1 was fast-forwarded and synchronized at
`229fd2ddbcc4e8bf514f0d1e5810b7b13b7d864f` before this work.

## Existing authority, not new peer permissions

The Group A decisions and existing `CurrentLearningOwnerActivityAuthority` already
support private Growth self-reports and private voluntary mentorship offers for
the current eligible Learning owner. They explicitly do not supply mentorship
offer publication, target access, Circle membership or multi-party purpose grants.
The user's B5-P10 request authorizes completion of supported repository/API wiring
with the delivered M2 serializable mechanism. This implements those independently
supported owner foundations; it does not claim multi-party integration complete.

The production repository shares the existing domain replay/validation and command
implementation with the isolated component. Its ordinary constructor retains the
isolated database guard. A closed production factory uses the real M2 serializable
callback, fixed backend-owned relation and a per-transaction owner-purpose lease.
The lease is issued only inside that actual callback, binds subject/account/profile
and the live transaction, is rechecked before release/commit, and clears in finally.
It is neither a cache nor an alternative to missing authority. Only Create/Read/List
for GrowthSelfReport and MentorshipOffer are allowed; targets deny and all peer
purposes/commands stay unavailable. Generic module access is not activated.

## Learning-owned storage and explicit composition

`LearningPrivateActivitiesDbContext` has its own Learning-owned migration history
and maps `andril_learning.private_owner_activities`. Existing M2 and Learning
migrations/helpers are prerequisites. No shared foreign-key cascade, account purge,
retention policy or peer data schema is invented. Model/snapshot nullability is
explicit; native migration application and rollback/reapplication are tested.

The table permits only the two private owner kinds and exactly one member, binds
content identity/creator/member metadata, and requires active-owner RLS with FORCE
RLS. Its runtime role can only SELECT and INSERT; it cannot transition, edit/delete
history, alter ownership, read identity/other document tables or publish evidence.
There are no pending-pair indexes or peer rows in this private table. Existing
isolated multi-party storage and security tests remain unchanged in behavior.

`Learning:OwnerActivities:Enabled` defaults off and requires an explicit restricted
RuntimeRole. No authority producer is installed globally. Confirmed, private/no-store
activity endpoints reuse existing DTO/status conventions, reject malformed and
client-ownership fields locally, and never expose private rows on error. Listing
chooses only the two fixed mapped tables, with bound values and the existing page
ordering/limit. No hosted migration, deployment configuration change or provider
activation occurs. Self-report provenance does not become verified evidence or XP.

## Exact external deliverables still required

- M1/M5/M6: authoritative, revisioned operation-specific privacy/blocking/standing
  evidence and participant/resource bindings for `ILearningActivityAuthority` and
  ordinary disclosure evidence for `ILearningBrowseSourceAuthority`. The current
  Classroom-bound reader cannot supply unrelated purpose consent. Eligible/active
  profiles alone do not authorize peer disclosure or commands.
- M5 with M1/M6: the actual Circle resource kind/identifier mapping and per-operation
  membership/capability grants. A domain Circle ID is not a Classroom binding.
- M1/Product/M5/M6: mentorship offer publication/visibility rules and the unresolved
  non-identical pending-request multiplicity rule. Private offers are not published
  by this delivery, and the pending-request comparison remains fail-closed.
- The approved Connections lifecycle remains preserved, but its current private
  participant/blocking evidence producer is still required; accepted status or a
  Classroom interaction is not an access grant.

These are missing exact policy/evidence contracts, not repeated general approval
requests or a refusal to edit cross-member code. AI processing/transport guarantees,
maintenance/retention eligibility and XP publication holds remain unchanged. No
positive substitute, new earning rule or automatic operation retry is introduced.
