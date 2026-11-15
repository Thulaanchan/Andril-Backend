# M2 Feature 07 — Supabase authentication flow integration

Scope: Screens 04–08 integration contracts and server-confirmed account identity.
Supabase owns credentials, signup, email confirmation, Google authentication,
sessions and recovery. Flutter uses `supabase_flutter`; C# validates identity and
enforces ANDRIL account rules. No Flutter files were found, and none were created.

**Screen-number mapping is an assumption pending the product screen specification:**
04 Registration, 05 Email verification, 06 Login, 07 Forgot password, 08 Reset password.
Google can be entered from Registration/Login. The numbered mapping was requested
for confirmation; the flow contracts below are identified by function so they can
be mapped to the final screen labels without changing backend behavior.

## Common Flutter → Supabase → C# boundary

Initialize the official Flutter SDK with the approved project URL and public
publishable key. Keep refresh tokens under the SDK's session-management facilities;
never send them, passwords, OTPs, Google ID/access tokens, or recovery links to C#.
C# accepts only the **Supabase user access token** in `Authorization: Bearer ...`.

After a successful non-recovery authentication event with a non-null session:

1. `GET /api/v1/account/me` with the current access token.
2. On 200, reuse the existing ANDRIL account. On 404 only, call
   `POST /api/v1/account/initialize` with no body and the same access token.
3. Initialization returns the existing `AccountDto` shape: `accountId`,
   `createdAtUtc`, `updatedAtUtc`. Repeated initialization remains idempotent.
4. A backend 401 means refresh/re-authenticate through Supabase; make at most one
   deliberate retry with a refreshed token, not an infinite retry loop. A 403
   indicates unconfirmed email at this boundary. A 429 requires honoring Retry-After;
   503 is an availability/configuration error, not a reason to bypass verification.

Do not initialize from a signup `user` object without a session, a client `verified`
flag, an email address, an OAuth launch result, or raw redirect parameters. Do not
create policy receipts on Screens 04–08. Terms/Privacy remain Screen 09; starter
selection remains Screen 10. Feature 08 onboarding routing is not implemented.

