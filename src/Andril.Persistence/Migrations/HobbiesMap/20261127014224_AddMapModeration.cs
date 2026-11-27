using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.HobbiesMap
{
    /// <inheritdoc />
    public partial class AddMapModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_map_contributions_contribution_id_attraction_id_owner_hobbi~",
                schema: "andril_hobbies",
                table: "map_contributions",
                columns: new[] { "contribution_id", "attraction_id", "owner_hobbies_module_profile_id", "capture_session_id" });

            migrationBuilder.CreateTable(
                name: "map_moderation_receipts",
                schema: "andril_hobbies",
                columns: table => new
                {
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contribution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewer_subject = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<int>(type: "integer", nullable: false),
                    expected_revision = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    media_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_moderation_receipts", x => x.decision_id);
                    table.CheckConstraint("ck_map_moderation_action", "(action = 1 AND media_id IS NOT NULL) OR (action = 2 AND media_id IS NULL)");
                    table.CheckConstraint("ck_map_moderation_identity", "decision_id <> '00000000-0000-0000-0000-000000000000' AND reviewer_subject <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("ck_map_moderation_revision", "expected_revision = 0 AND revision = 1");
                    table.ForeignKey(
                        name: "fk_map_moderation_media",
                        column: x => x.media_id,
                        principalSchema: "andril_hobbies",
                        principalTable: "map_private_media",
                        principalColumn: "media_id");
                    table.ForeignKey(
                        name: "fk_map_moderation_scope",
                        columns: x => new { x.contribution_id, x.attraction_id, x.profile_id, x.session_id },
                        principalSchema: "andril_hobbies",
                        principalTable: "map_contributions",
                        principalColumns: new[] { "contribution_id", "attraction_id", "owner_hobbies_module_profile_id", "capture_session_id" });
                });

            migrationBuilder.CreateTable(
                name: "map_moderation_audit",
                schema: "andril_hobbies",
                columns: table => new
                {
                    decision_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_moderation_audit", x => x.decision_id);
                    table.ForeignKey(
                        name: "fk_map_moderation_audit_receipt",
                        column: x => x.decision_id,
                        principalSchema: "andril_hobbies",
                        principalTable: "map_moderation_receipts",
                        principalColumn: "decision_id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_map_moderation_receipts_contribution_id_attraction_id_profi~",
                schema: "andril_hobbies",
                table: "map_moderation_receipts",
                columns: new[] { "contribution_id", "attraction_id", "profile_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "IX_map_moderation_receipts_media_id",
                schema: "andril_hobbies",
                table: "map_moderation_receipts",
                column: "media_id");

            migrationBuilder.CreateIndex(
                name: "ux_map_moderation_contribution",
                schema: "andril_hobbies",
                table: "map_moderation_receipts",
                column: "contribution_id",
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE andril_hobbies.map_moderation_receipts
                    ADD CONSTRAINT fk_map_moderation_receipt_audit
                    FOREIGN KEY (decision_id) REFERENCES andril_hobbies.map_moderation_audit (decision_id)
                    DEFERRABLE INITIALLY DEFERRED;
                CREATE FUNCTION andril_hobbies.map_moderation_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Map moderation evidence is immutable'
                        USING ERRCODE = '23514', CONSTRAINT = 'ck_map_moderation_immutable';
                END;
                $$;
                CREATE TRIGGER map_moderation_receipt_immutable BEFORE UPDATE OR DELETE
                    ON andril_hobbies.map_moderation_receipts FOR EACH ROW
                    EXECUTE FUNCTION andril_hobbies.map_moderation_immutable();
                CREATE TRIGGER map_moderation_audit_immutable BEFORE UPDATE OR DELETE
                    ON andril_hobbies.map_moderation_audit FOR EACH ROW
                    EXECUTE FUNCTION andril_hobbies.map_moderation_immutable();
                CREATE FUNCTION andril_hobbies.map_moderation_validate_media() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.action = 1 AND NOT EXISTS (
                        SELECT 1 FROM andril_hobbies.map_private_media m
                        JOIN andril_hobbies.map_contributions c ON c.contribution_id = NEW.contribution_id
                        WHERE m.media_id = NEW.media_id AND m.profile_id = NEW.profile_id
                            AND m.attraction_id = NEW.attraction_id AND m.session_id = NEW.session_id
                            AND m.finalized_at IS NOT NULL AND m.cleanup_claim IS NULL
                            AND m.private_reference = c.private_photo_storage_object_key
                    ) THEN
                        RAISE EXCEPTION 'Approval requires finalized contribution evidence'
                            USING ERRCODE = '23514', CONSTRAINT = 'ck_map_moderation_finalized_media';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER map_moderation_validate_media BEFORE INSERT
                    ON andril_hobbies.map_moderation_receipts FOR EACH ROW
                    EXECUTE FUNCTION andril_hobbies.map_moderation_validate_media();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Map contribution moderation is a forward-only migration.");
        }
    }
}
