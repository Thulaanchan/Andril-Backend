using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Groups
{
    /// <inheritdoc />
    public partial class UnifiedGroupLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION andril_groups.group_capacity(creator uuid, source_module text) RETURNS integer
                LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT CASE andril_groups.creator_entitlement(creator, source_module) WHEN 2 THEN 100 ELSE 50 END;
                $$;
                REVOKE ALL ON FUNCTION andril_groups.group_capacity(uuid, text) FROM PUBLIC;

                ALTER FUNCTION andril_groups.read_club_capacity(uuid, uuid) RENAME TO read_group_capacity;

                CREATE OR REPLACE FUNCTION andril_groups.create_group(actor uuid, source_module text, source_kind integer, target_id uuid,
                    new_name text, new_description text, new_visibility text, new_discoverable boolean)
                RETURNS TABLE(id uuid, name text, description text, is_discoverable boolean, visibility text, revision bigint)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_groups.lock_group_operations();
                    IF andril_groups.actor_standing(actor, source_module) IS NOT TRUE OR (source_module = 'learning' AND NOT EXISTS
                        (SELECT FROM andril_groups.classroom_host_grants grant_row WHERE grant_row.profile_id = actor)) THEN
                        RAISE EXCEPTION 'Group creation denied' USING ERRCODE = 'AG403';
                    END IF;
                    IF andril_groups.creator_entitlement(actor, source_module) <> 2 AND
                        (SELECT count(*) FROM andril_groups.groups g WHERE g.module = source_module AND g.creator_profile_id = actor AND g.is_active) >= 5 THEN
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

                CREATE OR REPLACE FUNCTION andril_groups.mutate_membership(actor uuid, source_module text, target_id uuid, applicant uuid,
                    operation text, expected_revision bigint)
                RETURNS TABLE(status integer, revision bigint, requested_at timestamp with time zone)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE target andril_groups.groups%ROWTYPE;
                DECLARE pending andril_groups.join_requests%ROWTYPE;
                DECLARE active_member boolean;
                DECLARE next_revision bigint;
                DECLARE operation_time timestamp with time zone;
                DECLARE action_name text;
                BEGIN
                    PERFORM andril_groups.lock_group_operations();
                    IF andril_groups.actor_standing(actor, source_module) IS NOT TRUE THEN RAISE EXCEPTION 'Group access denied' USING ERRCODE = 'AG403'; END IF;
                    SELECT g.* INTO target FROM andril_groups.groups g WHERE g.id = target_id AND g.module = source_module AND g.is_active FOR UPDATE;
                    IF NOT FOUND THEN RAISE EXCEPTION 'Group access denied' USING ERRCODE = 'AG403'; END IF;
                    operation_time := clock_timestamp();
                    IF operation IN ('approve', 'reject') THEN
                        IF NOT andril_groups.has_capability(actor, source_module, target_id, 1) OR applicant IS NULL OR NOT target.is_open THEN
                            RAISE EXCEPTION 'Group admission denied' USING ERRCODE = 'AG403';
                        END IF;
                    ELSE
                        applicant := actor;
                    END IF;
                    SELECT EXISTS (SELECT FROM andril_groups.memberships m WHERE m.group_id = target_id AND m.module = source_module
                        AND m.profile_id = applicant AND m.departed_at_utc IS NULL) INTO active_member;
                    SELECT request.* INTO pending FROM andril_groups.join_requests request WHERE request.group_id = target_id
                        AND request.module = source_module AND request.profile_id = applicant AND request.state = 'pending';
                    IF operation NOT IN ('approve', 'reject') AND actor <> target.creator_profile_id AND NOT active_member AND pending.id IS NULL
                        AND NOT (target.is_discoverable AND target.visibility = 'public' AND andril_groups.can_interact_with_hosts(actor, source_module, target_id)) THEN
                        RAISE EXCEPTION 'Group access denied' USING ERRCODE = 'AG403';
                    END IF;
                    IF expected_revision IS NOT NULL AND expected_revision <> target.revision THEN
                        RETURN QUERY SELECT 6, target.revision, NULL::timestamptz; RETURN;
                    END IF;

                    CASE operation
                    WHEN 'submit' THEN
                        IF NOT target.is_open OR (NOT active_member AND NOT (target.is_discoverable AND target.visibility = 'public'))
                            OR NOT andril_groups.can_interact_with_hosts(actor, source_module, target_id) THEN
                            RAISE EXCEPTION 'Group request denied' USING ERRCODE = 'AG403';
                        END IF;
                        IF active_member THEN RETURN QUERY SELECT 2, target.revision, NULL::timestamptz; RETURN; END IF;
                        IF pending.id IS NOT NULL THEN RETURN QUERY SELECT 3, target.revision, pending.requested_at_utc; RETURN; END IF;
                        INSERT INTO andril_groups.join_requests(id, group_id, module, profile_id, state, requested_at_utc)
                            VALUES (gen_random_uuid(), target_id, source_module, actor, 'pending', operation_time);
                        action_name := 'request_submitted';
                    WHEN 'cancel' THEN
                        IF pending.id IS NULL THEN RETURN QUERY SELECT 4, target.revision, NULL::timestamptz; RETURN; END IF;
                        UPDATE andril_groups.join_requests SET state = 'cancelled', resolved_at_utc = greatest(operation_time, pending.requested_at_utc) WHERE id = pending.id;
                        action_name := 'request_cancelled';
                    WHEN 'approve', 'reject' THEN
                        IF pending.id IS NULL THEN RETURN QUERY SELECT 4, target.revision, NULL::timestamptz; RETURN; END IF;
                        IF operation = 'approve' THEN
                            IF NOT andril_groups.can_interact_with_hosts(applicant, source_module, target_id) THEN
                                RAISE EXCEPTION 'Group admission denied' USING ERRCODE = 'AG403';
                            END IF;
                            IF active_member THEN RETURN QUERY SELECT 2, target.revision, NULL::timestamptz; RETURN; END IF;
                            IF (SELECT count(*) FROM andril_groups.memberships m WHERE m.group_id = target_id
                                AND m.module = source_module AND m.departed_at_utc IS NULL) >= andril_groups.group_capacity(target.creator_profile_id, source_module) THEN
                                RETURN QUERY SELECT 7, target.revision, NULL::timestamptz; RETURN;
                            END IF;
                            INSERT INTO andril_groups.memberships(group_id, module, profile_id, joined_at_utc)
                                VALUES (target_id, source_module, applicant, operation_time)
                                ON CONFLICT (group_id, module, profile_id) DO UPDATE SET joined_at_utc = EXCLUDED.joined_at_utc, departed_at_utc = NULL;
                            action_name := 'request_approved';
                        ELSE
                            action_name := 'request_rejected';
                        END IF;
                        UPDATE andril_groups.join_requests SET state = CASE WHEN operation = 'approve' THEN 'approved' ELSE 'rejected' END,
                            resolved_at_utc = greatest(operation_time, pending.requested_at_utc) WHERE id = pending.id;
                    WHEN 'leave' THEN
                        IF target.creator_profile_id = actor THEN RAISE EXCEPTION 'Group departure denied' USING ERRCODE = 'AG403'; END IF;
                        IF NOT active_member THEN RETURN QUERY SELECT 5, target.revision, NULL::timestamptz; RETURN; END IF;
                        UPDATE andril_groups.memberships m SET departed_at_utc = greatest(operation_time, m.joined_at_utc)
                            WHERE m.group_id = target_id AND m.module = source_module AND m.profile_id = actor;
                        DELETE FROM andril_groups.capabilities c WHERE c.group_id = target_id AND c.module = source_module AND c.profile_id = actor;
                        action_name := 'member_departed';
                    ELSE
                        RAISE EXCEPTION 'Unknown group operation' USING ERRCODE = '22023';
                    END CASE;

                    UPDATE andril_groups.groups g SET revision = g.revision + 1 WHERE g.id = target_id AND g.module = source_module RETURNING g.revision INTO next_revision;
                    INSERT INTO andril_groups.audit(group_id, module, actor_profile_id, action, revision, recorded_at_utc)
                        VALUES (target_id, source_module, actor, action_name, next_revision, operation_time);
                    RETURN QUERY SELECT 1, next_revision, CASE WHEN operation = 'submit' THEN operation_time ELSE NULL::timestamptz END;
                END;
                $$;

                CREATE OR REPLACE FUNCTION andril_groups.read_group_capacity(actor uuid, target_id uuid) RETURNS integer
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE creator uuid;
                DECLARE source_module text := current_setting('andril.module', true);
                BEGIN
                    IF andril_groups.actor_standing(actor, source_module) IS NOT TRUE OR NOT andril_groups.has_capability(actor, source_module, target_id, 1) THEN
                        RAISE EXCEPTION 'Admission authority denied' USING ERRCODE = 'AG403';
                    END IF;
                    SELECT g.creator_profile_id INTO creator FROM andril_groups.groups g WHERE g.id = target_id AND g.module = source_module AND g.is_active;
                    RETURN andril_groups.group_capacity(creator, source_module);
                END;
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER FUNCTION andril_groups.read_group_capacity(uuid, uuid) RENAME TO read_club_capacity;

                CREATE OR REPLACE FUNCTION andril_groups.read_club_capacity(actor uuid, target_id uuid) RETURNS integer
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

                CREATE OR REPLACE FUNCTION andril_groups.mutate_membership(actor uuid, source_module text, target_id uuid, applicant uuid,
                    operation text, expected_revision bigint)
                RETURNS TABLE(status integer, revision bigint, requested_at timestamp with time zone)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE target andril_groups.groups%ROWTYPE;
                DECLARE pending andril_groups.join_requests%ROWTYPE;
                DECLARE active_member boolean;
                DECLARE next_revision bigint;
                DECLARE operation_time timestamp with time zone;
                DECLARE action_name text;
                BEGIN
                    PERFORM andril_groups.lock_group_operations();
                    IF andril_groups.actor_standing(actor, source_module) IS NOT TRUE THEN RAISE EXCEPTION 'Group access denied' USING ERRCODE = 'AG403'; END IF;
                    SELECT g.* INTO target FROM andril_groups.groups g WHERE g.id = target_id AND g.module = source_module AND g.is_active FOR UPDATE;
                    IF NOT FOUND THEN RAISE EXCEPTION 'Group access denied' USING ERRCODE = 'AG403'; END IF;
                    operation_time := clock_timestamp();
                    IF operation IN ('approve', 'reject') THEN
                        IF NOT andril_groups.has_capability(actor, source_module, target_id, 1) OR applicant IS NULL OR NOT target.is_open THEN
                            RAISE EXCEPTION 'Group admission denied' USING ERRCODE = 'AG403';
                        END IF;
                    ELSE
                        applicant := actor;
                    END IF;
                    SELECT EXISTS (SELECT FROM andril_groups.memberships m WHERE m.group_id = target_id AND m.module = source_module
                        AND m.profile_id = applicant AND m.departed_at_utc IS NULL) INTO active_member;
                    SELECT request.* INTO pending FROM andril_groups.join_requests request WHERE request.group_id = target_id
                        AND request.module = source_module AND request.profile_id = applicant AND request.state = 'pending';
                    IF operation NOT IN ('approve', 'reject') AND actor <> target.creator_profile_id AND NOT active_member AND pending.id IS NULL
                        AND NOT (target.is_discoverable AND target.visibility = 'public' AND andril_groups.can_interact_with_hosts(actor, source_module, target_id)) THEN
                        RAISE EXCEPTION 'Group access denied' USING ERRCODE = 'AG403';
                    END IF;
                    IF expected_revision IS NOT NULL AND expected_revision <> target.revision THEN
                        RETURN QUERY SELECT 6, target.revision, NULL::timestamptz; RETURN;
                    END IF;

                    CASE operation
                    WHEN 'submit' THEN
                        IF NOT target.is_open OR (NOT active_member AND NOT (target.is_discoverable AND target.visibility = 'public'))
                            OR NOT andril_groups.can_interact_with_hosts(actor, source_module, target_id) THEN
                            RAISE EXCEPTION 'Group request denied' USING ERRCODE = 'AG403';
                        END IF;
                        IF active_member THEN RETURN QUERY SELECT 2, target.revision, NULL::timestamptz; RETURN; END IF;
                        IF pending.id IS NOT NULL THEN RETURN QUERY SELECT 3, target.revision, pending.requested_at_utc; RETURN; END IF;
                        INSERT INTO andril_groups.join_requests(id, group_id, module, profile_id, state, requested_at_utc)
                            VALUES (gen_random_uuid(), target_id, source_module, actor, 'pending', operation_time);
                        action_name := 'request_submitted';
                    WHEN 'cancel' THEN
                        IF pending.id IS NULL THEN RETURN QUERY SELECT 4, target.revision, NULL::timestamptz; RETURN; END IF;
                        UPDATE andril_groups.join_requests SET state = 'cancelled', resolved_at_utc = greatest(operation_time, pending.requested_at_utc) WHERE id = pending.id;
                        action_name := 'request_cancelled';
                    WHEN 'approve', 'reject' THEN
                        IF pending.id IS NULL THEN RETURN QUERY SELECT 4, target.revision, NULL::timestamptz; RETURN; END IF;
                        IF operation = 'approve' THEN
                            IF NOT andril_groups.can_interact_with_hosts(applicant, source_module, target_id) THEN
                                RAISE EXCEPTION 'Group admission denied' USING ERRCODE = 'AG403';
                            END IF;
                            IF active_member THEN RETURN QUERY SELECT 2, target.revision, NULL::timestamptz; RETURN; END IF;
                            IF source_module = 'hobbies' AND (SELECT count(*) FROM andril_groups.memberships m WHERE m.group_id = target_id
                                AND m.module = source_module AND m.departed_at_utc IS NULL) >= andril_groups.club_limit(target.creator_profile_id) THEN
                                RETURN QUERY SELECT 7, target.revision, NULL::timestamptz; RETURN;
                            END IF;
                            INSERT INTO andril_groups.memberships(group_id, module, profile_id, joined_at_utc)
                                VALUES (target_id, source_module, applicant, operation_time)
                                ON CONFLICT (group_id, module, profile_id) DO UPDATE SET joined_at_utc = EXCLUDED.joined_at_utc, departed_at_utc = NULL;
                            action_name := 'request_approved';
                        ELSE
                            action_name := 'request_rejected';
                        END IF;
                        UPDATE andril_groups.join_requests SET state = CASE WHEN operation = 'approve' THEN 'approved' ELSE 'rejected' END,
                            resolved_at_utc = greatest(operation_time, pending.requested_at_utc) WHERE id = pending.id;
                    WHEN 'leave' THEN
                        IF target.creator_profile_id = actor THEN RAISE EXCEPTION 'Group departure denied' USING ERRCODE = 'AG403'; END IF;
                        IF NOT active_member THEN RETURN QUERY SELECT 5, target.revision, NULL::timestamptz; RETURN; END IF;
                        UPDATE andril_groups.memberships m SET departed_at_utc = greatest(operation_time, m.joined_at_utc)
                            WHERE m.group_id = target_id AND m.module = source_module AND m.profile_id = actor;
                        DELETE FROM andril_groups.capabilities c WHERE c.group_id = target_id AND c.module = source_module AND c.profile_id = actor;
                        action_name := 'member_departed';
                    ELSE
                        RAISE EXCEPTION 'Unknown group operation' USING ERRCODE = '22023';
                    END CASE;

                    UPDATE andril_groups.groups g SET revision = g.revision + 1 WHERE g.id = target_id AND g.module = source_module RETURNING g.revision INTO next_revision;
                    INSERT INTO andril_groups.audit(group_id, module, actor_profile_id, action, revision, recorded_at_utc)
                        VALUES (target_id, source_module, actor, action_name, next_revision, operation_time);
                    RETURN QUERY SELECT 1, next_revision, CASE WHEN operation = 'submit' THEN operation_time ELSE NULL::timestamptz END;
                END;
                $$;

                CREATE OR REPLACE FUNCTION andril_groups.create_group(actor uuid, source_module text, source_kind integer, target_id uuid,
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

                DROP FUNCTION andril_groups.group_capacity(uuid, text);
                """);
        }
    }
}
