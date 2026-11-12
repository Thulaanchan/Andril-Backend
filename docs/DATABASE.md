# Database Plan — Not Implemented

Retained data platform: PostgreSQL/Supabase plus private Supabase Storage (ANDRIL BRD v2.4-R1). There is **no DbContext, EF Core package, connection string, migration, schema or deployed database** in this starter archive.

Proposed implementation: use transactional relational constraints for module profiles, group memberships, canonical names, Map evidence and separate coin/XP/Flight Point ledgers; use backend authorization and appropriate RLS. Exact EF Core/Npgsql versions and auth provider are pending decision P-14.

Never hardcode credentials in `appsettings.json` or commit local secrets.
