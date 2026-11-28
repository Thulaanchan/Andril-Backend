using System.Text;
using Andril.Learning.CV;
using Andril.Learning.Progress;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningGroupCPostgresTests
{
    [LocalPostgresFact]
    public async Task IndependentConnectionsCannotExceedConfiguredTotalOrReverseCreditTwice()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();await f.ConfigureSyntheticPolicy(cap:10);
        var awards=await Task.WhenAll(f.Repository().CreditAsync(f.VerifiedSource()),f.Repository().CreditAsync(f.VerifiedSource()));
        Assert.Equal(1,awards.Count(x=>x.Outcome==LearningXpOutcome.Credited));Assert.Equal(1,awards.Count(x=>x.Outcome==LearningXpOutcome.Conflict));
        var credit=Assert.Single((await f.Repository().ReadAsync()).Entries!);
        var first=new LearningXpAdjustment(Guid.NewGuid(),credit.Id,LearningXpAdjustmentKind.Reversal);var second=first with{OperationId=Guid.NewGuid()};
        f.Authority.Adjustments[first.OperationId]=(first,-7,"SYNTHETIC race one");f.Authority.Adjustments[second.OperationId]=(second,-7,"SYNTHETIC race two");
        var adjustments=await Task.WhenAll(f.Repository().AdjustAsync(first),f.Repository().AdjustAsync(second));
        Assert.Equal(1,adjustments.Count(x=>x.Outcome==LearningXpOutcome.Credited));Assert.Equal(1,adjustments.Count(x=>x.Outcome==LearningXpOutcome.Conflict));
        Assert.Equal(0,(await f.Repository().ReadAsync()).Total);
    }
    [LocalPostgresFact]
    public async Task ApprovedPolicyVersionsAreDurableImmutableAndRecheckedBeforeAwardCommit()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();
        Assert.Equal(LearningXpOutcome.Unavailable,(await f.Repository().StageApprovedPolicyAsync("not-approved")).Outcome);
        f.Authority.ConfigureSyntheticPolicy();var source=f.VerifiedSource();
        Assert.Equal(LearningXpOutcome.Unavailable,(await f.Repository().CreditAsync(source)).Outcome);
        Assert.Equal(LearningXpOutcome.PolicyStored,(await f.Repository().StageApprovedPolicyAsync(f.Authority.Policy.Revision)).Outcome);
        Assert.Equal(LearningXpOutcome.PolicyStored,(await f.Repository().StageApprovedPolicyAsync(f.Authority.Policy.Revision)).Outcome);
        var original=f.Authority.Policy;f.Authority.Policy=original with{MaximumTotal=10};
        Assert.Equal(LearningXpOutcome.Conflict,(await f.Repository().StageApprovedPolicyAsync(original.Revision)).Outcome);
        f.Authority.Policy=original;
        var denied=await f.Repository((phase,_)=>{if(phase=="before-commit")f.Authority.Policy=original with{MaximumTotal=10};return Task.CompletedTask;}).CreditAsync(source);
        Assert.Equal(LearningXpOutcome.Denied,denied.Outcome);f.Authority.Policy=original;
        Assert.Equal(0,(await f.Repository().ReadAsync()).Total);
        await using var db=await f.OpenBoundRuntime();Assert.Single(await db.Set<LearningXpPolicyRow>().ToListAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,'andril_learning_xp_isolated.xp_policy_versions','UPDATE') OR has_table_privilege(current_user,'andril_learning_xp_isolated.xp_policy_versions','DELETE') AS \"Value\"").SingleAsync());
    }
    [LocalPostgresFact]
    public async Task XpDefaultsToUnconfiguredAndRejectsUnverifiedClientLikeEventsWithoutCredit()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();var source=f.VerifiedSource();
        Assert.Equal(LearningXpOutcome.Unavailable,(await f.Repository().CreditAsync(source)).Outcome);
        await f.ConfigureSyntheticPolicy();
        foreach(var kind in new[]{"like","message","connection","purchase","quiz","mission"})
            Assert.Equal(LearningXpOutcome.Unavailable,(await f.Repository().CreditAsync(new(kind,source.EventId))).Outcome);
        Assert.Equal(LearningXpOutcome.Unavailable,(await f.Repository().CreditAsync(new("synthetic-verified-completion",Guid.NewGuid()))).Outcome);
        Assert.Equal(0,(await f.Repository().ReadAsync()).Total);
    }
    [LocalPostgresFact]
    public async Task VerifiedCreditsAreDurableIdempotentAndConcurrentDuplicatesDoNotMintAgain()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();await f.ConfigureSyntheticPolicy();var source=f.VerifiedSource();
        var result=await Task.WhenAll(f.Repository().CreditAsync(source),f.Repository().CreditAsync(source));
        Assert.Equal(1,result.Count(x=>x.Outcome==LearningXpOutcome.Credited));Assert.Equal(1,result.Count(x=>x.Outcome==LearningXpOutcome.AlreadyCredited));
        var read=await f.Repository().ReadAsync();Assert.Equal(7,read.Total);Assert.Single(read.Entries!);Assert.Equal("synthetic-level",read.Progress!.Level);
        Assert.Equal(LearningXpOutcome.AlreadyCredited,(await f.Repository().CreditAsync(source)).Outcome);
        Assert.Equal(LearningXpOutcome.Credited,(await f.Repository().CreditAsync(f.VerifiedSource())).Outcome);
        Assert.Contains("synthetic-achievement",(await f.Repository().ReadAsync()).Progress!.Achievements);
    }
    [LocalPostgresFact]
    public async Task VersionedTotalAndOccurrenceCapsAreAppliedOnlyFromVerifiedSyntheticConfiguration()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();await f.ConfigureSyntheticPolicy(cap:10,occurrences:1);var first=f.VerifiedSource();
        Assert.Equal(LearningXpOutcome.Credited,(await f.Repository().CreditAsync(first)).Outcome);
        Assert.Equal(LearningXpOutcome.Conflict,(await f.Repository().CreditAsync(f.VerifiedSource())).Outcome);
        f.Authority.Policy=f.Authority.Policy with{Revision="SYNTHETIC-TEST-POLICY-v2",EventAmounts=new Dictionary<string,long>{{first.Kind,9}}};
        Assert.Equal(LearningXpOutcome.PolicyStored,(await f.Repository().StageApprovedPolicyAsync(f.Authority.Policy.Revision)).Outcome);
        Assert.Equal(LearningXpOutcome.Conflict,(await f.Repository().CreditAsync(first)).Outcome);Assert.Equal(7,(await f.Repository().ReadAsync()).Total);
    }
    [LocalPostgresFact]
    public async Task AuditedCorrectionsAndReversalsAppendOnceAndPreserveOriginalCredits()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();await f.ConfigureSyntheticPolicy();Assert.Equal(LearningXpOutcome.Credited,(await f.Repository().CreditAsync(f.VerifiedSource())).Outcome);
        var credit=Assert.Single((await f.Repository().ReadAsync()).Entries!);
        var correction=new LearningXpAdjustment(Guid.NewGuid(),credit.Id,LearningXpAdjustmentKind.Correction);
        f.Authority.Adjustments[correction.OperationId]=(correction,2,"SYNTHETIC audited correction");
        Assert.Equal(LearningXpOutcome.Credited,(await f.Repository().AdjustAsync(correction)).Outcome);
        Assert.Equal(LearningXpOutcome.AlreadyCredited,(await f.Repository().AdjustAsync(correction)).Outcome);Assert.Equal(9,(await f.Repository().ReadAsync()).Total);
        var reversal=new LearningXpAdjustment(Guid.NewGuid(),credit.Id,LearningXpAdjustmentKind.Reversal);
        f.Authority.Adjustments[reversal.OperationId]=(reversal,-9,"SYNTHETIC verified reversal");
        Assert.Equal(LearningXpOutcome.Credited,(await f.Repository().AdjustAsync(reversal)).Outcome);
        Assert.Equal(LearningXpOutcome.AlreadyCredited,(await f.Repository().AdjustAsync(reversal)).Outcome);
        var duplicate=reversal with{OperationId=Guid.NewGuid()};f.Authority.Adjustments[duplicate.OperationId]=(duplicate,-9,"SYNTHETIC duplicate reversal");
        Assert.Equal(LearningXpOutcome.Conflict,(await f.Repository().AdjustAsync(duplicate)).Outcome);
        var result=await f.Repository().ReadAsync();Assert.Equal(0,result.Total);Assert.Equal(3,result.Entries!.Count);
        await using var db=await f.OpenBoundRuntime();Assert.Equal(2,await db.Set<LearningXpRow>().CountAsync(x=>x.AuditReason!=null));
    }
    [LocalPostgresFact]
    public async Task LatePolicyRevocationAndCancellationRollbackAndReadRevocationWithholdsAllXp()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();await f.ConfigureSyntheticPolicy();
        var denied=await f.Repository((phase,_)=>{if(phase=="before-commit")f.Authority.Denied=true;return Task.CompletedTask;}).CreditAsync(f.VerifiedSource());
        Assert.Equal(LearningXpOutcome.Denied,denied.Outcome);f.Authority.Denied=false;Assert.Equal(0,(await f.Repository().ReadAsync()).Total);
        using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Repository((phase,_)=>{if(phase=="before-commit")cancel.Cancel();return Task.CompletedTask;}).CreditAsync(f.VerifiedSource(),cancel.Token));
        Assert.Equal(0,(await f.Repository().ReadAsync()).Total);
        var read=await f.Repository((phase,_)=>{if(phase=="read-release")f.Authority.Denied=true;return Task.CompletedTask;}).ReadAsync();Assert.Equal(LearningXpOutcome.Denied,read.Outcome);Assert.Null(read.Total);Assert.Null(read.Entries);
    }
    [LocalPostgresFact]
    public async Task XpRuntimeIsImmutableAndRlsDeniesForeignAccountWhileBalancePaginationIsStable()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();await f.ConfigureSyntheticPolicy();
        for(var i=0;i<3;i++)Assert.Equal(LearningXpOutcome.Credited,(await f.Repository().CreditAsync(f.VerifiedSource())).Outcome);
        var first=await f.Repository().ReadAsync(limit:2);var second=await f.Repository().ReadAsync(first.Entries![^1].Id,2);Assert.Equal(2,first.Entries.Count);Assert.Single(second.Entries!);Assert.Equal(21,second.Total);
        await using var db=await f.OpenBoundRuntime();
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT has_table_privilege(current_user,'andril_learning_xp_isolated.xp_credits','UPDATE') OR has_table_privilege(current_user,'andril_learning_xp_isolated.xp_credits','DELETE') AS \"Value\"").SingleAsync());
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT relrowsecurity AND relforcerowsecurity AS \"Value\" FROM pg_class WHERE oid='andril_learning_xp_isolated.xp_credits'::regclass").SingleAsync());
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.account',{Guid.NewGuid().ToString()},false)");Assert.Empty(await db.Set<LearningXpRow>().ToListAsync());
    }
    [LocalPostgresFact]
    public async Task StructuredCvFieldsPersistAndLegacyContentAndConsentStayIndependentAcrossTemplates()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();var cv=(await f.Cv.Service.CreateAsync(CvTemplate.Modern)).Cv!;var section=cv.GetSection(CvSectionCategory.Projects);var id=Guid.NewGuid();
        await f.Cv.Service.SaveSectionAsync(cv.Id,section.Id,section.Category,[new(id,section.Id,"IMPORTED-UNCLASSIFIED")]);
        var entry=CvStructuredFields.ReviewedEntry(id,section.Id,section.Category,new Dictionary<string,string>{{"ProjectTitle","Reviewed project"},{"Description","Approved professional description"}});
        Assert.Equal(CvOperationState.Available,(await f.Cv.Repository().SaveAsync(cv.Id,CvSaveChange.ReviewedFields(section.Category,[entry]))).State);
        foreach(var template in Enum.GetValues<CvTemplate>())
        {
            await f.Cv.Service.SelectTemplateAsync(cv.Id,template);var reopened=(await f.Cv.Service.ReopenAsync(cv.Id)).Cv!;
            var saved=Assert.Single(reopened.GetSection(section.Category).Entries);Assert.Equal(id,saved.Id);Assert.Equal("IMPORTED-UNCLASSIFIED",saved.Text);Assert.Equal("Reviewed project",saved.Fields["ProjectTitle"]);
            var html=(await f.Cv.Service.PreviewAsync(cv.Id)).Preview!.Html;Assert.Contains("Reviewed project",html);Assert.DoesNotContain("IMPORTED-UNCLASSIFIED",html);
            var pdf=Encoding.ASCII.GetString((await f.Cv.Service.ExportPdfAsync(cv.Id)).Document!.Bytes);Assert.Contains("Reviewed project",pdf);Assert.DoesNotContain("IMPORTED-UNCLASSIFIED",pdf);
        }
    }
    [LocalPostgresFact]
    public async Task PersistedContactRevocationAndOwnerRevocationPreventPreviewAndPdfDisclosure()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();var cv=(await f.Cv.Service.CreateAsync(CvTemplate.Professional)).Cv!;var s=cv.GetSection(CvSectionCategory.PersonalInformation);
        await f.Cv.Repository().SaveAsync(cv.Id,CvSaveChange.ReviewedFields(s.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),s.Id,s.Category,new Dictionary<string,string>{{"Email","private@example.test"}})]));
        Assert.DoesNotContain("private@example.test",(await f.Cv.Service.PreviewAsync(cv.Id)).Preview!.Html);
        await f.Cv.Service.SetContactConsentAsync(cv.Id,true);Assert.Contains("private@example.test",Encoding.ASCII.GetString((await f.Cv.Service.ExportPdfAsync(cv.Id)).Document!.Bytes));
        await f.Cv.Service.SetContactConsentAsync(cv.Id,false);Assert.DoesNotContain("private@example.test",Encoding.ASCII.GetString((await f.Cv.Service.ExportPdfAsync(cv.Id)).Document!.Bytes));
        var preview=await f.Cv.Repository(checkpoint:(phase,_)=>{if(phase=="read-release")f.Cv.Owner.Denied=true;return Task.CompletedTask;}).PreviewAsync(cv.Id);
        Assert.Equal(CvOperationState.Denied,preview.State);Assert.Null(preview.Preview);
        Assert.Equal(CvOperationState.Denied,(await f.Cv.Service.ExportPdfAsync(cv.Id)).State);
    }
}
