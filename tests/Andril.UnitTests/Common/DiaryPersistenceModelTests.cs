using Andril.Common.Diary;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Andril.UnitTests.Common;

/// <summary>Offline structural checks only; actual authorization/RLS/atomicity is verified by PostgreSQL tests.</summary>
public sealed class DiaryPersistenceModelTests
{
    private static DiaryDbContext Open() => new(new DbContextOptionsBuilder<DiaryDbContext>()
        .UseNpgsql("Host=127.0.0.1;Database=unused;Username=unused;Pooling=false").Options);

    [Fact]
    public void DiaryHasAnIndependentUniqueProfileModuleDateKeyAndNoSpeculativeSharedDeleteRelationship()
    {
        using var db = Open(); var entry = Assert.Single(db.Model.GetEntityTypes());
        Assert.Equal("andril_diary", entry.GetSchema()); Assert.Equal("module_diary_entries", entry.GetTableName());
        Assert.Equal(new[] { "OwnerModuleProfileId", "Module", "CalendarDate" }, entry.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Empty(entry.GetForeignKeys()); Assert.Equal("pk_module_diary_entries", entry.FindPrimaryKey()!.GetName());
        Assert.Equal(typeof(DateOnly), entry.FindProperty("CalendarDate")!.ClrType); Assert.Equal("date", entry.FindProperty("CalendarDate")!.GetColumnType());
        Assert.Equal(typeof(DateTime), entry.FindProperty("CalendarDate")!.GetValueConverter()!.ProviderClrType);
    }

    [Fact]
    public void DiarySchemaPreservesKnownModuleDateReflectionAndPlanShapeConstraints()
    {
        using var db = Open(); var model = db.GetService<IDesignTimeModel>().Model; var entry = Assert.Single(model.GetEntityTypes());
        Assert.Equal(new[] { "ck_diary_date", "ck_diary_module", "ck_diary_plans", "ck_diary_profile", "ck_diary_reflection" },
            entry.GetCheckConstraints().Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("jsonb", entry.FindProperty("Plans")!.GetColumnType()); Assert.Equal("text", entry.FindProperty("ReflectionBody")!.GetColumnType());
        Assert.Null(entry.FindProperty("ReflectionBody")!.GetMaxLength()); // Existing grapheme validation stays in the domain.
    }

    [Fact]
    public void FrozenDiarySnapshotMatchesAndMigrationIsSeparateFromLearningAndAccounts()
    {
        using var db = Open(); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal("20261126080000_ModuleDiaryPersistence", Assert.Single(db.Database.GetMigrations()));
        var script = db.GetService<IMigrator>().GenerateScript("0", "20261126080000_ModuleDiaryPersistence");
        Assert.Contains("__EFMigrationsHistory_Diary", script); Assert.Contains("FORCE ROW LEVEL SECURITY", script);
        Assert.Contains("andril_diary.lock_owner", script); Assert.DoesNotContain("ALTER TABLE andril_identity", script);
        Assert.DoesNotContain("andril_learning", script); Assert.DoesNotContain("ON DELETE CASCADE", script);
        Assert.DoesNotContain("BYPASSRLS", script); Assert.DoesNotContain("DISABLE ROW LEVEL SECURITY", script);
    }

    [Fact]
    public void UnavailableResultsNeverFabricateAnEmptyAuthorizedEntryOrCalendar()
    {
        Assert.Null(DiaryReadResult.Unavailable().Entry); Assert.Null(DiaryCalendarResult.Unavailable().Dates);
        Assert.Null(DiaryReadResult.Denied().Entry); Assert.Null(DiaryCalendarResult.Denied().Dates);
        Assert.Equal(DiaryReadState.NotFound, DiaryReadResult.Missing().State);
        Assert.Empty(DiaryCalendarResult.Available([]).Dates!);
    }
}
