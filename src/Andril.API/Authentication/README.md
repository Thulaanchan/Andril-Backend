# Supabase authentication configuration

Set these non-secret deployment environment variables:

- `SupabaseAuth__ProjectUrl=https://<project-ref>.supabase.co`
- `SupabaseAuth__SigningAlgorithm=ES256` or `RS256`, matching the confirmed active signing key
- `SupabaseAuth__Audience=authenticated`

The checked-in blank URL and signing algorithm deliberately reject all authentication.
Unknown algorithms, HS256, invalid project URLs and other audiences also fail closed.
Public `/health` remains available; it is process liveness, not authentication readiness.
Configuration changes require an application restart.

In the Supabase dashboard, inspect Auth signing keys before configuring this API.
If the active key is legacy HS256, migrate to an asymmetric key and issue fresh user
sessions before enabling this integration. This feature does not support legacy tokens.
Enable required email confirmation and configure Google Sign-In in Supabase separately.
Feature 07 adds a confirmed-email check for all `/api/v1/account` routes after JWT
authentication. It does not implement onboarding routing.

Public keys are fetched over HTTPS from the configured project's
`/auth/v1/.well-known/jwks.json`. Redirects are disabled. Microsoft IdentityModel caches
and refreshes keys (10-minute automatic refresh; unknown-key refresh throttled to one
minute). Previously cached keys can remain accepted until refresh; this is not instant
session/key revocation. Key retrieval failures with no usable cached key reject tokens.
Only hosted `*.supabase.co` project URLs are supported; custom domains and self-hosting
require a separately reviewed configuration change.

Never configure a signing private key, JWT shared secret, service-role key or user token
here. Do not enable IdentityModel PII logging or log Authorization headers.
The current-user abstraction exposes only the verified Supabase user UUID. It is not
an account record, a private module identity, an email verification guarantee, or a
claim that a session has not been revoked. Feature 07 separately validates confirmed
email evidence against Supabase's user endpoint; it does not promise instant revocation.

401 challenges and 403 forbids have empty bodies and do not return validation details.
Health is explicitly anonymous; other endpoints require authentication by default.
Test-only identity/restricted controllers exist only in the security test assembly.

## Feature 07 confirmed account identity

Configure `SupabaseIdentity__PublishableKey` with the project's **public publishable**
key (`sb_publishable_...`). No key value is checked in. Secret/service-role keys and
legacy JWT API keys are not supported by this integration. Missing configuration
returns safe 503 for authenticated account requests, while `/health` stays public.

The API sends the already locally validated user access token to the pinned
`GET /auth/v1/user` endpoint with that publishable key. It requires the same UUID,
authenticated audience/role, a non-anonymous user, a nonempty email, and a top-level
`email_confirmed_at` timestamp no later than the current time. User-editable metadata,
client flags, `confirmed_at` (which may be phone confirmation), provider names and
matching email strings are not confirmation evidence. No response payload is stored.

Enable Supabase **Confirm email** in the dashboard. Supabase may auto-confirm users
when that setting is disabled; the backend cannot infer the dashboard setting or
distinguish such auto-confirmation from mailbox verification using this endpoint.
The trusted boundary is Supabase's confirmed-user state, not an independent mailbox test.

All account responses have no-store headers. Unconfirmed email returns safe 403,
upstream rejected identity returns empty 401, and verification outages return safe 503.
The outbound client disables redirects, cookies and HTTP-client loggers, limits
responses to 64 KiB and times out after five seconds. No cross-request evidence cache
or automatic retry is used. Each account request therefore depends on Supabase Auth.

`SupabaseIdentity__AccountRequestsPerMinute` defaults to 60 (valid range 1–600),
a technical per-subject/per-process fixed-window limit with no queue. Exhaustion
returns empty 429 with Retry-After before user lookup. Set shared edge/IP/global
limits for multi-instance deployments; this is not a distributed limiter. Existing
JWT key fetching and validation remain unchanged. Never log inbound Authorization,
OTP/password/reset data or upstream user payloads at the hosting layer.

Screen contracts and dashboard checklist: [M2 Feature 07](../../../docs/M2_FEATURE07.md).
