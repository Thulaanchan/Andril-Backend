using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.HobbiesMap
{
    /// <inheritdoc />
    public partial class AddMapSubmissionSessionUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_map_capture_session_scope",
                schema: "andril_hobbies",
                table: "map_contribution_capture_sessions",
                columns: new[] { "session_id", "owner_hobbies_module_profile_id", "attraction_id" });

            migrationBuilder.CreateIndex(
                name: "IX_map_contributions_capture_session_id_owner_hobbies_module_p~",
                schema: "andril_hobbies",
                table: "map_contributions",
                columns: new[] { "capture_session_id", "owner_hobbies_module_profile_id", "attraction_id" });

            migrationBuilder.CreateIndex(
                name: "ux_map_contributions_capture_session_id",
                schema: "andril_hobbies",
                table: "map_contributions",
                column: "capture_session_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_map_contributions_capture_session_scope",
                schema: "andril_hobbies",
                table: "map_contributions",
                columns: new[] { "capture_session_id", "owner_hobbies_module_profile_id", "attraction_id" },
                principalSchema: "andril_hobbies",
                principalTable: "map_contribution_capture_sessions",
                principalColumns: new[] { "session_id", "owner_hobbies_module_profile_id", "attraction_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_map_contributions_capture_session_scope",
                schema: "andril_hobbies",
                table: "map_contributions");

            migrationBuilder.DropIndex(
                name: "IX_map_contributions_capture_session_id_owner_hobbies_module_p~",
                schema: "andril_hobbies",
                table: "map_contributions");

            migrationBuilder.DropIndex(
                name: "ux_map_contributions_capture_session_id",
                schema: "andril_hobbies",
                table: "map_contributions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_map_capture_session_scope",
                schema: "andril_hobbies",
                table: "map_contribution_capture_sessions");
        }
    }
}
