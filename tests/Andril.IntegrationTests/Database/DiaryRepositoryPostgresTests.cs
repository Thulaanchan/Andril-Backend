using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Diary;
using Andril.Learning.Interfaces;
using Andril.Persistence.Repositories.Diary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

/// <summary>Actual Diary EF/PostgreSQL, current owner/module/lifecycle rows, FORCE RLS and
/// distinct restricted login. Common authorization contracts are synthetic isolated evidence.</summary>
public sealed class DiaryRepositoryPostgresTests
{
    private static readonly DateOnly Day = new(2026, 11, 26);

    [LocalPostgresFact]
    public async Task CreateReadAndFreshConnectionPreservePrivateReflectionAndPlanIdentity()
    {
        await using var f = await DiaryPostgresFixture.Create(); var entry = f.Entry(Day, body: "SYNTHETIC தமிழ் e\u0301 reflection"); var plan = entry.Plans.Single();
        Assert.Equal(DiaryWriteOutcome.Committed, await f.Repository().CreateAsync(entry));
        var read = await f.Repository().ReadAsync("learning", Day); Assert.Equal(DiaryReadState.Available, read.State);
        Assert.Equal(entry.Reflection!.Body, read.Entry!.Reflection!.Body); Assert.Equal(plan.Id, Assert.Single(read.Entry.Plans).Id);
        Assert.Equal(Day, read.Entry.Date); Assert.Equal(f.Owner.Profiles["learning"], read.Entry.OwnerModuleProfileId);
        await using var raw = f.OpenRuntime(); await DiaryPostgresFixture.Bind(raw, f.Owner, "learning");
        var row = await raw.Set<DiaryEntryRow>().AsNoTracking().SingleAsync(); Assert.Equal(Day, row.CalendarDate); Assert.Equal(entry.Reflection.Body, row.ReflectionBody);
    }

    [LocalPostgresFact]
    public async Task EditPreservesScopeCalendarPlanIdsAndPreviousPrivateSnapshots()
    {
        await using var f = await DiaryPostgresFixture.Create(); var entry = f.Entry(Day); var id = entry.Plans.Single().Id;
        Assert.Equal(DiaryWriteOutcome.Committed, await f.Repository().CreateAsync(entry)); var before = await f.Repository().ReadAsync("learning", Day);
        entry.EditReflection(null, "SYNTHETIC-EDITED"); entry.EditPlan(id, "SYNTHETIC-EDITED-PLAN", null);
        Assert.Equal(DiaryWriteOutcome.Committed, await f.Repository().EditAsync(entry)); var after = await f.Repository().ReadAsync("learning", Day);
        Assert.Equal("SYNTHETIC-EDITED", after.Entry!.Reflection!.Body); Assert.Equal(id, after.Entry.Plans.Single().Id);
        Assert.Equal(before.Entry!.Date, after.Entry.Date); Assert.Equal(before.Entry.OwnerModuleProfileId, after.Entry.OwnerModuleProfileId);
        Assert.Equal("SYNTHETIC-PRIVATE-DIARY", before.Entry.Reflection!.Body); Assert.Equal("SYNTHETIC-PLAN", before.Entry.Plans.Single().Title);
    }

    [LocalPostgresFact]
    public async Task ExplicitOwnerDeletionPermanentlyRemovesOnlyThatDateAndAllowsRecreation()
    {
        await using var f = await DiaryPostgresFixture.Create(); var repo = f.Repository();
        Assert.Equal(DiaryWriteOutcome.Committed, await repo.CreateAsync(f.Entry(Day)));
        Assert.Equal(DiaryWriteOutcome.Committed, await repo.CreateAsync(f.Entry(Day.AddDays(1))));
        Assert.Equal(DiaryWriteOutcome.Committed, await repo.DeleteAsync("learning", Day));
        Assert.Equal(DiaryReadState.NotFound, (await repo.ReadAsync("learning", Day)).State);
        Assert.Equal(DiaryReadState.Available, (await repo.ReadAsync("learning", Day.AddDays(1))).State);
        Assert.Equal(DiaryWriteOutcome.NotFound, await repo.DeleteAsync("learning", Day));
        Assert.Equal(DiaryWriteOutcome.Committed, await repo.CreateAsync(f.Entry(Day, body: "SYNTHETIC-RECREATED")));
        Assert.Equal("SYNTHETIC-RECREATED", (await repo.ReadAsync("learning", Day)).Entry!.Reflection!.Body);
    }

