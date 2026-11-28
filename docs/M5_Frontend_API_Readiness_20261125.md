# M5 frontend API readiness and activation boundaries

Updated 2026-11-28 for Task 294. Repository `Thulaanchan/Andril-Backend`, working branch `Member-5`, integration branch `Develop`. This reference covers the questionnaire contract verified in isolated local acceptance.

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

All twelve questions have `isRequired: true` for a completed submission.
**Questionnaire participation itself remains optional.**
A skipped submission contains no answers; incomplete answers do not constitute a completed questionnaire.
The definition API does not submit or persist answers.
Render server-provided IDs, labels and version rather than fabricated questions or alternative option identifiers.
Distinguish loading and retry states from authentication and service failures.
A missing canonical definition is a server configuration failure, not an approved empty-catalog response.

## Verified questionnaire request flow

Send the confirmed caller's Supabase access token in `Authorization: Bearer …`. Use `Content-Type: application/json` for submission. The server resolves Social account ownership, active private profile, opt-in, and eligibility from trusted Accounts data. Use the same authenticated caller for submission and retrieval. Treat questionnaire responses as private and honor `Cache-Control: no-store`.

| Request | Verified outcomes |
|---|---|
| `GET /api/v1/social/questionnaires/active` | `200` canonical definition, `401` missing authentication, `403` unconfirmed identity. |
| `POST /api/v1/social/questionnaires/responses` | `200` persisted submission, `400` invalid input, `401` missing authentication, `403` denied Social access, `409` existing current response, `503` unavailable storage. |
| `GET /api/v1/social/questionnaires/responses/me` | `200` caller's current response, `401` missing authentication, `403` denied Social access, `404` no current response. |

For completed participation, send one answer for each required question using the IDs returned by the definition. The following verified `v1` example uses the Neutral option throughout.

```json
{
  "version": "v1",
  "skip": false,
  "answers": [
    {"questionId": "q01", "optionId": "3"}, {"questionId": "q02", "optionId": "3"},
    {"questionId": "q03", "optionId": "3"}, {"questionId": "q04", "optionId": "3"},
    {"questionId": "q05", "optionId": "3"}, {"questionId": "q06", "optionId": "3"},
    {"questionId": "q07", "optionId": "3"}, {"questionId": "q08", "optionId": "3"},
    {"questionId": "q09", "optionId": "3"}, {"questionId": "q10", "optionId": "3"},
    {"questionId": "q11", "optionId": "3"}, {"questionId": "q12", "optionId": "3"}
  ]
}
```

For optional participation, submit `{"version":"v1","skip":true,"answers":[]}`. A successful submission returns `{"responseId":"11111111-1111-4111-8111-111111111111","status":"Skipped"}` or the same shape with `Completed`. The ID is generated by the server.

`GET /responses/me` on the same questionnaire base path returns the caller's persisted response. An example skipped response is:

```json
{
  "responseId": "11111111-1111-4111-8111-111111111111",
  "version": "v1",
  "status": "Skipped",
  "createdAtUtc": "2026-11-28T00:00:00+00:00",
  "answers": []
}
```

The submission parser accepts `version`, `skip`, and `answers` with `questionId` and `optionId` items. Invalid versions, invalid answers, and extra fields such as `profileId` return a safe `400` problem. A repeated or concurrent submission can return `409` with `code` set to `CURRENT_RESPONSE_EXISTS`. Retrieve `/responses/me` to reconcile a conflict or an uncertain submission before the user retries. `503` carries a generic unavailable result without internal database diagnostics.

## Local activation and acceptance

1. Use an authorized, isolated PostgreSQL database. Apply the Accounts and Social migration scripts in that order through the existing migration operator workflow. The Social history is `andril_social.__EFMigrationsHistory_Social` and must include `20261127203424_SocialQuestionnaireLifecycle`. Use generated EF migration SQL for the offline Social factory and an authorized operator connection for application.
2. Provision a dedicated PostgreSQL runtime role subject to RLS. Give it `USAGE` on `andril_social`, `SELECT` and `INSERT` on the responses, answers, and current-response tables, and `EXECUTE` on `andril_social.questionnaire_owner_visible(uuid)`. Grant the Accounts application login membership with `SET` and without `INHERIT`. Keep direct identity-table reads and Social `UPDATE`, `DELETE`, and `TRUNCATE` out of the Social runtime role.
3. Set `ConnectionStrings__AndrilAccounts` through the existing secure environment configuration. Supply the configured Supabase and policy catalog settings. Set `SocialQuestionnaire__RuntimeRole` to the provisioned database role and `SocialQuestionnaire__EnablePersistence=true`. Run `dotnet run --project src/Andril.API`. Opted-in startup verifies the shared database, migration history, identity discriminator, and effective grants. The default host retains the existing definition route and leaves submission and self retrieval unmapped.
4. Run the relational acceptance gate with `ANDRIL_M2_TEST_POSTGRES` supplied securely for an isolated loopback instance and its `postgres` maintenance database. The gate creates and cleans up generated test databases and roles. Use `pwsh -NoProfile -File scripts/ci/run-member-05-social-db.ps1`. A passing run requires all 26 exact manifest entries with zero skips. The tests exercise real PostgreSQL and ASP.NET Core with an isolated identity provider fixture.

