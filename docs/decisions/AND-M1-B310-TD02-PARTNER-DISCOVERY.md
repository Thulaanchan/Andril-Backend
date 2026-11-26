# TD-02 isolated Learning partner discovery

Authority: confirmed TC-02 and AND-M1-B310-CROSS-MEMBER-ACCESS-20261126.
This introduces explicit new technical interfaces and an isolated model for review.
It does not fabricate an existing M5 reader or production policy evidence.

`ILearningPartnerDiscoveryReader` is Learning-owned. The API adapter uses the
existing current AI actor/privacy guard and validated `discover_learning_partners`
tool arguments. Only the Learning module is accepted. Self, inactive, unknown,
hidden, blocked or otherwise ineligible evidence is excluded. All eligibility,
visibility and blocking decisions must be explicitly Allowed for the exact
requester. No Social/Dating matching or eligibility is reused.

The output allowlist is exactly display name, interests, skills, goals and
professional biography. Each field requires independent current AI disclosure
classification for its exact content and requester. Unknown, denied or empty
fields are withheld; entirely empty cards are omitted. No internal account/profile
references, contacts, locations, other module identities or private Diary/CV/
Portfolio data are output. Classification is not inferred from ordinary visibility.
Names and other authorized text remain untrusted data and cannot invoke tools.
Explicit JSON escaping preserves original text and avoids inheriting relaxed host
encoders; it is not a provider prompt-injection guarantee.

`IsolatedLearningPartnerDiscoveryReader` is an actual PostgreSQL implementation
using the existing isolated serializable Account/Membership/Profile transaction
boundary and exact requester identity. Unknown evidence fails closed. Search
values are SQL parameters. Matching and SQL output projection use only fields
whose AI disclosure classification is Allowed: unclassified text cannot leak
presence through search matching. Only eligible Learning candidates enter the
projection; the adapter independently filters and revalidates before release.

The new disposable `learning_partner_discovery` table is keyed by requester account,
requester Learning profile and candidate Learning profile. It has a Learning-only
constraint, nonempty identity constraints, FORCE RLS and SELECT-only runtime access.
The fixture owner provisions explicit synthetic evidence separately; no client or
runtime writer can grant discovery or disclosure. No production migration history,
shared foreign key, retention/deletion, ranking, paging or entitlement rule is added.

Production authoritative ingestion and updates remain unregistered. Actual M5/M6
Learning discoverability/opt-in/block authorities must populate exact current
evidence, atomically bind content/classification and coordinate changes with this
owner boundary before production reuse. A serializable snapshot does not prove
immediate ordering against arbitrary uncoordinated policy writers. No such policy
is invented here, and no synthetic record is represented as production consent.

Tests cover field minimization, self/hidden/inactive/blocked exclusion, unknown
authority, malicious arguments, foreign bindings, release revocation, cancellation,
real PostgreSQL SQL filtering, runtime permissions, RLS and controlled HTTP-to-
PostgreSQL serialization. HTTP hosts use isolated synthetic authority, not production
authentication or live providers. No production route or DI registration is added.

This delivers an independently reviewable isolated TD-02 reader/adapter. Full
production TD-02, dispatch coordination, atomic conversation results, schema/cleanup,
policy wiring and integrated B3-10 acceptance remain outstanding. Existing delivered
security, Classroom, acknowledgement, journal and conversation behavior are preserved.
