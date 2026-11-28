# Shared Groups API v1 for Flutter

Flutter uses one Shared Groups API for Learning Classrooms, Hobbies Clubs, and Social Zones. Every request uses the signed-in person's Supabase bearer token and requires confirmed identity. Keep module and Group kind paired in all requests.

| Module | Group | `groupKind` |
| --- | --- | --- |
| `learning` | Classroom | 1 |
| `hobbies` | Club | 2 |
| `social` | Zone | 3 |

The API derives the acting profile from confirmed identity and the requested module. All responses use `Cache-Control: no-store`. Keep Group records and opaque request handles in session memory and remove them when the user changes modules, signs out, or loses access. Use explicit authorization results before rendering management controls.

## Operations

Paths below are relative to `/api/v1/groups`. Routes with a JSON body use `application/json`.

| Method and path | Input | Success |
| --- | --- | --- |
| `POST /` | Scope, name, optional description, explicit `isDiscoverable`, optional visibility | `201` with Group details and a `Location` header |
| `GET /discover` | Scope query, optional `search` and `maximum` | `200` with cards containing ID, module, Group kind and name |
| `GET /{groupId}` | Scope query | `200` with authorized Group details |
| `PUT /{groupId}/configuration` | Scope, name, optional description, discoverability, visibility and `expectedRevision` | `200` with the committed settings and revision |
| `GET /{groupId}/participation/me` | Scope query | `200` with only the caller's state |
| `POST /{groupId}/join-requests` | Scope body | `200` with membership operation result |
| `POST /{groupId}/join-requests/cancel` | Scope body | `200` with membership operation result |
| `DELETE /{groupId}/join-requests` | Scope body | Same cancellation result |
| `GET /{groupId}/admissions` | Scope query, optional `maximum` | `200` with pending request handles and UTC submission times for the authorized host |
| `POST /{groupId}/admissions` | Scope body, `requestId`, and `approve` | `200` with membership operation result |
| `POST /{groupId}/leave` | Scope body | `200` with membership operation result |
| `DELETE /{groupId}/members/me` | Scope body | Same departure result |
| `PUT /safety/{module}/blocks/{profileId}` | `{ "enabled": true }` or `false` | `204` for the caller's own directional Learning or Hobbies block |
| `PUT /safety/{module}/restrictions/{profileId}` | `{ "enabled": true }` or `false` | `204` with current module moderation authority |
| `PUT /learning/educators/{profileId}` | `{ "enabled": true }` or `false` | `204` with current Learning administrative authority |

For discovery and host lists, `maximum` defaults to 25 and accepts 1–100. Search accepts up to 200 characters. Pending request handles remain valid for the outstanding request only. A host who uses the older admission body may still supply `applicantProfileId` instead of `requestId`. Use the opaque request handle in new Flutter code. These endpoints do not provide applicant profile identifiers.

## Client states

| Result | Flutter behavior |
| --- | --- |
| Loading | Wait for the server response before granting UI access or marking requests committed. |
| `200 []` | Show an empty discovery list or no pending requests within the authorized scope. |
| `200` personal participation | `0` means neither current membership nor outstanding request, `1` means current membership, and `2` means outstanding request. |
| `200` membership operation | Read `success`, `membershipCreated`, and `entityRemoved`, then refresh personal participation. |
| `400` or `415` | Validate scope, required fields and media type before resubmitting. |
| `401` | Refresh or request sign-in. |
| `403` | Hide or dismiss the unauthorized view. Confirmation and current module access are required. |
| `404` on the Groups prefix | Groups can be disabled for the deployment. |
| `409 creation_quota` | Show the current creation quota and keep the form contents. |
| `409 group_capacity` or the existing Club code `club_capacity` | Keep the pending request visible to the host and refresh current admissions. |
| `409 revision_conflict` | Reload Group details and reconcile configuration edits against the latest revision. |
| `409 participation_conflict` | Refresh participation and any authorized pending list before retrying. |
| `503` | Show a retryable unavailable state. Treat the current permission or database result as unverified. |

New Groups are private unless the creator explicitly selects public visibility. Discovery requires explicit discoverability and current eligibility. Classroom creation requires verified educator capability. Zones require an active, opted-in Social profile age 18 or older. A request requires current host approval in each module. An owner retains ownership while ordinary members may leave voluntarily. Group membership grants no communication or AI access.

For each module independently, Free creators can have up to five active Groups and Premium creators have unlimited creation. Capacity is 50 for Free and 100 for Premium, including the owner and every other active role. Expiry or revocation retains existing Groups and members. After a downgrade the server denies creation at five or more active Groups in the same module and admission at 50 or more active members. The server resolves the creator's current canonical M2 entitlement for each qualifying write.

## Examples

An explicitly discoverable public Club can be created with this body.

```json
{
  "module": "hobbies",
  "groupKind": 2,
  "name": "Weekend Photography",
  "isDiscoverable": true,
  "visibility": "public"
}
```

An authorized host can submit the opaque handle from the pending list.

```json
{
  "module": "hobbies",
  "groupKind": 2,
  "requestId": "45f0894f-e081-4b08-a655-b7e16fb04055",
  "approve": true
}
```

After approval, the caller can receive their own current membership state.

```json
{
  "groupId": "631d61f7-1e14-40e8-a366-7b7ffaed0637",
  "module": "hobbies",
  "groupKind": 2,
  "state": 1
}
```

## Deployment configuration

Provision the Accounts migrations and the `GroupsDbContext` migrations on the same PostgreSQL database. Groups uses its own migration history in `andril_groups`. Social Zone activation also requires the canonical Social safety schema and its authorization functions. Every Shared Groups module requires the authoritative M2 entitlement relation `andril_economy.premium_entitlements` created and maintained by the approved M2 deployment. Groups does not provision subscriptions. Keep Shared Groups activation disabled in deployments where that prerequisite is unavailable.

Set `SharedGroups__EnablePersistence` and enable each selected scope with `SharedGroups__EnableLearning`, `SharedGroups__EnableHobbies`, or `SharedGroups__EnableSocial`. Supply `SharedGroups__RuntimeRole` and the deployment's secure `ConnectionStrings__AndrilAccounts` configuration. The runtime role must use the verified Group functions with no direct Group, identity, entitlement or safety table privileges. The migration authority needs the permissions to create the Group schema and coordinated identity triggers. Human moderation grants are provisioned by an authorized server administrator. The application verifies the selected modules and role at startup.

[Shared Groups OpenAPI v1](../contracts/openapi/shared-groups.v1.yaml)

The selected Group scopes require the installed canonical M2 entitlement relation and the restricted transition boundary at startup. A verified absent entitlement uses Free limits. Provisioning remains a server-side operator operation. See the [secure startup and runtime acceptance matrix](M5_Runtime_API_Acceptance.md).