    [LocalPostgresFact]
    public async Task DatabaseRejectsDuplicateIdentityDateAndConcurrentCreatesHaveOneWinner()
    {
        await using var f = await DiaryPostgresFixture.Create(); var results = await Task.WhenAll(
            f.Repository().CreateAsync(f.Entry(Day, body: "LEFT")), f.Repository().CreateAsync(f.Entry(Day, body: "RIGHT")));
        Assert.Single(results, r => r == DiaryWriteOutcome.Committed); Assert.Single(results, r => r == DiaryWriteOutcome.Conflict);
        await using var db = f.OpenRuntime(); await DiaryPostgresFixture.Bind(db, f.Owner, "learning"); Assert.Single(await db.Set<DiaryEntryRow>().ToListAsync());
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO andril_diary.module_diary_entries(owner_module_profile_id,module,calendar_date,reflection_body) VALUES({f.Owner.Profiles["learning"]},'learning',{Day},'duplicate')"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState); Assert.Equal("pk_module_diary_entries", error.ConstraintName);
    }

    [LocalPostgresFact]
    public async Task CalendarReturnsOnlyAuthorizedModuleDatesInRequestedRangeInOrder()
    {
        await using var f = await DiaryPostgresFixture.Create(); var repo = f.Repository();
        foreach (var offset in new[] { 2, -1, 0 }) Assert.Equal(DiaryWriteOutcome.Committed, await repo.CreateAsync(f.Entry(Day.AddDays(offset))));
        Assert.Equal(DiaryWriteOutcome.Committed, await repo.CreateAsync(f.Entry(Day.AddDays(1), "hobbies")));
        var dates = await repo.CalendarAsync("learning", Day, Day.AddDays(2)); Assert.Equal(DiaryReadState.Available, dates.State);
        Assert.Equal(new[] { Day, Day.AddDays(2) }, dates.Dates); Assert.Equal(new[] { Day.AddDays(1) }, (await repo.CalendarAsync("hobbies", Day, Day.AddDays(2))).Dates);
        await Assert.ThrowsAsync<ArgumentException>(() => repo.CalendarAsync("learning", Day.AddDays(1), Day));
    }

    [LocalPostgresFact]
    public async Task LearningHobbiesAndProductDatingBackendSocialAreIndependentAtSameDate()
    {
        await using var f = await DiaryPostgresFixture.Create(); var repo = f.Repository();
        foreach (var module in new[] { "learning", "hobbies", "social" })
            Assert.Equal(DiaryWriteOutcome.Committed, await repo.CreateAsync(f.Entry(Day, module, "SYNTHETIC-" + module)));
        foreach (var module in new[] { "learning", "hobbies", "social" })
        {
            var read = await repo.ReadAsync(module, Day); Assert.Equal("SYNTHETIC-" + module, read.Entry!.Reflection!.Body);
            Assert.Equal(f.Owner.Profiles[module], read.Entry.OwnerModuleProfileId); Assert.Equal(module, read.Entry.Module);
        }
        Assert.Equal(DiaryWriteOutcome.Committed, await repo.DeleteAsync("hobbies", Day));
        Assert.Equal(DiaryReadState.Available, (await repo.ReadAsync("learning", Day)).State); Assert.Equal(DiaryReadState.Available, (await repo.ReadAsync("social", Day)).State);
    }

    [LocalPostgresFact]
    public async Task ForgedProfileOrModuleReferencesCannotAuthorizeForeignPersistence()
    {
        await using var f = await DiaryPostgresFixture.Create(); var repo = f.Repository();
        var forged = new ModuleDiaryEntry(f.Owner.Profiles["hobbies"], "learning", Day); forged.SetReflection(null, "SYNTHETIC-FOREIGN");
        Assert.Equal(DiaryWriteOutcome.Denied, await repo.CreateAsync(forged));
        var foreign = new LearningSetupScope(Guid.NewGuid(), Guid.NewGuid()); await LearningPostgresFixture.Profile(f.Database, foreign);
        var liar = new DiaryPostgresFixture.Authority(f.Owner.Subject, f.Owner.Account); liar.Profiles["learning"] = foreign.LearningProfileId;
        Assert.Equal(DiaryReadState.Denied, (await f.Repository(liar).ReadAsync("learning", Day)).State);
        Assert.Equal(DiaryWriteOutcome.Denied, await f.Repository(liar).CreateAsync(new(foreign.LearningProfileId, "learning", Day)));
        Assert.Equal(DiaryReadState.NotFound, (await repo.ReadAsync("learning", Day)).State);
    }

