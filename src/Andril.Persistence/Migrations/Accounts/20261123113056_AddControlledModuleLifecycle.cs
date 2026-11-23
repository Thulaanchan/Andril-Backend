using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Accounts
{
    /// <inheritdoc />
    public partial class AddControlledModuleLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_module_lifecycles",
                schema: "andril_identity",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    state = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, collation: "C"),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    first_activated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "statement_timestamp()"),
                    state_changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "statement_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_module_lifecycles", x => new { x.account_id, x.module });
                    table.CheckConstraint("ck_account_module_lifecycles_module", "module IN ('learning', 'hobbies', 'social')");
                    table.CheckConstraint("ck_account_module_lifecycles_revision", "revision > 0");
                    table.CheckConstraint("ck_account_module_lifecycles_state", "state IN ('active', 'inactive')");
                    table.CheckConstraint("ck_account_module_lifecycles_timestamps", "isfinite(first_activated_at_utc) AND isfinite(state_changed_at_utc) AND state_changed_at_utc >= first_activated_at_utc");
                    table.ForeignKey(
                        name: "fk_account_module_lifecycles_accounts",
                        column: x => x.account_id,
                        principalSchema: "andril_identity",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_module_lifecycle_receipts",
                schema: "andril_identity",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    request_key = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, collation: "C"),
                    expected_revision = table.Column<long>(type: "bigint", nullable: true),
                    intent_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    state_changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "statement_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_module_lifecycle_receipts", x => new { x.account_id, x.module, x.request_key });
                    table.CheckConstraint("ck_lifecycle_receipt_intent", "intent_hash COLLATE \"C\" ~ '^[0-9A-F]{64}$'");
                    table.CheckConstraint("ck_lifecycle_receipt_key", "request_key <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("ck_lifecycle_receipt_revision", "revision > 0 AND ((operation = 'activate' AND expected_revision IS NULL) OR (operation = 'reactivate' AND expected_revision IS NOT NULL AND expected_revision > 0 AND expected_revision < 9223372036854775807))");
                    table.CheckConstraint("ck_lifecycle_receipt_time", "isfinite(recorded_at_utc) AND isfinite(state_changed_at_utc) AND recorded_at_utc >= state_changed_at_utc");
                    table.ForeignKey(
                        name: "FK_account_module_lifecycle_receipts_account_module_lifecycles~",
                        columns: x => new { x.account_id, x.module },
                        principalSchema: "andril_identity",
                        principalTable: "account_module_lifecycles",
                        principalColumns: new[] { "account_id", "module" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_module_lifecycle_audit",
                schema: "andril_identity",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    request_key = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, collation: "C"),
                    previous_state = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true, collation: "C"),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "statement_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_module_lifecycle_audit", x => new { x.account_id, x.module, x.revision });
                    table.CheckConstraint("ck_lifecycle_audit_correlation", "correlation_id <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("ck_lifecycle_audit_time", "isfinite(recorded_at_utc)");
                    table.CheckConstraint("ck_lifecycle_audit_transition", "revision > 0 AND ((operation = 'activate' AND previous_state IS NULL AND revision = 1) OR (operation = 'reactivate' AND previous_state IS NOT NULL AND previous_state = 'inactive' AND revision > 1))");
                    table.ForeignKey(
                        name: "FK_account_module_lifecycle_audit_account_module_lifecycle_rec~",
                        columns: x => new { x.account_id, x.module, x.request_key },
                        principalSchema: "andril_identity",
                        principalTable: "account_module_lifecycle_receipts",
                        principalColumns: new[] { "account_id", "module", "request_key" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_module_lifecycle_audit_account_id_module_request_key",
                schema: "andril_identity",
                table: "account_module_lifecycle_audit",
                columns: new[] { "account_id", "module", "request_key" },
                unique: true);

            // No runtime grants are made by this migration. The controlled test fixture
            // grants only its dedicated NOINHERIT/NOBYPASSRLS role. No security definer.
            foreach (var table in new[] { "account_module_lifecycles", "account_module_lifecycle_receipts", "account_module_lifecycle_audit" })
            {
                migrationBuilder.Sql($"""
                    REVOKE ALL ON andril_identity.{table} FROM PUBLIC;
                    ALTER TABLE andril_identity.{table} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE andril_identity.{table} FORCE ROW LEVEL SECURITY;
                    CREATE POLICY lifecycle_scope ON andril_identity.{table}
                    USING (account_id = nullif(current_setting('andril.account', true), '')::uuid
                        AND module = current_setting('andril.module', true)
                        AND EXISTS (SELECT 1 FROM andril_identity.accounts a WHERE a.id = account_id
                            AND a.supabase_user_id = nullif(current_setting('andril.subject', true), '')::uuid))
                    WITH CHECK (account_id = nullif(current_setting('andril.account', true), '')::uuid
                        AND module = current_setting('andril.module', true)
                        AND EXISTS (SELECT 1 FROM andril_identity.accounts a WHERE a.id = account_id
                            AND a.supabase_user_id = nullif(current_setting('andril.subject', true), '')::uuid));
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_module_lifecycle_audit",
                schema: "andril_identity");

            migrationBuilder.DropTable(
                name: "account_module_lifecycle_receipts",
                schema: "andril_identity");

            migrationBuilder.DropTable(
                name: "account_module_lifecycles",
                schema: "andril_identity");
        }
    }
}
