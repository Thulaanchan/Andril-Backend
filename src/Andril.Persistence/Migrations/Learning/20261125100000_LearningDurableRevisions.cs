using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Learning;

/// <summary>Learning-owned only. Requires the eighth Accounts migration. Never run at startup.</summary>
[DbContext(typeof(LearningDbContext))]
[Migration("20261125100000_LearningDurableRevisions")]
public sealed class LearningDurableRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
          IF NOT EXISTS (SELECT 1 FROM pg_constraint
              WHERE conrelid = 'andril_identity.private_module_profiles'::regclass
                AND conname = 'AK_private_module_profiles_id_module') THEN
            RAISE EXCEPTION 'Learning requires the eighth Accounts migration';
          END IF;
        END $$;
        CREATE SCHEMA IF NOT EXISTS andril_learning;
        CREATE TABLE IF NOT EXISTS andril_learning.learning_skill_selections (
          id uuid CONSTRAINT pk_learning_skill_selections PRIMARY KEY,
          learning_profile_id uuid NOT NULL, module varchar(16) NOT NULL,
          skill_id uuid NOT NULL, kind integer NOT NULL,
          CONSTRAINT ck_learning_skill_selection_module CHECK (module = 'learning'),
          CONSTRAINT ck_learning_skill_selection_kind CHECK (kind IN (1,2)),
          CONSTRAINT ck_learning_skill_selection_id CHECK (id <> '00000000-0000-0000-0000-000000000000'),
          CONSTRAINT ck_learning_skill_selection_profile CHECK (learning_profile_id <> '00000000-0000-0000-0000-000000000000'),
          CONSTRAINT ck_learning_skill_selection_skill CHECK (skill_id <> '00000000-0000-0000-0000-000000000000'));
        CREATE UNIQUE INDEX IF NOT EXISTS uq_learning_skill_selections_profile_purpose_skill
          ON andril_learning.learning_skill_selections(learning_profile_id,module,kind,skill_id);
        CREATE TABLE IF NOT EXISTS andril_learning.learning_personal_goals (
          id uuid CONSTRAINT pk_learning_personal_goals PRIMARY KEY,
          learning_profile_id uuid NOT NULL, module varchar(16) NOT NULL, text text NOT NULL,
          CONSTRAINT ck_learning_personal_goal_module CHECK (module = 'learning'),
          CONSTRAINT ck_learning_personal_goal_id CHECK (id <> '00000000-0000-0000-0000-000000000000'),
          CONSTRAINT ck_learning_personal_goal_profile CHECK (learning_profile_id <> '00000000-0000-0000-0000-000000000000'));
        CREATE INDEX IF NOT EXISTS ix_learning_personal_goals_profile_module
          ON andril_learning.learning_personal_goals(learning_profile_id,module);
        CREATE TABLE andril_learning.learning_revision_generations (
          generation_id uuid CONSTRAINT pk_learning_revision_generations PRIMARY KEY,
          CONSTRAINT ck_learning_generation_nonempty CHECK (generation_id <> '00000000-0000-0000-0000-000000000000'));
        CREATE TABLE andril_learning.learning_profile_revisions (
          learning_profile_id uuid NOT NULL, module varchar(16) NOT NULL,
          account_id uuid NOT NULL, generation_id uuid NOT NULL, version bigint NOT NULL,
          last_mutation_transaction text NULL,
          selection_ids uuid[] NOT NULL DEFAULT '{}'::uuid[], goal_ids uuid[] NOT NULL DEFAULT '{}'::uuid[],
          CONSTRAINT pk_learning_profile_revisions PRIMARY KEY(learning_profile_id,module),
          CONSTRAINT ck_learning_revision_module CHECK(module='learning'),
          CONSTRAINT ck_learning_revision_version CHECK(version>=0),
          CONSTRAINT ck_learning_revision_profile CHECK(learning_profile_id <> '00000000-0000-0000-0000-000000000000'),
          CONSTRAINT ck_learning_revision_account CHECK(account_id <> '00000000-0000-0000-0000-000000000000'),
          CONSTRAINT ck_learning_revision_generation CHECK(generation_id <> '00000000-0000-0000-0000-000000000000'),
          CONSTRAINT fk_learning_revision_generation FOREIGN KEY(generation_id)
            REFERENCES andril_learning.learning_revision_generations(generation_id) ON DELETE RESTRICT,
          CONSTRAINT fk_learning_revision_profile FOREIGN KEY(learning_profile_id,module)
            REFERENCES andril_identity.private_module_profiles(id,module) ON DELETE CASCADE);
        CREATE UNIQUE INDEX uq_learning_revision_generation ON andril_learning.learning_profile_revisions(generation_id);
        CREATE TABLE andril_learning.learning_mutation_receipts (
          operation_id uuid CONSTRAINT pk_learning_mutation_receipts PRIMARY KEY,
          learning_profile_id uuid NOT NULL,module varchar(16) NOT NULL,generation_id uuid NOT NULL,
          expected_version bigint NOT NULL,result_version bigint NOT NULL,intent_hash text NOT NULL,
          CONSTRAINT fk_learning_receipt_revision FOREIGN KEY(learning_profile_id,module)
            REFERENCES andril_learning.learning_profile_revisions(learning_profile_id,module) ON DELETE CASCADE);
        CREATE INDEX ix_learning_mutation_receipts_profile ON andril_learning.learning_mutation_receipts(learning_profile_id,module);

        CREATE FUNCTION andril_learning.register_revision_generation() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
        BEGIN
          IF NOT EXISTS(SELECT 1 FROM andril_identity.private_module_profiles
              WHERE id=NEW.learning_profile_id AND module=NEW.module AND account_id=NEW.account_id) THEN
            RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Learning revision owner mismatch';
          END IF;
          -- INSERT, never ON CONFLICT reuse: historical generations remain reserved after cascade.
          INSERT INTO andril_learning.learning_revision_generations VALUES(NEW.generation_id);
          RETURN NEW;
        END $$;
        REVOKE ALL ON FUNCTION andril_learning.register_revision_generation() FROM PUBLIC;
        CREATE TRIGGER learning_register_generation BEFORE INSERT ON andril_learning.learning_profile_revisions
          FOR EACH ROW EXECUTE FUNCTION andril_learning.register_revision_generation();
        CREATE FUNCTION andril_learning.protect_revision_identity() RETURNS trigger
        LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN
          IF (NEW.learning_profile_id,NEW.module,NEW.account_id,NEW.generation_id)
             IS DISTINCT FROM (OLD.learning_profile_id,OLD.module,OLD.account_id,OLD.generation_id)
             OR NEW.version<OLD.version THEN
            RAISE EXCEPTION USING ERRCODE='P2003',MESSAGE='Learning revision identity is immutable';
          END IF;
          RETURN NEW;
        END $$;
        REVOKE ALL ON FUNCTION andril_learning.protect_revision_identity() FROM PUBLIC;
        CREATE TRIGGER learning_protect_revision BEFORE UPDATE ON andril_learning.learning_profile_revisions
          FOR EACH ROW EXECUTE FUNCTION andril_learning.protect_revision_identity();

        -- Stable profile ordering; baseline 0 describes current content, never fabricated history.
        -- The migration transaction rolls back entirely on an orphan/invalid legacy row below.
        DO $$ DECLARE p record; g uuid; BEGIN
          FOR p IN SELECT id,account_id FROM andril_identity.private_module_profiles
                   WHERE module='learning' ORDER BY id LOOP
            g := gen_random_uuid();
            INSERT INTO andril_learning.learning_profile_revisions(learning_profile_id,module,account_id,generation_id,version)
              VALUES(p.id,'learning',p.account_id,g,0);
          END LOOP;
        END $$;
        UPDATE andril_learning.learning_profile_revisions revision SET
          selection_ids=ARRAY(SELECT id FROM andril_learning.learning_skill_selections WHERE learning_profile_id=revision.learning_profile_id AND module=revision.module ORDER BY id),
          goal_ids=ARRAY(SELECT id FROM andril_learning.learning_personal_goals WHERE learning_profile_id=revision.learning_profile_id AND module=revision.module ORDER BY id);
        ALTER TABLE andril_learning.learning_skill_selections ADD CONSTRAINT fk_learning_selection_revision
          FOREIGN KEY(learning_profile_id,module) REFERENCES andril_learning.learning_profile_revisions ON DELETE CASCADE;
        ALTER TABLE andril_learning.learning_personal_goals ADD CONSTRAINT fk_learning_goal_revision
          FOREIGN KEY(learning_profile_id,module) REFERENCES andril_learning.learning_profile_revisions ON DELETE CASCADE;

        -- Lifecycle provisioning only, never lazy repair at a read/mutation boundary.
        CREATE FUNCTION andril_learning.provision_profile_revision() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
        DECLARE g uuid;
        BEGIN
          IF NEW.module='learning' THEN
            g:=gen_random_uuid();
            INSERT INTO andril_learning.learning_profile_revisions(learning_profile_id,module,account_id,generation_id,version)
              VALUES(NEW.id,NEW.module,NEW.account_id,g,0);
          END IF;
          RETURN NEW;
        END $$;
        REVOKE ALL ON FUNCTION andril_learning.provision_profile_revision() FROM PUBLIC;
        CREATE TRIGGER learning_provision_revision AFTER INSERT ON andril_identity.private_module_profiles
          FOR EACH ROW EXECUTE FUNCTION andril_learning.provision_profile_revision();

        -- All goal/selection writers advance once per atomic logical transaction. Runtime roles
        -- cannot update revision metadata directly. Owner-only fixture seeding is not runtime authority.
        CREATE FUNCTION andril_learning.advance_profile_revision() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
        DECLARE p uuid; m varchar(16); n integer;
        BEGIN
          IF TG_OP='UPDATE' THEN
            IF NEW.learning_profile_id<>OLD.learning_profile_id OR NEW.module<>OLD.module OR NEW.id<>OLD.id THEN
              RAISE EXCEPTION USING ERRCODE='P2003',MESSAGE='Learning scope is immutable';
            END IF;
            IF NEW IS NOT DISTINCT FROM OLD THEN RETURN NEW; END IF;
          END IF;
          IF TG_OP='DELETE' THEN p:=OLD.learning_profile_id;m:=OLD.module;
          ELSE p:=NEW.learning_profile_id;m:=NEW.module; END IF;
          -- Authorized parent deletion cascades; never recreate an anchor during deletion.
          IF TG_OP='DELETE' AND NOT EXISTS(SELECT 1 FROM andril_identity.private_module_profiles WHERE id=p AND module=m) THEN
            RETURN OLD;
          END IF;
          IF EXISTS(SELECT 1 FROM andril_learning.learning_profile_revisions
             WHERE learning_profile_id=p AND module=m AND version=9223372036854775807
             AND last_mutation_transaction IS DISTINCT FROM pg_current_xact_id()::text) THEN
            RAISE EXCEPTION USING ERRCODE='P2002',MESSAGE='Learning revision exhausted';
          END IF;
          UPDATE andril_learning.learning_profile_revisions SET
            version=version+CASE WHEN last_mutation_transaction IS DISTINCT FROM pg_current_xact_id()::text THEN 1 ELSE 0 END,
            last_mutation_transaction=pg_current_xact_id()::text,
            selection_ids=CASE WHEN TG_TABLE_NAME='learning_skill_selections' AND TG_OP='INSERT' THEN array_append(selection_ids,NEW.id)
              WHEN TG_TABLE_NAME='learning_skill_selections' AND TG_OP='DELETE' THEN array_remove(selection_ids,OLD.id) ELSE selection_ids END,
            goal_ids=CASE WHEN TG_TABLE_NAME='learning_personal_goals' AND TG_OP='INSERT' THEN array_append(goal_ids,NEW.id)
              WHEN TG_TABLE_NAME='learning_personal_goals' AND TG_OP='DELETE' THEN array_remove(goal_ids,OLD.id) ELSE goal_ids END
            WHERE learning_profile_id=p AND module=m;
          GET DIAGNOSTICS n=ROW_COUNT;
          IF n=0 AND NOT EXISTS(SELECT 1 FROM andril_learning.learning_profile_revisions WHERE learning_profile_id=p AND module=m) THEN
            RAISE EXCEPTION USING ERRCODE='P2001',MESSAGE='Learning revision unavailable';
          END IF;
          IF TG_OP='DELETE' THEN RETURN OLD; END IF; RETURN NEW;
        END $$;
        REVOKE ALL ON FUNCTION andril_learning.advance_profile_revision() FROM PUBLIC;
        CREATE TRIGGER learning_selection_revision AFTER INSERT OR UPDATE OR DELETE ON andril_learning.learning_skill_selections
          FOR EACH ROW EXECUTE FUNCTION andril_learning.advance_profile_revision();
        CREATE TRIGGER learning_goal_revision AFTER INSERT OR UPDATE OR DELETE ON andril_learning.learning_personal_goals
          FOR EACH ROW EXECUTE FUNCTION andril_learning.advance_profile_revision();

        CREATE FUNCTION andril_learning.validate_mutation_receipt() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
        BEGIN
          IF NEW.operation_id='00000000-0000-0000-0000-000000000000'::uuid
             OR NEW.expected_version<0 OR NEW.result_version<NEW.expected_version
             OR NEW.result_version-NEW.expected_version>1 OR NEW.intent_hash !~ '^[0-9A-F]{64}$'
             OR NOT EXISTS(SELECT 1 FROM andril_learning.learning_profile_revisions
                WHERE learning_profile_id=NEW.learning_profile_id AND module=NEW.module
                  AND generation_id=NEW.generation_id AND version=NEW.result_version) THEN
            RAISE EXCEPTION USING ERRCODE='P2003',MESSAGE='Learning receipt does not match committed intent';
          END IF;
          RETURN NEW;
        END $$;
        REVOKE ALL ON FUNCTION andril_learning.validate_mutation_receipt() FROM PUBLIC;
        CREATE TRIGGER learning_validate_receipt BEFORE INSERT ON andril_learning.learning_mutation_receipts
          FOR EACH ROW EXECUTE FUNCTION andril_learning.validate_mutation_receipt();

        -- Bind to M2's existing subject/account/module settings and authoritative physical rows.
        -- Session/standing/purpose evidence is additionally checked by the unregistered boundary.
        CREATE FUNCTION andril_learning.scope_visible(p uuid,m text) RETURNS boolean
        LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
          SELECT m='learning' AND current_setting('andril.module',true)='learning' AND EXISTS(
            SELECT 1 FROM andril_identity.private_module_profiles profile
            JOIN andril_identity.accounts account ON account.id=profile.account_id
            JOIN andril_identity.account_module_lifecycles membership ON membership.account_id=account.id AND membership.module=profile.module
            WHERE profile.id=p AND profile.module=m AND membership.state='active'
              AND account.id::text=current_setting('andril.account',true)
              AND account.supabase_user_id::text=current_setting('andril.subject',true))
        $$;
        ALTER TABLE andril_learning.learning_skill_selections ENABLE ROW LEVEL SECURITY;
        ALTER TABLE andril_learning.learning_personal_goals ENABLE ROW LEVEL SECURITY;
        ALTER TABLE andril_learning.learning_profile_revisions ENABLE ROW LEVEL SECURITY;
        ALTER TABLE andril_learning.learning_mutation_receipts ENABLE ROW LEVEL SECURITY;
        CREATE POLICY learning_owner_selections ON andril_learning.learning_skill_selections
          USING(andril_learning.scope_visible(learning_profile_id,module)) WITH CHECK(andril_learning.scope_visible(learning_profile_id,module));
        CREATE POLICY learning_owner_goals ON andril_learning.learning_personal_goals
          USING(andril_learning.scope_visible(learning_profile_id,module)) WITH CHECK(andril_learning.scope_visible(learning_profile_id,module));
        CREATE POLICY learning_owner_revisions ON andril_learning.learning_profile_revisions
          FOR SELECT USING(andril_learning.scope_visible(learning_profile_id,module));
        CREATE POLICY learning_owner_receipts ON andril_learning.learning_mutation_receipts
          USING(andril_learning.scope_visible(learning_profile_id,module)) WITH CHECK(andril_learning.scope_visible(learning_profile_id,module));
        ALTER TABLE andril_learning.learning_revision_generations ENABLE ROW LEVEL SECURITY;

        CREATE FUNCTION andril_learning.lock_profile_revision(p uuid,a uuid) RETURNS void
        LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
        BEGIN
          IF current_user<>session_user AND NOT andril_learning.scope_visible(p,'learning') THEN
            RAISE EXCEPTION USING ERRCODE='42501',MESSAGE='Learning access denied';
          END IF;
          PERFORM 1 FROM andril_learning.learning_profile_revisions
            WHERE learning_profile_id=p AND account_id=a AND module='learning' FOR NO KEY UPDATE;
        END $$;
        CREATE FUNCTION andril_learning.lock_learning_owners(s uuid,a uuid,p uuid) RETURNS boolean
        LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
        BEGIN
          IF s::text IS DISTINCT FROM current_setting('andril.subject',true)
             OR a::text IS DISTINCT FROM current_setting('andril.account',true)
             OR current_setting('andril.module',true) IS DISTINCT FROM 'learning' THEN RETURN false; END IF;
          IF current_setting('transaction_isolation')<>'serializable' THEN
            RAISE EXCEPTION USING ERRCODE='P2004',MESSAGE='Learning requires Serializable isolation';
          END IF;
          PERFORM pg_advisory_xact_lock(hashtextextended(s::text,0));
          PERFORM 1 FROM andril_identity.accounts WHERE id=a AND supabase_user_id=s FOR SHARE;
          IF NOT FOUND THEN RETURN false; END IF;
          PERFORM 1 FROM andril_identity.account_module_lifecycles WHERE account_id=a AND module='learning' AND state='active' FOR SHARE;
          IF NOT FOUND THEN RETURN false; END IF;
          PERFORM 1 FROM andril_identity.private_module_profiles WHERE id=p AND account_id=a AND module='learning' FOR SHARE;
          RETURN FOUND;
        END $$;
        REVOKE ALL ON FUNCTION andril_learning.scope_visible(uuid,text) FROM PUBLIC;
        REVOKE ALL ON FUNCTION andril_learning.lock_profile_revision(uuid,uuid) FROM PUBLIC;
        REVOKE ALL ON FUNCTION andril_learning.lock_learning_owners(uuid,uuid,uuid) FROM PUBLIC;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER learning_provision_revision ON andril_identity.private_module_profiles;
        DROP TABLE andril_learning.learning_mutation_receipts;
        DROP TABLE andril_learning.learning_skill_selections;
        DROP TABLE andril_learning.learning_personal_goals;
        DROP TABLE andril_learning.learning_profile_revisions;
        DROP TABLE andril_learning.learning_revision_generations;
        DROP FUNCTION andril_learning.advance_profile_revision();
        DROP FUNCTION andril_learning.provision_profile_revision();
        DROP FUNCTION andril_learning.register_revision_generation();
        DROP FUNCTION andril_learning.protect_revision_identity();
        DROP FUNCTION andril_learning.validate_mutation_receipt();
        DROP FUNCTION andril_learning.lock_profile_revision(uuid,uuid);
        DROP FUNCTION andril_learning.lock_learning_owners(uuid,uuid,uuid);
        DROP FUNCTION andril_learning.scope_visible(uuid,text);
        """);

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        LearningSnapshotV1.Configure(modelBuilder);
    }
}
