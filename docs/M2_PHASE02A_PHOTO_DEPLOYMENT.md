# Phase 02A private profile photos — Phase 05 deployment review

This is a future deployment checklist. Phase 02A production routes remain unavailable even with the configuration flag set. Complete separately authorized Phases 02B and 02C before planning enablement.

## Accounts migration review

1. Verify a database backup and its restore procedure. Review the forward and reverse Accounts scripts for `20261120152336_AddPrivateProfilePhotos`, starting at `20261116000200_AddPrivateProfileFields`. Use the existing Accounts migration owner. The only model changes are the supporting `(id, module)` key on `andril_identity.private_module_profiles` and the new `andril_identity.private_profile_photos` table, constraints, and indexes. EF records the change in `andril_identity.__EFMigrationsHistory`.
2. Verify that `andril_identity` is excluded from Supabase's exposed Data API schemas. The migration enables metadata RLS and revokes direct table permissions from PUBLIC, anon, and authenticated. Inspect inherited grants, role memberships, and default privileges separately. Do not grant the runtime role ownership, BYPASSRLS, admin privileges, or migration-history access.
3. Review the following example with the database owner. Substitute the existing dedicated Accounts backend runtime role for the example identifier. The application enforces account/module ownership. The role-based RLS policy below limits access to the backend role and does not provide per-user isolation for callers using that role directly.

```sql
-- Future Phase 05, after review and database backup. Do not run in Phase 02A.
GRANT USAGE ON SCHEMA andril_identity TO andril_accounts_runtime;
GRANT SELECT, INSERT ON andril_identity.private_profile_photos TO andril_accounts_runtime;
GRANT UPDATE (state, reason, is_current, cleanup_pending, late_write_risk,
              lease_token, lease_until_utc, updated_at_utc, superseded_at_utc,
              next_cleanup_attempt_at_utc, cleanup_claim_id, cleanup_claim_expires_at_utc)
    ON andril_identity.private_profile_photos TO andril_accounts_runtime;

CREATE POLICY profile_photos_backend_select ON andril_identity.private_profile_photos
    FOR SELECT TO andril_accounts_runtime USING (true);
CREATE POLICY profile_photos_backend_insert ON andril_identity.private_profile_photos
    FOR INSERT TO andril_accounts_runtime WITH CHECK (true);
CREATE POLICY profile_photos_backend_update ON andril_identity.private_profile_photos
    FOR UPDATE TO andril_accounts_runtime USING (true) WITH CHECK (true);
```

Retain the existing Accounts grants for prerequisite reads and row locks. Confirm that the restricted role can take the existing account and demographics locks and the new profile lock. PostgreSQL row locking requires an UPDATE privilege on at least one column of each locked table, plus applicable RLS policies. The existing profile update permission may already satisfy this. Do not widen table grants before testing the actual restricted role. Photo metadata DELETE is not granted. Test owner-only behavior through the API, and separately verify that anon and authenticated cannot use table access or Storage directly.

## Private Storage and readiness

- Provision only the approved private bucket `andril-profile-photos`, with `image/jpeg` and a 5 MiB object limit. Deny anonymous and ordinary authenticated access. Do not create public or signed delivery URLs.
- Use the exact hosted project HTTPS origin and a modern backend-only secret key from the approved secret provider. Provide `ProfilePhotos__Storage__ProjectUrl`, `ProfilePhotos__Storage__SecretKey`, and optionally the fixed `ProfilePhotos__Storage__BucketName`. Send the modern secret in the `apikey` header. Verify the hosted gateway behavior in the future controlled environment.
- Configuration keys use the `ProfilePhotos__` prefix: `Enabled`, `MaximumUploadBytes`, `MinimumWidth`, `MinimumHeight`, `MaximumWidth`, `MaximumHeight`, `MaximumDecodedPixels`, `MaximumOutputWidth`, `MaximumOutputHeight`, `JpegQuality`, `MaximumConcurrentDecodes`, `CleanupBudgetSeconds`, `CleanupRetryDelaySeconds`, and `MaximumCleanupCandidates`. The approved defaults are checked exactly. Keep `Enabled` false through infrastructure setup and until decoder readiness and integration are approved.
- With a controlled synthetic account, verify grant enforcement, four scoped routes, non-overwriting writes and retries, safe provider errors, private retrieval, and the replacement/cleanup race cases. Exercise failure, timeout, and reconciliation in the approved environment before enablement.

