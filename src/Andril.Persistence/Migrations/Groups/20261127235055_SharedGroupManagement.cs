using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Groups
{
    /// <inheritdoc />
    public partial class SharedGroupManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE andril_groups.classroom_host_grants (
                    profile_id uuid PRIMARY KEY,
                    module character varying(16) NOT NULL CHECK (module = 'learning'),
                    FOREIGN KEY (profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                );
                REVOKE ALL ON andril_groups.classroom_host_grants FROM PUBLIC;
                ALTER TABLE andril_groups.classroom_host_grants ENABLE ROW LEVEL SECURITY;
                ALTER TABLE andril_groups.classroom_host_grants FORCE ROW LEVEL SECURITY;
                CREATE POLICY groups_authority ON andril_groups.classroom_host_grants TO CURRENT_USER USING (true) WITH CHECK (true);

                CREATE FUNCTION andril_groups.lock_group_operations() RETURNS void
                LANGUAGE sql VOLATILE SET search_path = pg_catalog AS $$
                    SELECT pg_advisory_xact_lock(hashtextextended('andril.groups.authority', 0));
                $$;

                CREATE FUNCTION andril_groups.club_limit(creator uuid) RETURNS integer
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE creator_account uuid;
                DECLARE subscription record;
                BEGIN
                    SELECT p.account_id INTO creator_account FROM andril_identity.private_module_profiles p WHERE p.id = creator AND p.module = 'hobbies';
                    IF creator_account IS NULL OR to_regclass('andril_economy.premium_entitlements') IS NULL THEN
                        RAISE EXCEPTION 'Group authority unavailable' USING ERRCODE = 'AG503';
                    END IF;
                    -- Share conflicts with the canonical writer's insert/update lock, including when there is no subscription row yet.
                    LOCK TABLE andril_economy.premium_entitlements IN SHARE MODE;
                    IF row_security_active('andril_economy.premium_entitlements'::regclass) THEN
                        RAISE EXCEPTION 'Entitlement visibility is indeterminate' USING ERRCODE = 'AG503';
                    END IF;
                    SELECT entitlement.tier, entitlement.is_active, entitlement.expires_at_utc INTO subscription
                      FROM andril_economy.premium_entitlements entitlement WHERE entitlement.account_id = creator_account;
                    IF NOT FOUND OR (subscription.tier = 0 AND NOT subscription.is_active AND subscription.expires_at_utc IS NULL) THEN RETURN 50; END IF;
                    IF subscription.tier IN (1, 2) AND subscription.is_active AND subscription.expires_at_utc IS NOT NULL
                        AND isfinite(subscription.expires_at_utc) THEN
                        RETURN CASE WHEN subscription.expires_at_utc > clock_timestamp() THEN 100 ELSE 50 END;
                    END IF;
                    RAISE EXCEPTION 'Entitlement status is indeterminate' USING ERRCODE = 'AG503';
                EXCEPTION WHEN insufficient_privilege OR undefined_table OR undefined_column THEN
                    RAISE EXCEPTION 'Group entitlement authority unavailable' USING ERRCODE = 'AG503';
                END;
                $$;

                CREATE FUNCTION andril_groups.actor_standing(actor uuid, source_module text) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT andril_groups.owns_current_profile(actor, source_module);
                $$;

                CREATE FUNCTION andril_groups.read_group_details(actor uuid, source_module text, target_id uuid)
                RETURNS TABLE(id uuid, name text, description text, is_discoverable boolean, visibility text, revision bigint)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_groups.lock_group_operations();
                    IF andril_groups.actor_standing(actor, source_module) IS NOT TRUE THEN RAISE EXCEPTION 'Group access denied' USING ERRCODE = 'AG403'; END IF;
                    RETURN QUERY SELECT g.id, g.name::text, g.description::text, g.is_discoverable, g.visibility::text, g.revision
                      FROM andril_groups.groups g WHERE g.id = target_id AND g.module = source_module AND g.is_active AND g.creator_profile_id = actor;
                END;
                $$;

                CREATE FUNCTION andril_groups.create_group(actor uuid, source_module text, source_kind integer, target_id uuid,
                    new_name text, new_description text, new_visibility text, new_discoverable boolean)
                RETURNS TABLE(id uuid, name text, description text, is_discoverable boolean, visibility text, revision bigint)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_groups.lock_group_operations();
                    IF andril_groups.actor_standing(actor, source_module) IS NOT TRUE OR (source_module = 'learning' AND NOT EXISTS
                        (SELECT FROM andril_groups.classroom_host_grants grant_row WHERE grant_row.profile_id = actor)) THEN
                        RAISE EXCEPTION 'Group creation denied' USING ERRCODE = 'AG403';
                    END IF;
                    IF source_module = 'hobbies' AND andril_groups.club_limit(actor) = 50 AND
                        (SELECT count(*) FROM andril_groups.groups g WHERE g.module = 'hobbies' AND g.creator_profile_id = actor AND g.is_active) >= 5 THEN
                        RAISE EXCEPTION 'Group creation quota reached' USING ERRCODE = 'AG409';
                    END IF;
                    INSERT INTO andril_groups.groups(id, module, kind, name, description, creator_profile_id, visibility, is_discoverable, is_active, is_open, revision)
                        VALUES (target_id, source_module, source_kind, new_name, new_description, actor, new_visibility, new_discoverable, true, true, 1);
                    INSERT INTO andril_groups.memberships(group_id, module, profile_id, joined_at_utc) VALUES (target_id, source_module, actor, clock_timestamp());
                    INSERT INTO andril_groups.capabilities(group_id, module, profile_id, capability) VALUES
                        (target_id, source_module, actor, 1), (target_id, source_module, actor, 4);
                    INSERT INTO andril_groups.audit(group_id, module, actor_profile_id, action, revision, recorded_at_utc)
                        VALUES (target_id, source_module, actor, 'created', 1, clock_timestamp());
                    RETURN QUERY SELECT target_id, new_name, new_description, new_discoverable, new_visibility, 1::bigint;
                END;
                $$;

                CREATE FUNCTION andril_groups.has_capability(actor uuid, source_module text, target_id uuid, needed integer) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT EXISTS (SELECT FROM andril_groups.capabilities c
                        JOIN andril_groups.memberships m ON m.group_id = c.group_id AND m.module = c.module AND m.profile_id = c.profile_id
                        WHERE c.group_id = target_id AND c.module = source_module AND c.profile_id = actor AND c.capability = needed
                            AND m.departed_at_utc IS NULL AND (source_module <> 'learning' OR EXISTS
                                (SELECT FROM andril_groups.classroom_host_grants grant_row WHERE grant_row.profile_id = actor)));
                $$;

                CREATE FUNCTION andril_groups.update_group(actor uuid, source_module text, target_id uuid, new_name text,
                    new_description text, new_visibility text, new_discoverable boolean, expected_revision bigint)
                RETURNS TABLE(id uuid, name text, description text, is_discoverable boolean, visibility text, revision bigint)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE next_revision bigint;
                BEGIN
                    PERFORM andril_groups.lock_group_operations();
                    IF andril_groups.actor_standing(actor, source_module) IS NOT TRUE OR NOT andril_groups.has_capability(actor, source_module, target_id, 4) THEN
                        RAISE EXCEPTION 'Group configuration denied' USING ERRCODE = 'AG403';
                    END IF;
                    UPDATE andril_groups.groups g SET name = new_name, description = new_description, visibility = new_visibility,
                        is_discoverable = new_discoverable, revision = g.revision + 1
                        WHERE g.id = target_id AND g.module = source_module AND g.revision = expected_revision AND g.is_active
                        RETURNING g.revision INTO next_revision;
                    IF NOT FOUND THEN RETURN; END IF;
                    INSERT INTO andril_groups.audit(group_id, module, actor_profile_id, action, revision, recorded_at_utc)
                        VALUES (target_id, source_module, actor, 'configured', next_revision, clock_timestamp());
                    RETURN QUERY SELECT target_id, new_name, new_description, new_discoverable, new_visibility, next_revision;
                END;
                $$;

                REVOKE ALL ON FUNCTION andril_groups.lock_group_operations(), andril_groups.club_limit(uuid), andril_groups.actor_standing(uuid, text),
                    andril_groups.read_group_details(uuid, text, uuid), andril_groups.create_group(uuid, text, integer, uuid, text, text, text, boolean),
                    andril_groups.has_capability(uuid, text, uuid, integer),
                    andril_groups.update_group(uuid, text, uuid, text, text, text, boolean, bigint) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION andril_groups.update_group(uuid, text, uuid, text, text, text, boolean, bigint);
                DROP FUNCTION andril_groups.has_capability(uuid, text, uuid, integer);
                DROP FUNCTION andril_groups.create_group(uuid, text, integer, uuid, text, text, text, boolean);
                DROP FUNCTION andril_groups.read_group_details(uuid, text, uuid);
                DROP FUNCTION andril_groups.actor_standing(uuid, text);
                DROP FUNCTION andril_groups.club_limit(uuid);
                DROP FUNCTION andril_groups.lock_group_operations();
                DROP TABLE andril_groups.classroom_host_grants;
                """);
        }
    }
}
