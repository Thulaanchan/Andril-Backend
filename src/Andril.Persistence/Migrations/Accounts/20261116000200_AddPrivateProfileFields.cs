using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Andril.Persistence.Migrations.Accounts;

[DbContext(typeof(AccountsDbContext))]
[Migration("20261116000200_AddPrivateProfileFields")]
public sealed class AddPrivateProfileFields : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.AddColumn<string>("display_name", "private_module_profiles", schema: "andril_identity", type: "text", nullable: true);
        m.AddColumn<string>("bio", "private_module_profiles", schema: "andril_identity", type: "text", nullable: true);
        m.AddColumn<string>("visibility", "private_module_profiles", schema: "andril_identity", type: "text", nullable: false, defaultValue: "private");
        m.AddColumn<string>("canonical_display_name", "private_module_profiles", schema: "andril_identity", type: "text", nullable: true,
            computedColumnSql: ProfileFieldsModel.CanonicalSql, stored: true, collation: "C");
        m.AddCheckConstraint("ck_profile_name_trimmed", "private_module_profiles", ProfileFieldsModel.TrimSql, "andril_identity");
        m.AddCheckConstraint("ck_profile_visibility", "private_module_profiles", "visibility = 'private'", "andril_identity");
        m.CreateIndex("uq_profiles_module_name", "private_module_profiles", new[] { "module", "canonical_display_name" }, "andril_identity", unique: true);
        m.CreateIndex("uq_profiles_owner_name", "private_module_profiles", new[] { "account_id", "canonical_display_name" }, "andril_identity", unique: true);
    }
    protected override void Down(MigrationBuilder m)
    {
        m.DropIndex("uq_profiles_module_name", "private_module_profiles", "andril_identity");
        m.DropIndex("uq_profiles_owner_name", "private_module_profiles", "andril_identity");
        m.DropCheckConstraint("ck_profile_name_trimmed", "private_module_profiles", "andril_identity");
        m.DropCheckConstraint("ck_profile_visibility", "private_module_profiles", "andril_identity");
        foreach (var name in new[] { "canonical_display_name", "display_name", "bio", "visibility" })
            m.DropColumn(name, "private_module_profiles", "andril_identity");
    }
    protected override void BuildTargetModel(ModelBuilder b) => ProfileFieldsModel.Build(b);
}

internal static class ProfileFieldsModel
{
    internal const string CanonicalSql = "translate(display_name, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')";
    // Explicit .NET Char.IsWhiteSpace set keeps trimming independent of DB locale.
    internal const string TrimSql = "display_name IS NULL OR (display_name <> '' AND display_name = btrim(display_name, chr(9)||chr(10)||chr(11)||chr(12)||chr(13)||chr(32)||chr(133)||chr(160)||chr(5760)||chr(8192)||chr(8193)||chr(8194)||chr(8195)||chr(8196)||chr(8197)||chr(8198)||chr(8199)||chr(8200)||chr(8201)||chr(8202)||chr(8232)||chr(8233)||chr(8239)||chr(8287)||chr(12288)))";
    internal static void Build(ModelBuilder b)
    {
        PrivateModuleProfilesModel.Build(b);
        b.Entity("Andril.Common.Accounts.Profiles.PrivateModuleProfile", e =>
        {
            e.Property<string>("DisplayName").HasColumnName("display_name").HasColumnType("text");
            e.Property<string>("Bio").HasColumnName("bio").HasColumnType("text");
            e.Property<string>("Visibility").IsRequired().HasColumnName("visibility").HasColumnType("text").HasDefaultValue("private");
            e.Property<string>("CanonicalDisplayName").HasColumnName("canonical_display_name").HasColumnType("text").UseCollation("C").HasComputedColumnSql(CanonicalSql, stored: true);
            e.HasIndex("Module", "CanonicalDisplayName").IsUnique().HasDatabaseName("uq_profiles_module_name");
            e.HasIndex("AccountId", "CanonicalDisplayName").IsUnique().HasDatabaseName("uq_profiles_owner_name");
            e.ToTable("private_module_profiles", "andril_identity", t => {
                t.HasCheckConstraint("ck_profile_name_trimmed", TrimSql);
                t.HasCheckConstraint("ck_profile_visibility", "visibility = 'private'");
            });
        });
    }
}
