using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Accounts;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261115000200_AddAccountDemographics")]
public sealed class AddAccountDemographics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "account_demographics", schema: "andril_identity",
            columns: table => new
            {
                account_id = table.Column<Guid>(type: "uuid", nullable: false),
                date_of_birth = table.Column<DateOnly>(type: "date", nullable: false),
                gender = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            }, constraints: table =>
            {
                table.PrimaryKey("pk_account_demographics", x => x.account_id);
                table.ForeignKey("fk_account_demographics_accounts", x => x.account_id,
                    principalSchema: "andril_identity", principalTable: "accounts", principalColumn: "id", onDelete: ReferentialAction.Cascade);
                table.CheckConstraint("ck_account_demographics_dob", "isfinite(date_of_birth) AND date_of_birth >= DATE '0001-01-01' AND date_of_birth <= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date");
                table.CheckConstraint("ck_account_demographics_gender", "char_length(btrim(gender)) > 0 AND gender !~ '^[[:space:]]*$' AND gender !~ '[[:cntrl:]]'");
                table.CheckConstraint("ck_account_demographics_timestamps", "updated_at_utc >= created_at_utc");
            });
        migrationBuilder.Sql("""
            REVOKE ALL ON TABLE andril_identity.account_demographics FROM PUBLIC;
            ALTER TABLE andril_identity.account_demographics ENABLE ROW LEVEL SECURITY;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "account_demographics", schema: "andril_identity");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AccountDemographicsModel.Build(modelBuilder);
}

internal static class AccountDemographicsModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        AccountLanguagePreferencesModel.Build(modelBuilder);
        modelBuilder.Entity("Andril.Common.Accounts.Eligibility.AccountDemographics", b =>
        {
            b.Property<Guid>("AccountId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("account_id");
            b.Property<DateOnly>("DateOfBirth").HasColumnType("date").HasColumnName("date_of_birth");
            b.Property<string>("Gender").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)").HasColumnName("gender");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("created_at_utc");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("updated_at_utc");
            b.HasKey("AccountId").HasName("pk_account_demographics");
            b.ToTable("account_demographics", "andril_identity", t =>
            {
                t.HasCheckConstraint("ck_account_demographics_dob", "isfinite(date_of_birth) AND date_of_birth >= DATE '0001-01-01' AND date_of_birth <= (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date");
                t.HasCheckConstraint("ck_account_demographics_gender", "char_length(btrim(gender)) > 0 AND gender !~ '^[[:space:]]*$' AND gender !~ '[[:cntrl:]]'");
                t.HasCheckConstraint("ck_account_demographics_timestamps", "updated_at_utc >= created_at_utc");
            });
            b.HasOne("Andril.Common.Accounts.Account", null).WithOne()
                .HasForeignKey("Andril.Common.Accounts.Eligibility.AccountDemographics", "AccountId")
                .OnDelete(DeleteBehavior.Cascade).IsRequired().HasConstraintName("fk_account_demographics_accounts");
        });
    }
}
