using Andril.Common.Accounts.Profiles;
using Andril.Learning.Progress;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>Current-owner balance/progress/history only; earning/adjustment/publication remain unavailable.</summary>
public static class LearningXpReadPersistence
{
    public static ILearningXpRepository Create(AccountsDbContext accounts,ICurrentUser user,IPrivateProfileService profiles,string role)=>
        new OwnerXpReader(new LearningOwnerDocumentTransaction(accounts,user,profiles,role,LearningPrivateDocument.XpRead));
    internal static ILearningXpRepository Create(AccountsDbContext accounts,ICurrentUser user,IPrivateProfileService profiles,string role,Func<CancellationToken,Task> checkpoint)=>
        new OwnerXpReader(new LearningOwnerDocumentTransaction(accounts,user,profiles,role,LearningPrivateDocument.XpRead),checkpoint);

    private sealed class OwnerXpReader(ILearningDocumentBoundary boundary,Func<CancellationToken,Task>? checkpoint=null):ILearningXpRepository
    {
        public Task<LearningXpResult> ReadAsync(Guid? after=null,int limit=50,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(after==Guid.Empty||limit is <1 or >100)return Task.FromResult(new LearningXpResult(LearningXpOutcome.Conflict));
            return boundary.Run(async(shared,owner,ct)=>
            {
                var text=await shared.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(ct);
                if(!Guid.TryParse(text,out var account))throw new ProfileAccessDeniedException();
                await using var db=new LearningXpReadDbContext(new DbContextOptionsBuilder<LearningXpReadDbContext>().UseNpgsql(shared.Database.GetDbConnection()).Options);
                await db.Database.UseTransactionAsync(shared.Database.CurrentTransaction!.GetDbTransaction(),ct);
                var total=await db.Database.SqlQuery<decimal>($"SELECT COALESCE(SUM(amount),0)::numeric AS \"Value\" FROM andril_learning.xp_credits WHERE account_id={account} AND profile_id={owner.Profile} AND module='learning'").SingleAsync(ct);
                if(total<0||total>long.MaxValue)return new LearningXpResult(LearningXpOutcome.Unavailable);
                FormattableString query=after is {} cursor
                    ?(FormattableString)$"SELECT * FROM andril_learning.xp_credits WHERE account_id={account} AND profile_id={owner.Profile} AND module='learning' AND id>{cursor} ORDER BY id LIMIT {limit}"
                    :$"SELECT * FROM andril_learning.xp_credits WHERE account_id={account} AND profile_id={owner.Profile} AND module='learning' ORDER BY id LIMIT {limit}";
                var rows=await db.Set<LearningXpRow>().FromSqlInterpolated(query).AsNoTracking().ToArrayAsync(ct);
                if(checkpoint is not null)await checkpoint(ct);
                return new LearningXpResult(LearningXpOutcome.Available,(long)total,
                    Array.AsReadOnly(rows.Select(row=>new LearningXpEntry(row.Id,row.Amount,row.CreditedAtUtc)).ToArray()),
                    new LearningXpProgress((long)total,null,[],null));
            },failure=>new LearningXpResult(failure==LearningDocumentFailure.Denied?LearningXpOutcome.Denied:LearningXpOutcome.Unavailable),cancellationToken);
        }
        private static Task<LearningXpResult> Unconfigured(CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(new LearningXpResult(LearningXpOutcome.Unavailable));}
        public Task<LearningXpResult> CreditAsync(LearningXpSource source,CancellationToken cancellationToken=default)=>Unconfigured(cancellationToken);
        public Task<LearningXpResult> AdjustAsync(LearningXpAdjustment adjustment,CancellationToken cancellationToken=default)=>Unconfigured(cancellationToken);
        public Task<LearningXpResult> StageApprovedPolicyAsync(string revision,CancellationToken cancellationToken=default)=>Unconfigured(cancellationToken);
    }
}
