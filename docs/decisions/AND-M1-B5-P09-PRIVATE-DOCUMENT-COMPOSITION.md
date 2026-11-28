# B5-P09 — Private Portfolio and Learning Diary composition

## Authority and supported scope

The user's B5-P09 instruction authorizes reuse of the delivered M2 serializable
callback and opt-in composition for independently authorized private documents.
This implements existing LRN-06/DEC-M1-04 Portfolio ownership rules and
CF-13/DEC-M1-05 Diary rules, including the explicit user decision to permanently
remove an explicitly deleted entry while preserving entries on deactivation.
It introduces no sharing, AI disclosure, media fetching or retention policy.

The existing transaction boundary now accepts a backend-only fixed resource
kind: CV, Portfolio or Learning Diary. Qualified relation names are fixed constants
passed as bound `to_regclass` values, never client-provided identifiers. Every
resource requires FORCE RLS, a non-owner restricted runtime role, the real M2
confirmed identity/current policy/age/membership checks and a locked active
Learning lifecycle in the same serializable Accounts transaction. The existing
subject/account/membership/resource coordination is reused without new lock keys.
No Learning preference revision or setup-completion requirement applies to Diary.

Portfolio reuses the delivered command/preview implementation and stored content
version. Its deployment grant includes only SELECT/INSERT/explicit DELETE and
updates to template, projects and the existing content_version column. CV and
Diary roles do not acquire Portfolio grants. The isolated constructor guard remains.

Diary reuses its existing CRUD/domain/storage implementation. A Learning-only
callback lease validates the exact current subject/profile/module and live
serializable transaction whenever it copies a private snapshot or revalidates.
The lease exists only inside the real M2 callback and is cleared in `finally`;
it cannot replace missing authority or authorize later work. The Diary context
enlists in that same transaction. Other module requests remain denied. Existing
isolated authorization behavior is retained. The shared physical owner helper
has a historical CV name; it is not a CV-purpose grant or a Learning revision.
This composition requires the existing Learning and Diary migrations and helpers;
it applies no migration or permission grant at startup.

## Private opt-in APIs

`Learning:PrivatePortfolios:Enabled` and `Learning:PrivateDiary:Enabled` each
default off and require their own restricted `RuntimeRole`. Neither registration
installs a permissive authority or activates the other component. Existing
architecture checks retain default-disabled composition and exact reviewed
document types, and check all four independent enablement combinations.

Protected Portfolio routes provide owner CRUD, explicit project editing/ordering,
template selection and the existing private preview. Protected Learning Diary
routes provide calendar/read/create/edit/explicit delete with literal ISO dates.
Diary writes require an explicit full reflection/plan payload, resolve the owner
server-side, then freshly revalidate that exact owner inside the write transaction.
Unknown ownership fields are rejected. Responses are private/no-store, and failures
are confidential. There are no Hobbies/Social Diary routes, public links or AI consumers.

## Remaining purpose-specific owner deliverables

Current Classroom-bound eligibility is not multi-party consent. The existing
`ILearningActivityAuthority.AuthorizeAsync` still requires authoritative,
operation-specific participants, current policy/blocking evidence and revocation
coordination for exchanges, Pacts, mentorship, Circles and Connections. M1/M5/M6
must supply/reconcile that purpose-specific policy evidence and actual resource
bindings, including the voluntary mentorship offer and Circle's M5 resource.
The approved Connections state machine alone is not an evidence producer.
Likewise `ILearningBrowseSourceAuthority` needs purpose-specific current disclosure
evidence; no Classroom flag is promoted into general People Discovery permission.
These are exact missing source/evidence deliverables, not another request for
general implementation approval. No permissive substitute is introduced.

AI transport/processing policy, maintenance-worker/retention eligibility and XP
publication holds remain unchanged. This delivery does not claim those functions,
all module Diary activation, B3-10 or Batch 5 complete. No hosted migration,
production configuration change, live provider activation or automatic merge occurs.
