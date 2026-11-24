using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Andril.Persistence.Migrations.Accounts;

public partial class AddControlledModuleDeactivation : Migration
{
    // Existing immutable operation discriminators reliably reconstruct journal result
    // state: activate/reactivate => active, deactivate => inactive. No data backfill,
    // new grants, lifecycle/profile updates or duplicate state column are necessary.
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ReplaceChecks(migrationBuilder, deactivation: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Old binaries cannot read deactivation journals. Never silently erase them.
        // With such data, retain this schema and use a reviewed forward fix, or restore
        // an explicitly approved pre-deactivation backup into a separate environment.
        migrationBuilder.Sql("""
            LOCK TABLE andril_identity.account_module_lifecycle_receipts,
                andril_identity.account_module_lifecycle_audit IN ACCESS EXCLUSIVE MODE;
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM andril_identity.account_module_lifecycle_receipts WHERE operation = 'deactivate')
                    OR EXISTS (SELECT 1 FROM andril_identity.account_module_lifecycle_audit WHERE operation = 'deactivate') THEN
                    RAISE EXCEPTION 'Deactivation journals prevent downgrade. Preserve records and use a reviewed forward fix or approved backup restoration.';
                END IF;
            END $$;
            """);
        ReplaceChecks(migrationBuilder, deactivation: false);
    }

    private static void ReplaceChecks(MigrationBuilder migrationBuilder, bool deactivation)
    {
        migrationBuilder.DropCheckConstraint("ck_lifecycle_receipt_revision", "account_module_lifecycle_receipts", "andril_identity");
        migrationBuilder.DropCheckConstraint("ck_lifecycle_audit_transition", "account_module_lifecycle_audit", "andril_identity");
        var receipt = "revision > 0 AND ((operation = 'activate' AND expected_revision IS NULL) OR (operation = 'reactivate' AND expected_revision IS NOT NULL AND expected_revision > 0 AND expected_revision < 9223372036854775807)";
        var audit = "revision > 0 AND ((operation = 'activate' AND previous_state IS NULL AND revision = 1) OR (operation = 'reactivate' AND previous_state IS NOT NULL AND previous_state = 'inactive' AND revision > 1)";
        if (deactivation)
        {
            receipt += " OR (operation = 'deactivate' AND expected_revision IS NOT NULL AND expected_revision > 0 AND expected_revision < 9223372036854775807 AND revision = expected_revision + 1)";
            audit += " OR (operation = 'deactivate' AND previous_state IS NOT NULL AND previous_state = 'active' AND revision > 1)";
        }
        migrationBuilder.AddCheckConstraint("ck_lifecycle_receipt_revision", "account_module_lifecycle_receipts", receipt + ")", "andril_identity");
        migrationBuilder.AddCheckConstraint("ck_lifecycle_audit_transition", "account_module_lifecycle_audit", audit + ")", "andril_identity");
    }
}
