using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Diary;

[DbContext(typeof(DiaryDbContext))]
[Migration("20261126080000_ModuleDiaryPersistence")]
public sealed class ModuleDiaryPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        -- M1 owns this new module-private table/history. No shared M2 table is altered and no
        -- parent deletion/cascade or retention contract is inferred from Learning migrations.
        CREATE SCHEMA IF NOT EXISTS andril_diary;
        CREATE TABLE andril_diary.module_diary_entries (
          owner_module_profile_id uuid NOT NULL, module varchar(16) NOT NULL, calendar_date date NOT NULL,
          reflection_title text NULL, reflection_body text NULL, plans jsonb NOT NULL DEFAULT '[]'::jsonb,
          CONSTRAINT pk_module_diary_entries PRIMARY KEY(owner_module_profile_id,module,calendar_date),
          CONSTRAINT ck_diary_profile CHECK(owner_module_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid),
          CONSTRAINT ck_diary_module CHECK(module IN ('learning','hobbies','social')),
          CONSTRAINT ck_diary_date CHECK(isfinite(calendar_date) AND calendar_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'),
          CONSTRAINT ck_diary_reflection CHECK(reflection_body IS NOT NULL OR reflection_title IS NULL),
          CONSTRAINT ck_diary_plans CHECK(jsonb_typeof(plans)='array' AND jsonb_array_length(plans)<=20));

        CREATE FUNCTION andril_diary.scope_visible(p uuid,m text) RETURNS boolean
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
          SELECT m=current_setting('andril.module',true) AND EXISTS(
            SELECT 1 FROM andril_identity.private_module_profiles profile
            JOIN andril_identity.accounts account ON account.id=profile.account_id
            JOIN andril_identity.account_module_lifecycles membership
              ON membership.account_id=account.id AND membership.module=profile.module
            WHERE profile.id=p AND profile.module=m AND membership.state='active'
              AND account.id::text=current_setting('andril.account',true)
              AND account.supabase_user_id::text=current_setting('andril.subject',true))
        $$;
        CREATE FUNCTION andril_diary.lock_owner(s uuid,p uuid,m text) RETURNS uuid
        LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
        DECLARE a uuid;
        BEGIN
          IF current_setting('transaction_isolation')<>'serializable' THEN
            RAISE EXCEPTION USING ERRCODE='P3001',MESSAGE='Diary requires Serializable isolation';
          END IF;
          IF s::text IS DISTINCT FROM current_setting('andril.subject',true)
             OR m IS DISTINCT FROM current_setting('andril.module',true)
             OR m NOT IN ('learning','hobbies','social') THEN RETURN NULL; END IF;
          -- The caller already holds the existing M2 subject advisory key on a dedicated
          -- nonpooled connection before its Serializable snapshot is created.
          PERFORM pg_advisory_xact_lock(hashtextextended(s::text,0));
          SELECT id INTO a FROM andril_identity.accounts WHERE supabase_user_id=s FOR SHARE;
          IF a IS NULL THEN RETURN NULL; END IF;
          PERFORM set_config('andril.account',a::text,true);
          PERFORM 1 FROM andril_identity.account_module_lifecycles
            WHERE account_id=a AND module=m AND state='active' FOR SHARE;
          IF NOT FOUND THEN RETURN NULL; END IF;
          PERFORM 1 FROM andril_identity.private_module_profiles
            WHERE id=p AND account_id=a AND module=m FOR SHARE;
          IF NOT FOUND THEN RETURN NULL; END IF;
          RETURN a;
        END $$;
        REVOKE ALL ON FUNCTION andril_diary.scope_visible(uuid,text) FROM PUBLIC;
        REVOKE ALL ON FUNCTION andril_diary.lock_owner(uuid,uuid,text) FROM PUBLIC;
        REVOKE ALL ON andril_diary.module_diary_entries FROM PUBLIC;
        ALTER TABLE andril_diary.module_diary_entries ENABLE ROW LEVEL SECURITY;
        ALTER TABLE andril_diary.module_diary_entries FORCE ROW LEVEL SECURITY;
        CREATE POLICY diary_owner_scope ON andril_diary.module_diary_entries
          USING(andril_diary.scope_visible(owner_module_profile_id,module))
          WITH CHECK(andril_diary.scope_visible(owner_module_profile_id,module));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE andril_diary.module_diary_entries;
        DROP FUNCTION andril_diary.scope_visible(uuid,text);
        DROP FUNCTION andril_diary.lock_owner(uuid,uuid,text);
        """);

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => DiarySnapshotV1.Configure(modelBuilder);
}
