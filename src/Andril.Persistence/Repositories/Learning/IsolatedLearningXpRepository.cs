using Andril.Common.Accounts.Profiles;
using Andril.Learning.Interfaces;
using Andril.Learning.Progress;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>Unregistered local-only immutable Learning XP ledger. No earning policy or currency conversion.</summary>
public sealed class IsolatedLearningXpRepository : ILearningXpRepository
{
    private readonly IsolatedLearningDocumentBoundary boundary;
    private readonly ILearningXpAuthority authority;
    private readonly TimeProvider clock;
    internal Func<string,CancellationToken,Task>? TestCheckpoint { get; set; }
    public IsolatedLearningXpRepository(DbContextOptions<LearningDbContext> options, ICurrentUser user,
        IPrivateModuleAccess access, IPrivateProfileService profiles, ILearningXpAuthority authority, TimeProvider? clock = null)
    { boundary = new(options, user, access, profiles); this.authority = authority; this.clock = clock ?? TimeProvider.System; }

    public Task<LearningXpResult> ReadAsync(Guid? after = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (after == Guid.Empty || limit is < 1 or > 100) return Task.FromResult(new LearningXpResult(LearningXpOutcome.Conflict));
        return Run(false, null, async (db, scope, _, ct) =>
        {
            var total = await Total(db, scope, ct);
            if (total > long.MaxValue) return new(LearningXpOutcome.Unavailable);
            var rows = after is { } cursor
                ? await db.Set<LearningXpRow>().FromSqlInterpolated($"SELECT * FROM andril_learning_xp_isolated.xp_credits WHERE account_id={scope.AccountId} AND profile_id={scope.LearningProfileId} AND id>{cursor} ORDER BY id LIMIT {limit}").AsNoTracking().ToListAsync(ct)
                : await db.Set<LearningXpRow>().Where(x => x.AccountId == scope.AccountId && x.ProfileId == scope.LearningProfileId).OrderBy(x => x.Id).Take(limit).AsNoTracking().ToListAsync(ct);
            var policy = authority is ILearningXpPolicyAuthority policies ? await policies.ReadCurrentPolicyAsync(ct) : null;
            if(policy?.IsConfigured==true && !await HasStoredPolicy(db,scope,policy,ct))policy=null;
            var progress = new LearningXpProgress((long)total,policy?.IsConfigured == true ? policy.Levels.Where(x=>x.MinimumTotal<=total).OrderByDescending(x=>x.MinimumTotal).FirstOrDefault()?.Key : null,
                policy?.IsConfigured == true ? policy.Achievements.Where(x=>x.MinimumTotal<=total).Select(x=>x.Key).ToArray() : [],policy?.IsConfigured == true ? policy.Revision : null);
            return new(LearningXpOutcome.Available, (long)total, Array.AsReadOnly(rows.Select(x => new LearningXpEntry(x.Id,x.Amount,x.CreditedAtUtc)).ToArray()),progress);
        }, cancellationToken);
    }
    public Task<LearningXpResult> CreditAsync(LearningXpSource source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is null || source.EventId == Guid.Empty || string.IsNullOrWhiteSpace(source.Kind) || source.Kind.Length > 128)
            return Task.FromResult(new LearningXpResult(LearningXpOutcome.Unavailable));
        return Run(true, source, async (db, scope, evidence, ct) =>
        {
            if (authority is not ILearningXpPolicyAuthority policies) return new(LearningXpOutcome.Unavailable);
            var policy = await policies.ReadCurrentPolicyAsync(ct);
            if (policy?.IsConfigured != true || evidence!.PolicyRevision != policy.Revision ||
                !policy.EventAmounts.TryGetValue(source.Kind,out var amount) || amount != evidence.Amount) return new(LearningXpOutcome.Unavailable);
            if(!await HasStoredPolicy(db,scope,policy,ct))return new(LearningXpOutcome.Unavailable);
            var existing = await db.Set<LearningXpRow>().SingleOrDefaultAsync(x => x.ProfileId == scope.LearningProfileId && x.SourceKind == source.Kind && x.SourceEventId == source.EventId,ct);
            if (existing is not null)
                return new(existing.AccountId == scope.AccountId && existing.Amount == evidence!.Amount && existing.PolicyRevision == evidence.PolicyRevision && existing.EvidenceRevision == evidence.EvidenceRevision
                    ? LearningXpOutcome.AlreadyCredited : LearningXpOutcome.Conflict);
            if (policy.MaximumOccurrences.TryGetValue(source.Kind,out var cap) && await db.Set<LearningXpRow>().CountAsync(x=>x.ProfileId==scope.LearningProfileId && x.SourceKind==source.Kind && x.EntryKind=="Credit",ct)>=cap)
                return new(LearningXpOutcome.Conflict);
            if (await Total(db,scope,ct) + evidence!.Amount > long.MaxValue) return new(LearningXpOutcome.Conflict);
            if (policy.MaximumTotal is { } maximum && await Total(db,scope,ct)+evidence.Amount>maximum) return new(LearningXpOutcome.Conflict);
            db.Add(new LearningXpRow { Id = Guid.NewGuid(), AccountId = scope.AccountId, ProfileId = scope.LearningProfileId,
                SourceKind = source.Kind, SourceEventId = source.EventId, Amount = evidence.Amount, PolicyRevision = evidence.PolicyRevision,
                EvidenceRevision = evidence.EvidenceRevision, CreditedAtUtc = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct); return new(LearningXpOutcome.Credited);
        },cancellationToken);
    }
    public Task<LearningXpResult> AdjustAsync(LearningXpAdjustment adjustment,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (authority is not ILearningXpAdjustmentAuthority || adjustment.OperationId==Guid.Empty || adjustment.OriginalCreditId==Guid.Empty || !Enum.IsDefined(adjustment.Kind))
            return Task.FromResult(new LearningXpResult(LearningXpOutcome.Unavailable));
        var source = new LearningXpSource("adjustment:"+adjustment.Kind,adjustment.OperationId);
        return Run(true,source,async(db,scope,evidence,ct)=>
        {
            var verified=await ((ILearningXpAdjustmentAuthority)authority).ResolveAdjustmentAsync(scope,adjustment,ct);
            if(verified is null || string.IsNullOrWhiteSpace(verified.AuditReason) || verified.Award!=evidence) return new(LearningXpOutcome.Unavailable);
            var original=await db.Set<LearningXpRow>().SingleOrDefaultAsync(x=>x.Id==adjustment.OriginalCreditId && x.AccountId==scope.AccountId && x.ProfileId==scope.LearningProfileId && x.EntryKind=="Credit",ct);
            if(original is null) return new(LearningXpOutcome.Denied);
            var prior=await db.Set<LearningXpRow>().SingleOrDefaultAsync(x=>x.ProfileId==scope.LearningProfileId && x.SourceKind==source.Kind && x.SourceEventId==source.EventId,ct);
            if(prior is not null) return new(prior.Amount==evidence!.Amount && prior.OriginalCreditId==original.Id && prior.AuditReason==verified.AuditReason && prior.PolicyRevision==evidence.PolicyRevision && prior.EvidenceRevision==evidence.EvidenceRevision ? LearningXpOutcome.AlreadyCredited : LearningXpOutcome.Conflict);
            var previous=await db.Set<LearningXpRow>().Where(x=>x.ProfileId==scope.LearningProfileId && x.OriginalCreditId==original.Id).SumAsync(x=>(decimal)x.Amount,ct);
            if(await db.Set<LearningXpRow>().AnyAsync(x=>x.ProfileId==scope.LearningProfileId && x.OriginalCreditId==original.Id && x.EntryKind=="Reversal",ct)) return new(LearningXpOutcome.Conflict);
            var remaining=original.Amount+previous;
            if(adjustment.Kind==LearningXpAdjustmentKind.Reversal && evidence!.Amount != -remaining) return new(LearningXpOutcome.Conflict);
            var next=await Total(db,scope,ct)+evidence!.Amount;
            if(next<0 || next>long.MaxValue || remaining+evidence.Amount<0 || evidence.Amount==0) return new(LearningXpOutcome.Conflict);
            if(authority is not ILearningXpPolicyAuthority policies) return new(LearningXpOutcome.Unavailable);
            var policy=await policies.ReadCurrentPolicyAsync(ct);
            if(policy?.IsConfigured!=true || policy.Revision!=evidence.PolicyRevision) return new(LearningXpOutcome.Unavailable);
            if(!await HasStoredPolicy(db,scope,policy,ct))return new(LearningXpOutcome.Unavailable);
            if(evidence.Amount>0 && policy.MaximumTotal is {} cap && next>cap) return new(LearningXpOutcome.Conflict);
            db.Add(new LearningXpRow{Id=Guid.NewGuid(),AccountId=scope.AccountId,ProfileId=scope.LearningProfileId,SourceKind=source.Kind,SourceEventId=source.EventId,
                Amount=evidence.Amount,EntryKind=adjustment.Kind.ToString(),OriginalCreditId=original.Id,AuditReason=verified.AuditReason,
                PolicyRevision=evidence.PolicyRevision,EvidenceRevision=evidence.EvidenceRevision,CreditedAtUtc=clock.GetUtcNow()});
            await db.SaveChangesAsync(ct);return new(LearningXpOutcome.Credited);
        },cancellationToken,adjustment);
    }
    private static Task<decimal> Total(IsolatedLearningXpDbContext db, LearningSetupScope scope, CancellationToken ct)
        => db.Database.SqlQuery<decimal>($"SELECT COALESCE(SUM(amount),0)::numeric AS \"Value\" FROM andril_learning_xp_isolated.xp_credits WHERE account_id={scope.AccountId} AND profile_id={scope.LearningProfileId}").SingleAsync(ct);
    private static async Task<bool> HasStoredPolicy(IsolatedLearningXpDbContext db,LearningSetupScope scope,LearningXpPolicy policy,CancellationToken ct)
    {
        var stored=await db.Set<LearningXpPolicyRow>().AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==scope.AccountId&&x.ProfileId==scope.LearningProfileId&&x.Revision==policy.Revision,ct);
        return stored is not null&&System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(stored.Configuration),System.Text.Json.Nodes.JsonNode.Parse(policy.CanonicalConfiguration()));
    }
    public async Task<LearningXpResult> StageApprovedPolicyAsync(string revision,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(string.IsNullOrWhiteSpace(revision)||authority is not ILearningXpPolicyPublicationAuthority publisher)return new(LearningXpOutcome.Unavailable);
        LearningSetupScope? scope=null;string? canonical=null;
        try
        {
            return await boundary.Run(async(parent,owner,ct)=>
            {
                var account=await parent.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(ct);
                scope=new(Guid.Parse(account),owner.Profile);var policy=await publisher.ResolveApprovedPublicationAsync(scope,revision,ct);
                if(policy?.IsConfigured!=true||policy.Revision!=revision)return new LearningXpResult(LearningXpOutcome.Unavailable);
                canonical=policy.CanonicalConfiguration();
                await using var db=new IsolatedLearningXpDbContext(new DbContextOptionsBuilder<IsolatedLearningXpDbContext>().UseNpgsql((NpgsqlConnection)parent.Database.GetDbConnection()).Options);
                await db.Database.UseTransactionAsync(parent.Database.CurrentTransaction!.GetDbTransaction(),ct);
                var existing=await db.Set<LearningXpPolicyRow>().AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==scope.AccountId&&x.ProfileId==scope.LearningProfileId&&x.Revision==revision,ct);
                if(existing is not null)return new LearningXpResult(await HasStoredPolicy(db,scope,policy,ct)?LearningXpOutcome.PolicyStored:LearningXpOutcome.Conflict);
                db.Add(new LearningXpPolicyRow{AccountId=scope.AccountId,ProfileId=scope.LearningProfileId,Revision=revision,Configuration=canonical});await db.SaveChangesAsync(ct);
                return new LearningXpResult(LearningXpOutcome.PolicyStored);
            },failure=>new LearningXpResult(failure switch{LearningDocumentFailure.Denied=>LearningXpOutcome.Denied,LearningDocumentFailure.Conflict=>LearningXpOutcome.Conflict,LearningDocumentFailure.CommitUnknown=>LearningXpOutcome.CommitUnknown,_=>LearningXpOutcome.Unavailable}),cancellationToken,true,
            async(phase,ct)=>
            {
                if(TestCheckpoint is not null)await TestCheckpoint(phase,ct);
                if(scope is not null&&canonical is not null)
                {var current=await publisher.ResolveApprovedPublicationAsync(scope,revision,ct);if(current?.IsConfigured!=true||current.CanonicalConfiguration()!=canonical)throw new ProfileAccessDeniedException();}
            });
        }
        catch(OperationCanceledException)when(cancellationToken.IsCancellationRequested){throw;}
        catch(Exception){return new(LearningXpOutcome.Unavailable);}
    }

    private async Task<LearningXpResult> Run(bool write, LearningXpSource? source,
        Func<IsolatedLearningXpDbContext,LearningSetupScope,LearningXpAwardEvidence?,CancellationToken,Task<LearningXpResult>> action, CancellationToken ct,LearningXpAdjustment? adjustment=null)
    {
        LearningSetupScope? scope = null; LearningXpAwardEvidence? evidence = null; string? configuration=null;
        try
        {
            return await boundary.Run(async (parent,owner,token) =>
            {
                var account = await parent.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(token);
                scope = new(Guid.Parse(account), owner.Profile);
                if(authority is ILearningXpPolicyAuthority initialPolicies)
                {
                    var initial=await initialPolicies.ReadCurrentPolicyAsync(token);
                    configuration=initial?.CanonicalConfiguration();
                }
                if (write)
                {
                    evidence = adjustment is null ? await authority.ResolveAwardAsync(scope,source!,token)
                        : (await ((ILearningXpAdjustmentAuthority)authority).ResolveAdjustmentAsync(scope,adjustment,token))?.Award;
                    if (evidence is null || evidence.AccountId != scope.AccountId || evidence.LearningProfileId != scope.LearningProfileId || evidence.Source != source ||
                        (adjustment is null ? evidence.Amount <= 0 : evidence.Amount==0) || string.IsNullOrWhiteSpace(evidence.PolicyRevision) || string.IsNullOrWhiteSpace(evidence.EvidenceRevision))
                        return new LearningXpResult(LearningXpOutcome.Unavailable);
                    if (!await authority.RevalidateAwardAsync(scope,evidence,token)) throw new ProfileAccessDeniedException();
                }
                else if (!await authority.MayReadAsync(scope,token)) throw new ProfileAccessDeniedException();
                await using var db = new IsolatedLearningXpDbContext(new DbContextOptionsBuilder<IsolatedLearningXpDbContext>()
                    .UseNpgsql((NpgsqlConnection)parent.Database.GetDbConnection()).Options);
                await db.Database.UseTransactionAsync(parent.Database.CurrentTransaction!.GetDbTransaction(),token);
                return await action(db,scope,evidence,token);
            }, failure => new LearningXpResult(failure switch
            {
                LearningDocumentFailure.Denied => LearningXpOutcome.Denied,
                LearningDocumentFailure.Conflict => LearningXpOutcome.Conflict,
                LearningDocumentFailure.CommitUnknown => LearningXpOutcome.CommitUnknown,
                _ => LearningXpOutcome.Unavailable
            }),ct,write,async (phase,token) =>
            {
                if (TestCheckpoint is not null) await TestCheckpoint(phase,token);
                if (scope is not null && (write ? evidence is not null && !await authority.RevalidateAwardAsync(scope,evidence,token) : !await authority.MayReadAsync(scope,token)))
                    throw new ProfileAccessDeniedException();
                if(write && evidence is not null && authority is ILearningXpPolicyAuthority policySource)
                {
                    var policy=await policySource.ReadCurrentPolicyAsync(token);
                    if(policy?.IsConfigured!=true || policy.Revision!=evidence.PolicyRevision || policy.CanonicalConfiguration()!=configuration) throw new ProfileAccessDeniedException();
                }
                else if(!write && authority is ILearningXpPolicyAuthority readPolicies)
                {
                    var current=await readPolicies.ReadCurrentPolicyAsync(token);
                    if(current?.CanonicalConfiguration()!=configuration) throw new ProfileAccessDeniedException();
                }
            },ex => ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } || ex is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(LearningXpOutcome.Unavailable); }
    }
}
