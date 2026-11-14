using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Accounts;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261115000100_AddAccountLanguagePreferences")]
public sealed class AddAccountLanguagePreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "account_language_preferences", schema: "andril_identity",
            columns: table => new
            {
                account_id = table.Column<Guid>(type: "uuid", nullable: false),
                language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            }, constraints: table =>
            {
                table.PrimaryKey("pk_account_language_preferences", x => x.account_id);
                table.ForeignKey("fk_account_language_preferences_accounts", x => x.account_id,
                    principalSchema: "andril_identity", principalTable: "accounts", principalColumn: "id", onDelete: ReferentialAction.Cascade);
                table.CheckConstraint("ck_account_language_supported", "language IN ('en', 'ta', 'si')");
                table.CheckConstraint("ck_account_language_timestamps", "updated_at_utc >= created_at_utc");
            });
        migrationBuilder.Sql("""
            REVOKE ALL ON TABLE andril_identity.account_language_preferences FROM PUBLIC;
            ALTER TABLE andril_identity.account_language_preferences ENABLE ROW LEVEL SECURITY;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "account_language_preferences", schema: "andril_identity");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => AccountLanguagePreferencesModel.Build(modelBuilder);
}

// Frozen migration target. The initial account model is preserved without schema changes.
internal static class AccountLanguagePreferencesModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        InitialAccountsModel.Build(modelBuilder);
        modelBuilder.Entity("Andril.Common.Accounts.Preferences.AccountLanguagePreference", b =>
        {
            b.Property<Guid>("AccountId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("account_id");
            b.Property<string>("Language").IsRequired().HasMaxLength(2).HasColumnType("character varying(2)").HasColumnName("language");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("created_at_utc");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("updated_at_utc");
            b.HasKey("AccountId").HasName("pk_account_language_preferences");
            b.ToTable("account_language_preferences", "andril_identity", t =>
            {
                t.HasCheckConstraint("ck_account_language_supported", "language IN ('en', 'ta', 'si')");
                t.HasCheckConstraint("ck_account_language_timestamps", "updated_at_utc >= created_at_utc");
            });
            b.HasOne("Andril.Common.Accounts.Account", null).WithOne()
                .HasForeignKey("Andril.Common.Accounts.Preferences.AccountLanguagePreference", "AccountId")
                .OnDelete(DeleteBehavior.Cascade).IsRequired().HasConstraintName("fk_account_language_preferences_accounts");
        });
    }
}
