# M5 runtime API acceptance and Flutter handoff

The configured Development host exposes 35 verified M5 operations. Shared Groups accounts for 15, Social matching and relationships for 17, and the Social questionnaire for three. HTTP acceptance runs against ASP.NET Core and disposable PostgreSQL using isolated bearer confirmation. It verifies the backend contracts for Flutter. A live Flutter build, hosted Supabase login, provider traffic, and production activation require their own environment evidence.

## Prepare a secure development backend

1. Restore and build from the repository root with the .NET SDK in `global.json`. Use a disposable PostgreSQL 17 development database and a separately approved Supabase development project. Use an HTTPS project origin, the project's actual `ES256` or `RS256` signing algorithm, and the `authenticated` audience. Load `SupabaseAuth__ProjectUrl`, `SupabaseAuth__SigningAlgorithm`, `SupabaseIdentity__PublishableKey`, and `ConnectionStrings__AndrilAccounts` through local environment configuration or a secret provider. Configure the approved Terms, Privacy and Community Guidelines versions, languages and HTTPS URLs in `AccountPolicies`.
2. Generate and review the EF SQL for `AccountsDbContext`, `SocialDbContext` and `GroupsDbContext`. The Social and Group design-time factories only generate offline artifacts. The migration operator applies Accounts first, then Social (including the safety and consent migrations), then `scripts/database/install-canonical-entitlements.sql`, then Groups. Keep all four on the same database. Use a dedicated migration owner and an explicit, restricted runtime role for each opt-in surface. Use the same authorized database ownership boundary for the entitlement table and Group entitlement definer. Keep application schemas outside the Supabase client API and apply the existing posture for RLS, function grants and private identity tables.
3. Provision role memberships server-side. Use `SocialQuestionnaire__RuntimeRole`, `SocialMatching__RuntimeRole` and `SharedGroups__RuntimeRole` for the named restricted roles, with the applicable function and column grants. Keep Group principals out of the entitlement transition and journal grants. The Group startup check requires a separately authorized and auditable provisioning service login with explicit function grants. The installed relation supplies verified Free absence. An inaccessible or unsupported relation prevents Shared Groups startup. These boundaries are described in the [entitlement runbook](M5_Group_Entitlement_Authority.md).
4. Use a loopback Development host and enable only the scopes needed. `SocialQuestionnaire__EnablePersistence` enables questionnaire submit and self retrieval. `SocialMatching__EnablePersistence` enables relationship and safety operations. Enable distance discovery with `SocialMatching__EnableDistanceDiscovery` and supply approved `SocialMatching__Location__CellSizeDegrees` and `SocialMatching__Location__MaximumAge`. Enable `SocialMatching__EnableInterestSelections` and `SocialMatching__EnablePersonality` separately. `SharedGroups__EnablePersistence` requires one or more of `SharedGroups__EnableLearning`, `SharedGroups__EnableHobbies` and `SharedGroups__EnableSocial`. Keep location absent until the user consents and provision catalogue entries through the authorized operator.

Generate the offline SQL files with the installed EF Core 10 tool and the checked-in projects. Run these commands in a separate shell with a nonconnecting model-only Accounts setting. The file names below are local build outputs.

```powershell
$env:ConnectionStrings__AndrilAccounts = 'Host=127.0.0.1;Port=1;Database=andril_model_only;Username=unused'
dotnet ef migrations script --context AccountsDbContext --project src/Andril.Persistence --startup-project src/Andril.API --output accounts.sql
dotnet ef migrations script --context SocialDbContext --project src/Andril.Persistence --startup-project src/Andril.API --output social.sql
dotnet ef migrations script --context GroupsDbContext --project src/Andril.Persistence --startup-project src/Andril.API --output groups.sql
```

After configuring the identified development database connection for the migration operator, apply one reviewed baseline on an empty development database.

```powershell
psql -X -v ON_ERROR_STOP=1 -f accounts.sql
psql -X -v ON_ERROR_STOP=1 -f social.sql
psql -X -v ON_ERROR_STOP=1 -f scripts/database/install-canonical-entitlements.sql
psql -X -v ON_ERROR_STOP=1 -f groups.sql
```

Run `dotnet run --project src/Andril.API --no-launch-profile` with `ASPNETCORE_ENVIRONMENT=Development` and `ASPNETCORE_URLS=https://localhost:5081` in the development shell. Configure the local trusted HTTPS certificate used by ASP.NET Core. Development OpenAPI is available at `/openapi/v1.json` on the loopback host. Flutter sends `Authorization: Bearer <signed-in-user-access-token>` on protected requests. Keep credentials out of commands, request logs and committed settings.

For the relational acceptance gate, load `ANDRIL_M2_TEST_POSTGRES` securely with the disposable loopback maintenance connection, database `postgres`, and fixture creation privileges. Then run the same commands used for backend verification.

```powershell
pwsh -NoProfile -File scripts/ci/run-member-05-social-db.ps1
dotnet test tests/Andril.UnitTests -c Release
dotnet test tests/Andril.SecurityTests -c Release
dotnet test tests/Andril.ArchitectureTests -c Release
dotnet build Andril.slnx -c Release
```

## Requirement-to-endpoint acceptance matrix

