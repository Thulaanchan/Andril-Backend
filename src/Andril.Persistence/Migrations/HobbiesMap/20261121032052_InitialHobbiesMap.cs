using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.HobbiesMap
{
    /// <inheritdoc />
    public partial class InitialHobbiesMap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "andril_hobbies");

            migrationBuilder.CreateTable(
                name: "flight_point_ledger",
                schema: "andril_hobbies",
                columns: table => new
                {
                    ledger_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_hobbies_module_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contribution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_type = table.Column<int>(type: "integer", nullable: false),
                    reward_type = table.Column<int>(type: "integer", nullable: false),
                    points_delta = table.Column<int>(type: "integer", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    reversed_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_flight_point_ledger", x => x.ledger_entry_id);
                    table.CheckConstraint("ck_flight_point_ledger_attraction_id", "attraction_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_flight_point_ledger_contribution_id", "contribution_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_flight_point_ledger_entry_type", "entry_type IN (1, 2)");
                    table.CheckConstraint("ck_flight_point_ledger_id", "ledger_entry_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_flight_point_ledger_idempotency_key", "idempotency_key !~ '^[[:space:]]*$'");
                    table.CheckConstraint("ck_flight_point_ledger_owner_id", "owner_hobbies_module_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_flight_point_ledger_points_delta", """
                        (entry_type = 1 AND reward_type = 1 AND points_delta = 5)
                        OR (entry_type = 1 AND reward_type = 2 AND points_delta = 10)
                        OR (entry_type = 2 AND reward_type = 1 AND points_delta = -5)
                        OR (entry_type = 2 AND reward_type = 2 AND points_delta = -10)
                        """);
                    table.CheckConstraint("ck_flight_point_ledger_reversal_reference", """
                        (entry_type = 1 AND reversed_entry_id IS NULL)
                        OR (entry_type = 2 AND reversed_entry_id IS NOT NULL
                            AND reversed_entry_id <> '00000000-0000-0000-0000-000000000000'::uuid AND reversed_entry_id <> ledger_entry_id)
                        """);
                    table.CheckConstraint("ck_flight_point_ledger_reward_type", "reward_type IN (1, 2)");
                    table.ForeignKey(
                        name: "fk_flight_point_ledger_reversed_entry",
                        column: x => x.reversed_entry_id,
                        principalTable: "flight_point_ledger",
                        principalSchema: "andril_hobbies",
                        principalColumn: "ledger_entry_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "map_attractions",
                schema: "andril_hobbies",
                columns: table => new
                {
                    attraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    latitude = table.Column<double>(type: "double precision", nullable: false),
                    longitude = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_attractions", x => x.attraction_id);
                    table.CheckConstraint("ck_map_attraction_id", "attraction_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_map_attraction_latitude", "latitude >= -90 AND latitude <= 90 AND latitude <> 'NaN'::double precision");
                    table.CheckConstraint("ck_map_attraction_longitude", "longitude >= -180 AND longitude <= 180 AND longitude <> 'NaN'::double precision");
                    table.CheckConstraint("ck_map_attraction_name", "char_length(btrim(name)) > 0 AND name !~ '^[[:space:]]*$'");
                });

            migrationBuilder.CreateTable(
                name: "map_contribution_capture_sessions",
                schema: "andril_hobbies",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_hobbies_module_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_map_contribution_capture_sessions", x => x.session_id);
                    table.CheckConstraint("ck_map_capture_attraction_id", "attraction_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_map_capture_owner_id", "owner_hobbies_module_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_map_capture_session_id", "session_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_map_capture_timestamp_order", "completed_at_utc IS NULL OR completed_at_utc >= started_at_utc");
                });

            migrationBuilder.CreateTable(
                name: "map_contributions",
                schema: "andril_hobbies",
                columns: table => new
                {
                    contribution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_hobbies_module_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attraction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capture_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    private_photo_storage_object_key = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    location_evidence = table.Column<string>(type: "jsonb", nullable: false),
                    submitted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    moderation_status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_contributions", x => x.contribution_id);
                    table.CheckConstraint("ck_map_contribution_attraction_id", "attraction_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_map_contribution_capture_session_id", "capture_session_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_map_contribution_description", "description !~ '^[[:space:]]*$'");
                    table.CheckConstraint("ck_map_contribution_id", "contribution_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_map_contribution_location_evidence", "jsonb_typeof(location_evidence) = 'object'");
                    table.CheckConstraint("ck_map_contribution_moderation_status", "moderation_status = 1");
                    table.CheckConstraint("ck_map_contribution_owner_id", "owner_hobbies_module_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_map_contribution_photo_key", "private_photo_storage_object_key !~ '^[[:space:]]*$'");
                });

            migrationBuilder.CreateIndex(
                name: "ux_flight_point_ledger_reversed_entry",
                table: "flight_point_ledger",
                schema: "andril_hobbies",
                column: "reversed_entry_id",
                unique: true,
                filter: "entry_type = 2");

            migrationBuilder.CreateIndex(
                name: "ix_map_capture_owner_attraction",
                table: "map_contribution_capture_sessions",
                schema: "andril_hobbies",
                columns: new[] { "owner_hobbies_module_profile_id", "attraction_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "flight_point_ledger",
                schema: "andril_hobbies");

            migrationBuilder.DropTable(
                name: "map_attractions",
                schema: "andril_hobbies");

            migrationBuilder.DropTable(
                name: "map_contribution_capture_sessions",
                schema: "andril_hobbies");

            migrationBuilder.DropTable(
                name: "map_contributions",
                schema: "andril_hobbies");
        }
    }
}
