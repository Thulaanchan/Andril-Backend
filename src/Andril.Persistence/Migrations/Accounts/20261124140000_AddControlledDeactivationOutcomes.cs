using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Accounts;

public partial class AddControlledDeactivationOutcomes : Migration
{
    private const string Schema = "andril_identity";
    private const string Receipts = "account_module_lifecycle_receipts";
    private const string MembershipFk = "FK_account_module_lifecycle_receipts_account_module_lifecycles~";
    private const string ResultMembershipFk = "fk_lifecycle_receipts_result_membership";
    private const string MembershipIndex = "ix_lifecycle_receipts_result_membership";
    internal const string MembershipModuleSql = "CASE WHEN revision > 0 THEN module END";
    private const string AccountFk = "fk_lifecycle_receipts_accounts";
    private const string Rule = "ck_lifecycle_receipt_revision";
    private const string Activation = "(operation = 'activate' AND expected_revision IS NULL AND revision > 0)";
    private const string Reactivation = "(operation = 'reactivate' AND expected_revision IS NOT NULL AND expected_revision > 0 AND expected_revision < 9223372036854775807 AND revision > 0)";
    private const string Deactivation = "operation = 'deactivate' AND expected_revision IS NOT NULL AND expected_revision > 0 AND expected_revision < 9223372036854775807";

    internal const string RevisionCheck = Activation + " OR " + Reactivation + " OR (" + Deactivation +
        " AND (revision::numeric = expected_revision::numeric + 1 OR revision = expected_revision OR (revision = 0 AND state_changed_at_utc = recorded_at_utc)))";

    protected override void Up(MigrationBuilder migration)
    {
        // An authorized never-activated request needs a durable idempotency receipt without a fake membership.
        migration.AddForeignKey(AccountFk, Receipts, "account_id", "accounts", Schema, Schema, "id", onDelete: ReferentialAction.Restrict);
        migration.AddColumn<string>("membership_module", Receipts, type: "character varying(16)", schema: Schema,
            maxLength: 16, nullable: true, computedColumnSql: MembershipModuleSql, stored: true, collation: "C");
        migration.CreateIndex(MembershipIndex, Receipts, new[] { "account_id", "membership_module" }, Schema);
        migration.AddForeignKey(ResultMembershipFk, Receipts, new[] { "account_id", "membership_module" },
            "account_module_lifecycles", Schema, Schema, new[] { "account_id", "module" }, onDelete: ReferentialAction.Restrict);
        migration.DropForeignKey(MembershipFk, Receipts, Schema);
        migration.AddCheckConstraint("ck_lifecycle_receipt_module", Receipts, "module IN ('learning', 'hobbies', 'social')", Schema);
        migration.DropCheckConstraint(Rule, Receipts, Schema);
        migration.AddCheckConstraint(Rule, Receipts, RevisionCheck, Schema);
    }

    protected override void Down(MigrationBuilder migration)
    {
        // Constraint validation rejects downgrade if any new receipt exists, including under FORCE RLS.
        migration.DropCheckConstraint(Rule, Receipts, Schema);
        migration.AddCheckConstraint(Rule, Receipts, Activation + " OR " + Reactivation + " OR (" + Deactivation +
            " AND revision::numeric = expected_revision::numeric + 1)", Schema);
        migration.AddForeignKey(MembershipFk, Receipts, new[] { "account_id", "module" }, "account_module_lifecycles",
            Schema, Schema, new[] { "account_id", "module" }, onDelete: ReferentialAction.Restrict);
        migration.DropCheckConstraint("ck_lifecycle_receipt_module", Receipts, Schema);
        migration.DropForeignKey(ResultMembershipFk, Receipts, Schema);
        migration.DropIndex(MembershipIndex, Receipts, Schema);
        migration.DropColumn("membership_module", Receipts, Schema);
        migration.DropForeignKey(AccountFk, Receipts, Schema);
    }
}
