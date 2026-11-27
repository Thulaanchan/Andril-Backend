# M5 frontend API readiness and activation boundaries

Updated 2026-11-27 for Task 255. Repository: `Thulaanchan/Andril-Backend`; working branch: `Member-5`; integration branch: `Develop`. This reference describes checked-in contracts, not evidence of a production deployment.

## Source-configured questionnaire definition API

`GET /api/v1/social/questionnaires/active` is discoverable by MVC in the default host. `Program.cs` calls `AddSocialQuestionnaireServices()`, `AddControllers()` and `MapControllers()`. The controller requires `[Authorize]` and delegates to `ISocialQuestionnaireDefinitionAccessService`; this does not establish a deployed URL, a healthy database or successful production acceptance.

Send a valid configured Supabase JWT bearer access token. The server derives account/profile scope; clients must not supply a profile or account ID to select another user's questionnaire. `SocialQuestionnaireDefinitionAccessService` calls `IPrivateProfileService.WithAuthorizedProfileAsync("social", ...)`, verifies a nonempty authorized Social profile and forwards cancellation. Authentication alone does not grant private-module access. Missing profiles, rejected access and unavailable approved contracts map through `PrivateProfileExceptionHandler` to 404, 403 and 503 respectively; authentication failures are handled by the bearer pipeline (normally 401). Do not infer age or account standing details from a generic 403.

The action returns `QuestionnaireDefinitionDto` with camelCase JSON properties `version` and `questions`. Each question contains `id`, `prompt`, `isRequired` and `options`; each option contains `id` and `label`. The action declares `[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]`. Clients must not cache private responses or rely on exact cache-header ordering.

### Canonical V1 content

`src/Andril.Social/Personality/QuestionnaireV1Definition.cs` is the source of truth. Version is exactly **`v1`**, with these twelve questions in order:

| ID | Prompt |
|---|---|
| q01 | I enjoy meeting new people and starting conversations. |
| q02 | I prefer a few meaningful connections over many casual ones. |
| q03 | I usually respond to messages within a reasonable time. |
| q04 | I need regular personal space even when I feel close to someone. |
| q05 | If something bothers me, I prefer to discuss it openly. |
| q06 | When I am busy, I still try to send a quick update. |
| q07 | I enjoy joining social activities and group plans. |
| q08 | Shared values matter more to me than shared hobbies. |
| q09 | I am comfortable taking the first step to start a conversation. |
| q10 | I prefer planned meetups more than spontaneous plans. |
| q11 | I enjoy lighthearted humor and playful conversation. |
| q12 | I am open to building connections with people from different backgrounds. |

Every question uses the same five-point Likert scale:

| Option ID | Label |
|---|---|
| 1 | Strongly Disagree |
| 2 | Disagree |
| 3 | Neutral |
| 4 | Agree |
| 5 | Strongly Agree |

All twelve questions have `isRequired: true` for a completed submission. **Questionnaire participation itself remains optional.** A skipped submission contains no answers; incomplete answers do not constitute a completed questionnaire. The definition API does not submit or persist answers. Render server-provided IDs, labels and version rather than fabricated questions or alternative option identifiers. Keep loading, authentication failure, forbidden/unavailable and retry states distinct. A missing canonical definition is a server configuration failure, not an approved empty-catalog response.

## Dormant HTTP surfaces

These classes retain `[NonController]` and are excluded from default MVC discovery. Route attributes describe proposed contracts, not callable APIs. An absent route may produce 404, but middleware or other host configuration can affect an HTTP response; do not use a particular status as proof of deployment.

| Controller | Declared route | Current boundary |
|---|---|---|
| `SocialQuestionnaireResponsesController` | `/api/v1/social/questionnaires/responses` | POST submission and GET `me` are dormant; the typed submission overload also has `[NonAction]`. |
| `SocialExploreController` | `/api/v1/social/explore` | Matching evidence, access, repository and activation acceptance remain required. |
| `SocialRelationshipsController` | `/api/v1/social/relationships` | Relationship persistence and authorized transaction integration remain required. |
| `GroupMembershipController` | `/api/v1/groups` | Trusted actor evidence, persistence and transaction integration remain required. |
| `SocialContextualRecommendationController` | `/api/v1/social/ai/recommendations` | Actions also retain `[NonAction]`; authoritative consent, admission, dispatch and delivery acceptance remain required. |
| `AiChatController` | No HTTP route attributes | Direct service preparation only; default host does not activate it. |