## Replacement, abandoned writes, and rollback

Storage requests have a ten-second client deadline. PostgreSQL assigns a 120-second upload lease using its own UTC clock. A retry with the same request ID can verify the persisted object after the lease becomes reclaimable. A new request can fence an expired pending upload as failed.

M2-D23 gives each eligible request one five-second monotonic cleanup budget, including coordination and Storage, and at most four candidates. Each delete inherits the lesser of the remaining budget and the ten-second Storage deadline. Cancellation is cooperative and is not a hard network or database completion guarantee. Claims are committed in short profile-scoped transactions before any delete. Failed or unresolved outcomes schedule a retry after 60 seconds, with no maximum retry count. An unrecorded claim expires after 65 seconds from acquisition, allowing for the five-second inline window and the 60-second retry interval. Expiry permits reclaim and fences stale result updates. It cannot cancel an already-dispatched delete. An initially eligible terminal row can be processed immediately. No cleanup step can roll back committed publication.

A timed-out or fenced request can still complete a previously sent Storage write. For reclaimed or abandoned uploads the late-write marker persists, and terminal cleanup stays pending even after deletion or a confirmed missing response. A database lease fences publication, not the external write itself. Phase 05 must define and validate provider-backed reconciliation before clearing any marker. Identify unresolved failed, abandoned, and superseded rows. Inspect the trusted scope, immutable key, and claim metadata. Reconfirm that the row is permanently non-current. Investigate delayed writes using provider evidence and documented write-settlement guarantees, then compare against actual private Storage objects. Delete only verified unused objects, record the evidence, and clear the marker only when the write risk has settled. Preserve the key inventory before rollback and incorporate reconciliation into the future personal-data erasure procedure. This phase does not implement an operator command or scheduled worker.

For content requests, the API checks scope and current status both before Storage access and after verifying downloaded bytes. The second check narrows the race but cannot recall bytes already sent or make external identity revocation instantaneous.

For rollback, disable processing and drain requests. Preserve metadata and reconcile objects before any reverse migration. The reverse migration removes photo metadata and the supporting key, but has no ability to remove objects. Export the reconciliation inventory first and review the rollback against the backup. Future account erasure must reconcile private objects before losing their metadata to a database cascade.

## Later disposable PostgreSQL QA

Run the opt-in database suite only against an explicitly isolated loopback PostgreSQL instance on maintenance database `postgres`, using `ANDRIL_M2_TEST_POSTGRES`. The fixture creates and force-drops its randomly named database. Its bootstrap role needs database creation rights. The role-boundary test additionally requires `ANDRIL_M2_TEST_POSTGRES_ALLOW_ROLES=1` and permission to create, grant, set, transfer ownership to, and remove randomly named test roles. A dedicated container bootstrap administrator is appropriate for that test. It is not the application runtime role. The test transfers photo-table and history ownership within its disposable database, tests the proposed runtime grants and client-equivalent RLS, and removes only roles it created. Cluster-level roles do not disappear when the test database is dropped. The migration conditionally revokes Supabase roles if present and does not require them on plain PostgreSQL.

Official contract references: [Storage upload guide](https://supabase.com/docs/guides/storage/uploads/standard-uploads), [Storage API implementation](https://github.com/supabase/storage/tree/master/src/http/routes/object), [Storage errors](https://supabase.com/docs/guides/storage/debugging/error-codes), and [API keys](https://supabase.com/docs/guides/getting-started/api-keys).
