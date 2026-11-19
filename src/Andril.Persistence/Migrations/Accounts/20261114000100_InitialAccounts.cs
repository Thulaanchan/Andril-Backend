using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Accounts;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261114000100_InitialAccounts")]
public sealed class InitialAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("andril_identity");
        migrationBuilder.CreateTable(
            name: "accounts", schema: "andril_identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                supabase_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_accounts", x => x.id);
                table.UniqueConstraint("uq_accounts_supabase_user_id", x => x.supabase_user_id);
                table.CheckConstraint("ck_accounts_id_nonempty", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.CheckConstraint("ck_accounts_subject_nonempty", "supabase_user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.CheckConstraint("ck_accounts_timestamps", "updated_at_utc >= created_at_utc");
            });
        // Private backend-owned table: no direct client policies/grants are introduced.
        migrationBuilder.Sql("""
            REVOKE ALL ON SCHEMA andril_identity FROM PUBLIC;
            REVOKE ALL ON TABLE andril_identity.accounts FROM PUBLIC;
            ALTER TABLE andril_identity.accounts ENABLE ROW LEVEL SECURITY;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "accounts", schema: "andril_identity");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => InitialAccountsModel.Build(modelBuilder);
}

// Frozen v1 definition stays with this migration when EF replaces the current snapshot.
internal static class InitialAccountsModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.13")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);
        modelBuilder.Entity("Andril.Common.Accounts.Account", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("id");
            b.Property<Guid>("SupabaseUserId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("supabase_user_id");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("created_at_utc");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("updated_at_utc");
            b.HasKey("Id").HasName("pk_accounts");
            b.HasAlternateKey("SupabaseUserId").HasName("uq_accounts_supabase_user_id");
            b.ToTable("accounts", "andril_identity", t =>
            {
                t.HasCheckConstraint("ck_accounts_id_nonempty", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_accounts_subject_nonempty", "supabase_user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_accounts_timestamps", "updated_at_utc >= created_at_utc");
            });
        });
    }
}
