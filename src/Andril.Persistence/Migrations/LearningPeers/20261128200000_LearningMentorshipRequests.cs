using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.LearningPeers;

[DbContext(typeof(LearningPeerActivitiesDbContext))]
[Migration("20261128200000_LearningMentorshipRequests")]
public sealed class LearningMentorshipRequests:Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE andril_learning.peer_authority_scopes DROP CONSTRAINT peer_authority_scopes_purpose_check;
            ALTER TABLE andril_learning.peer_authority_scopes ADD CONSTRAINT peer_authority_scopes_purpose_check CHECK(purpose IN(1,2,5,7));
            CREATE POLICY learning_mentorship_eligibility ON andril_identity.account_module_lifecycles FOR SELECT TO CURRENT_USER
                USING(module='learning' AND EXISTS(SELECT FROM andril_learning.peer_authority_scopes s
                    WHERE s.backend_pid=pg_backend_pid() AND s.transaction_id=pg_current_xact_id()::text AND s.purpose=5
                        AND s.target_account=account_id AND s.actor_account=nullif(current_setting('andril.account',true),'')::uuid
                        AND s.actor_subject=nullif(current_setting('andril.subject',true),'')::uuid AND current_setting('andril.module',true)='learning'
                        AND EXISTS(SELECT FROM andril_identity.accounts a JOIN andril_identity.private_module_profiles p ON p.account_id=a.id
                            WHERE a.id=s.actor_account AND a.supabase_user_id=s.actor_subject AND p.id=s.actor_profile AND p.module='learning')
                        AND EXISTS(SELECT FROM andril_identity.private_module_profiles p WHERE p.id=s.target_profile AND p.account_id=s.target_account AND p.module='learning')));
            DO $$ DECLARE qualifier text; BEGIN
                SELECT pg_get_expr(polqual,polrelid) INTO STRICT qualifier FROM pg_policy
                    WHERE polrelid='andril_identity.account_module_lifecycles'::regclass AND polname='learning_mentorship_eligibility';
                EXECUTE format('CREATE POLICY learning_mentorship_eligibility_lock ON andril_identity.account_module_lifecycles FOR UPDATE TO %I USING (%s) WITH CHECK(false)',current_user,qualifier);
            END $$;
            CREATE FUNCTION andril_learning.mentorship_target_account(actor uuid,target uuid,terms text,privacy text) RETURNS uuid
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
            DECLARE caller_account uuid; caller_subject uuid; target_account uuid; scope_id uuid; eligible boolean;
            BEGIN
                IF actor=target OR nullif(terms,'') IS NULL OR nullif(privacy,'') IS NULL
                    OR current_setting('transaction_isolation')<>'serializable' THEN RETURN NULL; END IF;
                IF NOT pg_try_advisory_xact_lock(hashtextextended('andril.groups.authority',0)) THEN
                    RAISE EXCEPTION 'Learning mentorship authority unavailable' USING ERRCODE='AG503'; END IF;
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
                INSERT INTO andril_learning.peer_authority_scopes VALUES(scope_id,pg_backend_pid(),pg_current_xact_id()::text,caller_account,caller_subject,actor,target_account,target,5);
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
                RAISE EXCEPTION 'Learning mentorship authority unavailable' USING ERRCODE='AG503';
            END $$;
            REVOKE ALL ON FUNCTION andril_learning.mentorship_target_account(uuid,uuid,text,text) FROM PUBLIC;
            CREATE FUNCTION andril_learning.mentorship_members(actor uuid,learner uuid,mentor uuid,offer uuid,terms text,privacy text) RETURNS jsonb
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
            DECLARE other_account uuid; learner_account uuid; mentor_account uuid; epoch bigint;
            BEGIN
                IF actor IS NULL OR learner IS NULL OR mentor IS NULL OR offer IS NULL OR learner=mentor
                    OR actor NOT IN(learner,mentor) OR offer='00000000-0000-0000-0000-000000000000'::uuid THEN RETURN NULL; END IF;
                other_account:=andril_learning.mentorship_target_account(actor,CASE WHEN actor=learner THEN mentor ELSE learner END,terms,privacy);
                IF other_account IS NULL THEN RETURN NULL; END IF;
                IF actor=learner THEN
                    learner_account:=nullif(current_setting('andril.account',true),'')::uuid;
                    mentor_account:=andril_learning.published_target_account(actor,mentor,2,offer,terms,privacy);
                    IF mentor_account IS NULL OR mentor_account<>other_account THEN RETURN NULL; END IF;
                ELSE
                    mentor_account:=nullif(current_setting('andril.account',true),'')::uuid;
                    learner_account:=other_account;
                    -- The mentor is the actual caller, never an impersonated learner.
                    SELECT f.lifecycle_revision INTO epoch FROM andril_learning.publication_owner_facts(mentor) f WHERE f.standing_allows;
                    IF epoch IS NULL OR NOT andril_learning.publication_owned(2,offer,mentor) THEN RETURN NULL; END IF;
                    PERFORM p.profile_id FROM andril_learning.publications p WHERE p.profile_id=mentor AND p.account_id=mentor_account
                        AND p.kind=2 AND p.resource_id=offer AND p.module='learning' AND p.published AND p.lifecycle_revision=epoch FOR SHARE NOWAIT;
                    IF NOT FOUND THEN RETURN NULL; END IF;
                END IF;
                RETURN jsonb_build_array(jsonb_build_object('AccountId',learner_account,'ProfileId',learner),jsonb_build_object('AccountId',mentor_account,'ProfileId',mentor));
            EXCEPTION WHEN lock_not_available OR serialization_failure OR deadlock_detected THEN
                RAISE EXCEPTION 'Learning mentorship authority unavailable' USING ERRCODE='AG503';
            END $$;
            REVOKE ALL ON FUNCTION andril_learning.mentorship_members(uuid,uuid,uuid,uuid,text,text) FROM PUBLIC;
            CREATE FUNCTION andril_learning.peer_content_visible(people jsonb,purpose integer,body jsonb) RETURNS boolean
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
            DECLARE actor uuid; learner uuid; mentor uuid; expected jsonb;
            BEGIN
                IF purpose<>5 THEN RETURN andril_learning.peer_row_visible(people,purpose); END IF;
                IF jsonb_typeof(people)<>'array' OR jsonb_array_length(people)<>2 OR body->'Members'<>people THEN RETURN false; END IF;
                learner:=(body->'Creator'->>'ProfileId')::uuid;
                SELECT (p->>'ProfileId')::uuid INTO mentor FROM jsonb_array_elements(people) p WHERE (p->>'ProfileId')::uuid<>learner;
                SELECT (p->>'ProfileId')::uuid INTO actor FROM jsonb_array_elements(people) p
                    WHERE p->>'AccountId'=current_setting('andril.account',true) AND andril_learning.scope_visible((p->>'ProfileId')::uuid,'learning');
                expected:=andril_learning.mentorship_members(actor,learner,mentor,(body->>'OfferId')::uuid,current_setting('andril.peer_terms',true),current_setting('andril.peer_privacy',true));
                RETURN COALESCE(expected IS NOT NULL AND people @> expected AND expected @> people AND expected->0=body->'Creator',false);
            END $$;
            REVOKE ALL ON FUNCTION andril_learning.peer_content_visible(jsonb,integer,jsonb) FROM PUBLIC;
            DROP POLICY learning_peer_participant ON andril_learning.peer_activities;
            CREATE POLICY learning_peer_participant ON andril_learning.peer_activities
                USING(andril_learning.peer_content_visible(members,kind,content)) WITH CHECK(andril_learning.peer_content_visible(members,kind,content));
            """);
        migrationBuilder.Sql($"""
            ALTER TABLE andril_learning.peer_activities DROP CONSTRAINT ck_learning_peer_scope;
            ALTER TABLE andril_learning.peer_activities ADD CONSTRAINT ck_learning_peer_scope CHECK({LearningPeerActivitiesModelV2.Scope});
            CREATE UNIQUE INDEX ux_learning_peer_mentorship_pending ON andril_learning.peer_activities(pair_low,pair_high,kind) WHERE kind=5 AND status='Pending';
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql($"""
        -- Refuse downgrade with durable mentorship records; never silently delete them.
        ALTER TABLE andril_learning.peer_activities DROP CONSTRAINT ck_learning_peer_scope;
        ALTER TABLE andril_learning.peer_activities ADD CONSTRAINT ck_learning_peer_scope CHECK({LearningPeerActivitiesModelV1.Scope});
        DROP INDEX andril_learning.ux_learning_peer_mentorship_pending;
        DROP POLICY learning_peer_participant ON andril_learning.peer_activities;
        CREATE POLICY learning_peer_participant ON andril_learning.peer_activities
            USING(andril_learning.peer_row_visible(members,kind)) WITH CHECK(andril_learning.peer_row_visible(members,kind));
        DROP FUNCTION andril_learning.peer_content_visible(jsonb,integer,jsonb);
        DROP FUNCTION andril_learning.mentorship_members(uuid,uuid,uuid,uuid,text,text);
        DROP FUNCTION andril_learning.mentorship_target_account(uuid,uuid,text,text);
        DROP POLICY learning_mentorship_eligibility_lock ON andril_identity.account_module_lifecycles;
        DROP POLICY learning_mentorship_eligibility ON andril_identity.account_module_lifecycles;
        ALTER TABLE andril_learning.peer_authority_scopes DROP CONSTRAINT peer_authority_scopes_purpose_check;
        ALTER TABLE andril_learning.peer_authority_scopes ADD CONSTRAINT peer_authority_scopes_purpose_check CHECK(purpose IN(1,2,7));
        """);
    protected override void BuildTargetModel(ModelBuilder modelBuilder)=>LearningPeerActivitiesModelV2.Configure(modelBuilder);
}
