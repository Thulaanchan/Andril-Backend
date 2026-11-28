using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Accounts
{
    /// <inheritdoc />
    public partial class AddProfileLocationsPhotosAndPrivacySettings : Migration
    {
        private const string Schema = "andril_identity";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_profile_locations",
                schema: Schema,
                columns: table => new
                {
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    region = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_profile_locations", x => x.profile_id);
                    table.CheckConstraint("ck_profile_location_city_nonempty", "length(trim(city)) > 0");
                    table.CheckConstraint("ck_profile_location_module", "module IN ('learning', 'hobbies', 'social')");
                    table.CheckConstraint("ck_profile_location_profile_id", "profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_profile_location_timestamps", "updated_at_utc >= created_at_utc");
                    table.ForeignKey(
                        name: "fk_profile_locations_profiles",
                        column: x => x.profile_id,
                        principalSchema: Schema,
                        principalTable: "private_module_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "profile_additional_photos",
                schema: Schema,
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    slot_index = table.Column<int>(type: "integer", nullable: false),
                    object_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    media_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    file_size_bytes = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_profile_additional_photos", x => x.id);
                    table.CheckConstraint("ck_additional_photo_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_additional_photo_module", "module IN ('learning', 'hobbies', 'social')");
                    table.CheckConstraint("ck_additional_photo_size", "file_size_bytes BETWEEN 1 AND 5242880");
                    table.CheckConstraint("ck_additional_photo_slot", "slot_index BETWEEN 1 AND 3");
                    table.CheckConstraint("ck_additional_photo_timestamps", "updated_at_utc >= created_at_utc");
                    table.ForeignKey(
                        name: "fk_additional_photos_profiles",
                        column: x => x.profile_id,
                        principalSchema: Schema,
                        principalTable: "private_module_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "account_privacy_settings",
                schema: Schema,
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_discoverability = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ghost_mode_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    direct_messaging_policy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    allow_friend_requests = table.Column<bool>(type: "boolean", nullable: false),
                    show_active_module = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_privacy_settings", x => x.account_id);
                    table.CheckConstraint("ck_privacy_account_id_nonempty", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("ck_privacy_discoverability", "profile_discoverability IN ('public', 'contacts_only', 'private')");
                    table.CheckConstraint("ck_privacy_messaging", "direct_messaging_policy IN ('everyone', 'friends_only', 'disabled')");
                    table.CheckConstraint("ck_privacy_timestamps", "updated_at_utc >= created_at_utc");
                    table.ForeignKey(
                        name: "fk_account_privacy_settings_accounts",
                        column: x => x.account_id,
                        principalSchema: Schema,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_profile_locations_account_module",
                schema: Schema,
                table: "account_profile_locations",
                columns: new[] { "account_id", "module" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_additional_photos_account_module",
                schema: Schema,
                table: "profile_additional_photos",
                columns: new[] { "account_id", "module", "slot_index" });

            migrationBuilder.CreateIndex(
                name: "uq_additional_photos_profile_slot",
                schema: Schema,
                table: "profile_additional_photos",
                columns: new[] { "profile_id", "slot_index" },
                unique: true);

            migrationBuilder.Sql("ALTER TABLE andril_identity.account_profile_locations ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE andril_identity.profile_additional_photos ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE andril_identity.account_privacy_settings ENABLE ROW LEVEL SECURITY;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_privacy_settings",
                schema: Schema);

            migrationBuilder.DropTable(
                name: "profile_additional_photos",
                schema: Schema);

            migrationBuilder.DropTable(
                name: "account_profile_locations",
                schema: Schema);
        }
    }
}
