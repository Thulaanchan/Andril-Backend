using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Social
{
    /// <inheritdoc />
    public partial class SocialInterestCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE SEQUENCE andril_social.social_interest_evidence_revision AS bigint;
                CREATE TABLE andril_social.social_interest_catalogue (
                    interest_id uuid PRIMARY KEY CHECK (interest_id <> '00000000-0000-0000-0000-000000000000'::uuid),
                    display_name text NOT NULL CHECK (display_name = btrim(display_name) AND length(display_name) BETWEEN 1 AND 160),
                    active boolean NOT NULL,
                    revision bigint NOT NULL CHECK (revision > 0)
                );
                CREATE TABLE andril_social.social_interest_selection_state (
                    profile_id uuid PRIMARY KEY,
                    module varchar(16) NOT NULL DEFAULT 'social' CHECK (module = 'social'),
                    revision bigint NOT NULL CHECK (revision > 0),
                    FOREIGN KEY (profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                );
                CREATE TABLE andril_social.social_interest_selections (
                    profile_id uuid NOT NULL REFERENCES andril_social.social_interest_selection_state(profile_id) ON DELETE CASCADE,
                    interest_id uuid NOT NULL REFERENCES andril_social.social_interest_catalogue(interest_id),
                    PRIMARY KEY (profile_id, interest_id)
                );

                DO $$
                DECLARE relation text;
                BEGIN
                    FOREACH relation IN ARRAY ARRAY['social_interest_catalogue', 'social_interest_selection_state', 'social_interest_selections'] LOOP
                        EXECUTE format('REVOKE ALL ON andril_social.%I FROM PUBLIC', relation);
                        EXECUTE format('ALTER TABLE andril_social.%I ENABLE ROW LEVEL SECURITY', relation);
                        EXECUTE format('ALTER TABLE andril_social.%I FORCE ROW LEVEL SECURITY', relation);
                        EXECUTE format('CREATE POLICY social_interest_authority ON andril_social.%I TO CURRENT_USER USING (true) WITH CHECK (true)', relation);
                    END LOOP;
                END;
                $$;
                REVOKE ALL ON SEQUENCE andril_social.social_interest_evidence_revision FROM PUBLIC;

                CREATE FUNCTION andril_social.provision_social_interest(interest uuid, display_label text, available boolean) RETURNS void
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_social.social_safety_lock();
                    INSERT INTO andril_social.social_interest_catalogue AS saved(interest_id, display_name, active, revision)
                        VALUES (interest, display_label, available, nextval('andril_social.social_interest_evidence_revision'))
                    ON CONFLICT (interest_id) DO UPDATE SET display_name = EXCLUDED.display_name, active = EXCLUDED.active, revision = EXCLUDED.revision
                        WHERE (saved.display_name, saved.active) IS DISTINCT FROM (EXCLUDED.display_name, EXCLUDED.active);
                END;
                $$;

                CREATE FUNCTION andril_social.social_interest_owner_allowed(caller uuid) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT EXISTS (SELECT FROM andril_social.social_safety_decision(caller, caller) evidence WHERE evidence.decision = 'allowed');
                $$;

                CREATE FUNCTION andril_social.read_social_interest_catalogue(caller uuid)
                RETURNS TABLE(interest_id uuid, display_name text)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_social.social_safety_lock();
                    IF NOT andril_social.social_interest_owner_allowed(caller) THEN
                        RAISE EXCEPTION 'Social catalogue access denied' USING ERRCODE = '42501';
                    END IF;
                    RETURN QUERY SELECT c.interest_id, c.display_name FROM andril_social.social_interest_catalogue c
                        WHERE c.active ORDER BY c.display_name, c.interest_id;
                END;
                $$;

                CREATE FUNCTION andril_social.social_interest_snapshot(candidate uuid) RETURNS TABLE(interest_ids uuid[], revision bigint)
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT CASE WHEN state.revision IS NULL THEN NULL ELSE ARRAY(
                        SELECT selection.interest_id FROM andril_social.social_interest_selections selection
                        JOIN andril_social.social_interest_catalogue item ON item.interest_id = selection.interest_id
                        WHERE selection.profile_id = candidate AND item.active ORDER BY selection.interest_id) END,
                        greatest(coalesce(state.revision, 0), coalesce((SELECT max(item.revision) FROM andril_social.social_interest_catalogue item),0))
                    FROM (SELECT candidate) requested
                    LEFT JOIN andril_social.social_interest_selection_state state ON state.profile_id = requested.candidate;
                $$;

                CREATE FUNCTION andril_social.read_own_social_interests(caller uuid) RETURNS TABLE(interest_ids uuid[], revision bigint)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_social.social_safety_lock();
                    IF NOT andril_social.social_interest_owner_allowed(caller) THEN
                        RAISE EXCEPTION 'Social interest access denied' USING ERRCODE = '42501';
                    END IF;
                    RETURN QUERY SELECT snapshot.interest_ids, snapshot.revision FROM andril_social.social_interest_snapshot(caller) snapshot;
                END;
                $$;

                CREATE FUNCTION andril_social.replace_own_social_interests(caller uuid, selected uuid[]) RETURNS void
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                DECLARE ordered uuid[];
                DECLARE current_selection uuid[];
                BEGIN
                    PERFORM andril_social.social_safety_lock();
                    IF NOT andril_social.social_interest_owner_allowed(caller) THEN
                        RAISE EXCEPTION 'Social interest access denied' USING ERRCODE = '42501';
                    END IF;
                    SELECT array_agg(value ORDER BY value) INTO ordered FROM unnest(selected) value;
                    ordered := coalesce(ordered, ARRAY[]::uuid[]);
                    IF selected IS NULL OR array_position(selected, NULL::uuid) IS NOT NULL
                        OR (SELECT count(DISTINCT value) FROM unnest(selected) value) <> cardinality(selected)
                        OR EXISTS (SELECT FROM unnest(selected) value WHERE NOT EXISTS (
                            SELECT FROM andril_social.social_interest_catalogue item WHERE item.interest_id = value AND item.active)) THEN
                        RAISE EXCEPTION 'Invalid Social interest selection' USING ERRCODE = '22023';
                    END IF;
                    SELECT ARRAY(SELECT s.interest_id FROM andril_social.social_interest_selections s WHERE s.profile_id = caller ORDER BY s.interest_id)
                        INTO current_selection;
                    IF current_selection = ordered AND EXISTS (SELECT FROM andril_social.social_interest_selection_state WHERE profile_id = caller) THEN RETURN; END IF;
                    INSERT INTO andril_social.social_interest_selection_state(profile_id, revision)
                        VALUES (caller, nextval('andril_social.social_interest_evidence_revision'))
                        ON CONFLICT (profile_id) DO UPDATE SET revision = EXCLUDED.revision;
                    DELETE FROM andril_social.social_interest_selections selection WHERE selection.profile_id = caller;
                    INSERT INTO andril_social.social_interest_selections(profile_id, interest_id) SELECT caller, value FROM unnest(ordered) value;
                END;
                $$;

                CREATE FUNCTION andril_social.read_social_matching_interests(caller uuid, exact_profile uuid, maximum integer)
                RETURNS TABLE(profile_id uuid, interest_ids uuid[], revision bigint)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_social.social_safety_lock();
                    RETURN QUERY SELECT evidence.profile_id, snapshot.interest_ids, snapshot.revision
                        FROM andril_social.read_social_matching_evidence(caller, exact_profile, maximum) evidence
                        CROSS JOIN LATERAL andril_social.social_interest_snapshot(evidence.profile_id) snapshot;
                END;
                $$;

                REVOKE ALL ON FUNCTION andril_social.provision_social_interest(uuid,text,boolean),
                    andril_social.social_interest_owner_allowed(uuid), andril_social.social_interest_snapshot(uuid),
                    andril_social.read_social_interest_catalogue(uuid), andril_social.read_own_social_interests(uuid),
                    andril_social.replace_own_social_interests(uuid,uuid[]), andril_social.read_social_matching_interests(uuid,uuid,integer) FROM PUBLIC;

                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION andril_social.read_social_matching_interests(uuid,uuid,integer);
                DROP FUNCTION andril_social.replace_own_social_interests(uuid,uuid[]);
                DROP FUNCTION andril_social.read_own_social_interests(uuid);
                DROP FUNCTION andril_social.social_interest_snapshot(uuid);
                DROP FUNCTION andril_social.read_social_interest_catalogue(uuid);
                DROP FUNCTION andril_social.social_interest_owner_allowed(uuid);
                DROP FUNCTION andril_social.provision_social_interest(uuid,text,boolean);
                DROP TABLE andril_social.social_interest_selections;
                DROP TABLE andril_social.social_interest_selection_state;
                DROP TABLE andril_social.social_interest_catalogue;
                DROP SEQUENCE andril_social.social_interest_evidence_revision;
                """);
        }
    }
}
