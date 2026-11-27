using Andril.Learning.Activities;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.IntegrationTests.Database;

internal sealed class LearningActivitiesPostgresFixture : IAsyncDisposable
{
    internal sealed class ActivityAuthority(LearningActivityMember peer) : ILearningActivityAuthority
    {
        internal bool Denied;
        internal bool Unavailable;
        internal int Calls;
        internal Func<CancellationToken, Task>? BeforeAuthorize;
        public async Task<LearningActivityPermission> AuthorizeAsync(LearningActivityMember actor, LearningActivityKind kind,
            LearningActivityAction action, Guid? resourceId, CreateLearningActivityRequest? creation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (BeforeAuthorize is not null) await BeforeAuthorize(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable) return new(LearningActivityPermissionOutcome.Unavailable, []);
            if (Denied) return new(LearningActivityPermissionOutcome.Denied, []);
            // Isolated synthetic authority only; not production target/offer/M5 evidence.
            var participants = kind is LearningActivityKind.SkillExchange or LearningActivityKind.Pact or LearningActivityKind.MentorshipRequest
                ? new[] { actor, peer } : new[] { actor };
            return new(LearningActivityPermissionOutcome.Allowed, participants);
        }
    }

    internal LearningAiConversationPostgresFixture Shared { get; private set; } = null!;
    internal LearningActivityMember Peer { get; } = new(Guid.NewGuid(), Guid.NewGuid());
    internal ActivityAuthority Authority { get; private set; } = null!;
    private string connection = null!;
    private string role = null!;
    private bool schemaCreated;

    internal static async Task<LearningActivitiesPostgresFixture> Create()
    {
        var f = new LearningActivitiesPostgresFixture();
        f.Shared = await LearningAiConversationPostgresFixture.Create();
        f.Authority = new(f.Peer);
        try
        {
            await using var owner = await f.Shared.BoundOwner();
            await using var model = new IsolatedLearningActivitiesDbContext(new DbContextOptionsBuilder<IsolatedLearningActivitiesDbContext>()
                .UseNpgsql(owner.Database.GetConnectionString()!).Options);
            await model.Database.ExecuteSqlRawAsync(model.Database.GenerateCreateScript()); f.schemaCreated = true;
            await model.Database.ExecuteSqlRawAsync("""
ALTER TABLE andril_learning_activities_isolated.activities ENABLE ROW LEVEL SECURITY;
ALTER TABLE andril_learning_activities_isolated.activities FORCE ROW LEVEL SECURITY;
CREATE POLICY learning_activity_scope ON andril_learning_activities_isolated.activities
USING (module='learning' AND EXISTS (SELECT 1 FROM jsonb_array_elements(members) AS people(member)
 WHERE member->>'AccountId'=current_setting('andril.account',true)
 AND andril_learning.scope_visible((member->>'ProfileId')::uuid,'learning')))
WITH CHECK (module='learning' AND EXISTS (SELECT 1 FROM jsonb_array_elements(members) AS people(member)
 WHERE member->>'AccountId'=current_setting('andril.account',true)
 AND andril_learning.scope_visible((member->>'ProfileId')::uuid,'learning')));
REVOKE ALL ON SCHEMA andril_learning_activities_isolated FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA andril_learning_activities_isolated FROM PUBLIC;
""");
            await using var runtime = f.Shared.OpenRuntime(); f.connection = runtime.Database.GetConnectionString()!;
            f.role = await runtime.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
            using var builder = new NpgsqlCommandBuilder(); var quoted = builder.QuoteIdentifier(f.role);
            await using var grant = new NpgsqlCommand($"GRANT USAGE ON SCHEMA andril_learning_activities_isolated TO {quoted}; GRANT SELECT,INSERT ON andril_learning_activities_isolated.activities TO {quoted}; GRANT UPDATE(content,status,version) ON andril_learning_activities_isolated.activities TO {quoted}",
                (NpgsqlConnection)owner.Database.GetDbConnection());
            await grant.ExecuteNonQueryAsync();
            return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }

    internal IsolatedLearningActivitiesRepository Repository(Func<string, CancellationToken, Task>? checkpoint = null)
        => new(new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(connection).Options,
            Shared.Owner, Shared.Owner, Shared.Owner, Authority) { TestCheckpoint = checkpoint };

    internal CreateLearningActivityRequest Request(LearningActivityKind kind) => kind switch
    {
        LearningActivityKind.SkillExchange => new([Peer.ProfileId], Guid.NewGuid(), Guid.NewGuid()),
        LearningActivityKind.Pact => new([Peer.ProfileId], Commitments: ["private shared commitment"]),
        LearningActivityKind.GrowthSelfReport => new(SkillIds: [Guid.NewGuid()]),
        LearningActivityKind.MentorshipRequest => new([Peer.ProfileId], RelatedOfferId: Guid.NewGuid()),
        LearningActivityKind.Circle => new(CircleId: Guid.NewGuid()),
        _ => new()
    };

    internal async Task<IsolatedLearningActivitiesDbContext> BoundOwner()
    {
        await using var source = await Shared.BoundOwner();
        var db = new IsolatedLearningActivitiesDbContext(new DbContextOptionsBuilder<IsolatedLearningActivitiesDbContext>()
            .UseNpgsql(source.Database.GetConnectionString()!).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{Shared.Owner.Subject.ToString()},false),set_config('andril.account',{Shared.Actor().UserId.ToString()},false),set_config('andril.module','learning',false)");
        return db;
    }

    public async ValueTask DisposeAsync()
    {
        if (schemaCreated && !string.IsNullOrEmpty(role))
        {
            await using var owner = await Shared.BoundOwner(); using var builder = new NpgsqlCommandBuilder(); var quoted = builder.QuoteIdentifier(role);
            await using var revoke = new NpgsqlCommand($"REVOKE UPDATE(content,status,version) ON andril_learning_activities_isolated.activities FROM {quoted}; REVOKE ALL ON ALL TABLES IN SCHEMA andril_learning_activities_isolated FROM {quoted}; REVOKE ALL ON SCHEMA andril_learning_activities_isolated FROM {quoted}", (NpgsqlConnection)owner.Database.GetDbConnection());
            await revoke.ExecuteNonQueryAsync();
        }
        await Shared.DisposeAsync();
    }
}