The [Social questionnaire v1 OpenAPI contract](../contracts/openapi/social-questionnaire.v1.yaml) describes the verified Flutter surface. The Development host serves `/openapi/v1.json` on localhost, and the HTTP acceptance suite checks all three operations against the running host. Production identity and deployment acceptance remain separate.

## Dormant HTTP surfaces

These classes retain `[NonController]` and are excluded from default MVC discovery. Route attributes describe proposed contracts, not callable APIs. An absent route may produce 404, but middleware or other host configuration can affect an HTTP response; do not use a particular status as proof of deployment.

| Controller | Declared route | Current boundary |
|---|---|---|
| `SocialExploreController` | `/api/v1/social/explore` | Matching evidence, access, repository and activation acceptance remain required. |
| `SocialRelationshipsController` | `/api/v1/social/relationships` | Relationship persistence and authorized transaction integration remain required. |
| `GroupMembershipController` | `/api/v1/groups` | Trusted actor evidence, persistence and transaction integration remain required. |
| `SocialContextualRecommendationController` | `/api/v1/social/ai/recommendations` | Actions also retain `[NonAction]`; authoritative consent, admission, dispatch and delivery acceptance remain required. |
| `AiChatController` | No HTTP route attributes | Direct service preparation only; default host does not activate it. |

The default MVC host leaves these surfaces inactive. Development OpenAPI and Swagger remain restricted to localhost and loopback.

## Persistence already present and remaining gaps

`SocialQuestionnairePersistenceRepository` implements `IQuestionnairePersistenceRepository` with `SocialDbContext` and a PostgreSQL read adapter. Questionnaire response/answer entities, EF mappings, snapshot persistence and owner-scoped orchestration already exist. Do not describe snapshot storage as wholly missing or permit re-implementation from this document.

The PostgreSQL lifecycle repository joins the authorized Accounts transaction and uses a restricted Social role to create one current response per profile. The server retains historical snapshots. Replacement and withdrawal require approved history policies. The checked-in opt-in activates only the questionnaire submission and self-retrieval routes.

`IPrivateProfileService` already has an Accounts registration. Production integration still requires authoritative authorized profile evidence and accepted runtime dependencies. Group integration requires a trusted `IGroupAuthoritativeEvidenceProvider` and real repositories/transaction boundaries. Test doubles and in-memory fixtures are not production authorities. Existing AI/communication implementations elsewhere in the repository must be assessed against their own contracts; do not characterize all upstream work as scaffolding.

## Deferred work and governing decisions

Tasks **237–244 remain deferred** under the Team Lead directive. R01 and R02 passed in isolated PostgreSQL during Tasks 285 and 286. The checked-in references do not provide a verified per-number specification for Tasks 237–244.

The governing M5 integration contracts define these pending decisions:

| Decision | Scope |
|---|---|
| P-04 | Social scoring weights, questionnaire retention and matching thresholds. |
| P-06 | Zone roles, joining and non-Club quotas. |
| P-07 | Group inactivity and restoration rules. |
| P-10 | Contextual AI consent, provider, retention and quotas. |

These are not approvals for a five-Zone quota, a notification mechanism, a default age gap or a thirty-day questionnaire cooldown. Do not invent policy values or treat pending decisions as implemented lifecycle permissions.

The new questionnaire configuration has passed controlled local PostgreSQL and HTTP acceptance. Staging and production decisions still require separate environment and identity-provider verification.

## Verification references

- `src/Andril.API/Program.cs`
- `src/Andril.API/Controllers/Social/SocialQuestionnaireDefinitionsController.cs`
- `src/Andril.API/Authorization/Social/SocialQuestionnaireDefinitionAccessService.cs`
- `src/Andril.API/Extensions/SocialQuestionnaireServiceCollectionExtensions.cs`
- `src/Andril.API/Middleware/PrivateProfileExceptionHandler.cs`
- `src/Andril.Social/Personality/QuestionnaireV1Definition.cs`
- `src/Andril.Persistence/Repositories/Social/SocialQuestionnairePersistenceRepository.cs`
- `src/Andril.API/Authorization/Social/SocialQuestionnaireLifecycleAccessService.cs`
- `src/Andril.Persistence/Repositories/Social/PostgresSocialQuestionnaireLifecycleRepository.cs`
- `tests/Andril.IntegrationTests/Database/SocialQuestionnaireHttpPostgresTests.cs`
- `scripts/ci/member-05-social-required-db-tests.txt`
- `docs/M5_Social_Integration_Contract_PROPOSED.md`
- `docs/M5_CF07_Groups_Integration_Contract_PROPOSED.md`
- `docs/M5_Offline_Development_Readiness_20261124.md`

The later [M5 runtime API acceptance matrix](M5_Runtime_API_Acceptance.md) records current opt-in activation, migration order, Flutter examples and the available evidence for deferred Tasks 237–244.