    [LocalPostgresFact]
    public async Task ForeignAccountCannotReadEditDeleteOrEnumerateAnotherAccountDiary()
    {
        await using var f = await DiaryPostgresFixture.Create(); var entry = f.Entry(Day); Assert.Equal(DiaryWriteOutcome.Committed, await f.Repository().CreateAsync(entry));
        var actor = new DiaryPostgresFixture.Authority(Guid.NewGuid(), Guid.NewGuid());
        await LearningPostgresFixture.Profile(f.Database, new(actor.Account, actor.Profiles["learning"]), actor.Subject);
        var foreignRepo = f.Repository(actor); Assert.Equal(DiaryReadState.NotFound, (await foreignRepo.ReadAsync("learning", Day)).State);
        Assert.Empty((await foreignRepo.CalendarAsync("learning", Day, Day)).Dates!);
        Assert.Equal(DiaryWriteOutcome.Denied, await foreignRepo.EditAsync(entry));
        Assert.Equal(DiaryWriteOutcome.NotFound, await foreignRepo.DeleteAsync("learning", Day));
        Assert.Equal(entry.Reflection!.Body, (await f.Repository().ReadAsync("learning", Day)).Entry!.Reflection!.Body);
    }

    [LocalPostgresFact]
    public async Task PhysicalInactiveMembershipDeniesAllOrdinaryAccessWithoutDeletingEntries()
    {
        await using var f = await DiaryPostgresFixture.Create(); var repo = f.Repository(); var entry = f.Entry(Day);
        Assert.Equal(DiaryWriteOutcome.Committed, await repo.CreateAsync(entry)); Assert.Equal(DiaryWriteOutcome.Committed, await repo.CreateAsync(f.Entry(Day, "hobbies")));
        await f.Lifecycle("learning", false);
        Assert.Equal(DiaryReadState.Denied, (await repo.ReadAsync("learning", Day)).State); Assert.Null((await repo.CalendarAsync("learning", Day, Day)).Dates);
        Assert.Equal(DiaryWriteOutcome.Denied, await repo.EditAsync(entry)); Assert.Equal(DiaryWriteOutcome.Denied, await repo.DeleteAsync("learning", Day));
        Assert.Equal(DiaryWriteOutcome.Denied, await repo.CreateAsync(f.Entry(Day.AddDays(1))));
        Assert.Equal(DiaryReadState.Available, (await repo.ReadAsync("hobbies", Day)).State);
        await f.Lifecycle("learning", true); var restored = await repo.ReadAsync("learning", Day);
        Assert.Equal(entry.Reflection!.Body, restored.Entry!.Reflection!.Body); Assert.Equal(entry.Plans.Single().Id, restored.Entry.Plans.Single().Id);
    }

    [LocalPostgresFact]
    public async Task UnavailableRestrictedOrUnauthenticatedAuthorityFailsClosedWithoutDataRelease()
    {
        await using var f = await DiaryPostgresFixture.Create(); var entry = f.Entry(Day); Assert.Equal(DiaryWriteOutcome.Committed, await f.Repository().CreateAsync(entry));
        f.Owner.Unavailable = true; var unavailable = await f.Repository().ReadAsync("learning", Day); Assert.Equal(DiaryReadState.Unavailable, unavailable.State); Assert.Null(unavailable.Entry);
        Assert.Equal(DiaryWriteOutcome.Unavailable, await f.Repository().DeleteAsync("learning", Day)); Assert.Null((await f.Repository().CalendarAsync("learning", Day, Day)).Dates);
        f.Owner.Unavailable = false; f.Owner.Denied = true; Assert.Equal(DiaryWriteOutcome.Denied, await f.Repository().EditAsync(entry));
        f.Owner.Denied = false; f.Owner.Unauthenticated = true; Assert.Equal(DiaryReadState.Denied, (await f.Repository().ReadAsync("learning", Day)).State);
        f.Owner.Unauthenticated = false; Assert.Equal(entry.Reflection!.Body, (await f.Repository().ReadAsync("learning", Day)).Entry!.Reflection!.Body);
    }

