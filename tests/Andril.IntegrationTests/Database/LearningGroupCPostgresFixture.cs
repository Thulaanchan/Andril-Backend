using Andril.Learning.Interfaces;
using Andril.Learning.Progress;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.IntegrationTests.Database;

internal sealed class LearningGroupCPostgresFixture : IAsyncDisposable
{
    internal CvPostgresFixture Cv { get; private set; } = null!;
    internal XpAuthority Authority { get; } = new();
    private string connection = null!, role = null!;
    internal sealed class XpAuthority : ILearningXpAuthority,ILearningXpPolicyAuthority,ILearningXpAdjustmentAuthority,ILearningXpPolicyPublicationAuthority
    {
        internal LearningXpPolicy Policy=LearningXpPolicy.Unconfigured;
        internal bool Denied,Unavailable;
        internal readonly HashSet<Guid> VerifiedEvents=[];
        internal readonly Dictionary<Guid,(LearningXpAdjustment Command,long Amount,string Reason)> Adjustments=[];
        internal void ConfigureSyntheticPolicy(long? cap=null,int? occurrences=null)
            => Policy=new("SYNTHETIC-TEST-POLICY-v1",true,new Dictionary<string,long>{{"synthetic-verified-completion",7}},cap,
                occurrences is {} count ? new Dictionary<string,int>{{"synthetic-verified-completion",count}} : new Dictionary<string,int>(),
                [new("synthetic-level",7)],[new("synthetic-achievement",14)]);
        public Task<bool> MayReadAsync(LearningSetupScope scope,CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();if(Unavailable)throw new IOException("PRIVATE_XP_SOURCE_FAILURE");return Task.FromResult(!Denied);}
        public Task<LearningXpPolicy?> ReadCurrentPolicyAsync(CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();return Task.FromResult<LearningXpPolicy?>(Unavailable?null:Policy);}
        public Task<LearningXpPolicy?> ResolveApprovedPublicationAsync(LearningSetupScope scope,string revision,CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();return Task.FromResult<LearningXpPolicy?>(Denied||Unavailable||Policy.Revision!=revision?null:Policy);}
        public Task<LearningXpAwardEvidence?> ResolveAwardAsync(LearningSetupScope scope,LearningXpSource source,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<LearningXpAwardEvidence?>(Denied||Unavailable||!Policy.IsConfigured||!VerifiedEvents.Contains(source.EventId)||!Policy.EventAmounts.TryGetValue(source.Kind,out var amount)
                ? null:new(scope.AccountId,scope.LearningProfileId,source,amount,Policy.Revision,"SYNTHETIC-VERIFIED-SOURCE-v1"));
        }
        public Task<bool> RevalidateAwardAsync(LearningSetupScope scope,LearningXpAwardEvidence evidence,CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();return Task.FromResult(!Denied&&!Unavailable&&Policy.IsConfigured&&Policy.Revision==evidence.PolicyRevision);}
        public Task<LearningXpAdjustmentEvidence?> ResolveAdjustmentAsync(LearningSetupScope scope,LearningXpAdjustment adjustment,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<LearningXpAdjustmentEvidence?>(Denied||Unavailable||!Policy.IsConfigured||!Adjustments.TryGetValue(adjustment.OperationId,out var configured)||configured.Command!=adjustment
                ? null:new(new(scope.AccountId,scope.LearningProfileId,new("adjustment:"+adjustment.Kind,adjustment.OperationId),configured.Amount,Policy.Revision,"SYNTHETIC-AUDIT-v1"),configured.Reason));
        }
    }
    internal IsolatedLearningXpRepository Repository(Func<string,CancellationToken,Task>? checkpoint=null)
        => new(new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(connection).Options,Cv.Owner,Cv.Owner,Cv.Owner,Authority) {TestCheckpoint=checkpoint};
    internal LearningXpSource VerifiedSource()
    {var source=new LearningXpSource("synthetic-verified-completion",Guid.NewGuid());Authority.VerifiedEvents.Add(source.EventId);return source;}
    internal async Task ConfigureSyntheticPolicy(long? cap=null,int? occurrences=null)
    {Authority.ConfigureSyntheticPolicy(cap,occurrences);var staged=await Repository().StageApprovedPolicyAsync(Authority.Policy.Revision);if(staged.Outcome!=LearningXpOutcome.PolicyStored)throw new InvalidOperationException("Synthetic test policy provisioning failed.");}
    internal static async Task<LearningGroupCPostgresFixture> Create()
    {
        var f=new LearningGroupCPostgresFixture{Cv=await CvPostgresFixture.Create()};
        try
        {
            await using var owner=f.Cv.OpenOwner();await CvPostgresFixture.Bind(owner,f.Cv.Owner);
            await using var model=new IsolatedLearningXpDbContext(new DbContextOptionsBuilder<IsolatedLearningXpDbContext>().UseNpgsql(owner.Database.GetConnectionString()!).Options);
            await model.Database.ExecuteSqlRawAsync(model.Database.GenerateCreateScript());
            await model.Database.ExecuteSqlRawAsync("""
ALTER TABLE andril_learning_xp_isolated.xp_credits ENABLE ROW LEVEL SECURITY;
ALTER TABLE andril_learning_xp_isolated.xp_credits FORCE ROW LEVEL SECURITY;
CREATE POLICY xp_owner ON andril_learning_xp_isolated.xp_credits
USING(module='learning' AND account_id::text=current_setting('andril.account',true) AND andril_learning.scope_visible(profile_id,'learning'))
WITH CHECK(module='learning' AND account_id::text=current_setting('andril.account',true) AND andril_learning.scope_visible(profile_id,'learning'));
REVOKE ALL ON SCHEMA andril_learning_xp_isolated FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA andril_learning_xp_isolated FROM PUBLIC;
ALTER TABLE andril_learning_xp_isolated.xp_policy_versions ENABLE ROW LEVEL SECURITY;
ALTER TABLE andril_learning_xp_isolated.xp_policy_versions FORCE ROW LEVEL SECURITY;
CREATE POLICY xp_policy_owner ON andril_learning_xp_isolated.xp_policy_versions
USING(module='learning' AND account_id::text=current_setting('andril.account',true) AND andril_learning.scope_visible(profile_id,'learning'))
WITH CHECK(module='learning' AND account_id::text=current_setting('andril.account',true) AND andril_learning.scope_visible(profile_id,'learning'));
""");
            await using var runtime=f.Cv.OpenRuntime();f.connection=runtime.Database.GetConnectionString()!;
            f.role=await runtime.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
            using var b=new NpgsqlCommandBuilder();var role=b.QuoteIdentifier(f.role);
            await using var grant=new NpgsqlCommand($"GRANT USAGE ON SCHEMA andril_learning_xp_isolated TO {role}; GRANT SELECT,INSERT ON andril_learning_xp_isolated.xp_credits,andril_learning_xp_isolated.xp_policy_versions TO {role}",(NpgsqlConnection)owner.Database.GetDbConnection());
            await grant.ExecuteNonQueryAsync();return f;
        }
        catch{await f.DisposeAsync();throw;}
    }
    internal async Task<IsolatedLearningXpDbContext> OpenBoundRuntime()
    {
        var db=new IsolatedLearningXpDbContext(new DbContextOptionsBuilder<IsolatedLearningXpDbContext>().UseNpgsql(connection).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{Cv.Owner.Subject.ToString()},false),set_config('andril.account',{Cv.Owner.Account.ToString()},false),set_config('andril.module','learning',false)");return db;
    }
    public async ValueTask DisposeAsync()
    {
        if(role is not null)
        {
            await using var owner=Cv.OpenOwner();await owner.Database.OpenConnectionAsync();using var b=new NpgsqlCommandBuilder();var quoted=b.QuoteIdentifier(role);
            await using var revoke=new NpgsqlCommand($"REVOKE ALL ON ALL TABLES IN SCHEMA andril_learning_xp_isolated FROM {quoted}; REVOKE ALL ON SCHEMA andril_learning_xp_isolated FROM {quoted}",(NpgsqlConnection)owner.Database.GetDbConnection());await revoke.ExecuteNonQueryAsync();
        }
        await Cv.DisposeAsync();
    }
}