Subscribe once to `onAuthStateChange`, including an `onError` handler, and dispose
the subscription with the app component lifecycle. Handle `initialSession`,
`signedIn`, `tokenRefreshed`, `signedOut` and `passwordRecovery` distinctly. Do not
let a generic signed-in handler start onboarding while handling recovery. SDK state
and events help the UI; C# independently validates every account request.
[Supabase auth events](https://supabase.com/docs/reference/dart/auth-onauthstatechange).

## Screen 04 — email registration

Flutter calls:

```dart
final response = await supabase.auth.signUp(
  email: enteredEmail,
  password: enteredPassword,
  emailRedirectTo: approvedConfirmationRedirect,
);
```

`approvedConfirmationRedirect` is an operator-provided, allowlisted value, not a
URL supplied by arbitrary navigation input. With Confirm email enabled, expect a
user object and no session. Show the verification flow; do not call C# yet.
Existing-user responses can be intentionally obscured or configuration-dependent:
do not infer that an account exists or was created from the returned user object.
Use neutral delivery messaging and keep credentials out of analytics/logs.
Surface approved password-policy validation without inventing a local policy.
Network, delivery, invalid-input and rate-limit failures remain on registration
with safe retry behavior. No Terms/Privacy checkbox or receipt is added here.
[Signup contract](https://supabase.com/docs/reference/dart/auth-signup).

## Screen 05 — six-digit email verification and resend

The approved Supabase confirmation email must expose its generated six-digit OTP
using the provider's token template variable. This repository supplies no email
copy/template. Keep the entered OTP as a string so leading zeroes survive; six ASCII
digits are a client formatting check, never proof of verification.
[Email template/token contract](https://supabase.com/docs/guides/auth/auth-email-templates).

```dart
final response = await supabase.auth.verifyOTP(
  email: registrationEmail,
  token: enteredSixDigitCode,
  type: OtpType.email,
);
```

Use the email from the active registration flow. Require successful verification
and a usable Supabase session before the common C# initialize/resume sequence.
Invalid, expired or previously consumed OTPs do not advance. Clear/re-enter the
code or explicitly resend; never synthesize verification locally. `signup` and
`magiclink` verification types are deprecated in current verification guidance.
[OTP verification](https://supabase.com/docs/reference/dart/auth-verifyotp).

Resend signup confirmation with:

```dart
await supabase.auth.resend(type: OtpType.signup, email: registrationEmail);
```

This is the current **resend** signup type, distinct from `verifyOTP`'s email type.
It requires an initial signup request. A successful resend does not verify the
user or create a C# account. Respect Supabase responses and configured resend limits;
do not invent expiry or cooldown values. Do not use passwordless `signInWithOtp`
as a substitute for password-registration resend, because that is a different flow.
[Resend contract](https://supabase.com/docs/reference/dart/auth-resend).

If product chooses an email confirmation link instead, let the approved Supabase
SDK callback complete verification/session establishment before calling C#. Merely
opening the app or seeing a callback URL is not proof. Test cold-start and already-open
app handling with the actual approved Android redirect configuration.
[Mobile deep linking](https://supabase.com/docs/guides/auth/native-mobile-deep-linking).

## Screen 06 — email/password login

```dart
final response = await supabase.auth.signInWithPassword(
  email: enteredEmail,
  password: enteredPassword,
);
```

On successful session creation, use the common C# sequence. Invalid credentials
receive neutral sign-in feedback; an unconfirmed-email state returns to the
verification/resend flow. Network failures and rate limits must not create a local
authenticated state. SDK-returned data is not an ANDRIL account until C# accepts
the token and identity evidence. No password is sent to or stored by this backend.
[Password sign-in](https://supabase.com/docs/reference/dart/auth-signinwithpassword).

## Google Sign-In — Registration/Login entry points

Browser OAuth via the official Supabase Flutter SDK is the documented integration:

```dart
await supabase.auth.signInWithOAuth(
  OAuthProvider.google,
  redirectTo: approvedGoogleReturnRedirect,
);
```

The launch result is not a completed login. Wait for the SDK to process the callback
and establish a Supabase session, then use the common C# sequence. Cancellation,
provider errors, callback failure or a missing session do not initialize accounts.
[Flutter OAuth](https://supabase.com/docs/reference/dart/auth-signinwithoauth).

Google redirects to the **Supabase OAuth callback shown in the dashboard**; Supabase
then redirects to the separately allowlisted **app return URI**. These are different
configuration values. Register the approved OAuth clients and consent-screen settings
with Google; store any Google client secret only in Supabase's provider configuration.
No credential or callback value is invented here.
[Google provider setup](https://supabase.com/docs/guides/auth/social-sign-in/auth-google).

If the Flutter team selects native Google authentication instead, exchange the
Google credential with `supabase.auth.signInWithIdToken(provider: OAuthProvider.google,
idToken: googleIdToken, accessToken: googleAccessToken)`, following the installed
official SDK/native package's nonce rules. Validate this alternative against the
actual Flutter lockfile; do not disable nonce validation just to make integration
work. C# still accepts only the resulting Supabase token.
[Native token exchange](https://supabase.com/docs/reference/dart/auth-signinwithidtoken).

ANDRIL keys accounts solely by the verified Supabase UUID. Two UUIDs with the same
email remain different ANDRIL accounts; the same UUID resumes the same account
regardless of provider. Supabase has its own identity-linking behavior, including
automatic linking under its verification rules. Review that provider behavior in
staging; this feature never merges or links ANDRIL accounts by email and does not
offer a manual linking endpoint.
[Supabase identity linking](https://supabase.com/docs/guides/auth/auth-identity-linking).

## Screen 07 — password recovery request

```dart
await supabase.auth.resetPasswordForEmail(
  enteredEmail,
  redirectTo: approvedRecoveryRedirect,
);
```

Use neutral messaging such as “If recovery is available, check your email.” Do not
check account existence through C# first. Successful delivery/request acceptance
is not a password reset. Respect provider delivery/rate limits. An explicit resend
uses this same recovery method, not signup resend. The approved recovery link
returns through the configured SDK callback to the reset flow.
[Password recovery request](https://supabase.com/docs/reference/dart/auth-resetpasswordforemail),
[resend distinctions](https://supabase.com/docs/reference/dart/auth-resend).

## Screen 08 — password reset

Let the SDK consume the recovery callback and establish the recovery session;
handle `AuthChangeEvent.passwordRecovery` before ordinary signed-in continuation.
Do not trust a client query parameter such as `type=recovery` by itself. Expired,
used, malformed or mismatched callbacks require restarting recovery, not bypassing it.
Use the registered callback/SDK flow rather than forwarding reset tokens to C#.
[Recovery events](https://supabase.com/docs/reference/dart/auth-onauthstatechange).

```dart
final response = await supabase.auth.updateUser(
  UserAttributes(password: enteredNewPassword),
);
```

This requires a Supabase-authenticated session. Let Supabase enforce its configured
password and reauthentication rules; no hardcoded password policy or OTP lifetime
is added. On failure remain in reset and show safe feedback. After success, ANDRIL's
client integration should sign out of the recovery session and perform normal
login before the common C# resume sequence. This is a documented client handoff,
not a new C# reset endpoint or Feature 08 onboarding router. The backend cannot
distinguish a recovery UI from another authenticated UI and makes no such claim.
[Password update](https://supabase.com/docs/reference/dart/auth-updateuser).

## C# implementation and security contract

No new endpoints or database migrations were needed. Existing `GET account/me`
and `POST account/initialize` are reused. Business implementations for accounts,
language, eligibility, policies and starter selection are unchanged.

All `/api/v1/account` routes now run these steps before controller execution:

1. Existing JWT signature, issuer, audience, lifetime, subject, role and anonymous
   checks, unchanged. Invalid tokens never trigger the new outbound user lookup.
2. Local per-subject account-request throttling.
3. `GET` to the configured project's `/auth/v1/user`, carrying the already validated
   access token and the configured public publishable key. The endpoint is pinned
   through existing hosted-project URL validation; token claims never select a URL.
   [Supabase authenticated user endpoint](https://supabase.com/docs/guides/auth/jwts).
4. Require a matching user UUID, authenticated audience/role, non-anonymous state,
   nonempty email and valid top-level `email_confirmed_at` no later than server time.
   Never use `user_metadata`, provider labels, phone `confirmed_at`, or client flags.
   [Trusted user fields](https://supabase.com/docs/guides/auth/users).
5. Run the existing account operation unchanged.

New failures: empty 401 when upstream rejects the identity; safe 403 when email is
unconfirmed; safe 503 for missing configuration, malformed/oversized responses,
network/timeout errors, redirects or upstream availability/rate-limit errors.
The five-second/64-KiB technical limits are not OTP expiry or product policies.
There are no automatic upstream retries, cookies, redirects or cross-request
confirmation caches. Outbound HttpClient loggers are disabled. No raw user response,
email, token, password or OTP is persisted or logged by this integration.

All M2 account responses are no-store, including 401/403/429/503. A verified Supabase
identity does not imply Screen 09 agreement, starter membership, profile creation,
instant session revocation, or permission to access another module. Other members'
routes retain their existing authorization behavior. Public health stays public.

## Configuration checklist — operator-owned, not deployed

- Enable email/password authentication and **Confirm email**. This is mandatory:
  Supabase can mark users confirmed automatically when confirmation is disabled.
  C# trusts the provider state and cannot independently inspect the dashboard or
  prove mailbox possession from an auto-confirmed timestamp.
- Confirm the active asymmetric signing algorithm and existing `SupabaseAuth`
  project URL/audience configuration. No HS256 support or JWT validation relaxation.
- Configure `SupabaseIdentity__PublishableKey` with the same project's public
  `sb_publishable_...` key. It is not an admin key; do not substitute `sb_secret_...`,
  a service-role token or a legacy JWT API key. No actual value is in this repository.
- Configure approved Site URL and exact confirmation, Google-return and recovery
  redirect allowlists. Register corresponding Android deep/app links. Keep development
  and production values separate; validate callback handling on the target devices.
- Configure approved SMTP/delivery, signup OTP and recovery email templates. Obtain
  reviewed wording from the product owner; confirm six-digit OTP compatibility with
  the deployed Supabase settings. Set the approved expiry/resend/password rules in
  Supabase; this implementation supplies no numeric values for those rules.
- Enable Google, configure approved OAuth clients and the Supabase callback in
  Google Cloud, and review provider identity-linking/nonce behavior.
- Configure Supabase authentication rate limits and CAPTCHA as required by the
  deployment. Flutter honors provider limits; UI cooldowns alone are insufficient.
  [Supabase rate-limit boundaries](https://supabase.com/docs/guides/auth/rate-limits).
- `SupabaseIdentity__AccountRequestsPerMinute` defaults to 60, configurable from
  1–600. This technical C# fixed-window quota is per validated subject per process,
  with no queue. 429 includes Retry-After before any new user lookup. Deploy edge/IP
  and aggregate limits separately; the in-process limiter is not distributed and
  does not throttle invalid JWT traffic or replace Supabase's credential limits.
- Hosting/proxy/APM settings must redact Authorization, credentials, codes, email
  addresses and callback query strings. Test delivery, Google callbacks, recovery,
  session refresh and revoked/deleted-user behavior with synthetic staging accounts.

## Outstanding issues and scope limits

Screen-number labels need confirmation against the actual screen specification.
The Flutter repository/version lockfile, approved callback values, Google provider
configuration, SMTP/templates and production auth policies were not supplied.
Their configuration and end-to-end validation remain outstanding; no deployment
or live Supabase dashboard checks were performed.

The user lookup adds an availability/latency dependency on Supabase Auth for every
account request. Cached local JWT validity and a user lookup do not promise immediate
logout/revocation across every provider/session configuration. Any stronger session
assurance is a separate requirement. There is no Feature 08 routing, account linking,
module change, password/OTP storage or authentication proxy in this change.

## Actual changed files

New:

- `src/Andril.Common/Authentication/ConfirmedUserEvidence.cs`
- `src/Andril.API/Authentication/SupabaseUserVerifier.cs`
- `src/Andril.API/Middleware/ConfirmedAccountIdentityMiddleware.cs`
- `src/Andril.API/Extensions/SupabaseIdentityServiceCollectionExtensions.cs`
- `tests/Andril.UnitTests/Common/ConfirmedUserEvidenceTests.cs`
- `tests/Andril.IntegrationTests/Authentication/SupabaseUserVerifierTests.cs`
- `tests/Andril.SecurityTests/Authentication/AuthFlowIntegrationTests.cs`
- `docs/M2_FEATURE07.md`

Modified:

- `src/Andril.API/Program.cs` — register verification/throttling and account-wide no-store behavior.
- `src/Andril.API/Authentication/README.md` — configuration and trust-boundary documentation.
- `contracts/openapi/andril-api.v1.yaml` — updated existing account operations/errors; no new endpoints.
- `tests/Andril.SecurityTests/Authentication/SupabaseAuthenticationTests.cs` — extend the shared test factory with synthetic user-endpoint responses and alternate subject claims; existing JWT assertions retained.

No Persistence, migration, other-member feature, existing account-service or
onboarding business-rule implementation files were changed.

## Validation and final Git state — 15 November 2026

Offline restore succeeded for all 15 projects using only the existing local NuGet
cache, with NuGet audit networking disabled. Solution build succeeded with zero
warnings and zero errors; the final test command rebuilt the rate-limit adjustment.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Unit | 119 | 0 | 0 |
| Security/API | 158 | 0 | 0 |
| Integration | 35 | 0 | 19 |
| Architecture | 1 | 0 | 0 |
| **Total** | **313** | **0** | **19** |

Feature 07 added **49 passing tests**: 1 evidence-policy unit test, 29 synthetic
HTTP integration cases and 19 authenticated API/security cases. They exercise
unconfirmed and mismatched subjects, forged metadata/client flags, provider-independent
UUID ownership, repeated account initialization, all existing M2 route boundaries,
JWT rejection before lookup, no-store errors, rate-limit enforcement, missing public
configuration, malformed/duplicate JSON, timeouts and response-size limits.
The existing regression suites still pass. The architecture case is the existing
discovery smoke test, not comprehensive architectural validation.

All new tests use synthetic data and in-memory HTTP handlers. No live Supabase call,
real user, Google OAuth session, email delivery or reset was used. The 19 existing
PostgreSQL tests were disabled/skipped with `ANDRIL_M2_TEST_POSTGRES` empty; they
have **not** passed in this run. No migration was created, edited, applied or deployed.
Live Flutter/Supabase flows remain blocked on the approved staging configuration
and absent Flutter repository; the documented contracts are not a claim of end-to-end
production readiness.

Commands used `MSBuildEnableWorkloadResolver=false`, single-worker MSBuild and a
CLI home under ignored `src/Andril.API/obj/feature07-cli`:

```powershell
dotnet restore Andril.slnx --source "$env:USERPROFILE/.nuget/packages" -p:NuGetAudit=false -p:RestoreDisableParallel=true -m:1
dotnet build Andril.slnx --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test Andril.slnx --no-restore -m:1 -p:UseSharedCompilation=false --logger trx
```

Tests ran in the approved local unrestricted runner with synthetic outbound handlers.
`git diff --check` passed. OpenAPI was reviewed; a standalone YAML/OpenAPI validator
was not run. Public Supabase documentation was consulted for SDK contracts; no
project dashboard or GitHub repository was accessed.

Final branch: **Member-2**, with **4 modified tracked files and 8 new files**, all
unstaged. Git was clean at the start. No remote Git commands, branch switching,
commits, pushes, pulls, merges, rebases or PR creation occurred. Other members'
feature code, all previous migrations, Git remote configuration and secrets were
untouched. No production database changes or deployment occurred. Work stops here
at Feature 07.