    [LocalPostgresFact]
    public async Task ReadReleaseAndWriteCommitRecheckCurrentOwnerEligibility()
    {
        await using var f = await DiaryPostgresFixture.Create(); var entry = f.Entry(Day); Assert.Equal(DiaryWriteOutcome.Committed, await f.Repository().CreateAsync(entry));
        var reader = f.Repository(checkpoint: (phase, _) => { if (phase == "release") f.Owner.Denied = true; return Task.CompletedTask; });
        var denied = await reader.ReadAsync("learning", Day); Assert.Equal(DiaryReadState.Denied, denied.State); Assert.Null(denied.Entry); f.Owner.Denied = false;
        entry.EditReflection(null, "SYNTHETIC-REVOKED-EDIT");
        var writer = f.Repository(checkpoint: (phase, _) => { if (phase == "before-commit") f.Owner.Denied = true; return Task.CompletedTask; });
        Assert.Equal(DiaryWriteOutcome.Denied, await writer.EditAsync(entry)); f.Owner.Denied = false;
        Assert.Equal("SYNTHETIC-PRIVATE-DIARY", (await f.Repository().ReadAsync("learning", Day)).Entry!.Reflection!.Body);
    }

    [LocalPostgresFact]
    public async Task CancellationAfterActualInsertRollsBackWithoutPartialEntry()
    {
        await using var f = await DiaryPostgresFixture.Create(); using var cts = new CancellationTokenSource(); var reached = false;
        var repo = f.Repository(checkpoint: (phase, ct) => { if (phase == "prepared") { reached = true; cts.Cancel(); ct.ThrowIfCancellationRequested(); } return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.CreateAsync(f.Entry(Day), cts.Token)); Assert.True(reached);
        Assert.Equal(DiaryReadState.NotFound, (await f.Repository().ReadAsync("learning", Day)).State); Assert.Empty((await f.Repository().CalendarAsync("learning", Day, Day)).Dates!);
    }

    [LocalPostgresFact]
    public async Task PostPreparationFailureRollsBackEditAndDeleteWithoutTouchingOtherDates()
    {
        await using var f = await DiaryPostgresFixture.Create(); var entry = f.Entry(Day); var normal = f.Repository();
        Assert.Equal(DiaryWriteOutcome.Committed, await normal.CreateAsync(entry)); Assert.Equal(DiaryWriteOutcome.Committed, await normal.CreateAsync(f.Entry(Day.AddDays(1))));
        var failing = f.Repository(checkpoint: (phase, _) => phase == "prepared" ? Task.FromException(new InvalidOperationException("SYNTHETIC-ROLLBACK")) : Task.CompletedTask);
        entry.EditReflection(null, "SYNTHETIC-UNCOMMITTED");
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.EditAsync(entry)); await Assert.ThrowsAsync<InvalidOperationException>(() => failing.DeleteAsync("learning", Day));
        Assert.Equal("SYNTHETIC-PRIVATE-DIARY", (await normal.ReadAsync("learning", Day)).Entry!.Reflection!.Body);
        Assert.Equal(new[] { Day, Day.AddDays(1) }, (await normal.CalendarAsync("learning", Day, Day.AddDays(1))).Dates);
    }

    [LocalPostgresFact]
    public async Task LostCommitAcknowledgementReturnsUnknownAndFreshAuthorizedReadReconcilesState()
    {
        await using var f = await DiaryPostgresFixture.Create(); var entry = f.Entry(Day);
        Assert.Equal(DiaryWriteOutcome.CommitUnknown, await f.Repository(loseAcknowledgement: true).CreateAsync(entry));
        Assert.Equal(DiaryReadState.Available, (await f.Repository().ReadAsync("learning", Day)).State);
        Assert.Equal(DiaryWriteOutcome.Conflict, await f.Repository().CreateAsync(entry));
        Assert.Equal(DiaryWriteOutcome.CommitUnknown, await f.Repository(loseAcknowledgement: true).DeleteAsync("learning", Day));
        Assert.Equal(DiaryReadState.NotFound, (await f.Repository().ReadAsync("learning", Day)).State);
    }

