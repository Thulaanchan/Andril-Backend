using Andril.Persistence.Repositories.Diary;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

/// <summary>M1-owned Diary model, unregistered. No startup migration or production authority.
/// No cross-schema FK/cascade is inferred from Learning: permanent shared-profile purge remains
/// an M2 integration decision. RLS verifies the real current owner/module/lifecycle instead.</summary>
public sealed class DiaryDbContext(DbContextOptions<DiaryDbContext> options) : DbContext(options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseNpgsql(
        options => options.MigrationsHistoryTable("__EFMigrationsHistory_Diary", "andril_diary"));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("andril_diary");
        var entry = modelBuilder.Entity<DiaryEntryRow>();
        entry.ToTable("module_diary_entries", "andril_diary", table =>
        {
            table.HasCheckConstraint("ck_diary_profile", "owner_module_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_diary_module", "module IN ('learning','hobbies','social')");
            table.HasCheckConstraint("ck_diary_date", "isfinite(calendar_date) AND calendar_date BETWEEN DATE '0001-01-01' AND DATE '9999-12-31'");
            table.HasCheckConstraint("ck_diary_reflection", "reflection_body IS NOT NULL OR reflection_title IS NULL");
            table.HasCheckConstraint("ck_diary_plans", "jsonb_typeof(plans) = 'array' AND jsonb_array_length(plans) <= 20");
        });
        entry.HasKey(x => new { x.OwnerModuleProfileId, x.Module, x.CalendarDate }).HasName("pk_module_diary_entries");
        entry.Property(x => x.OwnerModuleProfileId).HasColumnName("owner_module_profile_id").HasColumnType("uuid").ValueGeneratedNever();
        entry.Property(x => x.Module).HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16).IsRequired();
        // DateOnly endpoints are valid domain dates, not PostgreSQL +/-infinity. A date-valued
        // noon DateTime avoids Npgsql's Min/Max infinity sentinels without any global switch,
        // timezone resolution or change to the literal calendar date stored by PostgreSQL.
        entry.Property(x => x.CalendarDate).HasConversion(
            date => date.ToDateTime(new TimeOnly(12, 0)), value => DateOnly.FromDateTime(value))
            .HasColumnName("calendar_date").HasColumnType("date");
        entry.Property(x => x.ReflectionTitle).HasColumnName("reflection_title").HasColumnType("text");
        entry.Property(x => x.ReflectionBody).HasColumnName("reflection_body").HasColumnType("text");
        entry.Property(x => x.Plans).HasColumnName("plans").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").IsRequired();
    }
}
