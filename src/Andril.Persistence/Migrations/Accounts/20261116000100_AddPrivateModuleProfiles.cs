using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Accounts;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261116000100_AddPrivateModuleProfiles")]
public sealed class AddPrivateModuleProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "private_module_profiles", schema: "andril_identity", columns: table => new
        {
            id = table.Column<Guid>(type: "uuid", nullable: false),
            account_id = table.Column<Guid>(type: "uuid", nullable: false),
            module = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
            created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("pk_private_module_profiles", x => x.id);
            table.ForeignKey("fk_profiles_accounts", x => x.account_id, principalSchema: "andril_identity",
                principalTable: "accounts", principalColumn: "id", onDelete: ReferentialAction.Cascade);
            table.CheckConstraint("ck_profile_module", "module IN ('learning', 'hobbies', 'social')");
            table.CheckConstraint("ck_profile_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.CheckConstraint("ck_profile_timestamp", "isfinite(created_at_utc)");
        });
        migrationBuilder.CreateIndex(name: "uq_profiles_account_module", schema: "andril_identity",
            table: "private_module_profiles", columns: new[] { "account_id", "module" }, unique: true);
        migrationBuilder.Sql("""
            REVOKE ALL ON TABLE andril_identity.private_module_profiles FROM PUBLIC;
            ALTER TABLE andril_identity.private_module_profiles ENABLE ROW LEVEL SECURITY;
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'anon') THEN
                    REVOKE ALL ON TABLE andril_identity.private_module_profiles FROM anon;
                END IF;
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated') THEN
                    REVOKE ALL ON TABLE andril_identity.private_module_profiles FROM authenticated;
                END IF;
            END $$;
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "private_module_profiles", schema: "andril_identity");
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => PrivateModuleProfilesModel.Build(modelBuilder);
}

internal static class PrivateModuleProfilesModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        StarterMembershipsModel.Build(modelBuilder);
        modelBuilder.Entity("Andril.Common.Accounts.Profiles.PrivateModuleProfile", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("id");
            b.Property<Guid>("AccountId").HasColumnType("uuid").HasColumnName("account_id");
            b.Property<string>("Module").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)").HasColumnName("module");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("created_at_utc");
            b.HasKey("Id").HasName("pk_private_module_profiles");
            b.HasIndex("AccountId", "Module").IsUnique().HasDatabaseName("uq_profiles_account_module");
            b.ToTable("private_module_profiles", "andril_identity", t =>
            {
                t.HasCheckConstraint("ck_profile_module", "module IN ('learning', 'hobbies', 'social')");
                t.HasCheckConstraint("ck_profile_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_profile_timestamp", "isfinite(created_at_utc)");
            });
            b.HasOne("Andril.Common.Accounts.Account", null).WithMany().HasForeignKey("AccountId")
                .OnDelete(DeleteBehavior.Cascade).IsRequired().HasConstraintName("fk_profiles_accounts");
        });
    }
}
