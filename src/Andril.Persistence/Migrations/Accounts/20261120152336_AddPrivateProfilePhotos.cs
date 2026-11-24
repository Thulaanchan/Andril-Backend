using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Accounts
{
    /// <inheritdoc />
    public partial class AddPrivateProfilePhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_private_module_profiles_id_module",
                schema: "andril_identity",
                table: "private_module_profiles",
                columns: new[] { "id", "module" });

            migrationBuilder.CreateTable(
                name: "private_profile_photos",
                schema: "andril_identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_key = table.Column<string>(type: "character varying(88)", maxLength: 88, nullable: false),
                    original_content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    stored_content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    media_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    file_size_bytes = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    cleanup_pending = table.Column<bool>(type: "boolean", nullable: false),
                    cleanup_claim_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cleanup_claim_expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_cleanup_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    late_write_risk = table.Column<bool>(type: "boolean", nullable: false),
                    lease_token = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    superseded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_private_profile_photos", x => x.id);
                    table.CheckConstraint("ck_profile_photo_hashes", "original_content_hash COLLATE \"C\" ~ '^[0-9a-f]{64}$' AND stored_content_hash COLLATE \"C\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_profile_photo_ids", "id <> '00000000-0000-0000-0000-000000000000' AND request_id <> '00000000-0000-0000-0000-000000000000' AND id <> request_id");
                    table.CheckConstraint("ck_profile_photo_key", "object_key = module || '/' || profile_id::text || '/' || id::text || '.jpg'");
                    table.CheckConstraint("ck_profile_photo_cleanup", "cleanup_pending = (next_cleanup_attempt_at_utc IS NOT NULL) AND (next_cleanup_attempt_at_utc IS NULL OR (isfinite(next_cleanup_attempt_at_utc) AND next_cleanup_attempt_at_utc >= created_at_utc)) AND ((cleanup_claim_id IS NULL AND cleanup_claim_expires_at_utc IS NULL) OR (cleanup_pending AND cleanup_claim_id IS NOT NULL AND cleanup_claim_id <> '00000000-0000-0000-0000-000000000000' AND cleanup_claim_expires_at_utc IS NOT NULL AND isfinite(cleanup_claim_expires_at_utc) AND cleanup_claim_expires_at_utc > updated_at_utc))");
                    table.CheckConstraint("ck_profile_photo_late_write", "NOT late_write_risk OR state = 'PendingStorage' OR is_current OR cleanup_pending");
                    table.CheckConstraint("ck_profile_photo_media", "media_type = 'image/jpeg' AND file_size_bytes BETWEEN 1 AND 5242880 AND width BETWEEN 1 AND 1024 AND height BETWEEN 1 AND 1024");
                    table.CheckConstraint("ck_profile_photo_module", "module IN ('learning','hobbies','social')");
                    table.CheckConstraint("ck_profile_photo_state", "(state = 'PendingStorage' AND reason = 'None' AND NOT is_current\n  AND NOT cleanup_pending AND superseded_at_utc IS NULL\n  AND lease_token IS NOT NULL AND lease_until_utc IS NOT NULL)\nOR (state = 'Stored' AND reason = 'None'\n  AND is_current = (superseded_at_utc IS NULL) AND (NOT is_current OR NOT cleanup_pending)\n  AND lease_token IS NULL AND lease_until_utc IS NULL)\nOR (state = 'Failed' AND reason IN ('StorageFailure','UploadAbandoned','IntegrityFailure')\n  AND NOT is_current AND superseded_at_utc IS NULL\n  AND lease_token IS NULL AND lease_until_utc IS NULL)");
                    table.CheckConstraint("ck_profile_photo_times", "isfinite(created_at_utc) AND isfinite(updated_at_utc) AND updated_at_utc >= created_at_utc\nAND (superseded_at_utc IS NULL OR (isfinite(superseded_at_utc) AND superseded_at_utc BETWEEN created_at_utc AND updated_at_utc))\nAND (lease_until_utc IS NULL OR (isfinite(lease_until_utc) AND lease_until_utc > updated_at_utc))\nAND (lease_token IS NULL OR lease_token <> '00000000-0000-0000-0000-000000000000')");
                    table.ForeignKey(
                        name: "fk_profile_photo_profile_module",
                        columns: x => new { x.profile_id, x.module },
                        principalSchema: "andril_identity",
                        principalTable: "private_module_profiles",
                        principalColumns: new[] { "id", "module" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_profile_photo_cleanup",
                schema: "andril_identity",
                table: "private_profile_photos",
                columns: new[] { "profile_id", "module", "next_cleanup_attempt_at_utc", "id" },
                filter: "cleanup_pending");

            migrationBuilder.CreateIndex(
                name: "uq_profile_photo_current",
                schema: "andril_identity",
                table: "private_profile_photos",
                column: "profile_id",
                unique: true,
                filter: "is_current");

            migrationBuilder.CreateIndex(
                name: "uq_profile_photo_object",
                schema: "andril_identity",
                table: "private_profile_photos",
                column: "object_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_profile_photo_pending",
                schema: "andril_identity",
                table: "private_profile_photos",
                column: "profile_id",
                unique: true,
                filter: "state = 'PendingStorage'");

            migrationBuilder.CreateIndex(
                name: "uq_profile_photo_request",
                schema: "andril_identity",
                table: "private_profile_photos",
                columns: new[] { "profile_id", "module", "request_id" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE andril_identity.private_profile_photos ENABLE ROW LEVEL SECURITY;
                REVOKE ALL ON TABLE andril_identity.private_profile_photos FROM PUBLIC;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
                        REVOKE ALL ON TABLE andril_identity.private_profile_photos FROM anon;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
                        REVOKE ALL ON TABLE andril_identity.private_profile_photos FROM authenticated;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "private_profile_photos",
                schema: "andril_identity");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_private_module_profiles_id_module",
                schema: "andril_identity",
                table: "private_module_profiles");
        }
    }
}
