# TD-01 Classroom search projection and authoritative-reader boundary

Authority: confirmed TC-01 and AND-M1-B310-CROSS-MEMBER-ACCESS-20261126.
New cross-member technical contracts are explicitly new; no existing discovery
policy or persistence implementation is fabricated by this decision.

## Verified source and mappings

- `Andril.Groups/Entities/GroupEntry.cs`: the existing entity exposes `Id`, `Scope`
  and `Name`. Only `Name` maps to the currently supported AI `DisplayName` output.
  Stored content is preserved. Ordinary visibility does not classify it for AI.
- `Andril.Groups/Entities/GroupModuleScope.cs` and
  `Policies/GroupModuleScopePolicy.cs`: the existing valid pairing is module
  `learning` and `GroupKind.Classroom`. Clubs and Zones are excluded.
- `Interfaces/IGroupEntryRepository.cs`: existing GetGroupAsync/ExistsAsync are
  generic lookups, not authorized discovery. Existing membership objects contain
  group/profile/scope bindings; they do not supply active/closed/discoverable,
  visibility, block or AI disclosure evidence.
- No source-backed description, subject/category or topic mapping is available
  on GroupEntry. Those fields are absent from the output rather than guessed.

## New technical boundary

`ILearningClassroomDiscoveryReader.WithAuthorizedSearchAsync` is the M5-owned
read-only source/release integration contract. An implementation must query only
authorized Learning Classroom data and establish the current exact requester,
active lifecycle, discoverability, visibility, membership, private discovery and
blocking decisions. It must revalidate the source evidence before releasing the
callback result. This API grants no join/write capability.

`LearningClassroomDiscoveryEvidence` represents **server-only current evidence**,
not a client request, stored permission flag or policy inferred from a group name.
Unknown decisions fail closed. The source must bind all predicates and each field
classification to the actual current source content/revision and requester.
NameAiDisclosure Allowed means the specific name content is authorized for AI,
including exclusion of prohibited private/contact information; it cannot be
inferred merely because a field is present or normally discoverable.

The source originally contained no concrete implementation of this reader. This
delivery adds `IsolatedLearningClassroomDiscoveryReader` and a deliberately
isolated, server-provisioned evidence table. This is a **new technical model**,
not a claim that production M5 visibility/block authorities already populate it.
Actual authoritative source ingestion, membership eligibility, paging/ranking and
field classifications remain production integration requirements. Tests provision
explicit synthetic evidence; no client or runtime writer can grant these decisions.

## Adapter behavior

`LearningClassroomAiSearchAdapter` is API-owned and **unregistered**. It uses the
existing Learning tool-argument parser and current AI privacy/actor guard. It
passes only the server-resolved account/profile and validated query to the reader.
The source must execute exactly one authorized projection callback and release
that projection. Foreign snapshot bindings, duplicate source identities and
unknown evidence cannot establish an authoritative result.

Only active, discoverable Learning Classrooms passing every applicable restriction
are projected. Private Classrooms require explicit discovery authorization.
Each output contains only an explicitly AI-classified name, with no group/account/
profile identifier, members, messages, invitations, contacts, moderation data,
links or other module fields. Unclassified names are omitted. Display text remains
untrusted data; it is never executed as a tool instruction or rendered as raw HTML.

Authorization is rechecked at projection and again after source release. Revoked
or switched identities discard all cards. Known availability errors produce a
sanitized unavailable outcome; cancellation propagates. Source release must
coordinate its own underlying authorization/policy changes. These rechecks alone
do not claim an external-provider dispatch fence.

## Acceptance boundaries

Focused tests verify the adapter and controlled in-process HTTP serialization.
Additional actual disposable PostgreSQL tests verify the concrete isolated reader
and controlled HTTP-to-PostgreSQL path, with synthetic requester and policy evidence.
This is not a production authenticated endpoint or production M5 policy acceptance.

The new `classroom_discovery` table is in the existing `andril_ai_isolated` model.
Its primary key binds account, Learning profile and Classroom identity. The reader
uses the existing serializable Account/Membership/Profile boundary; all search
values are database parameters. Only active, discoverable, visible, membership-
permitted, unblocked and explicitly AI-classified records can pass the SQL query.
Private records additionally require explicit requester discovery authorization.
Unknown evidence never passes. The adapter independently filters the evidence too.

Runtime has SELECT only on this table: no INSERT, UPDATE or DELETE, including no
column update privileges. FORCE RLS binds account context and the existing Learning
profile visibility function. The test fixture owner provisions evidence separately;
it is not a runtime source of permissions. This adds no production migration history,
shared foreign key, lifecycle/deletion policy, retention rule, provider or DI wiring.

Evidence writes must coordinate through the same owner transaction boundary and
atomically update content and its exact AI disclosure decision; the current
repository exposes no such production writer. The serializable source snapshot
and release checks do not by themselves establish immediate policy/revocation
ordering for an arbitrary uncoordinated production writer. This limitation must
be resolved when integrating the real source authority, never hidden by fixture data.

Protected production HTTP activation remains gated on source-authoritative
lifecycle/block/visibility mechanisms, coordinated evidence writers and AI field
classifications. The new database tests prove isolated PostgreSQL behavior, not
those missing production policies. Full TD-01 and B3-10 remain incomplete after
this scoped reader/adapter delivery. Existing TD-04 journal and privacy gates remain.

The output serializer explicitly uses JavaScriptEncoder.Default instead of
inheriting relaxed web-host JSON settings. HTML-significant characters are escaped
without changing stored source names or deserialized display text. JSON encoding
is not a prompt-injection defense: names remain untrusted data, cannot invoke tools,
and must not be inserted into raw HTML or treated as provider/system instructions.
