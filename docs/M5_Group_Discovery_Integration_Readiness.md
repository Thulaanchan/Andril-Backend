# Shared Groups discovery integration readiness

Updated 2026-11-27 for Tasks 265–274. This describes working, tested application orchestration against explicitly supplied authoritative dependencies. It does not certify live persistence, a deployed endpoint, relational acceptance or pending product policy approval.

## Implemented pipeline

`GroupDiscoveryAccessService` resolves the caller through `IGroupActorContext`; its request has only an exact `GroupModuleScope`, search text, an explicit positive technical candidate bound and cancellation. It never accepts a client actor/profile ID. Actor identity, active standing, restrictions, module correlation, freshness and verified Social adult age are checked before discovery. The service re-resolves the actor after projection and suppresses the cards if access was revoked, evidence is unavailable/stale or identity changed. Dependency failures return a non-disclosing null outcome; cancellation propagates.

`GroupDiscoveryService` passes the trusted profile identity, scope, unchanged search text and explicit bound to `IGroupDiscoveryReadRepository.WithAuthorizedReadAsync`. The repository must search within a current authorized read/release boundary. The service checks snapshot bindings, required authorization revision and candidate count before processing rows. A batch larger than the supplied bound is rejected entirely, rather than truncating away contradictory evidence. Each row needs active lifecycle and explicit allowed discoverability, visibility, membership restrictions and blocking restrictions. Privacy classification must be known; private groups additionally require allowed private discovery authorization. Missing revisions, stale/future evidence, foreign scopes and unknown/denied decisions do not expose a card.

Equivalent independently materialized duplicates are deduplicated. Conflicting names, state, scope, revisions or permissions exclude that group regardless of order; later duplicates cannot restore it. Surviving groups are ordered by ID for deterministic output. The service calls `RevalidateAsync` on the exact query/snapshot before producing its server-only result. A denied, unknown or undefined release decision suppresses the whole result. It also checks actor and retained row freshness after revalidation. The outer repository boundary must fence release after callback completion; an earlier preflight check followed by an unfenced read is insufficient.

The existing `GroupParticipationAuthorizationPolicy.MaximumEvidenceAge` is used for evidence freshness, and its existing Social adult minimum remains in force. This introduces no retention period, restoration permission, quota, role or product search policy. Bounds must be supplied deliberately by the integrating server; there is no default page size, maximum search length or pagination product contract in this batch. An integration must enforce its approved operational limits at the data source, not enumerate an unbounded source then trim in memory.

`AuthorizedGroupDiscoveryResult` is an immutable server-only snapshot with non-serializing properties. `GroupDiscoveryCardDto.FromAuthorizedResult` validates the exact requested scope and exposes only `Id`, `Module`, `GroupKind` and `Name`, all backed by `GroupEntry`. The card and output collection are immutable. No owner identity, private membership, entitlement, moderation state, evidence revision, actor ID or authorization diagnostic is projected. Cards are snapshots, not reusable permissions to join, read members, communicate or perform AI disclosure.

| Exact scope | Group kind | Caller identity |
|---|---|---|
| learning | Classroom | Authorized Learning private profile |
| hobbies | Club | Authorized Hobbies private profile |
| social | Zone | Authorized Social private profile with verified adult eligibility |

A successful authorized search with no visible groups returns an empty collection. Unavailable or denied current authorization returns null. These internal outcomes are not HTTP status contracts. No controller or route was added or activated.

## Authoritative repository obligations

There is deliberately no production implementation of `IGroupDiscoveryReadRepository` in this batch. Test doubles exercise orchestration only. A real adapter must provide:

- Server-authenticated profile/account binding and exact module/kind isolation for every read.
- Bounded source queries and immutable snapshots bound to that actor and scope.
- Authoritative lifecycle, explicit discoverability, known public/private classification, caller-specific visibility, private discovery permission, membership restrictions and current blocking restrictions.
- Revisions covering both visible content and all permission inputs; current authorization verification cannot check only a timestamp or group existence.
- Transaction/locking/fencing coordinated with every relevant content, standing, membership, visibility and blocking writer through final read/release. Changes during projection must suppress the callback result.
- Cancellation propagation, non-disclosing availability failure and no partial result release on errors or revocation.

`GroupEntry` contains only ID, scope and name. `IGroupEntryRepository` and `GroupEntryScopedLookupService` are exact entity lookups, not discovery authorization providers. Neither may manufacture discoverability, membership or blocking evidence. No GroupEntry property, schema, table or migration was added to simulate those authorities.

## Cross-member integration

