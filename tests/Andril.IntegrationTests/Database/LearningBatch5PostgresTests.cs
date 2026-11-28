using System.Text;
using Andril.API.Integrations.Learning;
using Andril.Learning.Activities;
using Andril.Learning.CV;
using Andril.Learning.Interfaces;
using Andril.Learning.Progress;
using Andril.Persistence.Repositories.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningBatch5PostgresTests
{
    [LocalPostgresFact]
    public async Task PersistedComplexScriptCvPreservesContentConsentAndCurrentOwnerAcrossTemplates()
    {
        const string title="தமிழ் கல்வி සිංහල අධ්‍යාපනය Cafe\u0301";
        await using var f=await LearningGroupCPostgresFixture.Create();
        foreach(var template in Enum.GetValues<CvTemplate>())
        {
            var cv=(await f.Cv.Service.CreateAsync(template)).Cv!;var section=cv.GetSection(CvSectionCategory.Projects);
            var entry=Guid.NewGuid();
            Assert.Equal(CvOperationState.Available,(await f.Cv.Service.SaveSectionAsync(cv.Id,section.Id,section.Category,[new(entry,section.Id,"WITHHELD_LEGACY")])).State);
            Assert.Equal(CvOperationState.Available,(await f.Cv.Repository().SaveAsync(cv.Id,CvSaveChange.ReviewedFields(section.Category,[CvStructuredFields.ReviewedEntry(entry,section.Id,section.Category,new Dictionary<string,string>{{"ProjectTitle",title},{"Description",string.Join('\n',Enumerable.Repeat(title,65))}})]))).State);
            var personal=cv.GetSection(CvSectionCategory.PersonalInformation);
            await f.Cv.Repository().SaveAsync(cv.Id,CvSaveChange.ReviewedFields(personal.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),personal.Id,personal.Category,new Dictionary<string,string>{{"Email","private@example.test"}})]));
            var reopened=(await f.Cv.Service.ReopenAsync(cv.Id)).Cv!;var stored=Assert.Single(reopened.GetSection(section.Category).Entries);
            Assert.Equal(entry,stored.Id);Assert.Equal("WITHHELD_LEGACY",stored.Text);Assert.Equal(title,stored.Fields["ProjectTitle"]);
            var result=await f.Cv.Service.ExportPdfAsync(cv.Id);Assert.Equal(CvOperationState.Available,result.State);
            var pdf=Encoding.Latin1.GetString(result.Document!.Bytes);
            Assert.Contains("NotoSansTamil-Regular",pdf);Assert.Contains("NotoSansSinhala-Regular",pdf);Assert.Contains("/ActualText",pdf);
            var contact=Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("private@example.test"));
            Assert.DoesNotContain(contact,pdf);Assert.DoesNotContain(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("WITHHELD_LEGACY")),pdf);
            await f.Cv.Service.SetContactConsentAsync(cv.Id,true);
            Assert.Contains(contact,Encoding.Latin1.GetString((await f.Cv.Service.ExportPdfAsync(cv.Id)).Document!.Bytes));
            await f.Cv.Service.SetContactConsentAsync(cv.Id,false);
            Assert.DoesNotContain(contact,Encoding.Latin1.GetString((await f.Cv.Service.ExportPdfAsync(cv.Id)).Document!.Bytes));
            f.Cv.Owner.Denied=true;Assert.Equal(CvOperationState.Denied,(await f.Cv.Service.ExportPdfAsync(cv.Id)).State);f.Cv.Owner.Denied=false;
        }
    }
    [LocalPostgresFact]
    public async Task SharedOwnerUsesActualAccountRowAndRechecksLifecycleForPrivateActivities()
    {
        await using var f=await LearningGroupBPostgresFixture.Create();
        await using var accounts=f.Activities.Shared.Shared.Shared.Shared.Database.Open();
        var user=f.Activities.Shared.Owner;var boundary=new CurrentLearningOwnerBoundary(user,new AccountRepository(accounts),user,user);
        var owner=await boundary.WithCurrentOwnerAsync<CurrentLearningOwner>((value,_)=>Task.FromResult<CurrentLearningOwner?>(value));
        Assert.Equal(user.Account,owner!.AccountId);Assert.Equal(user.Profiles["learning"],owner.ProfileId);
        Assert.Null(await boundary.WithCurrentOwnerAsync<CurrentLearningOwner>((value,_)=>Task.FromResult<CurrentLearningOwner?>(value),expectedAccountId:Guid.NewGuid()));
        var authority=new CurrentLearningOwnerActivityAuthority(boundary);
        var repository=f.Activities.RepositoryFor(user,authority);
        var created=await repository.CreateAsync(LearningActivityKind.GrowthSelfReport,new(SkillIds:[]));Assert.Equal(LearningActivityOutcome.Saved,created.Outcome);
        Assert.Equal(LearningActivityOutcome.Available,(await repository.ReadAsync(LearningActivityKind.GrowthSelfReport,created.Activity!.Id)).Outcome);
        user.Denied=true;Assert.Null(await boundary.WithCurrentOwnerAsync<CurrentLearningOwner>((value,_)=>Task.FromResult<CurrentLearningOwner?>(value)));
        Assert.Equal(LearningActivityOutcome.Denied,(await repository.ReadAsync(LearningActivityKind.GrowthSelfReport,created.Activity.Id)).Outcome);
    }
    [LocalPostgresFact]
    public async Task PersistedUnicodeCvUsesPinnedEmbeddedFontsWhileLegacyAndContactRemainPrivate()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();
        foreach(var template in Enum.GetValues<CvTemplate>())
        {
            var cv=(await f.Cv.Service.CreateAsync(template)).Cv!;var section=cv.GetSection(CvSectionCategory.Projects);var legacy=Guid.NewGuid();
            await f.Cv.Service.SaveSectionAsync(cv.Id,section.Id,section.Category,[new(legacy,section.Id,"LEGACY_PRIVATE_CONTENT")]);
            await f.Cv.Repository().SaveAsync(cv.Id,CvSaveChange.ReviewedFields(section.Category,[CvStructuredFields.ReviewedEntry(legacy,section.Id,section.Category,new Dictionary<string,string>{{"ProjectTitle","Éducation — Ελληνικά — Привет"}})]));
            var person=cv.GetSection(CvSectionCategory.PersonalInformation);
            await f.Cv.Repository().SaveAsync(cv.Id,CvSaveChange.ReviewedFields(person.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),person.Id,person.Category,new Dictionary<string,string>{{"Email","withheld@example.test"}})]));
            var reopened=(await f.Cv.Service.ReopenAsync(cv.Id)).Cv!;
            Assert.Equal("LEGACY_PRIVATE_CONTENT",reopened.GetSection(section.Category).Entries[0].Text);
            var export=await f.Cv.Service.ExportPdfAsync(cv.Id);Assert.Equal(CvOperationState.Available,export.State);
            var pdf=Encoding.Latin1.GetString(export.Document!.Bytes);Assert.Contains("/FontFile2",pdf);Assert.Contains("/ToUnicode",pdf);Assert.DoesNotContain("LEGACY_PRIVATE_CONTENT",pdf);Assert.DoesNotContain("withheld@example.test",pdf);
        }
        f.Cv.Owner.Denied=true;var existing=await f.Cv.Repository().ListAsync();Assert.Equal(CvOperationState.Denied,existing.State);
    }
}