    [LocalPostgresFact]
    public async Task RestrictedRuntimeRlsCannotChangeIdentityReadOtherModulesOrAcquireInfrastructurePrivileges()
    {
        await using var f = await DiaryPostgresFixture.Create(); foreach (var module in new[] { "learning", "hobbies" }) Assert.Equal(DiaryWriteOutcome.Committed, await f.Repository().CreateAsync(f.Entry(Day, module)));
        await using var db = f.OpenRuntime(); await DiaryPostgresFixture.Bind(db, f.Owner, "learning");
        Assert.Single(await db.Set<DiaryEntryRow>().ToListAsync()); Assert.Empty(await db.Set<DiaryEntryRow>().Where(r => r.Module == "hobbies").ToListAsync());
        foreach (var sql in new[] { "UPDATE andril_diary.module_diary_entries SET module='social'", "UPDATE andril_diary.module_diary_entries SET calendar_date=CURRENT_DATE", "UPDATE andril_diary.module_diary_entries SET owner_module_profile_id=gen_random_uuid()", "SELECT * FROM andril_identity.accounts", "CREATE TABLE andril_diary.forbidden(id integer)" })
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
        var forged = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_diary.module_diary_entries(owner_module_profile_id,module,calendar_date) VALUES({f.Owner.Profiles["hobbies"]},'hobbies',{Day.AddDays(1)})"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, forged.SqlState);
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT rolsuper OR rolbypassrls OR rolcreatedb OR rolcreaterole AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync());
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT relrowsecurity AND relforcerowsecurity AS \"Value\" FROM pg_class WHERE oid='andril_diary.module_diary_entries'::regclass").SingleAsync());
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [LocalPostgresFact]
    public async Task CalendarResolverPersistsLeapDateAndTimezoneChangesNeverRekeyStoredDates()
    {
        await using var f = await DiaryPostgresFixture.Create(); var clock = new Clock(DateTimeOffset.Parse("2024-02-29T00:30:00Z")); var resolver = new DiaryCalendarResolver(clock);
        var leap = ModuleDiaryEntry.CreateOffline(f.Owner.Profiles["learning"], "learning", resolver, "Asia/Colombo")!;
        leap.SetReflection(null, "SYNTHETIC-LEAP"); Assert.Equal(new DateOnly(2024, 2, 29), leap.Date); Assert.Equal(DiaryWriteOutcome.Committed, await f.Repository().CreateAsync(leap));
        var differentZone = ModuleDiaryEntry.CreateOffline(f.Owner.Profiles["learning"], "learning", resolver, "America/Los_Angeles")!;
        Assert.Equal(new DateOnly(2024, 2, 28), differentZone.Date); Assert.Equal(DiaryWriteOutcome.Committed, await f.Repository().CreateAsync(differentZone));
        clock.Now = DateTimeOffset.Parse("2026-11-26T23:59:00Z");
        Assert.Null(ModuleDiaryEntry.CreateOffline(f.Owner.Profiles["learning"], "learning", resolver, null));
        var historical = ModuleDiaryEntry.CreateOffline(f.Owner.Profiles["learning"], "learning", resolver, "invalid", new DateOnly(2000, 2, 29))!;
        Assert.Equal(DiaryWriteOutcome.Committed, await f.Repository().CreateAsync(historical));
        Assert.Equal("SYNTHETIC-LEAP", (await f.Repository().ReadAsync("learning", leap.Date)).Entry!.Reflection!.Body);
        Assert.Equal(new[] { historical.Date, differentZone.Date, leap.Date }, (await f.Repository().CalendarAsync("learning", DateOnly.MinValue, DateOnly.MaxValue)).Dates);
    }

    [LocalPostgresFact]
    public async Task FullDateOnlyDomainEndpointsRemainFiniteLiteralPostgreSqlDates()
    {
        await using var f = await DiaryPostgresFixture.Create(); var repo = f.Repository(); var first = f.Entry(DateOnly.MinValue); var last = f.Entry(DateOnly.MaxValue);
        Assert.Equal(DiaryWriteOutcome.Committed, await repo.CreateAsync(first)); Assert.Equal(DiaryWriteOutcome.Committed, await repo.CreateAsync(last));
        Assert.Equal(DateOnly.MinValue, (await repo.ReadAsync("learning", DateOnly.MinValue)).Entry!.Date);
        Assert.Equal(DateOnly.MaxValue, (await repo.ReadAsync("learning", DateOnly.MaxValue)).Entry!.Date);
        last.EditReflection(null, "SYNTHETIC-LAST-DATE-EDIT"); Assert.Equal(DiaryWriteOutcome.Committed, await repo.EditAsync(last));
        Assert.Equal(DiaryWriteOutcome.Committed, await repo.DeleteAsync("learning", DateOnly.MinValue));
        Assert.Equal(new[] { DateOnly.MaxValue }, (await repo.CalendarAsync("learning", DateOnly.MinValue, DateOnly.MaxValue)).Dates);
    }

    [LocalPostgresFact]
    public async Task DateOnlyAndExistingPlanLimitAreEnforcedByPostgreSqlWithoutNewBusinessPolicies()
    {
        await using var f = await DiaryPostgresFixture.Create(); await using var db = f.OpenRuntime(); await DiaryPostgresFixture.Bind(db, f.Owner, "learning");
        var badDate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_diary.module_diary_entries(owner_module_profile_id,module,calendar_date) VALUES({f.Owner.Profiles["learning"]},'learning',DATE '0001-01-01 BC')"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, badDate.SqlState); Assert.Equal("ck_diary_date", badDate.ConstraintName);
        var badPlans = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_diary.module_diary_entries(owner_module_profile_id,module,calendar_date,plans) VALUES({f.Owner.Profiles["learning"]},'learning',{Day},(SELECT jsonb_agg(i) FROM generate_series(1,21) i))"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, badPlans.SqlState); Assert.Equal("ck_diary_plans", badPlans.ConstraintName);
    }

    [LocalPostgresFact]
    public async Task MalformedStoredContentFailsClosedRatherThanReturningPartialPrivateData()
    {
        await using var f = await DiaryPostgresFixture.Create(); Assert.Equal(DiaryWriteOutcome.Committed, await f.Repository().CreateAsync(f.Entry(Day)));
        await using (var db = f.OpenOwner()) { await DiaryPostgresFixture.Bind(db, f.Owner, "learning"); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_diary.module_diary_entries SET reflection_body='' WHERE owner_module_profile_id={f.Owner.Profiles["learning"]} AND module='learning' AND calendar_date={Day}"); }
        var read = await f.Repository().ReadAsync("learning", Day); Assert.Equal(DiaryReadState.Unavailable, read.State); Assert.Null(read.Entry);
    }

    [LocalPostgresFact]
    public async Task PrivateResultsAndDefaultSerializationDoNotExposeDatesPlansOrReflectionToOtherConsumers()
    {
        await using var f = await DiaryPostgresFixture.Create(); var entry = f.Entry(Day); Assert.Equal(DiaryWriteOutcome.Committed, await f.Repository().CreateAsync(entry));
        var result = await f.Repository().ReadAsync("learning", Day); var calendar = await f.Repository().CalendarAsync("learning", Day, Day);
        foreach (var text in new[] { JsonSerializer.Serialize(result), JsonSerializer.Serialize(result.Entry), JsonSerializer.Serialize(calendar), result.ToString(), calendar.ToString() })
        { Assert.DoesNotContain("SYNTHETIC", text); Assert.DoesNotContain(f.Owner.Profiles["learning"].ToString(), text); Assert.DoesNotContain(Day.ToString("yyyy-MM-dd"), text); }
    }

    [LocalPostgresFact]
    public async Task DiaryMigrationHasSeparateHistoryAndRollsBackReappliesWithoutChangingM2SchemaOrHistory()
    {
        await using var f = await DiaryPostgresFixture.Create(); await using var accounts = f.Database.Open(); var baseline = (await accounts.Database.GetAppliedMigrationsAsync()).ToArray();
        await using var db = f.OpenOwner(); Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal("20261126080000_ModuleDiaryPersistence", Assert.Single(await db.Database.GetAppliedMigrationsAsync()));
        await db.GetService<IMigrator>().MigrateAsync("0"); Assert.Empty(await db.Database.GetAppliedMigrationsAsync()); await db.Database.MigrateAsync();
        Assert.Equal(baseline, (await accounts.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT relrowsecurity AND relforcerowsecurity AS \"Value\" FROM pg_class WHERE oid='andril_diary.module_diary_entries'::regclass").SingleAsync());
    }
}