| Requirement | Verified method and path | Acceptance evidence |
| --- | --- | --- |
| Active definition and private questionnaire | `GET /api/v1/social/questionnaires/active`, `POST /api/v1/social/questionnaires/responses`, `GET /api/v1/social/questionnaires/responses/me` | Authenticated definition, complete/skip, owner readback, duplicate prevention, outage and module isolation |
| Discovery preferences, consented location and reciprocal filters | `PUT /api/v1/social/matching/preferences`, `PUT, DELETE /api/v1/social/location`, `GET /api/v1/social/explore` | Current evidence on both profiles, withdrawal, approximate distance only, no raw location in cards |
| Private interest selections | `GET /api/v1/social/matching/interests/catalogue`, `GET, PUT /api/v1/social/matching/interests/me` | Approved catalogue, owner-only storage, retirement and ranked discovery |
| Questionnaire-bound archetype | `GET /api/v1/social/matching/personality/me`, `PUT /api/v1/social/matching/personality/preferences`, `PUT /api/v1/social/matching/personality/confirmation` | Current completed response, explicit owner choices and privacy-safe ranking |
| Mutual relationship and revocation | `POST /api/v1/social/relationships/interest`, `GET /api/v1/social/relationships/{targetProfileId}/status`, `POST /api/v1/social/relationships/{targetProfileId}/revoke`, `DELETE /api/v1/social/relationships/{targetProfileId}` | Private unilateral intent, one committed reciprocal match under concurrent requests, independent revocation |
| Social safety | `PUT, DELETE /api/v1/social/safety/blocks/{targetProfileId}`, `PUT /api/v1/social/safety/restrictions/{targetProfileId}` | Verified actor, no disclosure of blocker, restricted human authority, match and Zone discovery suppressed |
| Classroom, Club and Zone creation and settings | `POST /api/v1/groups`, `GET /api/v1/groups/{groupId}`, `PUT /api/v1/groups/{groupId}/configuration`, `GET /api/v1/groups/discover` | Canonical Free/Premium transitions, capacities and downgrade, atomic creation, scope and host rights |
| Participation and admission | `GET /api/v1/groups/{groupId}/participation/me`, `POST, DELETE /api/v1/groups/{groupId}/join-requests`, `POST /api/v1/groups/{groupId}/join-requests/cancel`, `GET, POST /api/v1/groups/{groupId}/admissions`, `POST /api/v1/groups/{groupId}/leave`, `DELETE /api/v1/groups/{groupId}/members/me` | Scoped private participation, duplicate requests, host handling, final-slot races and departure |
| Group safety and educator provisioning | `PUT /api/v1/groups/safety/{module}/blocks/{profileId}`, `PUT /api/v1/groups/safety/{module}/restrictions/{profileId}`, `PUT /api/v1/groups/learning/educators/{profileId}` | Module isolation, authorized grants, visibility and admission safety |
| Session revocation and module switching | Existing account starter selection only. No published M5 session or switch operations. | The controlled M2 lifecycle writer rotates epochs and revokes consent in local PostgreSQL. Production switching still needs durable authentication lifecycle and deployed M2 writers. |
| Contextual AI offer, decision, status, withdrawal and dispatch | Dormant | PostgreSQL consent and audit tests use explicit fixture authorities. A deployed session boundary, approved private conversation and interaction evidence, quota, budget, cancellation and late-result delivery remain pending. |
| Chat and calls | Dormant for M5 | A committed match or Group membership confers no transport permission. M4 still needs production conversation ownership, durable transport enforcement and revocation. |

## Flutter calls

Use the current definition's question and option identifiers. To explicitly skip, send `POST /api/v1/social/questionnaires/responses` with `{"version":"v1","skip":true,"answers":[]}`. Read the current response from `/api/v1/social/questionnaires/responses/me`. A skipped response cannot supply an archetype confirmation. A complete submission must contain the current definition's full set of choices. Save `PUT /api/v1/social/matching/preferences` before sending partner personality preferences.

To create an explicitly private Zone, send `POST /api/v1/groups` with `{"module":"social","groupKind":3,"name":"Study Zone","isDiscoverable":false}`. Use the resulting `Location` and scope to refresh the Group. For an authorized visible Group, use the same module and kind in the request body and the `requestId` returned to the host for admission. Keep account IDs, coordinates, questionnaire choices and pending request handles out of screen analytics and persistent logs. Clear local scoped data on logout, switch, or authorization loss.

## Deferred requirements

The task handoffs and checked-in readiness history identify Tasks 237–244 as a batch. They do not provide an authoritative requirement for each number. Subsequent verified work now covers questionnaire persistence and owner HTTP, Social discovery, matching, location and safety, and scoped Group creation, discovery, admission and limits. Exact numeric task closure remains unverified. The open requirements include production session and switch revocation, M4 chat/call authority, Contextual AI consent HTTP and generation, Group inactivity/archive/restoration policy, and any questionnaire history retention, provider pricing or completed AI content retention decision that still lacks approval.

The detailed contracts and client behaviors are in the [Social questionnaire OpenAPI](../contracts/openapi/social-questionnaire.v1.yaml), [Social matching Flutter guide](M5_Social_Matching_Flutter_Integration.md) and [Shared Groups Flutter guide](M5_Shared_Groups_Flutter_Integration.md).
