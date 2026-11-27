# M1 AI core production-schema preparation

Authority: confirmed TD-05/cross-member implementation scope. This is new M1-owned
schema preparation for review, not a claim that operational release gates passed.

`LearningAiDbContext` maps the five existing core conversation/message/purge-work/
acknowledgement/journal entities under `andril_ai`. It reuses the verified mapping
code without changing the isolated repository connection guard. Fixture-only
Classroom/partner evidence tables are explicitly excluded. Production source
authority/classification ingestion is not inferred from those fixture tables.

The initial `20261127090000_LearningAiCore` migration contains frozen SQL generated
offline from the production model. It never generates new DDL from a mutable model
when running Up. It requires the actual Learning `scope_visible(uuid,text)`
authority function before applying any core DDL. No new shared FK or cascade/delete
policy is invented. Existing own-data composite foreign keys, uniqueness, expiry,
revision and state constraints are preserved. Every core table has FORCE RLS.

The dedicated history target is `andril_ai.__EFMigrationsHistory_AI`; callers must
configure Npgsql MigrationsHistoryTable with the context constants. No automatic
startup migration, default-schema history, production DI registration or hosted
migration is introduced. Down removes only this migration's five tables, preserving
other schemas and the history container. Rollback is destructive to these five
tables and is verified only on disposable data; no hosted rollback is authorized.
Future differential scaffolding/snapshot evolution and upgrade approval remain
separate review work. The initial migration's frozen DDL must not be silently
regenerated when later mappings change.

PUBLIC has no schema/table/sequence privileges. `LearningAiRuntimePermissions`
produces explicit operator-run least-privilege role scripts; the application does
not execute them at startup. The role must already exist as LOGIN without
SUPERUSER/CREATEDB/CREATEROLE/BYPASSRLS. Identifiers are strict ASCII allowlisted
and provider-quoted. Grants are SELECT/INSERT plus only the already approved
conversation expiry/deletion marker, acknowledgement revision/evidence, and journal
state/time columns. No DELETE, message UPDATE, ownership rewrite, generation
rewrite or authority-binding UPDATE is granted. Existing privileges outside this
schema are not erased or asserted safe by these scripts; operator review remains.

RLS requires trusted current account context and existing Learning scope authority.
These settings are application/server-owned context, not client authorization.
Actual runtime repository/profile/privacy/session coordination must be integrated
before production use; a table policy does not replace the application boundary.

Tests apply the actual migration only to disposable PostgreSQL, verify dedicated
history, default denial, FORCE RLS, no PUBLIC grants, scoped core row round-trip,
existing-schema preservation, rollback/reapplication and restricted runtime grants.
The existing isolated context and its seven-table model remain unchanged. Runtime
test grants are revoked before role/database/server cleanup.

This does not activate or complete production cleanup. Existing fourteen-day user-
save expiry and twenty-four-hour manual deletion target remain unchanged. Purge
work storage alone is not an operational purge/backup/account-deletion integration.
No retention, profile deletion/recreation or cleanup-role policy is invented.
Transport fencing, authoritative policy ingestion, production repositories/wiring,
cleanup worker, pooled context isolation and full HTTP production acceptance remain
release blockers; all unsupported paths stay unregistered/fail-closed.
