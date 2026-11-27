# Batch 4 Group B — supported integration slice

Authority: Group B implementation request and separately confirmed B4-07 contract
in `AND-M1-B407-CONNECTIONS-20261127.md`. PR #557 exact twelve-file head, merge
and both successful CI checks were verified from retained interrupted-check
evidence. Member-1 was fast-forwarded and normally pushed; local/live parity was
verified at `656539cbcade695f665f2033610d1d9f4c0126bc` before implementation.

## Supported functionality

- **Learning Home:** a private projection of existing persisted skill selections,
  goals and storage revision. Uses the delivered preferences application/repository
  read-release checks and explicitly requires the existing `EnterHome` permission
  before and after read. Incomplete setup, revoked authority and foreign identity
  cannot receive Home content. No fabricated feed, ranking, XP or counters.
- **People:** ordinary Learning discovery with profile/name cards only. A new
  purpose-specific reader/source-authority contract separates ordinary disclosure
  from AI-processing classifications. Exact requester and target Learning scope,
  current lifecycle, eligibility, visibility, both-direction blocking and ordinary
  name disclosure are mandatory. Unknown and contradictory candidates are withheld;
  hidden/unclassified names are filtered before search matching to prevent presence
  leaks. Bounded search/page limits are technical work bounds, not product quotas.
- **Connections:** approved explicit request/accept/decline/cancel/disconnect
  lifecycle, participant-only records, optimistic generation/version preconditions,
  durable terminal history and no inferred communication/group/AI rights. Existing
  activity persistence and controlled create/read/list/command handlers are reused.
  A separate partial unique index covers Pending **and** Accepted unordered pairs,
  with the canonical UUID pair bound to immutable member metadata by a database
  check. This does not alter DEC-M1-01 exchange uniqueness. No automatic retries.
- **Classrooms:** an ordinary, read-only Learning facade over the delivered M5
  `GroupDiscoveryService`, actor context, repository contract and authorized DTO.
  Reuses existing current actor, lifecycle, freshness, visibility, private-discovery,
  membership and blocking predicates. Projects only authorized group ID/module/kind/
  name. Does not grant joining, invite or member/message access, or create a second
  Classroom lifecycle. Existing strict M5 membership operations remain authoritative.

## Physical boundaries and tests

`IsolatedLearningBrowseReader` is a new unregistered local-only PostgreSQL reader.
It reuses the existing M2 subject fence/account → membership → profile transaction
and `scope_visible` function, without changing their connection or identity guards.
Requester-bound ordinary disclosure evidence is persisted in a separate isolated
schema, not the AI evidence tables. Runtime gets SELECT only; FORCE RLS and exact
account/profile/module predicates prevent cross-scope reads. A mandatory source
authority revalidates exact source revisions before projection and transaction
release. Writer and revocation coordination are part of that authority contract,
not inferred from stored booleans or timestamps. A live callback token used by the
M5 reader is not a transferable authorization cache.

The activity model extends its existing isolated table to the approved connection
kind and status constraints. No shared FK, production migration history, retention,
delete, expiry, cooldown or notification policy is invented. Runtime still cannot
rewrite participant metadata, DELETE, bypass RLS or alter schema. Blocking denies
access immediately through the required current authority checks, including commit/
read release; it preserves stored relationship history rather than inventing a
lifecycle rewrite.

Controlled Home/People/Classroom routes and existing activity routes require
authentication, server-derived current identity, inert JSON output and confidential
error outcomes. No new endpoint composition is invoked at startup. Test hosts use
explicit synthetic authentication/target/source authorities only. Actual PostgreSQL
tests use independently provisioned sender and recipient accounts/profiles and
restricted connections; Home reads the actual preferences repository; discovery
queries, database constraints, RLS, concurrency, rollback and HTTP are real. These
tests do not prove production policy provenance, fact ingestion or live activation.

## Genuine remaining dependencies

1. Production People visibility/disclosure/block data mapping and a coordinated
   source writer/`ILearningBrowseSourceAuthority` implementation. Ordinary purpose
   must not be inferred from the AI field-disclosure flags or Social matching.
2. Production Connections target eligibility, privacy and both-direction block
   authority implementing the existing activity contract with commit/release
   coordination for both parties. Acceptance must never grant communication access.
3. M5 production Classroom discovery source ingestion and current actor/evidence
   provider. Actual group/member/message mutation remains in M5 strict operations;
   no permissive stand-in, duplicate group repository or guessed permission is added.
4. Production schema/migration/permissions and DI/route activation require those
   physical authorities. Isolated evidence is not production truth. The existing
   current-owner Home/preferences authority and catalogue wiring remain necessary.
5. Any wider Learning Home feed/counter/ranking or Classroom content requirements
   need their actual approved data contracts. This slice exposes only existing
   source-backed owner preferences/goals and authorized discovery fields.

All unresolved Group A and B3-10 dependencies remain preserved and fail-closed.
No hosted migrations, live providers, production activation, full Group B completion
or completion of the outstanding earlier batches is claimed.
