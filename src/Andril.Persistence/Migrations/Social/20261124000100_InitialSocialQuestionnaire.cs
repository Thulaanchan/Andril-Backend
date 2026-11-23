using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Social
{
    /// <inheritdoc />
    public partial class InitialSocialQuestionnaire : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "andril_social");

            migrationBuilder.CreateTable(
                name: "social_questionnaire_responses",
                schema: "andril_social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    row_version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_questionnaire_responses", x => x.id);
                    table.CheckConstraint("ck_questionnaire_profile_id", "profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_questionnaire_response_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_questionnaire_timestamp", "isfinite(created_at_utc)");
                });

            migrationBuilder.CreateTable(
                name: "social_questionnaire_answers",
                schema: "andril_social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    response_id = table.Column<Guid>(type: "uuid", nullable: false),
                    question_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    option_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    answered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_social_questionnaire_answers", x => x.id);
                    table.CheckConstraint("ck_questionnaire_answer_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_questionnaire_answer_response_id", "response_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_questionnaire_answer_timestamp", "isfinite(answered_at_utc)");
                    table.ForeignKey(
                        name: "fk_social_questionnaire_answers_response",
                        column: x => x.response_id,
                        principalSchema: "andril_social",
                        principalTable: "social_questionnaire_responses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_social_questionnaire_answers_response",
                schema: "andril_social",
                table: "social_questionnaire_answers",
                column: "response_id");

            migrationBuilder.CreateIndex(
                name: "ix_social_questionnaire_profile_version",
                schema: "andril_social",
                table: "social_questionnaire_responses",
                columns: new[] { "profile_id", "version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "social_questionnaire_answers",
                schema: "andril_social");

            migrationBuilder.DropTable(
                name: "social_questionnaire_responses",
                schema: "andril_social");
        }
    }
}
