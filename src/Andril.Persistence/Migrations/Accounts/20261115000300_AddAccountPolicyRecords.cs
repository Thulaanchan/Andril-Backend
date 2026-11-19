using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Accounts;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261115000300_AddAccountPolicyRecords")]
public sealed class AddAccountPolicyRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "account_policy_records", schema: "andril_identity",
            columns: table => new
            {
                account_id = table.Column<Guid>(type: "uuid", nullable: false),
                action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            }, constraints: table =>
            {
                table.PrimaryKey("pk_account_policy_records", x => new { x.account_id, x.action, x.version });
                table.ForeignKey("fk_account_policy_records_accounts", x => x.account_id,
                    principalSchema: "andril_identity", principalTable: "accounts", principalColumn: "id", onDelete: ReferentialAction.Cascade);
                table.CheckConstraint("ck_account_policy_action", "action IN ('terms_agreed', 'privacy_acknowledged')");
                table.CheckConstraint("ck_account_policy_version", "char_length(btrim(version)) > 0 AND version = btrim(version) AND version !~ '[[:cntrl:]]'");
                table.CheckConstraint("ck_account_policy_timestamp", "isfinite(recorded_at_utc)");
            });
        migrationBuilder.Sql("""
            REVOKE ALL ON TABLE andril_identity.account_policy_records FROM PUBLIC;
            ALTER TABLE andril_identity.account_policy_records ENABLE ROW LEVEL SECURITY;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
                    REVOKE ALL ON TABLE andril_identity.account_policy_records FROM anon;
                END IF;
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
                    REVOKE ALL ON TABLE andril_identity.account_policy_records FROM authenticated;
                END IF;
            END $$;
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "account_policy_records", schema: "andril_identity");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AccountPolicyRecordsModel.Build(modelBuilder);
}

internal static class AccountPolicyRecordsModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        AccountDemographicsModel.Build(modelBuilder);
        modelBuilder.Entity("Andril.Common.Accounts.Policies.AccountPolicyRecord", b =>
        {
            b.Property<Guid>("AccountId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("account_id");
            b.Property<string>("Action").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)").HasColumnName("action");
            b.Property<string>("Version").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)").HasColumnName("version");
            b.Property<DateTimeOffset>("RecordedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("recorded_at_utc");
            b.HasKey("AccountId", "Action", "Version").HasName("pk_account_policy_records");
            b.ToTable("account_policy_records", "andril_identity", t =>
            {
                t.HasCheckConstraint("ck_account_policy_action", "action IN ('terms_agreed', 'privacy_acknowledged')");
                t.HasCheckConstraint("ck_account_policy_version", "char_length(btrim(version)) > 0 AND version = btrim(version) AND version !~ '[[:cntrl:]]'");
                t.HasCheckConstraint("ck_account_policy_timestamp", "isfinite(recorded_at_utc)");
            });
            b.HasOne("Andril.Common.Accounts.Account", null).WithMany().HasForeignKey("AccountId")
                .OnDelete(DeleteBehavior.Cascade).IsRequired().HasConstraintName("fk_account_policy_records_accounts");
        });
    }
}
