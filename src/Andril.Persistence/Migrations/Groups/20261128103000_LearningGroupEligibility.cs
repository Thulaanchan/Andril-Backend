using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Groups;

/// <summary>Private purpose-scoped bridge to M2 lifecycle evidence. No runtime grants,
/// RLS bypass, target-subject impersonation, retention policy or eligibility projection.</summary>
public partial class LearningGroupEligibility : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE andril_groups.learning_eligibility_read_scopes (
                id uuid PRIMARY KEY,
                backend_pid integer NOT NULL,
                transaction_id text NOT NULL,
                actor_account uuid NOT NULL,
                actor_subject uuid NOT NULL,
                actor_profile uuid NOT NULL,
                target_account uuid NOT NULL,
                target_profile uuid NOT NULL,
                group_id uuid NOT NULL,
                purpose text NOT NULL CHECK (purpose = 'learning-group-interaction'),
                module text NOT NULL CHECK (module = 'learning')
            );
            REVOKE ALL ON andril_groups.learning_eligibility_read_scopes FROM PUBLIC;
            ALTER TABLE andril_groups.learning_eligibility_read_scopes ENABLE ROW LEVEL SECURITY;
            ALTER TABLE andril_groups.learning_eligibility_read_scopes FORCE ROW LEVEL SECURITY;
            CREATE POLICY eligibility_scope_owner ON andril_groups.learning_eligibility_read_scopes
                TO CURRENT_USER USING (true) WITH CHECK (true);

            -- Only the migration/definer owner can use this additional SELECT policy.
            -- The private row must match this backend, transaction, caller and exact target.
            -- No caller-controlled GUC alone grants access and no target context is installed.
            CREATE POLICY learning_group_eligibility_scope ON andril_identity.account_module_lifecycles
                FOR SELECT TO CURRENT_USER USING (module = 'learning' AND EXISTS (
                    SELECT FROM andril_groups.learning_eligibility_read_scopes s
                    WHERE s.backend_pid = pg_backend_pid() AND s.transaction_id = pg_current_xact_id()::text
                        AND s.purpose = 'learning-group-interaction' AND s.module = 'learning'
                        AND s.target_account = account_id
                        AND s.actor_account = nullif(current_setting('andril.account', true), '')::uuid
                        AND s.actor_subject = nullif(current_setting('andril.subject', true), '')::uuid
                        AND current_setting('andril.module', true) = 'learning'
                        AND EXISTS (SELECT FROM andril_identity.accounts a JOIN andril_identity.private_module_profiles ap ON ap.account_id=a.id
                            WHERE a.id=s.actor_account AND a.supabase_user_id=s.actor_subject AND ap.id=s.actor_profile AND ap.module='learning')
                        AND EXISTS (SELECT FROM andril_identity.private_module_profiles p
                            WHERE p.id = s.target_profile AND p.account_id = s.target_account AND p.module = s.module)
                        AND EXISTS (SELECT FROM andril_groups.groups g
                            WHERE g.id = s.group_id AND g.module = s.module AND g.kind = 1
                                AND (g.creator_profile_id = s.target_profile OR s.actor_profile = s.target_profile
                                    OR (EXISTS (SELECT FROM andril_groups.capabilities h JOIN andril_groups.memberships hm
                                        ON hm.group_id=h.group_id AND hm.module=h.module AND hm.profile_id=h.profile_id
                                        WHERE h.group_id=g.id AND h.module='learning' AND h.profile_id=s.actor_profile
                                            AND h.capability=1 AND hm.departed_at_utc IS NULL)
                                        AND (EXISTS (SELECT FROM andril_groups.memberships m WHERE m.group_id=g.id AND m.module='learning'
                                                AND m.profile_id=s.target_profile AND m.departed_at_utc IS NULL)
                                            OR EXISTS (SELECT FROM andril_groups.join_requests r WHERE r.group_id=g.id AND r.module='learning'
                                                AND r.profile_id=s.target_profile AND r.state='pending')))))));

            -- PostgreSQL applies UPDATE USING policies to SELECT FOR SHARE too.
            -- Permit only the identical scoped lock; WITH CHECK false cannot authorize
            -- an actual cross-owner UPDATE. Existing tenant writes remain unchanged.
            DO $$
            DECLARE qualifier text;
            BEGIN
                SELECT pg_get_expr(polqual,polrelid) INTO STRICT qualifier FROM pg_policy
                    WHERE polrelid='andril_identity.account_module_lifecycles'::regclass
                        AND polname='learning_group_eligibility_scope';
                EXECUTE format('CREATE POLICY learning_group_eligibility_lock ON andril_identity.account_module_lifecycles FOR UPDATE TO %I USING (%s) WITH CHECK (false)',current_user,qualifier);
            END;
            $$;

            CREATE FUNCTION andril_groups.learning_group_target_eligible(candidate uuid, target_id uuid) RETURNS boolean
            LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
            DECLARE target_profile uuid;
            DECLARE target_account uuid;
            DECLARE caller_account uuid;
            DECLARE caller_subject uuid;
            DECLARE caller_profile uuid;
            DECLARE creator_profile uuid;
            DECLARE scope_id uuid;
            DECLARE eligible boolean;
            BEGIN
                PERFORM andril_groups.lock_group_operations();
                SELECT p.id, a.id, a.supabase_user_id INTO caller_profile, caller_account, caller_subject
                    FROM andril_identity.accounts a JOIN andril_identity.private_module_profiles p ON p.account_id = a.id
                    WHERE p.module = 'learning'
                        AND a.id = nullif(current_setting('andril.account', true), '')::uuid
                        AND a.supabase_user_id = nullif(current_setting('andril.subject', true), '')::uuid;
                IF NOT FOUND OR andril_groups.owns_current_profile(caller_profile, 'learning') IS NOT TRUE
                    OR andril_groups.profile_is_restricted(caller_profile, 'learning') THEN RETURN false; END IF;
                SELECT g.creator_profile_id, p.account_id INTO target_profile, target_account
                    FROM andril_groups.groups g JOIN andril_identity.private_module_profiles p
                        ON p.id = g.creator_profile_id AND p.module = g.module
                    WHERE g.id = target_id AND g.module = 'learning' AND g.kind = 1;
                IF NOT FOUND THEN RETURN false; END IF;
                creator_profile := target_profile;
                -- A host reviewing admission may validate only an actual participant
                -- or pending applicant, never an arbitrary selected Learning profile.
                IF candidate <> caller_profile AND NOT (
                    EXISTS (SELECT FROM andril_groups.capabilities h JOIN andril_groups.memberships hm
                        ON hm.group_id=h.group_id AND hm.module=h.module AND hm.profile_id=h.profile_id
                        WHERE h.group_id=target_id AND h.module='learning' AND h.profile_id=caller_profile
                            AND h.capability=1 AND hm.departed_at_utc IS NULL)
                    AND (EXISTS (SELECT FROM andril_groups.memberships m WHERE m.group_id=target_id AND m.module='learning'
                            AND m.profile_id=candidate AND m.departed_at_utc IS NULL)
                        OR EXISTS (SELECT FROM andril_groups.join_requests r WHERE r.group_id=target_id AND r.module='learning'
                            AND r.profile_id=candidate AND r.state='pending'))) THEN RETURN false; END IF;
                IF andril_groups.profile_is_restricted(candidate, 'learning')
                    OR andril_groups.profile_is_restricted(creator_profile, 'learning')
                    OR andril_groups.pair_is_blocked(candidate, creator_profile, 'learning')
                    OR EXISTS (SELECT FROM andril_groups.capabilities c JOIN andril_groups.memberships m
                        ON m.group_id = c.group_id AND m.module = c.module AND m.profile_id = c.profile_id
                        WHERE c.group_id = target_id AND c.module = 'learning' AND c.capability = 1
                            AND m.departed_at_utc IS NULL AND andril_groups.pair_is_blocked(candidate, c.profile_id, 'learning'))
                    THEN RETURN false; END IF;

                IF (SELECT count(*) FROM andril_identity.private_module_profiles p WHERE p.module='learning'
                    AND p.id IN (candidate,creator_profile)) <> (CASE WHEN candidate=creator_profile THEN 1 ELSE 2 END) THEN RETURN false; END IF;
                FOR target_profile, target_account IN
                    SELECT p.id,p.account_id FROM andril_identity.private_module_profiles p
                    WHERE p.module='learning' AND p.id IN (candidate,creator_profile) ORDER BY p.account_id,p.id
                LOOP
                scope_id := gen_random_uuid();
                INSERT INTO andril_groups.learning_eligibility_read_scopes
                    VALUES (scope_id, pg_backend_pid(), pg_current_xact_id()::text, caller_account, caller_subject,
                        caller_profile, target_account, target_profile, target_id, 'learning-group-interaction', 'learning');
                -- The original M5 Learning rule is unchanged: active lifecycle, current
                -- starter membership and date of birth at least 16 years ago.
                -- NOWAIT avoids waiting in reverse order behind an existing M2/M5
                -- operation. Successful shared row locks survive scope-row deletion
                -- until the enclosing Accounts read/commit boundary releases them.
                PERFORM a.id FROM andril_identity.accounts a
                    JOIN andril_identity.account_starter_memberships m ON m.account_id = a.id AND m.module = 'learning'
                    JOIN andril_identity.account_demographics d ON d.account_id = a.id
                    JOIN andril_identity.account_module_lifecycles l ON l.account_id = a.id AND l.module = m.module
                    JOIN andril_identity.private_module_profiles p ON p.account_id = a.id AND p.module = m.module
                    WHERE a.id = target_account AND p.id = target_profile AND l.state = 'active'
                        AND d.date_of_birth <= timezone('UTC', statement_timestamp())::date - interval '16 years'
                    FOR SHARE OF a, m, d, l, p NOWAIT;
                eligible := FOUND;
                DELETE FROM andril_groups.learning_eligibility_read_scopes WHERE id = scope_id;
                IF NOT eligible THEN RETURN false; END IF;
                END LOOP;
                RETURN true;
            EXCEPTION WHEN lock_not_available OR serialization_failure OR deadlock_detected THEN
                -- The enclosing exception subtransaction removes the private scope
                -- and all partially acquired locks. No waiting/replay or stale grant.
                RAISE EXCEPTION 'Learning eligibility unavailable' USING ERRCODE='AG503';
            END;
            $$;
            REVOKE ALL ON FUNCTION andril_groups.learning_group_target_eligible(uuid, uuid) FROM PUBLIC;

            CREATE OR REPLACE FUNCTION andril_groups.can_interact_with_hosts(candidate uuid, source_module text, target_id uuid) RETURNS boolean
            LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                SELECT CASE WHEN source_module = 'learning'
                    THEN andril_groups.learning_group_target_eligible(candidate, target_id)
                    ELSE andril_groups.profile_is_active(candidate, source_module) AND NOT andril_groups.profile_is_restricted(candidate, source_module)
                        AND EXISTS (SELECT FROM andril_groups.groups g WHERE g.id = target_id AND g.module = source_module
                            AND andril_groups.profile_is_active(g.creator_profile_id, source_module)
                            AND NOT andril_groups.profile_is_restricted(g.creator_profile_id, source_module)
                            AND NOT andril_groups.pair_is_blocked(candidate, g.creator_profile_id, source_module))
                        AND NOT EXISTS (SELECT FROM andril_groups.capabilities c
                            JOIN andril_groups.memberships m ON m.group_id = c.group_id AND m.module = c.module AND m.profile_id = c.profile_id
                            WHERE c.group_id = target_id AND c.module = source_module AND c.capability = 1 AND m.departed_at_utc IS NULL
                                AND andril_groups.pair_is_blocked(candidate, c.profile_id, source_module)) END;
            $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
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
            DROP FUNCTION andril_groups.learning_group_target_eligible(uuid, uuid);
            DROP POLICY learning_group_eligibility_lock ON andril_identity.account_module_lifecycles;
            DROP POLICY learning_group_eligibility_scope ON andril_identity.account_module_lifecycles;
            DROP TABLE andril_groups.learning_eligibility_read_scopes;
            """);
    }
}
