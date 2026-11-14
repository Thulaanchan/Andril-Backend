# M2 account foundation — migration review

Migration `20261114000100_InitialAccounts` is authored for PostgreSQL and has not been
deployed. It creates only `andril_identity.accounts`. The dedicated AccountsDbContext
uses `andril_identity.__EFMigrationsHistory`; other contexts/schemas are not included.

Columns: application-generated UUID `id` (primary key), UUID `supabase_user_id`
(required unique constraint), required UTC `created_at_utc` and `updated_at_utc`.
Checks reject empty UUIDs and an updated timestamp preceding creation. No passwords,
email, tokens, provider metadata, module identities or invented onboarding flags exist.
The subject is an external identity link, not a database FK into Supabase's managed
`auth` schema. Future eligibility, language, consent and module records should reference
the private account ID through separately validated models/migrations.

Initialization uses parameterized INSERT ON CONFLICT DO NOTHING, followed by a separate
subject-scoped SELECT in a READ COMMITTED transaction. Concurrent requests wait on the
unique constraint and then read the committed winner. Failures before commit roll back.
Retries after an ambiguous commit outcome resolve to the existing row. No update,
delete, account-ID lookup or arbitrary user bootstrap endpoint is exposed.

Set `ConnectionStrings__AndrilAccounts` through a protected environment/secret provider;
the checked-in placeholder is empty. Use verified TLS for a non-local deployment and
never enable sensitive-data logging. Missing configuration returns safe 503 for account
operations, while authentication and health remain usable. No startup migration or
EnsureCreated call is registered. Provisioning/deployment is a separate operator action.

Review before any deployment:
- Check this dedicated schema name is unused and review generated SQL against the target.
- Keep this schema outside Supabase's exposed API schemas. PUBLIC schema/table grants
  are revoked and row-level security is enabled without client policies.
- The backend connection must have authorized access: the table-owning server role can
  operate with RLS; a separate restricted runtime role requires reviewed grants and a
  server-only RLS policy. Do not grant direct access to anon/authenticated client roles.
- No auth.users or teammate table is changed. Down drops only accounts, preserving the
  schema/history and any unrelated objects; rolling back destroys account rows and
  must never be performed against live data without a separate reviewed plan.
- Existing schema grants/default privileges and Supabase settings still require review.

Tests: the model/snapshot and generated up/down SQL checks need no database. Real
constraint/concurrency/rollback tests require explicit `ANDRIL_M2_TEST_POSTGRES`, a
connection string to a disposable local PostgreSQL maintenance database named postgres.
They reject non-loopback hosts, create a random `andril_m2_test_<uuid>` database per test,
apply migrations only there, and remove only that created database afterwards. The
local test role needs CREATE DATABASE. Without the variable these tests are skipped.
Never point this setting at a production service or a tunnel to one.