No client should invoke these dormant surfaces as live endpoints. Existing direct-call tests and opt-in registrations do not change MVC discovery. `Program.cs` is not modified by this documentation. Development OpenAPI/Swagger remains Development-only and restricted by the host's localhost/loopback checks; this document does not authorize public documentation exposure.

## Persistence already present and remaining gaps

`SocialQuestionnairePersistenceRepository` implements `IQuestionnairePersistenceRepository` with `SocialDbContext` and a PostgreSQL read adapter. Questionnaire response/answer entities, EF mappings, snapshot persistence and owner-scoped orchestration already exist. Do not describe snapshot storage as wholly missing or authorize reimplementation from this document.

The separate `ISocialQuestionnaireLifecycleRepository` contract underpins active-response lookup, initial creation, replacement and withdrawal. No concrete production lifecycle repository is supplied by the existing snapshot repository. `AddSocialQuestionnaireResponseServices()` is explicitly opt-in and requires separately supplied persistence dependencies; the default host calls only the baseline questionnaire registration. Existing lifecycle interfaces and service methods do not approve retakes, restoration, retention periods or deployment.

`IPrivateProfileService` already has an Accounts registration. Production integration still requires authoritative authorized profile evidence and accepted runtime dependencies. Group integration requires a trusted `IGroupAuthoritativeEvidenceProvider` and real repositories/transaction boundaries. Test doubles and in-memory fixtures are not production authorities. Existing AI/communication implementations elsewhere in the repository must be assessed against their own contracts; do not characterize all upstream work as scaffolding.

## Deferred work and governing decisions

Tasks **237–244 remain deferred** under the Team Lead directive, as do **R01/R02**. This task does not reopen them. The checked-in references do not provide a verified per-number specification for 237–244; do not relabel that range as eight approved Group schema/migration tasks. Existing Social snapshot persistence must be distinguished from remaining lifecycle, relationship and Group integration requirements.

The governing M5 integration contracts define these pending decisions:

| Decision | Scope |
|---|---|
| P-04 | Social scoring weights, questionnaire retention and matching thresholds. |
| P-06 | Zone roles, joining and non-Club quotas. |
| P-07 | Group inactivity and restoration rules. |
| P-10 | Contextual AI consent, provider, retention and quotas. |

These are not approvals for a five-Zone quota, a notification mechanism, a default age gap or a thirty-day questionnaire cooldown. Do not invent policy values or treat pending decisions as implemented lifecycle permissions.

Activation requires explicit approval for the affected surface, authoritative dependency wiring, privacy/security acceptance, applicable relational gates and deployment verification. No mandatory Redis installation, Supabase migration, schema change or endpoint activation is introduced here. Preserve PostgreSQL and existing configuration. Source configuration and offline test success alone do not prove staging or production readiness.

## Verification references

- `src/Andril.API/Program.cs`
- `src/Andril.API/Controllers/Social/SocialQuestionnaireDefinitionsController.cs`
- `src/Andril.API/Authorization/Social/SocialQuestionnaireDefinitionAccessService.cs`
- `src/Andril.API/Extensions/SocialQuestionnaireServiceCollectionExtensions.cs`
- `src/Andril.API/Middleware/PrivateProfileExceptionHandler.cs`
- `src/Andril.Social/Personality/QuestionnaireV1Definition.cs`
- `src/Andril.Persistence/Repositories/Social/SocialQuestionnairePersistenceRepository.cs`
- `src/Andril.API/Authorization/Social/SocialQuestionnaireLifecycleAccessService.cs`
- `docs/M5_Social_Integration_Contract_PROPOSED.md`
- `docs/M5_CF07_Groups_Integration_Contract_PROPOSED.md`
- `docs/M5_Offline_Development_Readiness_20261124.md`
