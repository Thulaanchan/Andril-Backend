using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.LearningPeers;

[DbContext(typeof(LearningPeerActivitiesDbContext))]
[Migration("20261128180000_LearningPeerActivities")]
public sealed class LearningPeerActivities:Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE andril_learning.peer_authority_scopes(
                id uuid PRIMARY KEY,backend_pid integer NOT NULL,transaction_id text NOT NULL,
                actor_account uuid NOT NULL,actor_subject uuid NOT NULL,actor_profile uuid NOT NULL,
                target_account uuid NOT NULL,target_profile uuid NOT NULL,purpose integer NOT NULL CHECK(purpose IN (1,2,7)));
            REVOKE ALL ON andril_learning.peer_authority_scopes FROM PUBLIC;
            ALTER TABLE andril_learning.peer_authority_scopes ENABLE ROW LEVEL SECURITY;
            ALTER TABLE andril_learning.peer_authority_scopes FORCE ROW LEVEL SECURITY;
            CREATE POLICY peer_scope_definer ON andril_learning.peer_authority_scopes TO CURRENT_USER USING(true) WITH CHECK(true);
            CREATE POLICY learning_peer_eligibility ON andril_identity.account_module_lifecycles FOR SELECT TO CURRENT_USER
                USING(module='learning' AND EXISTS(SELECT FROM andril_learning.peer_authority_scopes s
                    WHERE s.backend_pid=pg_backend_pid() AND s.transaction_id=pg_current_xact_id()::text
                        AND s.target_account=account_id AND s.purpose IN (1,2,7)
                        AND s.actor_account=nullif(current_setting('andril.account',true),'')::uuid
                        AND s.actor_subject=nullif(current_setting('andril.subject',true),'')::uuid
                        AND current_setting('andril.module',true)='learning'
                        AND EXISTS(SELECT FROM andril_identity.accounts a JOIN andril_identity.private_module_profiles p ON p.account_id=a.id
                            WHERE a.id=s.actor_account AND a.supabase_user_id=s.actor_subject AND p.id=s.actor_profile AND p.module='learning')
                        AND EXISTS(SELECT FROM andril_identity.private_module_profiles p WHERE p.id=s.target_profile AND p.account_id=s.target_account AND p.module='learning')));
            DO $$ DECLARE qualifier text; BEGIN
                SELECT pg_get_expr(polqual,polrelid) INTO STRICT qualifier FROM pg_policy
                    WHERE polrelid='andril_identity.account_module_lifecycles'::regclass AND polname='learning_peer_eligibility';
                EXECUTE format('CREATE POLICY learning_peer_eligibility_lock ON andril_identity.account_module_lifecycles FOR UPDATE TO %I USING (%s) WITH CHECK(false)',current_user,qualifier);
            END $$;
            CREATE FUNCTION andril_learning.peer_account(actor uuid,target uuid,purpose integer,terms text,privacy text) RETURNS uuid
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
            DECLARE caller_account uuid; DECLARE caller_subject uuid; DECLARE target_account uuid; DECLARE scope_id uuid; DECLARE eligible boolean;
            BEGIN
                IF purpose NOT IN (1,2,7) OR actor=target OR nullif(terms,'') IS NULL OR nullif(privacy,'') IS NULL THEN RETURN NULL; END IF;
                IF NOT pg_try_advisory_xact_lock(hashtextextended('andril.groups.authority',0)) THEN
                    RAISE EXCEPTION 'Learning peer authority unavailable' USING ERRCODE='AG503'; END IF;
                SELECT a.id,a.supabase_user_id INTO caller_account,caller_subject FROM andril_identity.accounts a
                    JOIN andril_identity.private_module_profiles p ON p.account_id=a.id
                    WHERE p.id=actor AND p.module='learning' AND a.id=nullif(current_setting('andril.account',true),'')::uuid
                        AND a.supabase_user_id=nullif(current_setting('andril.subject',true),'')::uuid;
                IF NOT FOUND OR NOT andril_learning.scope_visible(actor,'learning')
                    OR andril_groups.profile_is_restricted(actor,'learning') OR andril_groups.profile_is_restricted(target,'learning')
                    OR andril_groups.pair_is_blocked(actor,target,'learning') THEN RETURN NULL; END IF;
                SELECT p.account_id INTO target_account FROM andril_identity.private_module_profiles p WHERE p.id=target AND p.module='learning';
                IF NOT FOUND OR target_account=caller_account THEN RETURN NULL; END IF;
                scope_id:=gen_random_uuid();
                INSERT INTO andril_learning.peer_authority_scopes VALUES(scope_id,pg_backend_pid(),pg_current_xact_id()::text,caller_account,caller_subject,actor,target_account,target,purpose);
                PERFORM a.id FROM andril_identity.accounts a
                    JOIN andril_identity.account_starter_memberships m ON m.account_id=a.id AND m.module='learning'
                    JOIN andril_identity.account_demographics d ON d.account_id=a.id
                    JOIN andril_identity.account_module_lifecycles l ON l.account_id=a.id AND l.module=m.module
                    JOIN andril_identity.private_module_profiles p ON p.account_id=a.id AND p.module=m.module
                    WHERE a.id=target_account AND p.id=target AND l.state='active'
                        AND d.date_of_birth<=timezone('UTC',statement_timestamp())::date-interval '16 years'
                    FOR SHARE OF a,m,d,l,p NOWAIT;
                eligible:=FOUND;
                IF eligible THEN
                    PERFORM r.account_id FROM andril_identity.account_policy_records r
                        WHERE r.account_id=target_account AND ((r.action='terms_agreed' AND r.version=terms) OR (r.action='privacy_acknowledged' AND r.version=privacy))
                        FOR SHARE NOWAIT;
                    eligible:=EXISTS(SELECT FROM andril_identity.account_policy_records r WHERE r.account_id=target_account AND r.action='terms_agreed' AND r.version=terms)
                        AND EXISTS(SELECT FROM andril_identity.account_policy_records r WHERE r.account_id=target_account AND r.action='privacy_acknowledged' AND r.version=privacy);
                END IF;
                DELETE FROM andril_learning.peer_authority_scopes WHERE id=scope_id;
                IF eligible THEN RETURN target_account; END IF; RETURN NULL;
            EXCEPTION WHEN lock_not_available OR serialization_failure OR deadlock_detected THEN
                RAISE EXCEPTION 'Learning peer authority unavailable' USING ERRCODE='AG503';
            END $$;
            REVOKE ALL ON FUNCTION andril_learning.peer_account(uuid,uuid,integer,text,text) FROM PUBLIC;
            CREATE FUNCTION andril_learning.peer_row_visible(people jsonb,purpose integer) RETURNS boolean
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
            DECLARE actor uuid; DECLARE target uuid; DECLARE mapped uuid; DECLARE person jsonb;
            BEGIN
                IF jsonb_array_length(people)<2 OR (purpose<>2 AND jsonb_array_length(people)<>2) THEN RETURN false; END IF;
                IF (SELECT count(DISTINCT p->>'ProfileId') FROM jsonb_array_elements(people) p)<>jsonb_array_length(people)
                    OR (SELECT count(DISTINCT p->>'AccountId') FROM jsonb_array_elements(people) p)<>jsonb_array_length(people) THEN RETURN false; END IF;
                SELECT (p->>'ProfileId')::uuid INTO actor FROM jsonb_array_elements(people) p
                    WHERE p->>'AccountId'=current_setting('andril.account',true) AND andril_learning.scope_visible((p->>'ProfileId')::uuid,'learning');
                IF actor IS NULL THEN RETURN false; END IF;
                FOR person IN SELECT p FROM jsonb_array_elements(people) p WHERE (p->>'ProfileId')::uuid<>actor LOOP
                    target:=(person->>'ProfileId')::uuid;
                    mapped:=andril_learning.peer_account(actor,target,purpose,current_setting('andril.peer_terms',true),current_setting('andril.peer_privacy',true));
                    IF mapped IS NULL OR person->>'AccountId'<>mapped::text THEN RETURN false; END IF;
                END LOOP;
                IF EXISTS(SELECT FROM jsonb_array_elements(people) a CROSS JOIN jsonb_array_elements(people) b
                    WHERE (a->>'ProfileId')::uuid<(b->>'ProfileId')::uuid AND andril_groups.pair_is_blocked((a->>'ProfileId')::uuid,(b->>'ProfileId')::uuid,'learning')) THEN RETURN false; END IF;
                RETURN true;
            END $$;
            REVOKE ALL ON FUNCTION andril_learning.peer_row_visible(jsonb,integer) FROM PUBLIC;
            """);
        migrationBuilder.Sql($"""
            CREATE TABLE andril_learning.peer_activities(
                id uuid CONSTRAINT pk_learning_peer_activities PRIMARY KEY,module varchar(16) NOT NULL,kind integer NOT NULL,generation uuid NOT NULL,version bigint NOT NULL,
                status varchar(32),pair_low uuid,pair_high uuid,members jsonb NOT NULL,content jsonb NOT NULL,
                CONSTRAINT ck_learning_peer_scope CHECK({LearningPeerActivitiesModelV1.Scope}),
                CONSTRAINT ck_learning_peer_members CHECK({LearningPeerActivitiesModelV1.Members}));
            CREATE UNIQUE INDEX ux_learning_peer_exchange_pending ON andril_learning.peer_activities(pair_low,pair_high) WHERE kind=1 AND status='Pending';
            CREATE UNIQUE INDEX ux_learning_peer_connection_live ON andril_learning.peer_activities(kind,pair_low,pair_high) WHERE kind=7 AND status IN ('Pending','Accepted');
            REVOKE ALL ON andril_learning.peer_activities FROM PUBLIC;
            ALTER TABLE andril_learning.peer_activities ENABLE ROW LEVEL SECURITY;
            ALTER TABLE andril_learning.peer_activities FORCE ROW LEVEL SECURITY;
            CREATE POLICY learning_peer_participant ON andril_learning.peer_activities
                USING(andril_learning.peer_row_visible(members,kind)) WITH CHECK(andril_learning.peer_row_visible(members,kind));
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
        DROP TABLE andril_learning.peer_activities;
        DROP FUNCTION andril_learning.peer_row_visible(jsonb,integer);
        DROP FUNCTION andril_learning.peer_account(uuid,uuid,integer,text,text);
        DROP POLICY learning_peer_eligibility_lock ON andril_identity.account_module_lifecycles;
        DROP POLICY learning_peer_eligibility ON andril_identity.account_module_lifecycles;
        DROP TABLE andril_learning.peer_authority_scopes;
        """);
    protected override void BuildTargetModel(ModelBuilder modelBuilder)=>LearningPeerActivitiesModelV1.Configure(modelBuilder);
}
[DbContext(typeof(LearningPeerActivitiesDbContext))]
public sealed class LearningPeerActivitiesSnapshot:ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)=>LearningPeerActivitiesModelV1.Configure(modelBuilder);
}
