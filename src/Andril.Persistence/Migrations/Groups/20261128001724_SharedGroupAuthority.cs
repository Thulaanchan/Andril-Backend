using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Groups
{
    /// <inheritdoc />
    public partial class SharedGroupAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE andril_groups.module_blocks (
                    actor_profile_id uuid NOT NULL, target_profile_id uuid NOT NULL, module text NOT NULL CHECK (module IN ('learning', 'hobbies')),
                    PRIMARY KEY (actor_profile_id, target_profile_id, module), CHECK (actor_profile_id <> target_profile_id),
                    FOREIGN KEY (actor_profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module),
                    FOREIGN KEY (target_profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                );
                CREATE INDEX ix_group_blocks_target ON andril_groups.module_blocks(target_profile_id, module);
                CREATE TABLE andril_groups.module_restrictions (
                    profile_id uuid NOT NULL, module text NOT NULL CHECK (module IN ('learning', 'hobbies')), PRIMARY KEY (profile_id, module),
                    FOREIGN KEY (profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                );
                CREATE TABLE andril_groups.moderator_grants (
                    account_id uuid NOT NULL REFERENCES andril_identity.accounts(id), module text NOT NULL CHECK (module IN ('learning', 'hobbies')),
                    PRIMARY KEY (account_id, module)
                );
                CREATE TABLE andril_groups.authority_audit (
                    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, actor_account_id uuid NOT NULL,
                    target_profile_id uuid NOT NULL, module text NOT NULL CHECK (module IN ('learning', 'hobbies')),
                    action text NOT NULL CHECK (action IN ('block', 'unblock', 'restrict', 'clear', 'grant_host', 'revoke_host')),
                    recorded_at_utc timestamptz NOT NULL CHECK (isfinite(recorded_at_utc)),
                    FOREIGN KEY (target_profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                );
                DO $$
                DECLARE table_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY['module_blocks', 'module_restrictions', 'moderator_grants', 'authority_audit'] LOOP
                        EXECUTE format('REVOKE ALL ON andril_groups.%I FROM PUBLIC', table_name);
                        EXECUTE format('ALTER TABLE andril_groups.%I ENABLE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('ALTER TABLE andril_groups.%I FORCE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('CREATE POLICY groups_authority ON andril_groups.%I TO CURRENT_USER USING (true) WITH CHECK (true)', table_name);
                    END LOOP;
                END;
                $$;

                CREATE FUNCTION andril_groups.lock_authority_write() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_groups.lock_group_operations();
                    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER groups_identity_lifecycle BEFORE UPDATE OR DELETE ON andril_identity.account_module_lifecycles
                    FOR EACH ROW EXECUTE FUNCTION andril_groups.lock_authority_write();
                CREATE TRIGGER groups_identity_demographics BEFORE UPDATE OR DELETE ON andril_identity.account_demographics
                    FOR EACH ROW EXECUTE FUNCTION andril_groups.lock_authority_write();
                CREATE TRIGGER groups_identity_starter BEFORE UPDATE OR DELETE ON andril_identity.account_starter_memberships
                    FOR EACH ROW EXECUTE FUNCTION andril_groups.lock_authority_write();
                CREATE TRIGGER groups_moderator_fence BEFORE INSERT OR UPDATE OR DELETE ON andril_groups.moderator_grants
                    FOR EACH ROW EXECUTE FUNCTION andril_groups.lock_authority_write();
                CREATE TRIGGER groups_host_fence BEFORE INSERT OR UPDATE OR DELETE ON andril_groups.classroom_host_grants
                    FOR EACH ROW EXECUTE FUNCTION andril_groups.lock_authority_write();

                CREATE FUNCTION andril_groups.social_source_visible(relation_id regclass) RETURNS boolean
                LANGUAGE sql STABLE SET search_path = pg_catalog AS $$
                    SELECT relation_id IS NOT NULL AND (NOT row_security_active(relation_id) OR (
                        EXISTS (SELECT FROM pg_policy policy WHERE policy.polrelid = relation_id
                            AND policy.polname = 'social_safety_authority' AND policy.polpermissive AND policy.polcmd IN ('*','r')
                            AND pg_get_expr(policy.polqual, policy.polrelid) = 'true'
                            AND EXISTS (SELECT FROM unnest(policy.polroles) role_id WHERE role_id = 0 OR pg_has_role(current_user, role_id, 'USAGE')))
                        AND NOT EXISTS (SELECT FROM pg_policy policy WHERE policy.polrelid = relation_id AND NOT policy.polpermissive
                            AND policy.polcmd IN ('*','r')
                            AND EXISTS (SELECT FROM unnest(policy.polroles) role_id WHERE role_id = 0 OR pg_has_role(current_user, role_id, 'USAGE')))));
                $$;

                CREATE FUNCTION andril_groups.profile_is_restricted(candidate uuid, source_module text) RETURNS boolean
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    IF source_module = 'social' THEN
                        IF NOT andril_groups.social_source_visible(to_regclass('andril_social.social_profile_restrictions')) THEN
                            RAISE EXCEPTION 'Social restriction authority unavailable' USING ERRCODE = 'AG503';
                        END IF;
                        PERFORM andril_social.social_safety_lock();
                        RETURN EXISTS (SELECT FROM andril_social.social_profile_restrictions restricted WHERE restricted.profile_id = candidate);
                    END IF;
                    RETURN EXISTS (SELECT FROM andril_groups.module_restrictions restricted WHERE restricted.profile_id = candidate AND restricted.module = source_module);
                EXCEPTION WHEN insufficient_privilege OR undefined_table OR undefined_function THEN
                    RAISE EXCEPTION 'Group restriction authority unavailable' USING ERRCODE = 'AG503';
                END;
                $$;

                CREATE OR REPLACE FUNCTION andril_groups.actor_standing(actor uuid, source_module text) RETURNS boolean
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_groups.lock_group_operations();
                    RETURN andril_groups.owns_current_profile(actor, source_module) AND NOT andril_groups.profile_is_restricted(actor, source_module);
                END;
                $$;

                CREATE FUNCTION andril_groups.pair_is_blocked(first_profile uuid, second_profile uuid, source_module text) RETURNS boolean
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    IF first_profile = second_profile THEN RETURN false; END IF;
                    IF source_module = 'social' THEN
                        IF NOT andril_groups.social_source_visible(to_regclass('andril_social.social_profile_blocks')) THEN
                            RAISE EXCEPTION 'Social blocking authority unavailable' USING ERRCODE = 'AG503';
                        END IF;
                        PERFORM andril_social.social_safety_lock();
                        RETURN EXISTS (SELECT FROM andril_social.social_profile_blocks b
                            WHERE (b.actor_profile_id, b.target_profile_id) IN ((first_profile, second_profile), (second_profile, first_profile)));
                    END IF;
                    RETURN EXISTS (SELECT FROM andril_groups.module_blocks b WHERE b.module = source_module
                        AND (b.actor_profile_id, b.target_profile_id) IN ((first_profile, second_profile), (second_profile, first_profile)));
                EXCEPTION WHEN insufficient_privilege OR undefined_table OR undefined_function THEN
                    RAISE EXCEPTION 'Group blocking authority unavailable' USING ERRCODE = 'AG503';
                END;
                $$;

                CREATE OR REPLACE FUNCTION andril_groups.can_interact_with_hosts(candidate uuid, source_module text, target_id uuid) RETURNS boolean
                LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT andril_groups.profile_is_active(candidate, source_module) AND NOT andril_groups.profile_is_restricted(candidate, source_module)
                        AND EXISTS (SELECT FROM andril_groups.groups g WHERE g.id = target_id AND g.module = source_module
                            AND andril_groups.profile_is_active(g.creator_profile_id, source_module)
                            AND NOT andril_groups.profile_is_restricted(g.creator_profile_id, source_module)
                            AND NOT andril_groups.pair_is_blocked(candidate, g.creator_profile_id, source_module))
                        AND NOT EXISTS (SELECT FROM andril_groups.capabilities c
                            JOIN andril_groups.memberships m ON m.group_id = c.group_id AND m.module = c.module AND m.profile_id = c.profile_id
                            WHERE c.group_id = target_id AND c.module = source_module AND c.capability = 1 AND m.departed_at_utc IS NULL
                                AND andril_groups.pair_is_blocked(candidate, c.profile_id, source_module));
                $$;

                CREATE FUNCTION andril_groups.write_authority(source_module text, target uuid, operation text, enabled boolean) RETURNS boolean
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE caller_account uuid;
                DECLARE caller_profile uuid;
                DECLARE target_account uuid;
                DECLARE changed integer;
                DECLARE audit_action text;
                BEGIN
                    PERFORM andril_groups.lock_group_operations();
                    IF source_module NOT IN ('learning', 'hobbies') OR enabled IS NULL OR target IS NULL THEN
                        RAISE EXCEPTION 'Group authority denied' USING ERRCODE = 'AG403';
                    END IF;
                    SELECT account.id INTO caller_account FROM andril_identity.accounts account
                        WHERE account.supabase_user_id::text = current_setting('andril.subject', true);
                    SELECT p.account_id INTO target_account FROM andril_identity.private_module_profiles p WHERE p.id = target AND p.module = source_module;
                    IF caller_account IS NULL OR target_account IS NULL THEN RAISE EXCEPTION 'Group authority denied' USING ERRCODE = 'AG403'; END IF;

                    CASE operation
                    WHEN 'block' THEN
                        SELECT p.id INTO caller_profile FROM andril_identity.private_module_profiles p WHERE p.account_id = caller_account AND p.module = source_module;
                        IF caller_profile IS NULL OR caller_profile = target THEN RAISE EXCEPTION 'Group authority denied' USING ERRCODE = 'AG403'; END IF;
                        IF enabled THEN INSERT INTO andril_groups.module_blocks VALUES (caller_profile, target, source_module) ON CONFLICT DO NOTHING;
                        ELSE DELETE FROM andril_groups.module_blocks b WHERE b.actor_profile_id = caller_profile AND b.target_profile_id = target AND b.module = source_module;
                        END IF;
                        GET DIAGNOSTICS changed = ROW_COUNT;
                        audit_action := CASE WHEN enabled THEN 'block' ELSE 'unblock' END;
                    WHEN 'restrict', 'educator' THEN
                        IF NOT EXISTS (SELECT FROM andril_groups.moderator_grants g WHERE g.account_id = caller_account AND g.module = source_module)
                            OR caller_account = target_account OR (operation = 'educator' AND source_module <> 'learning') THEN
                            RAISE EXCEPTION 'Group authority denied' USING ERRCODE = 'AG403';
                        END IF;
                        IF operation = 'educator' THEN
                            IF enabled THEN INSERT INTO andril_groups.classroom_host_grants VALUES (target, source_module) ON CONFLICT DO NOTHING;
                            ELSE DELETE FROM andril_groups.classroom_host_grants g WHERE g.profile_id = target;
                            END IF;
                            GET DIAGNOSTICS changed = ROW_COUNT;
                            audit_action := CASE WHEN enabled THEN 'grant_host' ELSE 'revoke_host' END;
                        ELSE
                            IF enabled THEN INSERT INTO andril_groups.module_restrictions VALUES (target, source_module) ON CONFLICT DO NOTHING;
                            ELSE DELETE FROM andril_groups.module_restrictions r WHERE r.profile_id = target AND r.module = source_module;
                            END IF;
                            GET DIAGNOSTICS changed = ROW_COUNT;
                            audit_action := CASE WHEN enabled THEN 'restrict' ELSE 'clear' END;
                        END IF;
                    ELSE RAISE EXCEPTION 'Group authority denied' USING ERRCODE = 'AG403';
                    END CASE;
                    IF changed = 0 THEN RETURN false; END IF;
                    INSERT INTO andril_groups.authority_audit(actor_account_id, target_profile_id, module, action, recorded_at_utc)
                        VALUES (caller_account, target, source_module, audit_action, clock_timestamp());
                    RETURN true;
                END;
                $$;

                REVOKE ALL ON FUNCTION andril_groups.lock_authority_write(), andril_groups.social_source_visible(regclass), andril_groups.profile_is_restricted(uuid, text),
                    andril_groups.pair_is_blocked(uuid, uuid, text), andril_groups.write_authority(text, uuid, text, boolean) FROM PUBLIC;

                CREATE FUNCTION andril_groups.read_profile_evidence(actor uuid, source_module text, candidate uuid)
                RETURNS TABLE(age integer, observed_at timestamptz)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    IF andril_groups.actor_standing(actor, source_module) IS NOT TRUE THEN RETURN; END IF;
                    IF candidate <> actor AND NOT EXISTS (SELECT FROM andril_groups.join_requests r
                        WHERE r.profile_id = candidate AND r.module = source_module AND r.state = 'pending'
                            AND andril_groups.has_capability(actor, source_module, r.group_id, 1)
                            AND andril_groups.can_interact_with_hosts(candidate, source_module, r.group_id)) THEN RETURN; END IF;
                    IF NOT andril_groups.profile_is_active(candidate, source_module) OR andril_groups.profile_is_restricted(candidate, source_module) THEN RETURN; END IF;
                    RETURN QUERY SELECT extract(year FROM pg_catalog.age(timezone('UTC', clock_timestamp())::date, d.date_of_birth))::integer, clock_timestamp()
                        FROM andril_identity.private_module_profiles p JOIN andril_identity.account_demographics d ON d.account_id = p.account_id
                        WHERE p.id = candidate AND p.module = source_module;
                END;
                $$;

                CREATE FUNCTION andril_groups.read_participation_evidence(actor uuid, source_module text, target_id uuid)
                RETURNS TABLE(is_open boolean, is_owner boolean, is_member boolean, capabilities integer, member_count integer, observed_at timestamptz)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE target andril_groups.groups%ROWTYPE;
                DECLARE member_now boolean;
                DECLARE pending_now boolean;
                DECLARE permissions integer;
                BEGIN
                    IF andril_groups.actor_standing(actor, source_module) IS NOT TRUE THEN RETURN; END IF;
                    SELECT g.* INTO target FROM andril_groups.groups g WHERE g.id = target_id AND g.module = source_module AND g.is_active;
                    IF NOT FOUND THEN RETURN; END IF;
                    SELECT EXISTS(SELECT FROM andril_groups.memberships m WHERE m.group_id = target_id AND m.module = source_module
                        AND m.profile_id = actor AND m.departed_at_utc IS NULL) INTO member_now;
                    SELECT EXISTS(SELECT FROM andril_groups.join_requests r WHERE r.group_id = target_id AND r.module = source_module
                        AND r.profile_id = actor AND r.state = 'pending') INTO pending_now;
                    IF NOT member_now AND NOT pending_now AND NOT (target.visibility = 'public' AND target.is_discoverable
                        AND andril_groups.can_interact_with_hosts(actor, source_module, target_id)) THEN RETURN; END IF;
                    permissions := 0;
                    IF andril_groups.has_capability(actor, source_module, target_id, 1) THEN permissions := permissions + 1; END IF;
                    IF andril_groups.has_capability(actor, source_module, target_id, 2) THEN permissions := permissions + 2; END IF;
                    IF andril_groups.has_capability(actor, source_module, target_id, 4) THEN permissions := permissions + 4; END IF;
                    RETURN QUERY SELECT target.is_open, target.creator_profile_id = actor, member_now, permissions,
                        CASE WHEN (permissions & 1) = 1 THEN (SELECT count(*)::integer FROM andril_groups.memberships m WHERE m.group_id = target_id
                            AND m.module = source_module AND m.departed_at_utc IS NULL) ELSE NULL::integer END, clock_timestamp();
                END;
                $$;

                CREATE FUNCTION andril_groups.read_club_capacity(actor uuid, target_id uuid) RETURNS integer
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE creator uuid;
                BEGIN
                    IF andril_groups.actor_standing(actor, 'hobbies') IS NOT TRUE OR NOT andril_groups.has_capability(actor, 'hobbies', target_id, 1) THEN
                        RAISE EXCEPTION 'Admission authority denied' USING ERRCODE = 'AG403';
                    END IF;
                    SELECT g.creator_profile_id INTO creator FROM andril_groups.groups g WHERE g.id = target_id AND g.module = 'hobbies' AND g.is_active;
                    RETURN andril_groups.club_limit(creator);
                END;
                $$;

                REVOKE ALL ON FUNCTION andril_groups.read_profile_evidence(uuid, text, uuid),
                    andril_groups.read_participation_evidence(uuid, text, uuid), andril_groups.read_club_capacity(uuid, uuid) FROM PUBLIC;

                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION andril_groups.read_club_capacity(uuid, uuid);
                DROP FUNCTION andril_groups.read_participation_evidence(uuid, text, uuid);
                DROP FUNCTION andril_groups.read_profile_evidence(uuid, text, uuid);
                DROP FUNCTION andril_groups.write_authority(text, uuid, text, boolean);
                CREATE OR REPLACE FUNCTION andril_groups.can_interact_with_hosts(candidate uuid, source_module text, target_id uuid) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT andril_groups.profile_is_active(candidate, source_module)
                        AND EXISTS (SELECT FROM andril_groups.groups g WHERE g.id = target_id AND g.module = source_module
                            AND andril_groups.profile_is_active(g.creator_profile_id, source_module));
                $$;
                CREATE OR REPLACE FUNCTION andril_groups.actor_standing(actor uuid, source_module text) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT andril_groups.owns_current_profile(actor, source_module);
                $$;
                DROP FUNCTION andril_groups.pair_is_blocked(uuid, uuid, text);
                DROP FUNCTION andril_groups.profile_is_restricted(uuid, text);
                DROP FUNCTION andril_groups.social_source_visible(regclass);
                DROP TRIGGER groups_identity_lifecycle ON andril_identity.account_module_lifecycles;
                DROP TRIGGER groups_identity_demographics ON andril_identity.account_demographics;
                DROP TRIGGER groups_identity_starter ON andril_identity.account_starter_memberships;
                DROP TRIGGER groups_moderator_fence ON andril_groups.moderator_grants;
                DROP TRIGGER groups_host_fence ON andril_groups.classroom_host_grants;
                DROP FUNCTION andril_groups.lock_authority_write();
                DROP TABLE andril_groups.authority_audit;
                DROP TABLE andril_groups.moderator_grants;
                DROP TABLE andril_groups.module_restrictions;
                DROP TABLE andril_groups.module_blocks;

                """);
        }
    }
}
