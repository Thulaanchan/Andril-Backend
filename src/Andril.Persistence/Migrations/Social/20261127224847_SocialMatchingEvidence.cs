using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Social
{
    /// <inheritdoc />
    public partial class SocialMatchingEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "social_location_evidence",
                schema: "andril_social",
                columns: table => new
                {
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    latitude_cell = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    longitude_cell = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    observed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_location_evidence", x => x.profile_id);
                    table.CheckConstraint("ck_social_location_cells", "(latitude_cell IS NULL AND longitude_cell IS NULL) OR\n(latitude_cell IS NOT NULL AND longitude_cell IS NOT NULL\n    AND latitude_cell BETWEEN -90 AND 90 AND longitude_cell >= -180 AND longitude_cell < 180)");
                    table.CheckConstraint("ck_social_location_module", "module = 'social'");
                    table.CheckConstraint("ck_social_location_revision", "revision > 0 AND isfinite(observed_at_utc)");
                });

            migrationBuilder.Sql("""
                ALTER TABLE andril_social.social_location_evidence
                    ADD CONSTRAINT fk_social_location_evidence_profile FOREIGN KEY (profile_id, module)
                        REFERENCES andril_identity.private_module_profiles(id, module) ON UPDATE RESTRICT ON DELETE RESTRICT;
                REVOKE ALL ON andril_social.social_location_evidence FROM PUBLIC;
                ALTER TABLE andril_social.social_location_evidence ENABLE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_location_evidence FORCE ROW LEVEL SECURITY;
                CREATE POLICY social_location_owner ON andril_social.social_location_evidence
                    USING (andril_social.questionnaire_owner_visible(profile_id))
                    WITH CHECK (andril_social.questionnaire_owner_visible(profile_id));
                CREATE POLICY social_location_evidence_authority ON andril_social.social_location_evidence FOR SELECT TO CURRENT_USER USING (true);
                CREATE POLICY social_preference_evidence_authority ON andril_social.social_matching_preferences FOR SELECT TO CURRENT_USER USING (true);
                CREATE POLICY social_current_evidence_authority ON andril_social.social_questionnaire_current_responses FOR SELECT TO CURRENT_USER USING (true);
                CREATE POLICY social_response_evidence_authority ON andril_social.social_questionnaire_responses FOR SELECT TO CURRENT_USER USING (true);
                CREATE POLICY social_answer_evidence_authority ON andril_social.social_questionnaire_answers FOR SELECT TO CURRENT_USER USING (true);

                CREATE FUNCTION andril_social.read_social_matching_evidence(caller uuid, exact_profile uuid, maximum integer)
                    RETURNS TABLE(profile_id uuid, profile_name text, date_of_birth date, gender text,
                        demographics_revision bigint, preferences_revision bigint, discoverable boolean,
                        show_me text, min_age integer, max_age integer, min_distance integer, max_distance integer,
                        languages text[], goals text[], partner_social text, partner_communication text, partner_planning text,
                        response_id uuid, questionnaire_version text, questionnaire_revision bigint, answers_json text,
                        latitude_cell numeric, longitude_cell numeric, location_observed timestamp with time zone, location_revision bigint,
                        safety_revision bigint, lifecycle_revision bigint)
                    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT profile.id, profile.display_name::text, demographics.date_of_birth, demographics.gender::text,
                        demographics.xmin::text::bigint, coalesce(preference.revision, 0), coalesce(preference.discoverable, false),
                        preference.show_me::text, preference.min_age, preference.max_age, preference.min_distance_km, preference.max_distance_km,
                        preference.languages, preference.connection_goals, preference.partner_social::text,
                        preference.partner_communication::text, preference.partner_planning::text,
                        response.id, response.version::text, coalesce(current_response.xmin::text::bigint, 0),
                        CASE WHEN response.status = 'Completed' THEN
                            (SELECT json_agg(json_build_object('QuestionId', answer.question_id, 'OptionId', answer.option_id)
                                ORDER BY answer.question_id)::text
                             FROM andril_social.social_questionnaire_answers answer WHERE answer.response_id = response.id)
                        ELSE NULL END,
                        location.latitude_cell, location.longitude_cell, location.observed_at_utc, coalesce(location.revision, 0),
                        safety.revision, lifecycle.revision
                    FROM andril_identity.private_module_profiles profile
                    JOIN andril_identity.account_demographics demographics ON demographics.account_id = profile.account_id
                    JOIN andril_identity.account_module_lifecycles lifecycle ON lifecycle.account_id = profile.account_id AND lifecycle.module = profile.module
                    LEFT JOIN andril_social.social_matching_preferences preference ON preference.profile_id = profile.id
                    LEFT JOIN andril_social.social_questionnaire_current_responses current_response ON current_response.profile_id = profile.id
                    LEFT JOIN andril_social.social_questionnaire_responses response ON response.id = current_response.response_id AND response.profile_id = profile.id
                    LEFT JOIN andril_social.social_location_evidence location ON location.profile_id = profile.id
                    CROSS JOIN LATERAL andril_social.social_safety_decision(caller, profile.id) safety
                    WHERE profile.module = 'social' AND profile.visibility = 'private' AND safety.decision = 'allowed'
                        AND demographics.date_of_birth <= (timezone('UTC', statement_timestamp())::date - interval '18 years')
                        AND (exact_profile IS NULL AND profile.id <> caller OR profile.id = exact_profile)
                        AND (profile.id = caller OR (preference.discoverable AND nullif(btrim(profile.display_name), '') IS NOT NULL))
                        AND maximum BETWEEN 1 AND 100
                    ORDER BY profile.id
                    LIMIT greatest(0, least(maximum, 100));
                $$;
                REVOKE ALL ON FUNCTION andril_social.read_social_matching_evidence(uuid, uuid, integer) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION andril_social.read_social_matching_evidence(uuid, uuid, integer);
                DROP POLICY social_answer_evidence_authority ON andril_social.social_questionnaire_answers;
                DROP POLICY social_response_evidence_authority ON andril_social.social_questionnaire_responses;
                DROP POLICY social_current_evidence_authority ON andril_social.social_questionnaire_current_responses;
                DROP POLICY social_preference_evidence_authority ON andril_social.social_matching_preferences;
                """);

            migrationBuilder.DropTable(
                name: "social_location_evidence",
                schema: "andril_social");
        }
    }
}
