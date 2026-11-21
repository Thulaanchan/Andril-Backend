using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.HobbiesGallery
{
    /// <inheritdoc />
    public partial class InitialHobbiesGallery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "andril_hobbies");

            migrationBuilder.CreateTable(
                name: "GalleryMedia",
                schema: "andril_hobbies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerModuleProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Module = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: false),
                    MediaType = table.Column<string>(type: "text", nullable: false),
                    StorageObjectKey = table.Column<string>(type: "text", nullable: false),
                    IsPrivate = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GalleryMedia", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GalleryPreferences",
                schema: "andril_hobbies",
                columns: table => new
                {
                    OwnerModuleProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    CoverMediaId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GalleryPreferences", x => x.OwnerModuleProfileId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GalleryMedia_OwnerModuleProfileId_Module_IsDeleted_CreatedA~",
                table: "GalleryMedia",
                schema: "andril_hobbies",
                columns: new[] { "OwnerModuleProfileId", "Module", "IsDeleted", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GalleryMedia",
                schema: "andril_hobbies");

            migrationBuilder.DropTable(
                name: "GalleryPreferences",
                schema: "andril_hobbies");
        }
    }
}
