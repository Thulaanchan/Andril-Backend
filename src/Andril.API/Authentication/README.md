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
This API does not implement an email-verification or onboarding authorization policy yet.

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
claim that a session has not been revoked. Those authorization controls are future work.

401 challenges and 403 forbids have empty bodies and do not return validation details.
Health is explicitly anonymous; other endpoints require authentication by default.
Test-only identity/restricted controllers exist only in the security test assembly.
