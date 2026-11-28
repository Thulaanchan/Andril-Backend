using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.LearningPublications;

[DbContext(typeof(LearningPublicationsDbContext))]
[Migration("20261128190000_LearningPublications")]
public sealed class LearningPublications:Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"""
            CREATE TABLE andril_learning.publications(
                account_id uuid NOT NULL,profile_id uuid NOT NULL,module varchar(16) NOT NULL,kind integer NOT NULL,resource_id uuid NOT NULL,
                generation uuid NOT NULL,version bigint NOT NULL,published boolean NOT NULL,name_disclosure boolean NOT NULL,
                lifecycle_revision bigint NOT NULL,reviewed_name text,
                CONSTRAINT pk_learning_publications PRIMARY KEY(profile_id,kind,resource_id),
                CONSTRAINT ck_learning_publication CHECK({LearningPublicationsModelV1.Check}));
            REVOKE ALL ON andril_learning.publications FROM PUBLIC;
            ALTER TABLE andril_learning.publications ENABLE ROW LEVEL SECURITY;
            ALTER TABLE andril_learning.publications FORCE ROW LEVEL SECURITY;
            CREATE FUNCTION andril_learning.publication_owned(publication_kind integer,resource uuid,owner_profile uuid) RETURNS boolean
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
            BEGIN
                IF NOT andril_learning.scope_visible(owner_profile,'learning') THEN RETURN false; END IF;
                IF publication_kind=1 THEN RETURN resource=owner_profile; END IF;
                IF publication_kind=2 THEN RETURN EXISTS(SELECT FROM andril_learning.private_owner_activities a
                    WHERE a.id=resource AND a.kind=4 AND a.module='learning' AND a.members->0->>'ProfileId'=owner_profile::text
                        AND a.members->0->>'AccountId'=current_setting('andril.account',true)); END IF;
                RETURN false;
            END $$;
            REVOKE ALL ON FUNCTION andril_learning.publication_owned(integer,uuid,uuid) FROM PUBLIC;
            CREATE POLICY publication_owner ON andril_learning.publications
                USING(account_id::text=current_setting('andril.account',true) AND andril_learning.publication_owned(kind,resource_id,profile_id))
                WITH CHECK(account_id::text=current_setting('andril.account',true) AND andril_learning.publication_owned(kind,resource_id,profile_id));
            CREATE POLICY publication_source_read ON andril_learning.publications FOR SELECT TO CURRENT_USER USING(true);
            CREATE FUNCTION andril_learning.publication_owner_facts(owner_profile uuid)
            RETURNS TABLE(lifecycle_revision bigint,display_name text,standing_allows boolean)
            LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
                SELECT l.revision,p.display_name,NOT andril_groups.profile_is_restricted(p.id,'learning')
                FROM andril_identity.private_module_profiles p JOIN andril_identity.accounts a ON a.id=p.account_id
                    JOIN andril_identity.account_module_lifecycles l ON l.account_id=a.id AND l.module=p.module
                WHERE p.id=owner_profile AND p.module='learning' AND l.state='active'
                    AND a.id=nullif(current_setting('andril.account',true),'')::uuid AND a.supabase_user_id=nullif(current_setting('andril.subject',true),'')::uuid
                    AND andril_learning.scope_visible(p.id,'learning') FOR SHARE OF p,l;
            $$;
            REVOKE ALL ON FUNCTION andril_learning.publication_owner_facts(uuid) FROM PUBLIC;
            CREATE TABLE andril_learning.publication_scopes(
                id uuid PRIMARY KEY,backend_pid integer NOT NULL,transaction_id text NOT NULL,actor_account uuid NOT NULL,actor_subject uuid NOT NULL,
                actor_profile uuid NOT NULL,target_account uuid NOT NULL,target_profile uuid NOT NULL,kind integer NOT NULL CHECK(kind IN (1,2)),resource_id uuid NOT NULL);
            REVOKE ALL ON andril_learning.publication_scopes FROM PUBLIC;
            ALTER TABLE andril_learning.publication_scopes ENABLE ROW LEVEL SECURITY;
            ALTER TABLE andril_learning.publication_scopes FORCE ROW LEVEL SECURITY;
            CREATE POLICY publication_scope_definer ON andril_learning.publication_scopes TO CURRENT_USER USING(true) WITH CHECK(true);
            -- FOR SHARE consults UPDATE USING too. This private lease permits only
            -- the exact published row lock; WITH CHECK false never permits a rewrite.
            CREATE POLICY publication_source_lock ON andril_learning.publications FOR UPDATE TO CURRENT_USER
                USING(published AND (kind=2 OR name_disclosure) AND EXISTS(SELECT FROM andril_learning.publication_scopes s
                    WHERE s.backend_pid=pg_backend_pid() AND s.transaction_id=pg_current_xact_id()::text
                        AND s.actor_account=nullif(current_setting('andril.account',true),'')::uuid
                        AND s.actor_subject=nullif(current_setting('andril.subject',true),'')::uuid
                        AND s.target_account=publications.account_id AND s.target_profile=publications.profile_id
                        AND s.kind=publications.kind AND s.resource_id=publications.resource_id)) WITH CHECK(false);
            CREATE POLICY learning_publication_lifecycle ON andril_identity.account_module_lifecycles FOR SELECT TO CURRENT_USER USING(module='learning'
                AND EXISTS(SELECT FROM andril_learning.publication_scopes s JOIN andril_learning.publications p
                    ON p.profile_id=s.target_profile AND p.account_id=s.target_account AND p.kind=s.kind AND p.resource_id=s.resource_id
                    WHERE s.backend_pid=pg_backend_pid() AND s.transaction_id=pg_current_xact_id()::text AND s.target_account=account_module_lifecycles.account_id
                        AND s.actor_account=nullif(current_setting('andril.account',true),'')::uuid AND s.actor_subject=nullif(current_setting('andril.subject',true),'')::uuid
                        AND current_setting('andril.module',true)='learning' AND p.published AND (p.kind=2 OR p.name_disclosure)
                        AND EXISTS(SELECT FROM andril_identity.accounts a JOIN andril_identity.private_module_profiles ap ON ap.account_id=a.id
                            WHERE a.id=s.actor_account AND a.supabase_user_id=s.actor_subject AND ap.id=s.actor_profile AND ap.module='learning')
                        AND EXISTS(SELECT FROM andril_identity.private_module_profiles tp WHERE tp.id=s.target_profile AND tp.account_id=s.target_account AND tp.module='learning')));
            DO $$ DECLARE qualifier text; BEGIN
                SELECT pg_get_expr(polqual,polrelid) INTO STRICT qualifier FROM pg_policy
                    WHERE polrelid='andril_identity.account_module_lifecycles'::regclass AND polname='learning_publication_lifecycle';
                EXECUTE format('CREATE POLICY learning_publication_lifecycle_lock ON andril_identity.account_module_lifecycles FOR UPDATE TO %I USING (%s) WITH CHECK(false)',current_user,qualifier);
            END $$;
            CREATE POLICY learning_published_offer_source ON andril_learning.private_owner_activities FOR SELECT TO CURRENT_USER
                USING(kind=4 AND module='learning' AND EXISTS(SELECT FROM andril_learning.publication_scopes s
                    WHERE s.backend_pid=pg_backend_pid() AND s.transaction_id=pg_current_xact_id()::text AND s.kind=2 AND s.resource_id=private_owner_activities.id
                        AND s.actor_account=nullif(current_setting('andril.account',true),'')::uuid AND s.actor_subject=nullif(current_setting('andril.subject',true),'')::uuid
                        AND members->0->>'ProfileId'=s.target_profile::text AND members->0->>'AccountId'=s.target_account::text));
            CREATE FUNCTION andril_learning.published_target_account(actor uuid,target uuid,publication_kind integer,resource uuid,terms text,privacy text) RETURNS uuid
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
            DECLARE caller_account uuid;DECLARE caller_subject uuid;DECLARE target_account uuid;DECLARE scope_id uuid;DECLARE publication_epoch bigint;DECLARE eligible boolean;
            BEGIN
                IF publication_kind NOT IN (1,2) OR actor=target OR current_setting('transaction_isolation')<>'serializable'
                    OR nullif(terms,'') IS NULL OR nullif(privacy,'') IS NULL THEN RETURN NULL; END IF;
                IF NOT pg_try_advisory_xact_lock(hashtextextended('andril.groups.authority',0)) THEN RAISE EXCEPTION 'Learning publication unavailable' USING ERRCODE='AG503'; END IF;
                SELECT a.id,a.supabase_user_id INTO caller_account,caller_subject FROM andril_identity.accounts a
                    JOIN andril_identity.private_module_profiles p ON p.account_id=a.id WHERE p.id=actor AND p.module='learning'
                        AND a.id=nullif(current_setting('andril.account',true),'')::uuid AND a.supabase_user_id=nullif(current_setting('andril.subject',true),'')::uuid;
                IF NOT FOUND OR NOT andril_learning.scope_visible(actor,'learning') OR andril_groups.profile_is_restricted(actor,'learning')
                    OR andril_groups.profile_is_restricted(target,'learning') OR andril_groups.pair_is_blocked(actor,target,'learning') THEN RETURN NULL; END IF;
                SELECT p.account_id,p.lifecycle_revision INTO target_account,publication_epoch FROM andril_learning.publications p
                    WHERE p.profile_id=target AND p.kind=publication_kind AND p.resource_id=resource AND p.published AND (p.kind=2 OR p.name_disclosure);
                IF NOT FOUND OR target_account=caller_account THEN RETURN NULL; END IF;
                scope_id:=gen_random_uuid();INSERT INTO andril_learning.publication_scopes VALUES(scope_id,pg_backend_pid(),pg_current_xact_id()::text,caller_account,caller_subject,actor,target_account,target,publication_kind,resource);
                PERFORM p.profile_id FROM andril_learning.publications p WHERE p.profile_id=target AND p.kind=publication_kind AND p.resource_id=resource
                    AND p.published AND (p.kind=2 OR p.name_disclosure) FOR SHARE NOWAIT;
                IF NOT FOUND THEN DELETE FROM andril_learning.publication_scopes WHERE id=scope_id;RETURN NULL; END IF;
                PERFORM a.id FROM andril_identity.accounts a
                    JOIN andril_identity.account_starter_memberships m ON m.account_id=a.id AND m.module='learning'
                    JOIN andril_identity.account_demographics d ON d.account_id=a.id
                    JOIN andril_identity.account_module_lifecycles l ON l.account_id=a.id AND l.module=m.module
                    JOIN andril_identity.private_module_profiles p ON p.account_id=a.id AND p.module=m.module
                    WHERE a.id=target_account AND p.id=target AND l.state='active' AND l.revision=publication_epoch
                        AND d.date_of_birth<=timezone('UTC',statement_timestamp())::date-interval '16 years' FOR SHARE OF a,m,d,l,p NOWAIT;
                eligible:=FOUND;
                IF eligible THEN
                    PERFORM r.account_id FROM andril_identity.account_policy_records r WHERE r.account_id=target_account
                        AND ((r.action='terms_agreed' AND r.version=terms) OR (r.action='privacy_acknowledged' AND r.version=privacy)) FOR SHARE NOWAIT;
                    eligible:=EXISTS(SELECT FROM andril_identity.account_policy_records r WHERE r.account_id=target_account AND r.action='terms_agreed' AND r.version=terms)
                        AND EXISTS(SELECT FROM andril_identity.account_policy_records r WHERE r.account_id=target_account AND r.action='privacy_acknowledged' AND r.version=privacy);
                END IF;
                IF eligible AND publication_kind=1 THEN
                    PERFORM p.id FROM andril_identity.private_module_profiles p JOIN andril_identity.account_privacy_settings settings ON settings.account_id=p.account_id
                        JOIN andril_learning.publications published ON published.profile_id=p.id AND published.kind=1 AND published.resource_id=p.id
                        WHERE p.id=target AND p.display_name=published.reviewed_name
                            AND settings.profile_discoverability='public' AND NOT settings.ghost_mode_enabled FOR SHARE OF p,settings NOWAIT;
                    eligible:=FOUND;
                ELSIF eligible AND publication_kind=2 THEN
                    eligible:=EXISTS(SELECT FROM andril_learning.private_owner_activities a WHERE a.id=resource AND a.kind=4 AND a.module='learning'
                        AND a.members->0->>'ProfileId'=target::text AND a.members->0->>'AccountId'=target_account::text);
                END IF;
                DELETE FROM andril_learning.publication_scopes WHERE id=scope_id;
                IF eligible THEN RETURN target_account; END IF; RETURN NULL;
            EXCEPTION WHEN lock_not_available OR serialization_failure OR deadlock_detected THEN
                RAISE EXCEPTION 'Learning publication unavailable' USING ERRCODE='AG503';
            END $$;
            REVOKE ALL ON FUNCTION andril_learning.published_target_account(uuid,uuid,integer,uuid,text,text) FROM PUBLIC;
            CREATE FUNCTION andril_learning.read_disclosed_people(actor uuid,query text,page_limit integer,terms text,privacy text)
            RETURNS TABLE(profile_id uuid,display_name text) LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
            DECLARE candidate record;DECLARE emitted integer:=0;
            BEGIN
                IF page_limit<1 OR page_limit>100 OR nullif(btrim(query),'') IS NULL OR length(query)>500 OR query~'[[:cntrl:]]' THEN RETURN; END IF;
                FOR candidate IN SELECT p.profile_id,p.reviewed_name FROM andril_learning.publications p
                    WHERE p.kind=1 AND p.resource_id=p.profile_id AND p.published AND p.name_disclosure AND p.profile_id<>actor ORDER BY p.profile_id LOOP
                    IF andril_learning.published_target_account(actor,candidate.profile_id,1,candidate.profile_id,terms,privacy) IS NOT NULL
                        AND position(lower(query) in lower(candidate.reviewed_name))>0 THEN
                        profile_id:=candidate.profile_id;display_name:=candidate.reviewed_name;RETURN NEXT;emitted:=emitted+1;IF emitted=page_limit THEN RETURN; END IF;
                    END IF;
                END LOOP;
            END $$;
            REVOKE ALL ON FUNCTION andril_learning.read_disclosed_people(uuid,text,integer,text,text) FROM PUBLIC;
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.Sql("""
        DROP FUNCTION andril_learning.read_disclosed_people(uuid,text,integer,text,text);
        DROP FUNCTION andril_learning.published_target_account(uuid,uuid,integer,uuid,text,text);
        DROP POLICY learning_published_offer_source ON andril_learning.private_owner_activities;
        DROP POLICY learning_publication_lifecycle_lock ON andril_identity.account_module_lifecycles;
        DROP POLICY learning_publication_lifecycle ON andril_identity.account_module_lifecycles;
        DROP POLICY publication_source_lock ON andril_learning.publications;
        DROP TABLE andril_learning.publication_scopes;
        DROP TABLE andril_learning.publications;
        DROP FUNCTION andril_learning.publication_owner_facts(uuid);
        DROP FUNCTION andril_learning.publication_owned(integer,uuid,uuid);
        """);
    protected override void BuildTargetModel(ModelBuilder modelBuilder)=>LearningPublicationsModelV1.Configure(modelBuilder);
}
[DbContext(typeof(LearningPublicationsDbContext))]
public sealed class LearningPublicationsSnapshot:ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)=>LearningPublicationsModelV1.Configure(modelBuilder);
}
