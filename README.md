# ANDRIL — Backend

ASP.NET Core backend for the ANDRIL Flutter **Android** client and Angular Admin, maintained in separate repositories. The repository contains shared HTTP APIs, domain foundations, EF Core models and migrations, and automated tests. It is not a completed or verified production deployment.

This README describes checked-in source. A class, route, EF mapping or passing offline test does not establish that a feature is deployed or ready for users. Business rules remain subject to ANDRIL BRD v2.4-R1 and the decision register; the modular-monolith/N-Tier architecture proposal still requires the stated technical sign-off.

## Scope

- Exactly three purpose-isolated product modules: **Learning, Hobbies, Social**.
- Shared technical projects (Groups, Communication, Economy, AI, etc.) are **not** extra product modules.
- Social requires 18+ explicit opt-in, mutual-match authorization for match-gated chat, and **Social-only MoodTone**.
- Mapbox is confirmed for the **Flutter Android map UI**. The reserved server-side `Integrations/Mapbox/` folder is optional and does not indicate an active Mapbox API integration.
- PostgreSQL/Supabase and private Supabase Storage remain the data/storage direction. Adapters and database models exist, but their presence does not verify live connectivity, deployment or operator configuration.
- Do not reintroduce removed Learning Focus/Quizzes or Hobbies Events/Photo Portfolios.

## Solution and architecture

[Andril.slnx](Andril.slnx) includes **11 source projects** and **4 test projects**:

| Project | Responsibility |
|---|---|
| `Andril.API` | HTTP controllers, authentication middleware, integrations and composition root |
| `Andril.Shared` | Shared abstractions, including verified current-user identity |
| `Andril.Common` | Accounts, private profiles, onboarding, Diary and shared concerns |
| `Andril.Learning` | Learning domain foundations and provisional application contracts |
| `Andril.Hobbies` | Hobbies domain and application code |
| `Andril.Social` | Social domain and application code |
| `Andril.Groups` | Shared group domain, scope policies and lookup services |
| `Andril.Communication` | Shared chat and notification foundations |
| `Andril.Economy` | Economy contracts and Flight Points code |
| `Andril.AI` | AI policies, service foundations and provider preparation |
| `Andril.Persistence` | EF Core contexts, mappings, migrations and repository implementations |

Feature projects reference Shared; Persistence references feature projects; API composes the backend. Shared technical projects are not additional product modules. See the [architecture proposal](docs/ARCHITECTURE.md) for the intended layering, not a claim of complete implementation.

The four solution test projects are `Andril.UnitTests`, `Andril.IntegrationTests`, `Andril.SecurityTests` and `Andril.ArchitectureTests`. The separate `Andril.ProfilePhotoProcessor` and `Andril.ProfilePhotoProcessor.Tests` project files also exist, but are not listed in this solution.

## Shared HTTP APIs in source

[Program.cs](src/Andril.API/Program.cs) maps controllers and the anonymous `GET /health` endpoint. The following shared controller routes exist; methods and paths below describe source registration, not verified live services.

| Area | Methods and paths |
|---|---|
| Account | `GET /api/v1/account/me`; `POST /api/v1/account/initialize` |
| Eligibility | `GET`, `PUT /api/v1/account/eligibility` |
| Policy receipts | `GET /api/v1/account/policies/status`; `POST /api/v1/account/policies/acknowledgments` |
| Language | `GET`, `PATCH /api/v1/account/preferences/language` |
| Starter module | `GET`, `POST /api/v1/account/starter-module` |
| Private profiles | `GET`, `POST`, `PUT /api/v1/account/profiles/{module}` |
| Profile photos | `POST /api/v1/account/profiles/{module}/photos/{requestId:guid}`; `GET /api/v1/account/profiles/{module}/photos/{uploadId:guid}`; `GET /api/v1/account/profiles/{module}/photos/current`; `GET /api/v1/account/profiles/{module}/photos/{uploadId:guid}/content` |
| Onboarding | `GET /api/v1/onboarding/status` |
| Notifications | `GET /api/v1/notifications/{module}`; `GET /api/v1/notifications/{module}/{notificationId:guid}`; `PATCH /api/v1/notifications/{module}/{notificationId:guid}/read` |

