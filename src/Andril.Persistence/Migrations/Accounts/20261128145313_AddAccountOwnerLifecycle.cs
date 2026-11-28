using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Accounts
{
    /// <inheritdoc />
    public partial class AddAccountOwnerLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_owner_states",
                schema: "andril_identity",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_owner_states", x => x.account_id);
                    table.CheckConstraint("ck_owner_revision", "revision > 0");
                    table.CheckConstraint("ck_owner_state", "state IN ('Active','Suspended','Deactivated','Deleted')");
                    table.CheckConstraint("ck_owner_time", "isfinite(changed_at_utc)");
                    table.ForeignKey(
                        name: "FK_account_owner_states_accounts_account_id",
                        column: x => x.account_id,
                        principalSchema: "andril_identity",
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_owner_receipts",
                schema: "andril_identity",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_key = table.Column<Guid>(type: "uuid", nullable: false),
                    intent_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, collation: "C"),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_owner_receipts", x => new { x.account_id, x.request_key });
                    table.CheckConstraint("ck_owner_receipt", "request_key <> '00000000-0000-0000-0000-000000000000'::uuid AND revision > 1 AND state IN ('Active','Deactivated','Deleted') AND intent_hash COLLATE \"C\" ~ '^[0-9A-F]{64}$' AND isfinite(changed_at_utc)");
                    table.ForeignKey(
                        name: "FK_account_owner_receipts_account_owner_states_account_id",
                        column: x => x.account_id,
                        principalSchema: "andril_identity",
                        principalTable: "account_owner_states",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_owner_audit",
                schema: "andril_identity",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    request_key = table.Column<Guid>(type: "uuid", nullable: false),
                    operation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    previous_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, collation: "C"),
                    changed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_owner_audit", x => new { x.account_id, x.revision });
                    table.CheckConstraint("ck_owner_audit", "revision > 1 AND isfinite(changed_at_utc) AND ((operation = 'Deactivate' AND previous_state = 'Active' AND state = 'Deactivated') OR (operation = 'Reactivate' AND previous_state = 'Deactivated' AND state = 'Active') OR (operation = 'Delete' AND previous_state IN ('Active','Deactivated') AND state = 'Deleted'))");
                    table.ForeignKey(
                        name: "FK_account_owner_audit_account_owner_receipts_account_id_reque~",
                        columns: x => new { x.account_id, x.request_key },
                        principalSchema: "andril_identity",
                        principalTable: "account_owner_receipts",
                        principalColumns: new[] { "account_id", "request_key" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_owner_audit_account_id_request_key",
                schema: "andril_identity",
                table: "account_owner_audit",
                columns: new[] { "account_id", "request_key" });

            // No implicit runtime/client grants, data backfill, purge or historical
            // state reinterpretation. Administrative operations require their own contract.
            foreach (var table in new[] { "account_owner_states", "account_owner_receipts", "account_owner_audit" })
            {
                migrationBuilder.Sql($"""
                    REVOKE ALL ON andril_identity.{table} FROM PUBLIC;
                    ALTER TABLE andril_identity.{table} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE andril_identity.{table} FORCE ROW LEVEL SECURITY;
                    CREATE POLICY owner_scope ON andril_identity.{table}
                    USING (account_id = nullif(current_setting('andril.account', true), '')::uuid
                        AND EXISTS (SELECT 1 FROM andril_identity.accounts a WHERE a.id = account_id
                            AND a.supabase_user_id = nullif(current_setting('andril.subject', true), '')::uuid))
                    WITH CHECK (account_id = nullif(current_setting('andril.account', true), '')::uuid
                        AND EXISTS (SELECT 1 FROM andril_identity.accounts a WHERE a.id = account_id
                            AND a.supabase_user_id = nullif(current_setting('andril.subject', true), '')::uuid));
                    """);
            }
            migrationBuilder.Sql("""
                CREATE FUNCTION andril_identity.check_owner_state_update() RETURNS trigger
                LANGUAGE plpgsql AS $$ BEGIN
                    IF OLD.state = 'Deleted' OR NEW.account_id <> OLD.account_id
                        OR NEW.revision <> OLD.revision + 1 OR NEW.changed_at_utc < OLD.changed_at_utc THEN
                        RAISE EXCEPTION 'Invalid account lifecycle transition' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                REVOKE ALL ON FUNCTION andril_identity.check_owner_state_update() FROM PUBLIC;
                CREATE TRIGGER owner_state_transition BEFORE UPDATE ON andril_identity.account_owner_states
                    FOR EACH ROW EXECUTE FUNCTION andril_identity.check_owner_state_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Transactional downgrade refuses to discard retained state/audit. The
            // temporary owner bypass below rolls back if the refusal is raised.
            migrationBuilder.Sql("""
                LOCK TABLE andril_identity.account_owner_states IN ACCESS EXCLUSIVE MODE;
                ALTER TABLE andril_identity.account_owner_states NO FORCE ROW LEVEL SECURITY;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM andril_identity.account_owner_states) THEN
                        RAISE EXCEPTION 'Retained account lifecycle data prevents downgrade';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "account_owner_audit",
                schema: "andril_identity");

            migrationBuilder.DropTable(
                name: "account_owner_receipts",
                schema: "andril_identity");

            migrationBuilder.DropTable(
                name: "account_owner_states",
                schema: "andril_identity");
            migrationBuilder.Sql("DROP FUNCTION andril_identity.check_owner_state_update()");
        }
    }
}
