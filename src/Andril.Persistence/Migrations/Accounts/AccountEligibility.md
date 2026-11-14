# Feature 04: private DOB, gender and module age eligibility

Migration 20261115000200_AddAccountDemographics adds only andril_identity.account_demographics.
account_id is its UUID primary key and cascading FK to accounts. DOB is a required date;
gender is required varchar(100). Created/updated UTC timestamps are required. Database
checks reject future/non-finite/pre-year-1 DOB, empty/control-character gender and
reversed timestamps. The old migrations, account schema and language table are untouched.
RLS is enabled with no direct-client policies, PUBLIC privileges revoked. Keep the
schema outside Supabase exposed schemas; reviewed backend grants/policies are required
as for the existing account tables. No automatic database initialization or migration.
Down drops demographics only. A rollback destroys those private records, not accounts.

Authenticated GET and PUT /api/v1/account/eligibility resolve ownership solely through
ICurrentUser and the existing account repository. Missing accounts return 404; missing
demographics return 200 with hasSubmission false and all three age checks false. PUT
requires both dateOfBirth (ISO yyyy-MM-dd) and gender, permits corrections and is idempotent
for identical values. Client account IDs, ages and eligibility flags are not accepted.
Parameterized transactional upsert preserves created time and avoids duplicate rows.
No DOB, gender or exact age appears in a response. Responses use no-store, including
validation errors. Do not enable request-body, sensitive EF parameter or database detail
logging for these endpoints; the repository uses the existing sanitized error boundary.

Age is recalculated using the UTC calendar date from TimeProvider, never the host's
local timezone or client age. Birthday comparisons use month/day. Feb 29 anniversaries
occur on March 1 in non-leap years, including non-leap century years such as 2100.
Learning/Hobbies require 16; Social requires 18. Under-16 submissions are stored with
all module age checks false. Age eligibility is not a grant to activate a module.
Feature 06 must enforce fresh age results along with explicit Social opt-in and its
activation rules. Feature 05 owns Terms/Privacy consent. Neither is implemented here.

Outstanding product decisions:
- Approve the gender vocabulary. GenderInputPolicy currently applies technical limits
  only: nonempty trimmed text, at most 100 UTF-16 code units, no control characters.
  No enum or gender-based eligibility rule has been invented.
- Confirm the conservative March 1 leap-day anniversary policy.
- Decide DOB correction restrictions and any evidence-based age verification. Current
  submissions are self-reported and can be corrected; the API does not attest verified age.
- No maximum age is imposed. DateOnly.MinValue (0001-01-01), which the PostgreSQL
  provider treats as an infinity sentinel, is rejected as an unsupported storage value.

Real database tests use the existing explicit disposable local PostgreSQL fixture.
Without ANDRIL_M2_TEST_POSTGRES they are skipped. The model/snapshot and SQL-generation
checks require no connection. Do not point tests or migration commands at production.
