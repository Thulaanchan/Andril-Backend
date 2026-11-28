using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Accounts
{
    /// <inheritdoc />
    public partial class BindProfileMetadataOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_profile_locations_profiles",
                schema: "andril_identity",
                table: "account_profile_locations");

            migrationBuilder.DropForeignKey(
                name: "fk_additional_photos_profiles",
                schema: "andril_identity",
                table: "profile_additional_photos");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_private_module_profiles_id_account_id_module",
                schema: "andril_identity",
                table: "private_module_profiles",
                columns: new[] { "id", "account_id", "module" });

            migrationBuilder.CreateIndex(
                name: "IX_profile_additional_photos_profile_id_account_id_module",
                schema: "andril_identity",
                table: "profile_additional_photos",
                columns: new[] { "profile_id", "account_id", "module" });

            migrationBuilder.CreateIndex(
                name: "IX_account_profile_locations_profile_id_account_id_module",
                schema: "andril_identity",
                table: "account_profile_locations",
                columns: new[] { "profile_id", "account_id", "module" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_profile_locations_profiles",
                schema: "andril_identity",
                table: "account_profile_locations",
                columns: new[] { "profile_id", "account_id", "module" },
                principalSchema: "andril_identity",
                principalTable: "private_module_profiles",
                principalColumns: new[] { "id", "account_id", "module" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_additional_photos_profiles",
                schema: "andril_identity",
                table: "profile_additional_photos",
                columns: new[] { "profile_id", "account_id", "module" },
                principalSchema: "andril_identity",
                principalTable: "private_module_profiles",
                principalColumns: new[] { "id", "account_id", "module" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_profile_locations_profiles",
                schema: "andril_identity",
                table: "account_profile_locations");

            migrationBuilder.DropForeignKey(
                name: "fk_additional_photos_profiles",
                schema: "andril_identity",
                table: "profile_additional_photos");

            migrationBuilder.DropIndex(
                name: "IX_profile_additional_photos_profile_id_account_id_module",
                schema: "andril_identity",
                table: "profile_additional_photos");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_private_module_profiles_id_account_id_module",
                schema: "andril_identity",
                table: "private_module_profiles");

            migrationBuilder.DropIndex(
                name: "IX_account_profile_locations_profile_id_account_id_module",
                schema: "andril_identity",
                table: "account_profile_locations");

            migrationBuilder.AddForeignKey(
                name: "fk_profile_locations_profiles",
                schema: "andril_identity",
                table: "account_profile_locations",
                column: "profile_id",
                principalSchema: "andril_identity",
                principalTable: "private_module_profiles",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_additional_photos_profiles",
                schema: "andril_identity",
                table: "profile_additional_photos",
                column: "profile_id",
                principalSchema: "andril_identity",
                principalTable: "private_module_profiles",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
