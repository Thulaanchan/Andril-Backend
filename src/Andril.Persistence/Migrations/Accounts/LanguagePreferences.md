# M2 Feature 03 — account language preference

`20261115000100_AddAccountLanguagePreferences` adds only the private
`andril_identity.account_language_preferences` table. The previous migration and
account table definition are unchanged. It has an account_id UUID primary key/FK to
accounts(id), ON DELETE CASCADE, required language varchar(2) restricted to en/ta/si,
and required UTC created/updated timestamps. One account has zero or one preference.
PUBLIC table access is revoked and RLS is enabled without direct-client policies.
Use the same reviewed backend access configuration as the account table, and keep
the schema outside Supabase's exposed schemas. No migration runs on application startup.
Down removes only the preference table; account rows are preserved.

Both endpoints require authentication and an initialized account; otherwise they
return 401 or 404 respectively. Existing account-store failures return safe 503.
GET/PATCH `/api/v1/account/preferences/language` return only language and isStored.
Responses and validation failures use Cache-Control: no-store.

GET with no saved preference returns {"language":"en","isStored":false} without
creating a row. This is an English display fallback, not a saved account decision.
The client must retain its pre-login device selection until first authenticated sync.

PATCH requires both language (exactly en, ta or si) and mode (sync or settings):
- sync: insert only if absent. If stored, return that account language unchanged,
  even if the device submits another language. The client adopts the response.
- settings: explicitly set the account choice. This also works when no row exists.
  Identical writes leave timestamps unchanged. Concurrent differing Settings updates
  use database write order (last applied update wins); there is no version/ETag contract.

Example first sync: {"language":"ta","mode":"sync"} returns
{"language":"ta","isStored":true}. A later settings update can send
{"language":"si","mode":"settings"}. Subsequent stale device syncs cannot overwrite it.
The mode expresses user intent, not a separate authorization privilege; both operations
are scoped to the authenticated caller. There is no account ID input. Unknown JSON
properties (including accountId) and missing/unsupported values return safe 400 errors.

No row is seeded with English and no device language is changed by this backend.
Clients are responsible for retaining/syncing their own local value; localization,
translated notifications and other onboarding fields remain outside this feature.

The repository uses parameterized SQL and READ COMMITTED transactions. The primary
key serializes first-sync races; a separate SELECT reads the committed winner. Settings
upserts change only language and updated_at_utc, preserving creation time. A failure
before commit rolls back. A retry after ambiguous commit does not create duplicate rows.

Validation: migration/model and API tests require no real database. Real PostgreSQL
tests reuse the explicit local-only disposable database fixture documented in README.md;
without ANDRIL_M2_TEST_POSTGRES they are skipped. Never use production connections.
