# Production AI core cleanup boundary

Authority: R42/R43, confirmed TC-01–05 and TD-01–07 cross-member implementation.

`LearningAiCoreCleanupStore` is a production-schema persistence primitive. It
removes an exact revoked Learning conversation, its messages and its purge work
in one serializable transaction. It uses the existing pre-snapshot subject fence
and `lock_cv_owner` account → membership → profile order. Scope, persisted
revocation time and the existing 24-hour purge target must match exactly. Cleanup
can begin before the target; this introduces no retention period. Active,
foreign, stale or unverified requests never remove content. A final authority
check and cancellation check precede commit. Uncertain commits propagate for
reconciliation, never automatic replay. Journal and acknowledgement records have
separate retention and are untouched.

The explicit `IAiCoreCleanupAuthority` is a new technical contract, not a claimed
existing M2 service. It must authenticate a maintenance principal and keep the
entire callback under current revocation-coordinated worker authority. Every
removal approval must come from verified legal/retention and content-coverage
evidence, covering the header and purge-work metadata as well as messages.
Unknown evidence denies removal. Synthetic test authority is not production
authority. No policy values, legal holds or operational retention rules are
inferred from a deadline. This primitive returns `CoreRemoved`, never a false
whole-system `IAiConversationCleanupRepository.Purged` result.

`LearningAiCleanupPermissions` supplies explicit scripts for a separate existing
restricted maintenance role. Ordinary runtime grants remain unchanged and have
no DELETE. The maintenance role gets DELETE and non-content SELECT only on the
three core cleanup tables, no message text SELECT, acknowledgement/journal access,
RLS bypass, role creation or capability expansion. Shared Learning function
permissions and identity provisioning require separate operator review. All
existing FORCE RLS policies remain unchanged. Existing lifecycle RLS denies
inactive-profile maintenance; no exception or elevated fallback is invented.

The API-owned `AddLearningAiCoreMaintenance` opt-in composition requires explicit
maintenance options and authority factories. It supplies scoped storage only and
does not register a provider, privacy authority, whole-system purge repository,
hosted worker, HTTP route or shared runtime DbContext. Construction does not
connect to PostgreSQL. Missing factories' results fail closed with safe errors.
This method is not invoked by application startup.

This slice is not scheduled or activated. To complete production
cleanup, supply verified worker/revocation and retention authority, attributable
external-store coverage and the approved inactive/deleted-profile maintenance
path. Transport-send fencing still requires an actual transport participating in
the revocation protocol. Production privacy policy/version/processing-basis
evidence and authoritative Classroom/partner fact writers are also unresolved;
existing denied defaults remain. No live provider, hosted migration, production
endpoint activation or B3-10 completion is claimed.
