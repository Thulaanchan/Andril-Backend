using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Social
{
    /// <inheritdoc />
    public partial class SocialPersonalityEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE andril_social.social_archetype_confirmations (
                    profile_id uuid PRIMARY KEY,
                    response_id uuid NOT NULL,
                    social_choice integer CHECK (social_choice IN (0,1)),
                    communication_choice integer CHECK (communication_choice IN (0,1)),
                    planning_choice integer CHECK (planning_choice IN (0,1)),
                    revision bigint NOT NULL CHECK (revision > 0),
                    CHECK (num_nonnulls(social_choice, communication_choice, planning_choice) > 0),
                    FOREIGN KEY (profile_id, response_id) REFERENCES andril_social.social_questionnaire_responses(profile_id, id)
                );
                REVOKE ALL ON andril_social.social_archetype_confirmations FROM PUBLIC;
                ALTER TABLE andril_social.social_archetype_confirmations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_archetype_confirmations FORCE ROW LEVEL SECURITY;
                CREATE POLICY social_archetype_authority ON andril_social.social_archetype_confirmations TO CURRENT_USER USING (true) WITH CHECK (true);

                CREATE FUNCTION andril_social.read_social_matching_personality(caller uuid, exact_profile uuid, maximum integer)
                RETURNS TABLE(profile_id uuid, social_choice integer, communication_choice integer, planning_choice integer, revision bigint)
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_social.social_safety_lock();
                    RETURN QUERY SELECT evidence.profile_id,
                        CASE WHEN evidence.answers_json IS NOT NULL AND saved.response_id = evidence.response_id THEN saved.social_choice END,
                        CASE WHEN evidence.answers_json IS NOT NULL AND saved.response_id = evidence.response_id THEN saved.communication_choice END,
                        CASE WHEN evidence.answers_json IS NOT NULL AND saved.response_id = evidence.response_id THEN saved.planning_choice END,
                        coalesce(saved.revision, 0)
                    FROM andril_social.read_social_matching_evidence(caller, exact_profile, maximum) evidence
                    LEFT JOIN andril_social.social_archetype_confirmations saved ON saved.profile_id = evidence.profile_id;
                END;
                $$;

                CREATE FUNCTION andril_social.set_social_archetype_confirmation(caller uuid, expected_response uuid, social integer,
                    communication integer, planning integer) RETURNS boolean
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_social.social_safety_lock();
                    IF NOT andril_social.social_interest_owner_allowed(caller) THEN
                        RAISE EXCEPTION 'Social archetype access denied' USING ERRCODE = '42501';
                    END IF;
                    IF NOT EXISTS (SELECT FROM andril_social.social_questionnaire_current_responses live
                        JOIN andril_social.social_questionnaire_responses response ON response.id = live.response_id AND response.profile_id = live.profile_id
                        WHERE live.profile_id = caller AND live.response_id = expected_response AND response.status = 'Completed') THEN RETURN false; END IF;
                    INSERT INTO andril_social.social_archetype_confirmations AS saved
                        (profile_id, response_id, social_choice, communication_choice, planning_choice, revision)
                    VALUES (caller, expected_response, social, communication, planning, 1)
                    ON CONFLICT (profile_id) DO UPDATE SET response_id = EXCLUDED.response_id, social_choice = EXCLUDED.social_choice,
                        communication_choice = EXCLUDED.communication_choice, planning_choice = EXCLUDED.planning_choice, revision = saved.revision + 1
                    WHERE (saved.response_id, saved.social_choice, saved.communication_choice, saved.planning_choice)
                        IS DISTINCT FROM (EXCLUDED.response_id, EXCLUDED.social_choice, EXCLUDED.communication_choice, EXCLUDED.planning_choice);
                    RETURN true;
                END;
                $$;

                CREATE FUNCTION andril_social.set_social_personality_preferences(caller uuid, social text, communication text, planning text) RETURNS boolean
                LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog AS $$
                BEGIN
                    PERFORM andril_social.social_safety_lock();
                    IF NOT andril_social.social_interest_owner_allowed(caller) THEN
                        RAISE EXCEPTION 'Social personality access denied' USING ERRCODE = '42501';
                    END IF;
                    IF NOT EXISTS (SELECT FROM andril_social.social_matching_preferences preference WHERE preference.profile_id = caller) THEN RETURN false; END IF;
                    UPDATE andril_social.social_matching_preferences preference SET
                        partner_social = social, partner_communication = communication, partner_planning = planning, revision = preference.revision + 1
                    WHERE preference.profile_id = caller AND (preference.partner_social, preference.partner_communication, preference.partner_planning)
                        IS DISTINCT FROM (social, communication, planning);
                    RETURN true;
                END;
                $$;

                REVOKE ALL ON FUNCTION andril_social.read_social_matching_personality(uuid,uuid,integer),
                    andril_social.set_social_archetype_confirmation(uuid,uuid,integer,integer,integer),
                    andril_social.set_social_personality_preferences(uuid,text,text,text) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION andril_social.set_social_personality_preferences(uuid,text,text,text);
                DROP FUNCTION andril_social.set_social_archetype_confirmation(uuid,uuid,integer,integer,integer);
                DROP FUNCTION andril_social.read_social_matching_personality(uuid,uuid,integer);
                DROP TABLE andril_social.social_archetype_confirmations;
                """);
        }
    }
}
