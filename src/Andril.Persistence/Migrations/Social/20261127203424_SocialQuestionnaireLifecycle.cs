using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Social
{
    /// <inheritdoc />
    public partial class SocialQuestionnaireLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_social_questionnaire_responses_profile_id_id",
                schema: "andril_social",
                table: "social_questionnaire_responses",
                columns: new[] { "profile_id", "id" });

            migrationBuilder.CreateTable(
                name: "social_questionnaire_current_responses",
                schema: "andril_social",
                columns: table => new
                {
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    response_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_questionnaire_current_responses", x => x.profile_id);
                    table.CheckConstraint("ck_social_questionnaire_current_module", "module = 'social'");
                    table.ForeignKey(
                        name: "fk_social_questionnaire_current_response",
                        columns: x => new { x.profile_id, x.response_id },
                        principalSchema: "andril_social",
                        principalTable: "social_questionnaire_responses",
                        principalColumns: new[] { "profile_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_social_questionnaire_current_responses_profile_id_response_~",
                schema: "andril_social",
                table: "social_questionnaire_current_responses",
                columns: new[] { "profile_id", "response_id" });

            migrationBuilder.Sql("""
                ALTER TABLE andril_social.social_questionnaire_current_responses
                    ADD CONSTRAINT fk_social_questionnaire_current_social_profile
                    FOREIGN KEY (profile_id, module) REFERENCES andril_identity.private_module_profiles (id, module)
                    ON UPDATE RESTRICT ON DELETE RESTRICT;

                CREATE FUNCTION andril_social.questionnaire_owner_visible(owner_profile uuid) RETURNS boolean
                LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog AS $$
                    SELECT current_setting('andril.module', true) = 'social' AND EXISTS (
                        SELECT 1 FROM andril_identity.private_module_profiles profile
                        JOIN andril_identity.accounts account ON account.id = profile.account_id
                        JOIN andril_identity.account_starter_memberships starter
                            ON starter.account_id = account.id AND starter.module = profile.module
                        JOIN andril_identity.account_module_lifecycles lifecycle
                            ON lifecycle.account_id = account.id AND lifecycle.module = profile.module
                        WHERE profile.id = owner_profile AND profile.module = 'social'
                            AND starter.social_opt_in_at_utc IS NOT NULL AND lifecycle.state = 'active'
                            AND account.id::text = current_setting('andril.account', true)
                            AND account.supabase_user_id::text = current_setting('andril.subject', true));
                $$;
                REVOKE ALL ON FUNCTION andril_social.questionnaire_owner_visible(uuid) FROM PUBLIC;

                REVOKE ALL ON andril_social.social_questionnaire_responses,
                    andril_social.social_questionnaire_answers, andril_social.social_questionnaire_current_responses FROM PUBLIC;
                ALTER TABLE andril_social.social_questionnaire_responses ENABLE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_questionnaire_responses FORCE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_questionnaire_answers ENABLE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_questionnaire_answers FORCE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_questionnaire_current_responses ENABLE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_questionnaire_current_responses FORCE ROW LEVEL SECURITY;

                CREATE POLICY questionnaire_response_select ON andril_social.social_questionnaire_responses
                    FOR SELECT USING (andril_social.questionnaire_owner_visible(profile_id));
                CREATE POLICY questionnaire_response_insert ON andril_social.social_questionnaire_responses
                    FOR INSERT WITH CHECK (andril_social.questionnaire_owner_visible(profile_id));
                CREATE POLICY questionnaire_answer_select ON andril_social.social_questionnaire_answers
                    FOR SELECT USING (EXISTS (SELECT 1 FROM andril_social.social_questionnaire_responses response
                        WHERE response.id = social_questionnaire_answers.response_id));
                CREATE POLICY questionnaire_answer_insert ON andril_social.social_questionnaire_answers
                    FOR INSERT WITH CHECK (EXISTS (SELECT 1 FROM andril_social.social_questionnaire_responses response
                        WHERE response.id = social_questionnaire_answers.response_id));
                CREATE POLICY questionnaire_current_select ON andril_social.social_questionnaire_current_responses
                    FOR SELECT USING (andril_social.questionnaire_owner_visible(profile_id));
                CREATE POLICY questionnaire_current_insert ON andril_social.social_questionnaire_current_responses
                    FOR INSERT WITH CHECK (andril_social.questionnaire_owner_visible(profile_id));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY questionnaire_current_insert ON andril_social.social_questionnaire_current_responses;
                DROP POLICY questionnaire_current_select ON andril_social.social_questionnaire_current_responses;
                DROP POLICY questionnaire_answer_insert ON andril_social.social_questionnaire_answers;
                DROP POLICY questionnaire_answer_select ON andril_social.social_questionnaire_answers;
                DROP POLICY questionnaire_response_insert ON andril_social.social_questionnaire_responses;
                DROP POLICY questionnaire_response_select ON andril_social.social_questionnaire_responses;
                ALTER TABLE andril_social.social_questionnaire_responses NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_questionnaire_responses DISABLE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_questionnaire_answers NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE andril_social.social_questionnaire_answers DISABLE ROW LEVEL SECURITY;
                DROP FUNCTION andril_social.questionnaire_owner_visible(uuid);
                """);

            migrationBuilder.DropTable(
                name: "social_questionnaire_current_responses",
                schema: "andril_social");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_social_questionnaire_responses_profile_id_id",
                schema: "andril_social",
                table: "social_questionnaire_responses");
        }
    }
}
