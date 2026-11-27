using System.Text.Json;
using Andril.Learning.CV;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Andril.IntegrationTests.Database;

/// <summary>Actual CV application/repository/PostgreSQL with existing synthetic Common gates
/// only in the disposable host. Does not claim a production field schema, consent receipt or API.</summary>
public sealed class CvRepositoryPostgresTests
{
    private static LearningCv Cv(CvReadResult result)
    { Assert.Equal(CvOperationState.Available, result.State); return Assert.IsType<LearningCv>(result.Cv); }

    [LocalPostgresFact]
    public async Task MultipleIndependentOwnerCvsSupportAllApprovedTemplatesWithoutACountCap()
    {
        await using var f = await CvPostgresFixture.Create(); var cvs = new List<LearningCv>();
        foreach (var template in new[] { CvTemplate.Modern, CvTemplate.Professional, CvTemplate.Creative, CvTemplate.Modern })
        { var cv = Cv(await f.Service.CreateAsync(template)); cvs.Add(cv); Assert.Equal(template, cv.Template); Assert.False(cv.IncludeContactInformation); Assert.Equal(7, cv.Sections.Count); }
        var ids = (await f.Service.ListOwnAsync()).CvIds!; Assert.Equal(4, ids.Count); Assert.Equal(4, ids.Distinct().Count());
        Assert.All(cvs, cv => Assert.Contains(cv.Id, ids));
    }

