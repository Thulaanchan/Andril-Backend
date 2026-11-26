using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.HobbiesMap
{
    /// <inheritdoc />
    public partial class AddMapPrivateMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "map_private_media",
                schema: "andril_hobbies",
                columns: table => new
                {
                    media_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capture_challenge = table.Column<Guid>(type: "uuid", nullable: false),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    upload_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    private_reference = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<string>(type: "text", nullable: true),
                    sha256 = table.Column<string>(type: "text", nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finalized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cleanup_claim = table.Column<Guid>(type: "uuid", nullable: true),
                    cleaned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_private_media", x => x.media_id);
                    table.CheckConstraint("ck_map_media_cleanup_state", "cleaned_at IS NULL OR (cleanup_claim IS NOT NULL AND finalized_at IS NULL)");
                    table.CheckConstraint("ck_map_media_evidence_state", "(finalized_at IS NULL AND private_reference IS NULL AND version IS NULL AND sha256 IS NULL AND captured_at IS NULL)\nOR (finalized_at IS NOT NULL AND private_reference IS NOT NULL AND length(private_reference) > 0\n    AND version IS NOT NULL AND length(version) > 0 AND sha256 IS NOT NULL\n    AND sha256 ~ '^[0-9A-Fa-f]{64}$' AND captured_at IS NOT NULL\n    AND captured_at <= finalized_at AND cleanup_claim IS NULL)");
                    table.CheckConstraint("ck_map_media_upload_window", "upload_expires_at > reserved_at AND upload_expires_at <= reserved_at + interval '120 seconds'");
                    table.ForeignKey(
                        name: "fk_map_private_media_session_scope",
                        columns: x => new { x.session_id, x.profile_id, x.attraction_id },
                        principalSchema: "andril_hobbies",
                        principalTable: "map_contribution_capture_sessions",
                        principalColumns: new[] { "session_id", "owner_hobbies_module_profile_id", "attraction_id" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_map_private_media_session_id_profile_id_attraction_id",
                schema: "andril_hobbies",
                table: "map_private_media",
                columns: new[] { "session_id", "profile_id", "attraction_id" });

            migrationBuilder.CreateIndex(
                name: "ux_map_private_media_challenge",
                schema: "andril_hobbies",
                table: "map_private_media",
                column: "capture_challenge",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_map_private_media_reference",
                schema: "andril_hobbies",
                table: "map_private_media",
                column: "private_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_map_private_media_session",
                schema: "andril_hobbies",
                table: "map_private_media",
                column: "session_id",
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION andril_hobbies.map_private_media_guard() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Map media bindings require an approved retention operation'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_map_private_media_immutable';
                    END IF;
                    IF OLD.finalized_at IS NOT NULL OR OLD.cleaned_at IS NOT NULL
                        OR (OLD.media_id, OLD.subject_id, OLD.profile_id, OLD.attraction_id, OLD.session_id,
                            OLD.capture_challenge, OLD.reserved_at, OLD.upload_expires_at)
                            IS DISTINCT FROM
                           (NEW.media_id, NEW.subject_id, NEW.profile_id, NEW.attraction_id, NEW.session_id,
                            NEW.capture_challenge, NEW.reserved_at, NEW.upload_expires_at)
                        OR (OLD.cleanup_claim IS NOT NULL AND OLD.cleanup_claim IS DISTINCT FROM NEW.cleanup_claim)
                    THEN
                        RAISE EXCEPTION 'Map media binding cannot be replaced'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_map_private_media_immutable';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER tr_map_private_media_guard
                    BEFORE UPDATE OR DELETE ON andril_hobbies.map_private_media
                    FOR EACH ROW EXECUTE FUNCTION andril_hobbies.map_private_media_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Map private media is forward-only to preserve evidence and cleanup records.");
        }
    }
}
