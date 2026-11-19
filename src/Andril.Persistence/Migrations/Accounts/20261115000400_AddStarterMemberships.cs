using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Accounts;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261115000400_AddStarterMemberships")]
public sealed class AddStarterMemberships : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "account_starter_memberships", schema: "andril_identity",
            columns: table => new
            {
                account_id = table.Column<Guid>(type: "uuid", nullable: false),
                module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                activated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                social_opt_in_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            }, constraints: table =>
            {
                table.PrimaryKey("pk_account_starter_memberships", x => x.account_id);
                table.ForeignKey("fk_starter_memberships_accounts", x => x.account_id,
                    principalSchema: "andril_identity", principalTable: "accounts", principalColumn: "id", onDelete: ReferentialAction.Cascade);
                table.CheckConstraint("ck_starter_module", "module IN ('learning', 'hobbies', 'social')");
                table.CheckConstraint("ck_starter_timestamp", "isfinite(activated_at_utc)");
                table.CheckConstraint("ck_starter_social_opt_in", "(module = 'social' AND social_opt_in_at_utc IS NOT NULL AND social_opt_in_at_utc = activated_at_utc) OR (module <> 'social' AND social_opt_in_at_utc IS NULL)");
            });
        migrationBuilder.Sql("""
            REVOKE ALL ON TABLE andril_identity.account_starter_memberships FROM PUBLIC;
            ALTER TABLE andril_identity.account_starter_memberships ENABLE ROW LEVEL SECURITY;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
                    REVOKE ALL ON TABLE andril_identity.account_starter_memberships FROM anon;
                END IF;
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
                    REVOKE ALL ON TABLE andril_identity.account_starter_memberships FROM authenticated;
                END IF;
            END $$;
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "account_starter_memberships", schema: "andril_identity");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => StarterMembershipsModel.Build(modelBuilder);
}

internal static class StarterMembershipsModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        AccountPolicyRecordsModel.Build(modelBuilder);
        modelBuilder.Entity("Andril.Common.Accounts.StarterModules.StarterMembership", b =>
        {
            b.Property<Guid>("AccountId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("account_id");
            b.Property<string>("Module").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)").HasColumnName("module");
            b.Property<DateTimeOffset>("ActivatedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("activated_at_utc");
            b.Property<DateTimeOffset?>("SocialOptInAtUtc").HasColumnType("timestamp with time zone").HasColumnName("social_opt_in_at_utc");
            b.HasKey("AccountId").HasName("pk_account_starter_memberships");
            b.ToTable("account_starter_memberships", "andril_identity", t =>
            {
                t.HasCheckConstraint("ck_starter_module", "module IN ('learning', 'hobbies', 'social')");
                t.HasCheckConstraint("ck_starter_timestamp", "isfinite(activated_at_utc)");
                t.HasCheckConstraint("ck_starter_social_opt_in", "(module = 'social' AND social_opt_in_at_utc IS NOT NULL AND social_opt_in_at_utc = activated_at_utc) OR (module <> 'social' AND social_opt_in_at_utc IS NULL)");
            });
            b.HasOne("Andril.Common.Accounts.Account", null).WithOne()
                .HasForeignKey("Andril.Common.Accounts.StarterModules.StarterMembership", "AccountId")
                .OnDelete(DeleteBehavior.Cascade).IsRequired().HasConstraintName("fk_starter_memberships_accounts");
        });
    }
}
