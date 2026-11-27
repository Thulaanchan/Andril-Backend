using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Social
{
    /// <inheritdoc />
    public partial class SocialSafetyAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE andril_social.social_profile_blocks (
                    actor_profile_id uuid NOT NULL,
                    target_profile_id uuid NOT NULL,
                    module character varying(16) NOT NULL CHECK (module = 'social'),
                    PRIMARY KEY (actor_profile_id, target_profile_id),
                    CHECK (actor_profile_id <> target_profile_id),
                    FOREIGN KEY (actor_profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module),
                    FOREIGN KEY (target_profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                );
                CREATE INDEX ix_social_profile_blocks_target ON andril_social.social_profile_blocks(target_profile_id);

                CREATE TABLE andril_social.social_profile_restrictions (
                    profile_id uuid PRIMARY KEY,
                    module character varying(16) NOT NULL CHECK (module = 'social'),
                    FOREIGN KEY (profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                );

                CREATE TABLE andril_social.social_moderation_grants (
                    account_id uuid PRIMARY KEY REFERENCES andril_identity.accounts(id)
                );

                CREATE TABLE andril_social.social_safety_audit (
                    sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                    actor_account_id uuid NOT NULL CHECK (actor_account_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                    target_profile_id uuid NOT NULL,
                    module character varying(16) NOT NULL CHECK (module = 'social'),
                    action text NOT NULL CHECK (action IN ('block', 'unblock', 'restrict', 'clear')),
                    recorded_at_utc timestamp with time zone NOT NULL CHECK (isfinite(recorded_at_utc)),
                    FOREIGN KEY (target_profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                );

                DO $$
                DECLARE table_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY['social_profile_blocks', 'social_profile_restrictions', 'social_moderation_grants', 'social_safety_audit']
                    LOOP
                        EXECUTE format('REVOKE ALL ON andril_social.%I FROM PUBLIC', table_name);
                        EXECUTE format('ALTER TABLE andril_social.%I ENABLE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('ALTER TABLE andril_social.%I FORCE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('CREATE POLICY social_safety_authority ON andril_social.%I TO CURRENT_USER USING (true) WITH CHECK (true)', table_name);
                    END LOOP;
                END;
                $$;

                CREATE POLICY social_match_safety_select ON andril_social.social_matches
                    FOR SELECT TO CURRENT_USER USING (true);
                CREATE POLICY social_match_safety_revoke ON andril_social.social_matches
                    FOR UPDATE TO CURRENT_USER USING (state = 'active') WITH CHECK (state = 'revoked');

                CREATE FUNCTION andril_social.social_safety_lock() RETURNS void
                LANGUAGE sql VOLATILE SET search_path = pg_catalog AS $$
                    SELECT pg_advisory_xact_lock(hashtextextended('andril.social.evidence', 0));
                $$;

                CREATE FUNCTION andril_social.lock_social_evidence_write() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_social.social_safety_lock();
                    RETURN NULL;
                END;
                $$;
                CREATE TRIGGER social_moderation_grant_fence
                    BEFORE INSERT OR UPDATE OR DELETE ON andril_social.social_moderation_grants
                    FOR EACH STATEMENT EXECUTE FUNCTION andril_social.lock_social_evidence_write();
                CREATE TRIGGER social_matching_preferences_fence
                    BEFORE INSERT OR UPDATE OR DELETE ON andril_social.social_matching_preferences
                    FOR EACH STATEMENT EXECUTE FUNCTION andril_social.lock_social_evidence_write();
                CREATE TRIGGER social_current_questionnaire_fence
                    BEFORE INSERT OR UPDATE OR DELETE ON andril_social.social_questionnaire_current_responses
                    FOR EACH STATEMENT EXECUTE FUNCTION andril_social.lock_social_evidence_write();

                CREATE FUNCTION andril_social.lock_social_identity_write() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_social.social_safety_lock();
                    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER social_profile_evidence_update_fence
                    BEFORE UPDATE ON andril_identity.private_module_profiles
                    FOR EACH ROW WHEN (OLD.module = 'social' OR NEW.module = 'social')
                    EXECUTE FUNCTION andril_social.lock_social_identity_write();
                CREATE TRIGGER social_starter_evidence_update_fence
                    BEFORE UPDATE ON andril_identity.account_starter_memberships
                    FOR EACH ROW WHEN (OLD.module = 'social' OR NEW.module = 'social')
                    EXECUTE FUNCTION andril_social.lock_social_identity_write();
                CREATE TRIGGER social_lifecycle_evidence_update_fence
                    BEFORE UPDATE ON andril_identity.account_module_lifecycles
                    FOR EACH ROW WHEN (OLD.module = 'social' OR NEW.module = 'social')
                    EXECUTE FUNCTION andril_social.lock_social_identity_write();

                CREATE FUNCTION andril_social.revoke_matches_on_deactivation() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    UPDATE andril_social.social_matches matched SET state = 'revoked', revoked_at_utc = greatest(clock_timestamp(), matched.created_at_utc)
                        FROM andril_identity.private_module_profiles profile
                        WHERE profile.account_id = NEW.account_id AND profile.module = 'social'
                            AND profile.id IN (matched.first_profile_id, matched.second_profile_id) AND matched.state = 'active';
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER social_lifecycle_revoke_matches
                    AFTER UPDATE OF state ON andril_identity.account_module_lifecycles
                    FOR EACH ROW WHEN (OLD.module = 'social' AND OLD.state = 'active' AND NEW.state IS DISTINCT FROM OLD.state)
                    EXECUTE FUNCTION andril_social.revoke_matches_on_deactivation();

                CREATE FUNCTION andril_social.social_safety_decision(caller uuid, target uuid)
                RETURNS TABLE(decision text, revision bigint)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT CASE
                        WHEN NOT andril_social.questionnaire_owner_visible(caller) OR target IS NULL OR NOT EXISTS (
                            SELECT 1 FROM andril_identity.private_module_profiles profile
                            JOIN andril_identity.account_starter_memberships membership
                                ON membership.account_id = profile.account_id AND membership.module = profile.module
                            JOIN andril_identity.account_module_lifecycles lifecycle
                                ON lifecycle.account_id = profile.account_id AND lifecycle.module = profile.module
                            WHERE profile.id = target AND profile.module = 'social'
                                AND membership.social_opt_in_at_utc IS NOT NULL AND lifecycle.state = 'active') THEN 'unavailable'
                        WHEN EXISTS (SELECT 1 FROM andril_social.social_profile_restrictions restriction WHERE restriction.profile_id IN (caller, target)) THEN 'restricted'
                        WHEN EXISTS (SELECT 1 FROM andril_social.social_profile_blocks pair_block WHERE
                            (pair_block.actor_profile_id, pair_block.target_profile_id) IN ((caller, target), (target, caller))) THEN 'blocked'
                        ELSE 'allowed'
                        END,
                        coalesce((SELECT max(audit.sequence) FROM andril_social.social_safety_audit audit), 0);
                $$;

                CREATE FUNCTION andril_social.set_social_block(target uuid, blocked boolean) RETURNS boolean
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE actor uuid;
                DECLARE actor_account uuid;
                DECLARE changed integer;
                BEGIN
                    PERFORM andril_social.social_safety_lock();
                    SELECT profile.id, account.id INTO actor, actor_account
                      FROM andril_identity.accounts account
                      JOIN andril_identity.private_module_profiles profile ON profile.account_id = account.id AND profile.module = 'social'
                      WHERE account.supabase_user_id::text = current_setting('andril.subject', true)
                        AND current_setting('andril.module', true) = 'social';
                    IF actor IS NULL OR target IS NULL OR actor = target OR blocked IS NULL OR NOT EXISTS (
                        SELECT 1 FROM andril_identity.private_module_profiles profile WHERE profile.id = target AND profile.module = 'social') THEN
                        RAISE EXCEPTION 'Social safety operation denied' USING ERRCODE = '42501';
                    END IF;

                    IF blocked THEN
                        INSERT INTO andril_social.social_profile_blocks(actor_profile_id, target_profile_id, module)
                            VALUES (actor, target, 'social') ON CONFLICT DO NOTHING;
                    ELSE
                        DELETE FROM andril_social.social_profile_blocks pair_block
                            WHERE pair_block.actor_profile_id = actor AND pair_block.target_profile_id = target;
                    END IF;
                    GET DIAGNOSTICS changed = ROW_COUNT;
                    IF changed = 0 THEN RETURN false; END IF;

                    IF blocked THEN
                        UPDATE andril_social.social_matches matched SET state = 'revoked', revoked_at_utc = greatest(clock_timestamp(), matched.created_at_utc)
                            WHERE matched.first_profile_id = LEAST(actor, target) AND matched.second_profile_id = GREATEST(actor, target) AND matched.state = 'active';
                    END IF;
                    INSERT INTO andril_social.social_safety_audit(actor_account_id, target_profile_id, module, action, recorded_at_utc)
                        VALUES (actor_account, target, 'social', CASE WHEN blocked THEN 'block' ELSE 'unblock' END, clock_timestamp());
                    RETURN true;
                END;
                $$;

                CREATE FUNCTION andril_social.set_social_restriction(target uuid, restricted boolean) RETURNS boolean
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE actor_account uuid;
                DECLARE changed integer;
                BEGIN
                    PERFORM andril_social.social_safety_lock();
                    SELECT account.id INTO actor_account FROM andril_identity.accounts account
                        JOIN andril_social.social_moderation_grants permission ON permission.account_id = account.id
                        WHERE account.supabase_user_id::text = current_setting('andril.subject', true)
                            AND current_setting('andril.module', true) = 'social';
                    IF actor_account IS NULL OR target IS NULL OR restricted IS NULL OR NOT EXISTS (
                        SELECT 1 FROM andril_identity.private_module_profiles profile WHERE profile.id = target AND profile.module = 'social') THEN
                        RAISE EXCEPTION 'Social safety operation denied' USING ERRCODE = '42501';
                    END IF;

                    IF restricted THEN
                        INSERT INTO andril_social.social_profile_restrictions(profile_id, module) VALUES (target, 'social') ON CONFLICT DO NOTHING;
                    ELSE
                        DELETE FROM andril_social.social_profile_restrictions restriction WHERE restriction.profile_id = target;
                    END IF;
                    GET DIAGNOSTICS changed = ROW_COUNT;
                    IF changed = 0 THEN RETURN false; END IF;

                    IF restricted THEN
                        UPDATE andril_social.social_matches matched SET state = 'revoked', revoked_at_utc = greatest(clock_timestamp(), matched.created_at_utc)
                            WHERE target IN (matched.first_profile_id, matched.second_profile_id) AND matched.state = 'active';
                    END IF;
                    INSERT INTO andril_social.social_safety_audit(actor_account_id, target_profile_id, module, action, recorded_at_utc)
                        VALUES (actor_account, target, 'social', CASE WHEN restricted THEN 'restrict' ELSE 'clear' END, clock_timestamp());
                    RETURN true;
                END;
                $$;

                REVOKE ALL ON FUNCTION andril_social.social_safety_lock(), andril_social.lock_social_evidence_write(),
                    andril_social.lock_social_identity_write(), andril_social.revoke_matches_on_deactivation(), andril_social.social_safety_decision(uuid, uuid),
                    andril_social.set_social_block(uuid, boolean), andril_social.set_social_restriction(uuid, boolean) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION andril_social.set_social_restriction(uuid, boolean);
                DROP FUNCTION andril_social.set_social_block(uuid, boolean);
                DROP FUNCTION andril_social.social_safety_decision(uuid, uuid);
                DROP TRIGGER social_lifecycle_revoke_matches ON andril_identity.account_module_lifecycles;
                DROP TRIGGER social_lifecycle_evidence_update_fence ON andril_identity.account_module_lifecycles;
                DROP TRIGGER social_starter_evidence_update_fence ON andril_identity.account_starter_memberships;
                DROP TRIGGER social_profile_evidence_update_fence ON andril_identity.private_module_profiles;
                DROP FUNCTION andril_social.revoke_matches_on_deactivation();
                DROP FUNCTION andril_social.lock_social_identity_write();
                DROP TRIGGER social_current_questionnaire_fence ON andril_social.social_questionnaire_current_responses;
                DROP TRIGGER social_matching_preferences_fence ON andril_social.social_matching_preferences;
                DROP TRIGGER social_moderation_grant_fence ON andril_social.social_moderation_grants;
                DROP FUNCTION andril_social.lock_social_evidence_write();
                DROP FUNCTION andril_social.social_safety_lock();
                DROP POLICY social_match_safety_revoke ON andril_social.social_matches;
                DROP POLICY social_match_safety_select ON andril_social.social_matches;
                DROP TABLE andril_social.social_safety_audit;
                DROP TABLE andril_social.social_moderation_grants;
                DROP TABLE andril_social.social_profile_restrictions;
                DROP TABLE andril_social.social_profile_blocks;
                """);
        }
    }
}
