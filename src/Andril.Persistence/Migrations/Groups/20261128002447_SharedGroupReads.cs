using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Groups
{
    /// <inheritdoc />
    public partial class SharedGroupReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION andril_groups.safety_source_visible(relation_id regclass, expected_policy text) RETURNS boolean
                LANGUAGE sql STABLE SET search_path = pg_catalog AS $$
                    SELECT relation_id IS NOT NULL AND (NOT row_security_active(relation_id) OR (
                        EXISTS (SELECT FROM pg_policy policy WHERE policy.polrelid = relation_id
                            AND policy.polname = expected_policy AND policy.polpermissive AND policy.polcmd IN ('*','r')
                            AND pg_get_expr(policy.polqual, policy.polrelid) = 'true'
                            AND EXISTS (SELECT FROM unnest(policy.polroles) role_id WHERE CASE WHEN role_id = 0 THEN true ELSE pg_has_role(current_user, role_id, 'USAGE') END))
                        AND NOT EXISTS (SELECT FROM pg_policy policy WHERE policy.polrelid = relation_id AND NOT policy.polpermissive
                            AND policy.polcmd IN ('*','r')
                            AND EXISTS (SELECT FROM unnest(policy.polroles) role_id WHERE CASE WHEN role_id = 0 THEN true ELSE pg_has_role(current_user, role_id, 'USAGE') END))));
                $$;

                REVOKE ALL ON FUNCTION andril_groups.safety_source_visible(regclass, text) FROM PUBLIC;

                CREATE OR REPLACE FUNCTION andril_groups.profile_is_restricted(candidate uuid, source_module text) RETURNS boolean
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    IF source_module = 'social' THEN
                        IF NOT andril_groups.safety_source_visible(to_regclass('andril_social.social_profile_restrictions'), 'social_safety_authority') THEN
                            RAISE EXCEPTION 'Social restriction authority unavailable' USING ERRCODE = 'AG503';
                        END IF;
                        PERFORM andril_social.social_safety_lock();
                        RETURN EXISTS (SELECT FROM andril_social.social_profile_restrictions restricted WHERE restricted.profile_id = candidate);
                    END IF;
                    IF NOT andril_groups.safety_source_visible(to_regclass('andril_groups.module_restrictions'), 'groups_authority') THEN
                        RAISE EXCEPTION 'Group restriction authority unavailable' USING ERRCODE = 'AG503';
                    END IF;
                    RETURN EXISTS (SELECT FROM andril_groups.module_restrictions restricted WHERE restricted.profile_id = candidate AND restricted.module = source_module);
                EXCEPTION WHEN insufficient_privilege OR undefined_table OR undefined_function THEN
                    RAISE EXCEPTION 'Group restriction authority unavailable' USING ERRCODE = 'AG503';
                END;
                $$;

                CREATE OR REPLACE FUNCTION andril_groups.pair_is_blocked(first_profile uuid, second_profile uuid, source_module text) RETURNS boolean
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    IF first_profile = second_profile THEN RETURN false; END IF;
                    IF source_module = 'social' THEN
                        IF NOT andril_groups.safety_source_visible(to_regclass('andril_social.social_profile_blocks'), 'social_safety_authority') THEN
                            RAISE EXCEPTION 'Social blocking authority unavailable' USING ERRCODE = 'AG503';
                        END IF;
                        PERFORM andril_social.social_safety_lock();
                        RETURN EXISTS (SELECT FROM andril_social.social_profile_blocks b
                            WHERE (b.actor_profile_id, b.target_profile_id) IN ((first_profile, second_profile), (second_profile, first_profile)));
                    END IF;
                    IF NOT andril_groups.safety_source_visible(to_regclass('andril_groups.module_blocks'), 'groups_authority') THEN
                        RAISE EXCEPTION 'Group blocking authority unavailable' USING ERRCODE = 'AG503';
                    END IF;
                    RETURN EXISTS (SELECT FROM andril_groups.module_blocks b WHERE b.module = source_module
                        AND (b.actor_profile_id, b.target_profile_id) IN ((first_profile, second_profile), (second_profile, first_profile)));
                EXCEPTION WHEN insufficient_privilege OR undefined_table OR undefined_function THEN
                    RAISE EXCEPTION 'Group blocking authority unavailable' USING ERRCODE = 'AG503';
                END;
                $$;

                DROP FUNCTION andril_groups.social_source_visible(regclass);

                CREATE FUNCTION andril_groups.can_read_group(actor uuid, source_module text, target_id uuid, discovery boolean)
                RETURNS boolean LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT EXISTS (SELECT FROM andril_groups.groups g
                        WHERE g.id = target_id AND g.module = source_module AND g.is_active
                            AND (NOT discovery OR (g.is_open AND g.is_discoverable))
                            AND ((g.visibility = 'public' AND g.is_discoverable)
                                OR EXISTS (SELECT FROM andril_groups.memberships m WHERE m.group_id = target_id AND m.module = source_module
                                    AND m.profile_id = actor AND m.departed_at_utc IS NULL)
                                OR (NOT discovery AND EXISTS (SELECT FROM andril_groups.join_requests r WHERE r.group_id = target_id
                                    AND r.module = source_module AND r.profile_id = actor AND r.state = 'pending')))
                            AND andril_groups.can_interact_with_hosts(actor, source_module, target_id));
                $$;

                CREATE OR REPLACE FUNCTION andril_groups.read_group_details(actor uuid, source_module text, target_id uuid)
                RETURNS TABLE(id uuid, name text, description text, is_discoverable boolean, visibility text, revision bigint)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    IF andril_groups.actor_standing(actor, source_module) IS NOT TRUE THEN RAISE EXCEPTION 'Group access denied' USING ERRCODE = 'AG403'; END IF;
                    RETURN QUERY SELECT g.id, g.name::text, g.description::text, g.is_discoverable, g.visibility::text, g.revision
                        FROM andril_groups.groups g WHERE g.id = target_id AND g.module = source_module
                            AND andril_groups.can_read_group(actor, source_module, target_id, false);
                END;
                $$;

                CREATE FUNCTION andril_groups.read_discovery(actor uuid, source_module text, requested_kind integer, search_term text, maximum integer)
                RETURNS TABLE(id uuid, name text, visibility text, revision bigint, observed_at timestamptz)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    IF maximum IS NULL OR maximum < 1 OR maximum > 100 OR search_term IS NULL OR length(search_term) > 200
                        OR andril_groups.actor_standing(actor, source_module) IS NOT TRUE THEN
                        RAISE EXCEPTION 'Group discovery denied' USING ERRCODE = 'AG403';
                    END IF;
                    RETURN QUERY SELECT g.id, g.name::text, g.visibility::text, g.revision, clock_timestamp()
                        FROM andril_groups.groups g WHERE g.module = source_module AND g.kind = requested_kind
                            AND strpos(lower(g.name), lower(search_term)) > 0
                            AND andril_groups.can_read_group(actor, source_module, g.id, true)
                        ORDER BY g.id LIMIT maximum;
                END;
                $$;

                CREATE FUNCTION andril_groups.read_own_participation(actor uuid, source_module text, target_id uuid)
                RETURNS TABLE(is_member boolean, requested_at timestamptz, revision bigint, observed_at timestamptz)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    IF andril_groups.actor_standing(actor, source_module) IS NOT TRUE THEN
                        RAISE EXCEPTION 'Group access denied' USING ERRCODE = 'AG403';
                    END IF;
                    IF NOT andril_groups.can_read_group(actor, source_module, target_id, false) THEN RETURN; END IF;
                    RETURN QUERY SELECT EXISTS (SELECT FROM andril_groups.memberships m WHERE m.group_id = target_id AND m.module = source_module
                                AND m.profile_id = actor AND m.departed_at_utc IS NULL),
                        (SELECT r.requested_at_utc FROM andril_groups.join_requests r WHERE r.group_id = target_id AND r.module = source_module
                            AND r.profile_id = actor AND r.state = 'pending'), g.revision, clock_timestamp()
                        FROM andril_groups.groups g WHERE g.id = target_id AND g.module = source_module;
                END;
                $$;

                REVOKE ALL ON FUNCTION andril_groups.can_read_group(uuid, text, uuid, boolean),
                    andril_groups.read_discovery(uuid, text, integer, text, integer),
                    andril_groups.read_own_participation(uuid, text, uuid) FROM PUBLIC;

                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION andril_groups.read_discovery(uuid, text, integer, text, integer);
                DROP FUNCTION andril_groups.read_own_participation(uuid, text, uuid);

                CREATE OR REPLACE FUNCTION andril_groups.read_group_details(actor uuid, source_module text, target_id uuid)
                RETURNS TABLE(id uuid, name text, description text, is_discoverable boolean, visibility text, revision bigint)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_groups.lock_group_operations();
                    IF andril_groups.actor_standing(actor, source_module) IS NOT TRUE THEN RAISE EXCEPTION 'Group access denied' USING ERRCODE = 'AG403'; END IF;
                    RETURN QUERY SELECT g.id, g.name::text, g.description::text, g.is_discoverable, g.visibility::text, g.revision
                      FROM andril_groups.groups g WHERE g.id = target_id AND g.module = source_module AND g.is_active AND g.creator_profile_id = actor;
                END;
                $$;

                DROP FUNCTION andril_groups.can_read_group(uuid, text, uuid, boolean);

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

                REVOKE ALL ON FUNCTION andril_groups.social_source_visible(regclass) FROM PUBLIC;

                CREATE OR REPLACE FUNCTION andril_groups.profile_is_restricted(candidate uuid, source_module text) RETURNS boolean
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

                CREATE OR REPLACE FUNCTION andril_groups.pair_is_blocked(first_profile uuid, second_profile uuid, source_module text) RETURNS boolean
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

                DROP FUNCTION andril_groups.safety_source_visible(regclass, text);

                """);
        }
    }
}