| Owner/boundary | Existing source and required integration |
|---|---|
| M1 Classroom discovery | Preserve `ILearningClassroomDiscoveryReader` and `IsolatedLearningClassroomDiscoveryReader`. The existing reader takes account ID, Learning profile ID and query, uses its isolated PostgreSQL boundary, and separately requires `NameAiDisclosure`. It is not a drop-in implementation of the new cross-module repository. |
| M2 identity | A trusted `IGroupActorContext` must resolve authenticated private-profile ownership and current standing. Existing `GroupActorContext` requires `IPrivateProfileService` and `IGroupAuthoritativeEvidenceProvider`; those dependencies must be real and separately supplied. A profile ID alone is not account authentication. |
| M3 Hobbies Clubs | Supply real Club discovery evidence and coordinated current membership/lifecycle restrictions. Existing admission capacity/creation quotas do not authorize discovery and are not applied as discovery filters here. No entitlement data is exposed in cards. |
| M6 security/moderation | Provide approved caller-specific visibility and blocking decisions coordinated with their writers, without inventing defaults or exposing internal moderation reasons. Unknown evidence fails closed. |

The M1 evidence vocabulary has parallel lifecycle, discoverability, visibility, membership and blocking fields, but compatibility requires an explicit adapter and accepted account/profile binding. `NameAiDisclosure` remains an additional independent AI permission. Ordinary Group cards must never be routed to AI merely because they can be displayed to an authorized user. This batch does not change or register the isolated M1 reader, generalize it to Clubs/Zones, add its tables to another schema, or claim production ingestion exists.

## Opt-in composition

`AddGroupDiscoveryServices()` registers scoped `GroupDiscoveryService`, `GroupDiscoveryAccessService` and its `IGroupDiscoveryAccessService` alias. Concrete/interface access resolution shares one scoped instance; request scopes are independent. Compatible existing scoped registrations/factories are preserved, duplicate or incompatible descriptors are rejected and missing dependencies fail at resolution. The extension does not install an actor context, discovery repository, authoritative evidence provider or fake fallback.

An integrating host or explicit test harness must first supply the real `IGroupDiscoveryReadRepository` and trusted `IGroupActorContext`. If using the existing `AddGroupActorContextServices()` path, supply its profile/evidence dependencies separately. A supplied trusted `TimeProvider` is respected; otherwise existing system time behavior applies. None of these opt-in calls were added to `Program.cs`.

## Acceptance boundaries and remaining work

Offline unit/security tests verify query orchestration, safe projection, trusted actor handling and composition. Live relational discovery acceptance still requires a concrete authoritative repository, accepted cross-member policy/evidence contracts, coordinated revision writers, transaction/fence testing, performance bounds and revocation-race acceptance against the approved data store. These tests do not establish PostgreSQL provisioning, production credentials, data ingestion or deployment readiness.

Tasks 237–244 and R01/R02 remain deferred. P-04/P-06/P-07/P-10 remain subject to their existing approval boundaries. No Zone quotas, roles, default discoverability, archival/restoration rules or communication permissions were invented. Proposed routes in `M5_CF07_Groups_Integration_Contract_PROPOSED.md` remain proposed; this document does not approve or activate them. Existing NonController/NonAction and Development-only OpenAPI boundaries remain unchanged. No database schema, migrations, packages, project references, configuration or credentials changed.

## Source and verification references

- `src/Andril.Groups/Interfaces/IGroupDiscoveryReadRepository.cs`
- `src/Andril.Groups/Services/GroupDiscoveryService.cs`
- `src/Andril.Groups/DTOs/GroupDiscoveryCardDto.cs`
- `src/Andril.API/Authorization/Groups/GroupDiscoveryAccessService.cs`
- `src/Andril.API/Extensions/GroupDiscoveryServiceCollectionExtensions.cs`
- `src/Andril.Groups/Interfaces/ILearningClassroomDiscoveryReader.cs`
- `src/Andril.Persistence/Repositories/AI/IsolatedLearningClassroomDiscoveryReader.cs`
- `src/Andril.API/Authorization/Groups/GroupActorContext.cs`
- `src/Andril.Groups/Policies/GroupParticipationAuthorizationPolicy.cs`
- `tests/Andril.UnitTests/Groups/GroupDiscoveryServiceTests.cs`
- `tests/Andril.UnitTests/Groups/GroupDiscoveryCardDtoTests.cs`
- `tests/Andril.SecurityTests/Groups/GroupDiscoveryAccessServiceTests.cs`
- `tests/Andril.SecurityTests/Groups/GroupDiscoveryServiceRegistrationTests.cs`
- `docs/M5_CF07_Groups_Integration_Contract_PROPOSED.md`