These are 21 shared operations plus health. There are no active Learning, Diary or AI feature endpoints in the current API composition. Test-only probe/prototype routes do not count as application APIs.

The [OpenAPI contract](contracts/openapi/andril-api.v1.yaml) documents account, profile/photo, onboarding and health operations. It does not yet include the notification routes. Source/contract parity and client compatibility still require review.

## Authentication and integration boundaries

The API registers Supabase JWT bearer validation, a verified `ICurrentUser`, confirmed-account identity middleware and a default authenticated-user authorization policy. Account and onboarding paths additionally require confirmed identity evidence. Authentication fails closed when required configuration is unavailable; health remains anonymous.

An authentication subject, internal account ID and private module-profile ID are distinct. Future Learning operations must reuse M2's authoritative ownership, eligibility, privacy and revocation boundaries. Starter selection, a profile shell or an offline setup result must not be treated as Learning completion or module access. Learning completion remains dependent on an approved profile contract; `IPrivateModuleAccess` currently denies protected module entry.

Some registered routes intentionally retain unavailable dependencies. Notification composition uses deny-all recipient access and repository implementations. Profile photo composition uses an unavailable image inspector. Route existence therefore does not establish successful notification delivery or photo processing. Operator configuration and reviewed authoritative integrations remain necessary.

## Learning and Diary foundations

| Feature | Existing source behavior | Remaining integration |
|---|---|---|
| CF-02 / setup | Interests, teaching/learning skill selections, private goals, catalogue eligibility and an internal offline setup evaluator | Authoritative catalogue reader, protected application services, persistence and setup APIs |
| LRN-01 Skill Exchange | Proposal, acceptance, decline, cancellation and local duplicate comparison | Authorized partner access, atomic storage and API |
| LRN-02 Learning Circles | Circle-scoped shared-goal creation and editing | M5 group membership/roles, lifecycle, progress and persistence |
| LRN-03 Pacts | Initial commitments, participant invitations and agreement lifecycle | Protected persistence, progress/completion and API |
| LRN-04 Growth/Mentorship | Private self-reported skill evidence and voluntary mentorship request lifecycle | Approved evidence schemas, verification authority, discovery and storage |
| LRN-05 CV | Private manual section editing, template selection and CV-specific contact inclusion choice | Field schemas, authorized storage, rendering and export |
| LRN-06 Portfolio | Private projects, manual edits, ordering, template selection and inert link/media references | Authorized storage, publication and export contracts |
| CF-13 Diary | Private module/profile/date entries, reflection and daily-plan editing | Owner/date uniqueness, calendar/timezone authority, concurrency and storage |

See [Learning source](src/Andril.Learning) and [Diary source](src/Andril.Common/Diary). These are domain foundations, not complete user-facing features. Learning Home, discovery/connections, XP, communication and group participation require their approved shared-member integrations. Existing domain objects must not be exposed as authorized API payloads.

### Persistence limitations

EF Core/Npgsql references and migration source exist. Account persistence is registered by the API; Accounts, HobbiesGallery and HobbiesMap migration directories are present under [Persistence](src/Andril.Persistence). Migration files do not prove application to any database, and account registration performs no startup migration.

[LearningDbContext](src/Andril.Persistence/DbContexts/LearningDbContext.cs) is an isolated model with exactly two mappings: `andril_learning.learning_skill_selections` and `andril_learning.learning_personal_goals`. It defines keys, required columns and module/kind checks, but no ownership/catalogue foreign keys or secondary indexes. It has no runtime registration, Learning migration or repository implementation. The future provider-options owner must configure its dedicated migration-history table before migration work.