    [LocalPostgresFact]
    public async Task AllSevenStructuredSectionsSaveAndReopenWithExactSectionAndEntryIdentities()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern));
        var originalSections = cv.Sections.Select(s => s.Id).ToArray(); var entries = new List<Guid>();
        foreach (var section in cv.Sections)
        {
            var entry = new CvSection.Entry(Guid.NewGuid(), section.Id, " SYNTHETIC தமிழ் e\u0301 " + section.Category); entries.Add(entry.Id);
            Cv(await f.Service.SaveSectionAsync(cv.Id, section.Id, section.Category, [entry]));
        }
        var reopened = Cv(await f.Service.ReopenAsync(cv.Id)); Assert.Equal(originalSections, reopened.Sections.Select(s => s.Id));
        Assert.Equal(entries, reopened.Sections.SelectMany(s => s.Entries).Select(e => e.Id));
        foreach (var section in reopened.Sections) Assert.Equal(" SYNTHETIC தமிழ் e\u0301 " + section.Category, section.Entries.Single().Text);
        Assert.All(cv.Sections, s => Assert.Empty(s.Entries)); // Earlier detached snapshot unchanged.
    }

    [LocalPostgresFact]
    public async Task TemplateSwitchingIsLosslessAndNeverChangesAnotherCvOrContactChoice()
    {
        await using var f = await CvPostgresFixture.Create(); var a = Cv(await f.Service.CreateAsync(CvTemplate.Modern)); var b = Cv(await f.Service.CreateAsync(CvTemplate.Creative));
        foreach (var section in a.Sections) Cv(await f.Service.SaveSectionAsync(a.Id, section.Id, section.Category, [new(Guid.NewGuid(), section.Id, "SYNTHETIC-" + section.Category)]));
        var before = Cv(await f.Service.SetContactConsentAsync(a.Id, true));
        foreach (var template in Enum.GetValues<CvTemplate>())
        {
            var changed = Cv(await f.Service.SelectTemplateAsync(a.Id, template)); Assert.Equal(template, changed.Template); Assert.True(changed.IncludeContactInformation);
            Assert.Equal(before.Sections.Select(s => s.Id), changed.Sections.Select(s => s.Id));
            Assert.Equal(before.Sections.SelectMany(s => s.Entries).Select(e => (e.Id, e.Text)), changed.Sections.SelectMany(s => s.Entries).Select(e => (e.Id, e.Text)));
        }
        var other = Cv(await f.Service.ReopenAsync(b.Id)); Assert.Equal(CvTemplate.Creative, other.Template); Assert.False(other.IncludeContactInformation); Assert.All(other.Sections, s => Assert.Empty(s.Entries));
    }

    [LocalPostgresFact]
    public async Task CallerHeldEditsAreNotAutosavedAndReopeningUsesCommittedState()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern));
        cv.SelectTemplate(CvTemplate.Creative); cv.SetContactInclusionConsent(cv.Id, true);
        var stored = Cv(await f.Service.ReopenAsync(cv.Id)); Assert.Equal(CvTemplate.Modern, stored.Template); Assert.False(stored.IncludeContactInformation);
        Cv(await f.Service.SelectTemplateAsync(cv.Id, CvTemplate.Professional)); Assert.Equal(CvTemplate.Professional, Cv(await f.Service.ReopenAsync(cv.Id)).Template);
    }

    [LocalPostgresFact]
    public async Task ExplicitContactConsentIsPrivateByDefaultPersistedPerDocumentAndRevocable()
    {
        await using var f = await CvPostgresFixture.Create(); var a = Cv(await f.Service.CreateAsync(CvTemplate.Modern)); var b = Cv(await f.Service.CreateAsync(CvTemplate.Modern));
        Assert.False(a.IncludeContactInformation); Assert.False(b.IncludeContactInformation);
        Cv(await f.Service.SetContactConsentAsync(a.Id, true)); Assert.True(Cv(await f.Service.ReopenAsync(a.Id)).IncludeContactInformation);
        Assert.False(Cv(await f.Service.ReopenAsync(b.Id)).IncludeContactInformation);
        Cv(await f.Service.SetContactConsentAsync(a.Id, false)); Assert.False(Cv(await f.Service.ReopenAsync(a.Id)).IncludeContactInformation);
    }

    [LocalPostgresFact]
    public async Task StructuredPreviewNeverDisclosesUnclassifiedContactTextEvenWithConsent()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern));
        foreach (var s in cv.Sections) Cv(await f.Service.SaveSectionAsync(cv.Id, s.Id, s.Category, [new(Guid.NewGuid(), s.Id, "SYNTHETIC-PRIVATE@example.test <img src='https://external.invalid' onerror='secret()'> javascript:secret")]));
        foreach (var consent in new[] { false, true, false })
        {
            Cv(await f.Service.SetContactConsentAsync(cv.Id, consent)); var preview = await f.Service.PreviewAsync(cv.Id); Assert.Equal(CvOperationState.Available, preview.State);
            var html = preview.Preview!.Html; foreach (var marker in new[] { "SYNTHETIC", "external.invalid", "onerror", "javascript:", cv.Id.ToString(), cv.OwnerLearningProfileId.ToString() }) Assert.DoesNotContain(marker, html);
            foreach (var category in Enum.GetValues<CvSectionCategory>()) Assert.Contains(category.ToString(), html);
        }
        Cv(await f.Service.SelectTemplateAsync(cv.Id, CvTemplate.Professional)); Assert.Contains("Professional", (await f.Service.PreviewAsync(cv.Id)).Preview!.Html);
    }

    [LocalPostgresFact]
    public async Task ForeignAccountCannotOpenSavePreviewOrEnumerateAnotherOwnersCv()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern));
        var foreign = new DiaryPostgresFixture.Authority(Guid.NewGuid(), Guid.NewGuid());
        await LearningPostgresFixture.Profile(f.Shared.Database, new(foreign.Account, foreign.Profiles["learning"]), foreign.Subject);
        var repo = f.Repository(foreign); Assert.Equal(CvOperationState.NotFound, (await repo.OpenAsync(cv.Id)).State); Assert.Empty((await repo.ListAsync()).CvIds!);
        Assert.Equal(CvOperationState.NotFound, (await repo.SaveAsync(cv.Id, CvSaveChange.ContactConsent(true))).State);
        Assert.Equal(CvOperationState.NotFound, (await repo.PreviewAsync(cv.Id)).State); Assert.False(Cv(await f.Service.ReopenAsync(cv.Id)).IncludeContactInformation);
    }

    [LocalPostgresFact]
    public async Task ForgedCommonProfileEvidenceCannotTurnHobbiesOrForeignProfilesIntoLearningOwnership()
    {
        await using var f = await CvPostgresFixture.Create(); var fake = new DiaryPostgresFixture.Authority(f.Owner.Subject, f.Owner.Account);
        fake.Profiles["learning"] = f.Owner.Profiles["hobbies"];
        Assert.Equal(CvOperationState.Denied, (await f.Repository(fake).CreateAsync(CvTemplate.Modern)).State);
        fake.Profiles["learning"] = Guid.NewGuid(); Assert.Equal(CvOperationState.Denied, (await f.Repository(fake).ListAsync()).State);
        Assert.Empty((await f.Service.ListOwnAsync()).CvIds!);
    }

    [LocalPostgresFact]
    public async Task InactiveLearningProfileCannotAccessButReactivationPreservesCvsAndConsent()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Creative)); Cv(await f.Service.SetContactConsentAsync(cv.Id, true));
        await f.Shared.Lifecycle("learning", false);
        Assert.Equal(CvOperationState.Denied, (await f.Service.ReopenAsync(cv.Id)).State); Assert.Null((await f.Service.ListOwnAsync()).CvIds);
        Assert.Equal(CvOperationState.Denied, (await f.Service.SelectTemplateAsync(cv.Id, CvTemplate.Modern)).State);
        Assert.Equal(CvOperationState.Denied, (await f.Service.CreateAsync(CvTemplate.Modern)).State); Assert.Null((await f.Service.PreviewAsync(cv.Id)).Preview);
        await f.Shared.Lifecycle("learning", true); var restored = Cv(await f.Service.ReopenAsync(cv.Id)); Assert.Equal(CvTemplate.Creative, restored.Template); Assert.True(restored.IncludeContactInformation);
    }

    [LocalPostgresFact]
    public async Task UnavailableRestrictedAndUnauthenticatedAuthorityCannotReleaseOrChangePrivateState()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern));
        f.Owner.Unavailable = true; var unavailable = await f.Service.ReopenAsync(cv.Id); Assert.Equal(CvOperationState.Unavailable, unavailable.State); Assert.Null(unavailable.Cv);
        Assert.Equal(CvOperationState.Unavailable, (await f.Service.SetContactConsentAsync(cv.Id, true)).State); Assert.Null((await f.Service.PreviewAsync(cv.Id)).Preview);
        f.Owner.Unavailable = false; f.Owner.Denied = true; Assert.Equal(CvOperationState.Denied, (await f.Service.ListOwnAsync()).State);
        f.Owner.Denied = false; f.Owner.Unauthenticated = true; Assert.Equal(CvOperationState.Denied, (await f.Service.ReopenAsync(cv.Id)).State);
        f.Owner.Unauthenticated = false; Assert.False(Cv(await f.Service.ReopenAsync(cv.Id)).IncludeContactInformation);
    }

    [LocalPostgresFact]
    public async Task InvalidTemplateSectionBindingAndCrossSectionEntryReuseRejectWithoutChangingStoredContent()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern)); var a = cv.Sections[0]; var b = cv.Sections[1]; var id = Guid.NewGuid();
        Cv(await f.Service.SaveSectionAsync(cv.Id, a.Id, a.Category, [new(id, a.Id, "SYNTHETIC-KEEP")]));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SelectTemplateAsync(cv.Id, (CvTemplate)99));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SaveSectionAsync(cv.Id, Guid.NewGuid(), a.Category, []));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SaveSectionAsync(cv.Id, b.Id, b.Category, [new(id, b.Id, "duplicate")]));
        var stored = Cv(await f.Service.ReopenAsync(cv.Id)); Assert.Equal(CvTemplate.Modern, stored.Template); Assert.Equal("SYNTHETIC-KEEP", stored.Sections[0].Entries.Single().Text); Assert.Empty(stored.Sections[1].Entries);
    }

    [LocalPostgresFact]
    public async Task CancellationAndInjectedPersistenceFailureRollBackActualSavesAndNewDocuments()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern)); using var cts = new CancellationTokenSource(); var reached = false;
        var canceled = f.Repository(checkpoint: (phase, ct) => { if (phase == "prepared") { reached = true; cts.Cancel(); ct.ThrowIfCancellationRequested(); } return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.SaveAsync(cv.Id, CvSaveChange.Template(CvTemplate.Creative), cts.Token)); Assert.True(reached);
        Assert.Equal(CvTemplate.Modern, Cv(await f.Service.ReopenAsync(cv.Id)).Template);
        var failed = f.Repository(checkpoint: (phase, _) => phase == "prepared" ? Task.FromException(new IOException("SYNTHETIC-PRIVATE-ERROR")) : Task.CompletedTask);
        var result = await failed.CreateAsync(CvTemplate.Modern); Assert.Equal(CvOperationState.Unavailable, result.State); Assert.Null(result.Cv); Assert.Single((await f.Service.ListOwnAsync()).CvIds!);
    }

    [LocalPostgresFact]
    public async Task ReadPreviewReleaseAndWriteCommitRecheckCurrentOwnerEligibility()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern));
        var reader = f.Repository(checkpoint: (phase, _) => { if (phase == "read-release") f.Owner.Denied = true; return Task.CompletedTask; });
        Assert.Null((await reader.PreviewAsync(cv.Id)).Preview); f.Owner.Denied = false;
        var writer = f.Repository(checkpoint: (phase, _) => { if (phase == "before-commit") f.Owner.Denied = true; return Task.CompletedTask; });
        Assert.Equal(CvOperationState.Denied, (await writer.SaveAsync(cv.Id, CvSaveChange.ContactConsent(true))).State); f.Owner.Denied = false;
        Assert.False(Cv(await f.Service.ReopenAsync(cv.Id)).IncludeContactInformation);
    }

    [LocalPostgresFact]
    public async Task ConcurrentDifferentSectionSavesPreserveBothChangesWithoutReplacingUnrelatedFields()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern)); var a = cv.Sections[0]; var b = cv.Sections[1];
        var results = await Task.WhenAll(f.Service.SaveSectionAsync(cv.Id, a.Id, a.Category, [new(Guid.NewGuid(), a.Id, "LEFT")]), f.Service.SaveSectionAsync(cv.Id, b.Id, b.Category, [new(Guid.NewGuid(), b.Id, "RIGHT")]));
        Assert.All(results, r => Assert.Equal(CvOperationState.Available, r.State)); var saved = Cv(await f.Service.ReopenAsync(cv.Id));
        Assert.Equal("LEFT", saved.Sections[0].Entries.Single().Text); Assert.Equal("RIGHT", saved.Sections[1].Entries.Single().Text); Assert.Equal(CvTemplate.Modern, saved.Template); Assert.False(saved.IncludeContactInformation);
    }

    [LocalPostgresFact]
    public async Task LostAcknowledgementReturnsUnknownWithoutClaimingRollbackOrReplayingCreation()
    {
        await using var f = await CvPostgresFixture.Create(); var result = await f.Repository(loseAck: true).CreateAsync(CvTemplate.Professional);
        Assert.Equal(CvOperationState.CommitUnknown, result.State); Assert.Null(result.Cv); var id = Assert.Single((await f.Service.ListOwnAsync()).CvIds!);
        Assert.Equal(CvTemplate.Professional, Cv(await f.Service.ReopenAsync(id)).Template);
    }

    [LocalPostgresFact]
    public async Task RestrictedRuntimeRlsAndColumnGrantsPreventScopeChangesDeletionAndInfrastructureAccess()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern));
        await using var db = f.OpenRuntime(); await CvPostgresFixture.Bind(db, f.Owner); Assert.Single(await db.Set<LearningCvRow>().ToListAsync());
        foreach (var sql in new[] { "UPDATE andril_learning.learning_cvs SET owner_learning_profile_id=gen_random_uuid()", "UPDATE andril_learning.learning_cvs SET module='social'", "UPDATE andril_learning.learning_cvs SET cv_id=gen_random_uuid()", "DELETE FROM andril_learning.learning_cvs", "SELECT * FROM andril_identity.accounts", "CREATE TABLE andril_learning.forbidden_cv(id integer)" })
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_learning.learning_cvs SELECT * FROM andril_learning.learning_cvs WHERE cv_id={cv.Id}"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState); Assert.Equal("pk_learning_cvs", duplicate.ConstraintName);
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT relrowsecurity AND relforcerowsecurity AS \"Value\" FROM pg_class WHERE oid='andril_learning.learning_cvs'::regclass").SingleAsync());
    }

    [LocalPostgresFact]
    public async Task CvSavesNeverAdvanceLearningPreferenceRevisionsOrAlterGoalsSelectionsAndDiary()
    {
        await using var f = await CvPostgresFixture.Create(); await using var owner = f.OpenOwner();
        await owner.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_learning.learning_personal_goals(id,learning_profile_id,module,text) VALUES({Guid.NewGuid()},{f.Owner.Profiles["learning"]},'learning','SYNTHETIC-PREFERENCE-GOAL')");
        var before = await owner.Set<LearningProfileRevisionRow>().AsNoTracking().SingleAsync();
        var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern)); Cv(await f.Service.SetContactConsentAsync(cv.Id, true)); Cv(await f.Service.SelectTemplateAsync(cv.Id, CvTemplate.Creative));
        var after = await owner.Set<LearningProfileRevisionRow>().AsNoTracking().SingleAsync(); Assert.Equal(before.Version, after.Version); Assert.Equal(before.GenerationId, after.GenerationId);
        Assert.Equal("SYNTHETIC-PREFERENCE-GOAL", (await owner.PersonalGoals.SingleAsync()).Text); Assert.Empty(await owner.SkillSelections.ToListAsync());
    }

    [LocalPostgresFact]
    public async Task CorruptStoredShapeFailsClosedAndPrivateResultsNeverSerializeGenericContent()
    {
        await using var f = await CvPostgresFixture.Create(); var cv = Cv(await f.Service.CreateAsync(CvTemplate.Modern)); var s = cv.Sections[0];
        var read = await f.Service.SaveSectionAsync(cv.Id, s.Id, s.Category, [new(Guid.NewGuid(), s.Id, "SYNTHETIC-PRIVATE")]);
        Assert.Equal("{}", JsonSerializer.Serialize(read)); Assert.DoesNotContain("SYNTHETIC", read.ToString());
        await using var owner = f.OpenOwner(); await CvPostgresFixture.Bind(owner, f.Owner);
        await owner.Database.ExecuteSqlInterpolatedAsync($"UPDATE andril_learning.learning_cvs SET sections=(SELECT jsonb_agg(NULL::jsonb) FROM generate_series(1,7)) WHERE cv_id={cv.Id}");
        var invalid = await f.Service.ReopenAsync(cv.Id); Assert.Equal(CvOperationState.Unavailable, invalid.State); Assert.Null(invalid.Cv); Assert.Null((await f.Service.PreviewAsync(cv.Id)).Preview);
    }

    [LocalPostgresFact]
    public async Task CvMigrationRollsBackReappliesWhilePreservingExistingLearningAndM2Data()
    {
        await using var f = await CvPostgresFixture.Create(); await using var db = f.OpenOwner();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO andril_learning.learning_personal_goals(id,learning_profile_id,module,text) VALUES({Guid.NewGuid()},{f.Owner.Profiles["learning"]},'learning','SYNTHETIC-KEEP-GOAL')");
        var revision = await db.Set<LearningProfileRevisionRow>().AsNoTracking().SingleAsync(); await using var accounts = f.Shared.Database.Open(); var history = (await accounts.Database.GetAppliedMigrationsAsync()).ToArray();
        Assert.False(db.Database.HasPendingModelChanges()); await db.GetService<IMigrator>().MigrateAsync("20261125100000_LearningDurableRevisions");
        Assert.Equal("20261125100000_LearningDurableRevisions", Assert.Single(await db.Database.GetAppliedMigrationsAsync())); await db.Database.MigrateAsync();
        Assert.Equal(history, (await accounts.Database.GetAppliedMigrationsAsync()).ToArray()); Assert.Equal("SYNTHETIC-KEEP-GOAL", (await db.PersonalGoals.SingleAsync()).Text);
        Assert.Equal(revision.Version, (await db.Set<LearningProfileRevisionRow>().AsNoTracking().SingleAsync()).Version);
    }
}
