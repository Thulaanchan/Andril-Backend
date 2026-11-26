using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Andril.Persistence.Migrations.Diary;

[DbContext(typeof(DiaryDbContext))]
internal sealed class DiaryDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => DiarySnapshotV1.Configure(modelBuilder);
}

// Frozen shadow model; does not call live runtime mappings or change the M2 snapshot/history.
internal static class DiarySnapshotV1
{
    internal static void Configure(ModelBuilder model)
    {
        model.HasAnnotation("ProductVersion", "10.0.13").HasAnnotation("Relational:MaxIdentifierLength", 63);
        model.UseIdentityByDefaultColumns(); model.HasDefaultSchema("andril_diary");
        model.Entity("Andril.Persistence.Repositories.Diary.DiaryEntryRow", b =>
        {
            b.Property<Guid>("OwnerModuleProfileId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("owner_module_profile_id");
            b.Property<string>("Module").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)").HasColumnName("module");
            b.Property<DateTime>("CalendarDate").HasColumnType("date").HasColumnName("calendar_date");
            b.Property<string>("ReflectionTitle").HasColumnType("text").HasColumnName("reflection_title");
            b.Property<string>("ReflectionBody").HasColumnType("text").HasColumnName("reflection_body");
            b.Property<string>("Plans").IsRequired().ValueGeneratedOnAdd().HasColumnType("jsonb").HasColumnName("plans").HasDefaultValueSql("'[]'::jsonb");
            b.HasKey("OwnerModuleProfileId", "Module", "CalendarDate").HasName("pk_module_diary_entries");
            b.ToTable("module_diary_entries", "andril_diary", t =>
            {
                t.HasCheckConstraint("ck_diary_profile", "owner_module_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_diary_module", "module IN ('learning','hobbies','social')");
                t.HasCheckConstraint("ck_diary_date", "isfinite(calendar_date) AND calendar_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'");
                t.HasCheckConstraint("ck_diary_reflection", "reflection_body IS NOT NULL OR reflection_title IS NULL");
                t.HasCheckConstraint("ck_diary_plans", "jsonb_typeof(plans) = 'array' AND jsonb_array_length(plans) <= 20");
            });
        });
    }
}