Other Learning aggregates and Diary content have no mapped persistence in this model. AI persistence entities and repository contracts likewise do not establish production database storage. Database authorization, RLS/grants, transaction/concurrency behavior and deployment need separate evidence.

## AI status

[AiChatController](src/Andril.API/Controllers/AI/AiChatController.cs) is explicitly `[NonController]`, has no HTTP route attributes and is dormant. `Program.cs` does not invoke the disabled AI composition extension. Controller-like internal methods and a live-enable option do not activate an endpoint.

AI source includes validation, module/conversation isolation, retention, privacy/consent, safety, operation/budget contracts and provider preparation. Generation preparation is not admission, dispatch or delivery authority. The local provider token counter cannot yet supply authoritative measurements. Classroom search and Learning partner tools are not implemented. No live AI readiness or AI-ERR-02/T05 closure is established by this README.

## Automated verification

Existing xUnit projects contain real tests:

| Project | Coverage and boundary |
|---|---|
| `Andril.UnitTests` | Domain, policy, service, entity and offline model tests, including Learning, Diary and AI |
| `Andril.SecurityTests` | Authentication, authorization, isolation and dormant integration checks; authentication HTTP/JWKS fixtures are synthetic |
| `Andril.ArchitectureTests` | Assembly/IL dependency and AI activation boundaries |
| `Andril.IntegrationTests` | Offline compositions/mapping checks and separately gated local PostgreSQL tests |

Some integration tests create and remove disposable local PostgreSQL databases using explicit M2/M3 test configuration; some photo tests require additional role-management opt-in. Do not point these settings at shared or production infrastructure. Inspect fixture prerequisites before running the integration project. A test-project name alone does not imply either an external-service test or a full HTTP/database end-to-end test.

The Windows-guarded EventLog filter is limited to the authentication test host; regression tests verify provider/host scope, other diagnostic providers and synthetic private-data exclusion. Production logging is unchanged. Test output applies only to the revision, environment and tests actually run; no fixed pass counts or production-readiness claims are maintained here.

### Local build and offline regression commands

Use the .NET 10 SDK selected by [global.json](global.json) (`10.0.400`, `latestFeature` roll-forward). NuGet restore requires package-feed access when packages/assets are not already available; perform it separately in an authorized environment.

With restored dependencies, run independently from the repository root:

```powershell
dotnet build .\Andril.slnx --configuration Release --no-restore
dotnet test .\tests\Andril.UnitTests\Andril.UnitTests.csproj --configuration Release --no-build --no-restore
dotnet test .\tests\Andril.ArchitectureTests\Andril.ArchitectureTests.csproj --configuration Release --no-build --no-restore
dotnet test .\tests\Andril.SecurityTests\Andril.SecurityTests.csproj --configuration Release --no-build --no-restore
```

Check each command's exit code and output before continuing. Do not use `--no-build` with stale binaries. These commands do not run the integration project or prove deployed endpoints/database behavior. Live application startup is a separate operation requiring reviewed configuration; keep credentials in protected configuration providers, never in this repository.

## Folder ownership and change control

- M1 Learning, CV, Portfolio and shared AI Chat integration (lead pending on shared services).
- M2 identity, purpose-based privacy, wallet, payments and secure ledgers.
- M3 Hobbies, Map and Flight Points; shared XP lead pending.
- M4 chat, **Social-only MoodTone**, notifications, calls and live streaming.
- M5 Social, matching and confirmed Group Management backend ownership (Classrooms, Clubs, Zones).
- M6 Angular Admin, moderation, safety and QA/CI.

Before shipping, verify server-side access checks on every protected operation, OpenAPI/client compatibility, database grants/RLS, transactional ledger behavior, private storage policies, migrations and deployment. Reuse existing services and shared contracts; do not duplicate endpoints or infer approval from placeholder directories. Preserve other members' work and require reviewed integration/QA evidence before declaring a feature complete.
