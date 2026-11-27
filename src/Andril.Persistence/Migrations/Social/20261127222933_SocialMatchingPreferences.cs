using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Social
{
    /// <inheritdoc />
    public partial class SocialMatchingPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "social_matching_preferences",
                schema: "andril_social",
                columns: table => new
                {
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    discoverable = table.Column<bool>(type: "boolean", nullable: false),
                    show_me = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    min_age = table.Column<int>(type: "integer", nullable: false),
                    max_age = table.Column<int>(type: "integer", nullable: false),
                    min_distance_km = table.Column<int>(type: "integer", nullable: false),
                    max_distance_km = table.Column<int>(type: "integer", nullable: false),
                    languages = table.Column<string[]>(type: "text[]", nullable: false),
                    connection_goals = table.Column<string[]>(type: "text[]", nullable: false),
                    partner_social = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    partner_communication = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    partner_planning = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_matching_preferences", x => x.profile_id);
                    table.CheckConstraint("ck_social_matching_preferences_age", "min_age >= 18 AND max_age <= 99 AND min_age <= max_age");
                    table.CheckConstraint("ck_social_matching_preferences_distance", "min_distance_km >= 0 AND max_distance_km <= 999 AND min_distance_km <= max_distance_km");
                    table.CheckConstraint("ck_social_matching_preferences_gender", "show_me IN ('All', 'Men', 'Women')");
                    table.CheckConstraint("ck_social_matching_preferences_module", "module = 'social'");
                    table.CheckConstraint("ck_social_matching_preferences_partner", "num_nonnulls(partner_social, partner_communication, partner_planning) = 0 OR\n(num_nonnulls(partner_social, partner_communication, partner_planning) = 3\n    AND partner_social IN ('Either', 'Reserved', 'Outgoing')\n    AND partner_communication IN ('Either', 'LessExpressive', 'Expressive')\n    AND partner_planning IN ('Either', 'Spontaneous', 'Structured'))");
                    table.CheckConstraint("ck_social_matching_preferences_revision", "revision > 0");
                });

            migrationBuilder.Sql("""
                ALTER TABLE andril_social.social_matching_preferences
                    ADD CONSTRAINT fk_social_matching_preferences_profile
                        FOREIGN KEY (profile_id, module) REFERENCES andril_identity.private_module_profiles(id, module)
                        ON UPDATE RESTRICT ON DELETE RESTRICT;
                REVOKE ALL ON andril_social.social_matching_preferences FROM PUBLIC;
                ALTER TABLE andril_social.social_matching_preferences ENABLE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_matching_preferences FORCE ROW LEVEL SECURITY;
                CREATE POLICY social_matching_preferences_owner_select ON andril_social.social_matching_preferences
                    FOR SELECT USING (andril_social.questionnaire_owner_visible(profile_id));
                CREATE POLICY social_matching_preferences_owner_insert ON andril_social.social_matching_preferences
                    FOR INSERT WITH CHECK (andril_social.questionnaire_owner_visible(profile_id));
                CREATE POLICY social_matching_preferences_owner_update ON andril_social.social_matching_preferences
                    FOR UPDATE USING (andril_social.questionnaire_owner_visible(profile_id))
                    WITH CHECK (andril_social.questionnaire_owner_visible(profile_id));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "social_matching_preferences",
                schema: "andril_social");
        }
    }
}
