using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Learning;

[DbContext(typeof(LearningDbContext))]
[Migration("20261126090000_LearningCvPersistence")]
public sealed class LearningCvPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE andril_learning.learning_cvs (
          owner_learning_profile_id uuid NOT NULL,module varchar(16) NOT NULL,cv_id uuid NOT NULL,
          template integer NOT NULL,include_contact_information boolean NOT NULL DEFAULT false,sections jsonb NOT NULL,
          CONSTRAINT pk_learning_cvs PRIMARY KEY(owner_learning_profile_id,module,cv_id),
          CONSTRAINT ck_learning_cv_owner CHECK(owner_learning_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid),
          CONSTRAINT ck_learning_cv_id CHECK(cv_id <> '00000000-0000-0000-0000-000000000000'::uuid),
          CONSTRAINT ck_learning_cv_module CHECK(module='learning'),
          CONSTRAINT ck_learning_cv_template CHECK(template IN (1,2,3)),
          CONSTRAINT ck_learning_cv_sections CHECK(jsonb_typeof(sections)='array' AND jsonb_array_length(sections)=7));
        -- Reuse the established physical M2 owner/lifecycle/lock ordering, not setup permissions
        -- or Learning preference revisions as a CV authority. Current Common gates are separate.
        CREATE FUNCTION andril_learning.lock_cv_owner(s uuid,p uuid) RETURNS boolean
        LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
        DECLARE a uuid;
        BEGIN
          IF s::text IS DISTINCT FROM current_setting('andril.subject',true)
             OR current_setting('andril.module',true) IS DISTINCT FROM 'learning' THEN RETURN false; END IF;
          SELECT account.id INTO a FROM andril_identity.accounts account WHERE account.supabase_user_id=s;
          IF a IS NULL THEN RETURN false; END IF;
          PERFORM set_config('andril.account',a::text,true);
          RETURN andril_learning.lock_learning_owners(s,a,p);
        END $$;
        REVOKE ALL ON FUNCTION andril_learning.lock_cv_owner(uuid,uuid) FROM PUBLIC;
        REVOKE ALL ON andril_learning.learning_cvs FROM PUBLIC;
        ALTER TABLE andril_learning.learning_cvs ENABLE ROW LEVEL SECURITY;
        ALTER TABLE andril_learning.learning_cvs FORCE ROW LEVEL SECURITY;
        CREATE POLICY cv_owner_scope ON andril_learning.learning_cvs
          USING(andril_learning.scope_visible(owner_learning_profile_id,module))
          WITH CHECK(andril_learning.scope_visible(owner_learning_profile_id,module));
        -- No shared-profile cascade/retention policy or runtime role grants are inferred.
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE andril_learning.learning_cvs;
        DROP FUNCTION andril_learning.lock_cv_owner(uuid,uuid);
        """);
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => LearningSnapshotV2.Configure(modelBuilder);
}
