# Social matching and relationships for Flutter

Use a confirmed Supabase bearer identity for every request. The server resolves the private Social profile from the authenticated account. Send the target's **Social profile ID** from an Explore card. Requests and responses are private and use `Cache-Control: no-store`.

## Explore and distance

`PUT /api/v1/social/matching/preferences` records an explicit discoverability choice and the approved filters. A profile begins with no matching preferences. Set `discoverable` to `false` to suppress the profile from new discovery. The age range is 18 through 99 and the distance range is 0 through 999 km, with each minimum at or below its maximum.

```json
{
  "discoverable": true,
  "showMe": "All",
  "minAge": 18,
  "maxAge": 55,
  "minDistanceKm": 0,
  "maxDistanceKm": 100,
  "languages": ["Tamil"],
  "connectionGoals": ["Friendship"]
}
```

The acknowledged update returns `204`. The `showMe` values are `All`, `Men`, and `Women`. Send the approved language and connection-goal labels from the Social selectors. `PUT /api/v1/social/location` accepts a foreground, consented device reading and returns `204`.

```json
{"latitude": 6.912345, "longitude": 79.911111, "consent": true}
```

These coordinates are synthetic test input. The server stores a coarse cell and the time of receipt. `DELETE /api/v1/social/location` clears the stored location evidence and returns `204`. Avoid background submissions. Withheld or withdrawn location leaves relationship operations available.

`GET /api/v1/social/explore?limit=20&rotationOffset=0` returns `200` and an array. `limit` defaults to 20 and caps at 100. `rotationOffset` is a non-negative integer that rotates the bounded ranked batch. A profile needs current authorized evidence for both participants, with distance within both ranges. Missing, expired, or withdrawn evidence excludes the candidate. Render `[]` as an empty feed.

```json
[
  {
    "candidateProfileId": "10000000-0000-0000-0000-000000000002",
    "displayName": "Sample profile",
    "age": 28,
    "approximateDistanceKm": 11,
    "sharedLanguages": ["Tamil"],
    "sharedConnectionGoals": ["Friendship"]
  }
]
```

The six fields in the example are the complete card. A skipped questionnaire leaves the personality factor unavailable, so the existing engine evaluates the available factors.

## Interest, relationship status and safety

Send `POST /api/v1/social/relationships/interest` with an explicit choice.

```json
{"targetProfileId":"10000000-0000-0000-0000-000000000002","isPositive":true}
```

A successful unilateral action returns `200` with the caller's own outcome.

```json
{"success":true,"matchCreated":false,"matchId":null,"visibleState":"PendingUnilateral"}
```

An accepted negative decision uses `Passed`. Two affirmative decisions can yield `Matched` and an authorized `matchId`. When an affirmative request repeats an active match, `matchCreated` is `false` and the match ID is stable. Handle `success:false` as a failed operation even when HTTP returns `200`.

`GET /api/v1/social/relationships/{targetProfileId}/status` returns `hasActiveMatch`, `matchId` and `matchedAtUtc`. Render an inactive result without a match card.

```json
{"hasActiveMatch":false,"matchId":null,"matchedAtUtc":null}
```

`POST /api/v1/social/relationships/{targetProfileId}/revoke` and `DELETE /api/v1/social/relationships/{targetProfileId}` return `200` with `success` and `matchRevoked`. A repeat can return `{"success":true,"matchRevoked":false}`. Communication and AI require separate authorization.

`PUT /api/v1/social/safety/blocks/{targetProfileId}` blocks a target and revokes any active match. `DELETE` on the same path removes only the caller's own block. Both return `204`. Unblocking leaves previously revoked matches revoked. Present generic unavailable or denied states without indicating whether the caller or target blocked the pair.

The moderator surface uses `PUT /api/v1/social/safety/restrictions/{targetProfileId}` with `{"restricted":true}` or `{"restricted":false}`. It requires a confirmed operator with an explicit Social moderation grant and returns `204` on committed decisions.

## Errors and environment

Handle `400` for invalid fields or malformed JSON, `401` for missing or invalid bearer identity, `403` for unconfirmed identity or denied access, `415` for a non-JSON request body on JSON operations, and `503` for unavailable identity verification or storage. Display generic retry or unavailable states for `503`. With feature activation off, the affected routes are absent and return `404`.

The local and CI acceptance runs use a disposable PostgreSQL instance and isolated bearer confirmation. Apply the Accounts and Social migrations on an identified environment before enabling the matching runtime. Provide the existing `ConnectionStrings:AndrilAccounts` configuration without committing credentials. Set `SocialMatching:EnablePersistence` and provide a restricted `SocialMatching:RuntimeRole`. Keep `SocialQuestionnaire:EnablePersistence` and its role independent. To enable location and Explore, explicitly set `SocialMatching:EnableDistanceDiscovery`, `SocialMatching:Location:CellSizeDegrees`, and `SocialMatching:Location:MaximumAge`. The cell size accepts values from `0.01` to `90` degrees with at most two fractional digits. The freshness limit is a positive `TimeSpan`. Choose approved values as part of environment configuration.

The role needs Social schema usage, the scoped safety and matching function grants, and the table and column privileges validated at startup. Keep account demographics, private profiles, block rows, safety audit and moderation grants under their authority owner. M6 provisions the trusted operator rows. Production deployment requires its own identity-provider verification and role provisioning.

Run `pwsh -NoProfile -File scripts/ci/run-member-05-social-db.ps1` with `ANDRIL_M2_TEST_POSTGRES` scoped to the disposable loopback maintenance connection. The script requires every manifest case to execute and pass. [Versioned Social matching OpenAPI](../contracts/openapi/social-matching.v1.yaml)
