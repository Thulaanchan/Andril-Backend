# Batch 4 Group A: controlled isolated integration

Authority: user Batch 4 Group A implementation request after PR #547 manual merge,
successful CI and verified Member-1 synchronization. B3-10 remains incomplete;
all its unresolved production AI paths remain fail-closed and unchanged.

This slice adds real PostgreSQL persistence and uninvoked controlled HTTP handlers
for supported existing Skill Exchange, Learning Pact, Growth/mentorship and Circle
domain operations. It is not production activation or a claim that all four
requirements are complete.

## Existing rules reused

- DEC-M1-01: one pending Skill Exchange per unordered Learning-profile pair,
  regardless of direction or terms. A partial unique database index enforces it.
  A database check binds its canonical UUID pair to the immutable member metadata;
  a mismatched pair cannot bypass the index.
  Recipient accepts/declines; sender cancels. Terminal history is preserved and
  permits fresh explicit intent. Identical offered/wanted skills remain unavailable
  because the overlap policy is unresolved. Skill IDs remain opaque references;
  no new qualification or catalogue-membership rule is invented.
- Learning Pacts use the existing proposal, participant invitations, cancellation
  and individual completion-confirmation rules. The domain decides transitions,
  including any unsupported withdrawal paths; the repository invents no rule.
- Growth stores private immutable self-reported provenance and skill references
  only. Exact evidence body fields/categories, verification authority, XP and
  proficiency/skill-gap inference remain undefined and are not implemented.
- Mentorship stores voluntary offers and requests and reuses designated mentor
  accept/decline and learner cancel rules. Exact pending duplicates are rejected.
  A non-identical pending proposal for the same pair remains unavailable under
  `PendingMentorshipComparison.PolicyDecisionRequired`; no multiplicity policy,
  offer-publication/visibility policy or offer-deactivation rule is invented.
- Circles reuse existing goal text/progress invariants. A domain Circle ID is
  never treated as an M5 group, membership grant or owner permission. Create/edit/
  progress commands require an explicitly verified Circle resource binding and
  operation permission from the authority contract.

## Technical implementation

`LearningActivityState` records private creation data and ordered domain commands.
On read/update it replays existing domain methods, rather than duplicating lifecycle
rules. Canonical commitment and goal IDs are persisted; temporary replay-instance
IDs do not leave the response projection. Invalid commands remove their tentative
event and do not change existing state. Accepted commands append one event and
advance the optimistic version; there is no automatic command replay/retry.

The isolated table stores a generation, version, immutable server-resolved member
metadata and private JSON state. No shared FK, deletion/retention policy or new
migration history is invented. FORCE RLS requires an actual current account/member
scope through the existing M2 `scope_visible` authority. Runtime may SELECT/INSERT
and update content/status/version only; it cannot rewrite scope/members, DELETE,
alter schema or bypass RLS. Serializable transactions and the existing pre-snapshot
subject fence/account → membership → profile locks protect the real current actor.
Revision and uniqueness failures are safe conflicts; uncertain commits require
reconciliation and are not replayed. There is no positive authorization cache.

The new provisional `ILearningActivityAuthority` must resolve real target accounts,
current Learning eligibility, voluntary-offer ownership and M5 Circle operation
rights. It must participate in the enclosing transaction/revocation protocol.
Unknown evidence denies or returns unavailable. It is called again at commit/read
release; changed participant evidence withholds the result or rolls back mutation.
No permissive production implementation is supplied. The actual current actor is
derived from the existing M2/Common boundary, never a request account/profile ID.

The API-owned `CurrentLearningOwnerActivityAuthority` independently supports the
existing private Growth provenance and voluntary mentorship-offer create/read
operations by reusing the delivered current-module ownership reader contract.
With the verified account-backed reader it freshly checks the same server actor
on every call. A denied/default reader remains denied. This does not grant AI
processing permission, verified evidence status, public offer visibility or target
access. Multi-party, Circle and undefined mutation paths return unavailable. It
is unregistered and does not alter the existing AI service collection.

Authorized listing uses stable UUID keyset pages (default 50, at most 100), a
technical per-request work bound rather than a record-count/product quota. Every
returned resource gets its own current read permission and release revalidation.
Any denied/unavailable entry withholds the batch; no unauthorized ID is exposed as
a cursor. The private owner adapter supports listing only its two private kinds.

Controlled handlers require authentication and accept no owner identity or module
authorization fields. Unknown JSON fields are rejected. Only applicable
per-kind/per-command fields are accepted: irrelevant content is rejected rather
than silently discarded or saved as an unapproved note/evidence schema. Unknown
stored fields also fail closed before mutation, preserving original data.
Responses project only
authorized activity content, not internal account/participant identities. Structured
JSON uses inert escaping; safe outcome/status mapping and exception handling preserve
framework logging without private exception payloads. No existing API convention,
shared authentication setting or global logger is changed.

## Limits and exact remaining owner deliverables

1. M2: authoritative target Learning-profile/account eligibility resolution and
   coordinated multi-party lifecycle/revocation participation, not just a positive
   synthetic participant list. Supply a production `ILearningActivityAuthority`
   implementation using the real current-owner and target authority sources.
2. M1/M2: the approved complete Growth evidence content schema and verification
   authority. Until then only existing private self-report reference metadata is
   supported; immutable records have no invented edit/delete/sharing behavior.
3. M1/M5: exact Learning Circle ↔ M5 resource binding and operation permissions,
   including membership changes. Do not infer these from an arbitrary UUID.
4. M1: mentorship offer visibility/verification source and non-identical pending
   request policy. Preserve the existing unavailable outcome until defined.
5. M1/M2/M6: production relational mapping/migration, runtime permission review and
   service/route registration after those authorities and policies are verified.

Actual disposable PostgreSQL and HTTP tests use synthetic target/offer/Circle
authority only, while exercising the real M2 current-owner transaction boundary,
restricted runtime SQL, FORCE RLS, storage, conflicts and rollback. They do not
prove a real peer session or production multi-party authority. Recipient decisions
are also exercised through the actual domain/application replay tests. No hosted
database, live AI provider, production registration or production endpoint is
activated. The isolated connection guard stays intact. No feature is marked fully
complete on the strength of these component tests.
