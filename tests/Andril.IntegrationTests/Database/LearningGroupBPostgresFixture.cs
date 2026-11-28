using Andril.API.Integrations.Learning;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Andril.Learning.Activities;
using Andril.Learning.Discovery;
using Andril.Learning.Entities;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.IntegrationTests.Database;

internal sealed class LearningGroupBPostgresFixture : IAsyncDisposable
{
    internal LearningActivitiesPostgresFixture Activities { get; private set; } = null!;
    internal DiaryPostgresFixture.Authority Peer { get; private set; } = null!;
    internal PairAuthority Pairs { get; private set; } = null!;
    internal BrowseAuthority Sources { get; } = new();
    internal OwnerEvidence Owner { get; private set; } = null!;
    private string role = "";
    private DbContextOptions<LearningDbContext> homeOptions = null!;

    // Test-only authoritative pair/block source; real M2 actor resolution and PostgreSQL locks still run.
    internal sealed class PairAuthority(LearningActivityMember first, LearningActivityMember second) : ILearningActivityAuthority
    {
        internal bool Blocked, Unavailable;
        public Task<LearningActivityPermission> AuthorizeAsync(LearningActivityMember actor, LearningActivityKind kind, LearningActivityAction action, Guid? resourceId,
            CreateLearningActivityRequest? creation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Unavailable) return Task.FromResult(new LearningActivityPermission(LearningActivityPermissionOutcome.Unavailable, []));
            if (Blocked || kind != LearningActivityKind.Connection || (actor != first && actor != second) ||
                (creation is not null && !(creation.TargetProfiles ?? []).SequenceEqual(new[] { actor == first ? second.ProfileId : first.ProfileId })))
                return Task.FromResult(new LearningActivityPermission(LearningActivityPermissionOutcome.Denied, []));
            return Task.FromResult(new LearningActivityPermission(LearningActivityPermissionOutcome.Allowed, [first, second]));
        }
    }
    internal sealed class BrowseAuthority : ILearningBrowseSourceAuthority
    {
        internal bool Denied, Throw;
        public Task<bool> RevalidateAsync(LearningSetupScope scope, string kind, IReadOnlyList<LearningBrowseSourceReference> sources, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw) throw new InvalidOperationException("PRIVATE_BROWSE_SOURCE_FAILURE");
            return Task.FromResult(!Denied && sources.All(x => x.TargetId != Guid.Empty && !string.IsNullOrWhiteSpace(x.Revision)));
        }
    }
    internal sealed class OwnerEvidence(DiaryPostgresFixture.Authority owner, TimeProvider clock) : ICurrentUser, ILearningSetupAuthorityReader, IGroupActorContext, ILearningSetupRepository, ILearningSkillCatalogueReader
    {
        public Guid? UserId => owner.Subject; public bool IsAuthenticated => !owner.Denied;
        internal bool Complete = true, Revoked;
        internal readonly Guid Version = Guid.NewGuid();
        public Task<LearningSetupAccessEvidence?> ReadCurrentAsync(Guid verifiedSubject, LearningSetupOperation operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<LearningSetupAccessEvidence?>(new(verifiedSubject, owner.Account, owner.Profiles["learning"], owner.Account, "learning", operation,
                true, true, true, true, true, Revoked || owner.Denied, Complete, Version, Version));
        }
        public Task<AuthoritativeGroupProfileEvidence?> GetCurrentActorAsync(string expectedModule, CancellationToken cancellationToken = default)
            => Task.FromResult<AuthoritativeGroupProfileEvidence?>(expectedModule != "learning" || Revoked || owner.Denied ? null : new(owner.Profiles["learning"], "learning", true, false, null, clock.GetUtcNow()));
        public Task<LearningSetupReadResult> ReadAsync(LearningSetupScope scope, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Use the actual PostgreSQL preferences repository for Home.");
        public Task<LearningSetupWriteOutcome> ReplaceAsync(LearningSetupSnapshot replacement, string expectedStorageRevision, string catalogueRevision, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Home must not write.");
        public Task<LearningSkillCatalogueReadResult> ReadCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(LearningSkillCatalogueReadResult.Unavailable());
    }
    internal LearningSetupAccessBoundary Access => new(Owner, Owner);
    internal LearningHomeApplicationService Home => new(new(Access, new IsolatedLearningSetupRepository(homeOptions, Owner, Access, Owner), Owner), Access);
    internal LearningPeopleDiscoveryService People(IsolatedLearningBrowseReader? reader = null) => new(Access, reader ?? Reader());
    internal LearningClassroomDiscoveryService Classrooms(IsolatedLearningBrowseReader? reader = null)
        => new(Owner, new GroupDiscoveryService(reader ?? Reader(), Activities.Shared.Time), Activities.Shared.Time);
    internal IsolatedLearningBrowseReader Reader(Func<string, CancellationToken, Task>? checkpoint = null)
        => new(Activities.LearningOptions(), Activities.Shared.Owner, Activities.Shared.Owner, Activities.Shared.Owner, Sources) { TestCheckpoint = checkpoint };
    internal IsolatedLearningActivitiesRepository Connections(bool peer = false, Func<string, CancellationToken, Task>? checkpoint = null)
        => Activities.RepositoryFor(peer ? Peer : Activities.Shared.Owner, Pairs, checkpoint);
    internal CreateLearningActivityRequest ConnectRequest(bool peer = false) => new([peer ? Activities.Shared.Actor().ModuleProfileId : Peer.Profiles["learning"]]);

    internal static async Task<LearningGroupBPostgresFixture> Create()
    {
        var f = new LearningGroupBPostgresFixture { Activities = await LearningActivitiesPostgresFixture.Create() };
        try
        {
            f.Peer = new(Guid.NewGuid(), Guid.NewGuid());
            await LearningPostgresFixture.Profile(f.Activities.Shared.Shared.Shared.Shared.Database,
                new(f.Peer.Account, f.Peer.Profiles["learning"]), f.Peer.Subject);
            f.Pairs = new(new(f.Activities.Shared.Actor().UserId, f.Activities.Shared.Actor().ModuleProfileId), new(f.Peer.Account, f.Peer.Profiles["learning"]));
            f.Owner = new(f.Activities.Shared.Owner, f.Activities.Shared.Time);
            await using var source = await f.Activities.Shared.BoundOwner();
            f.homeOptions = new DbContextOptionsBuilder<LearningDbContext>().UseNpgsql(source.Database.GetConnectionString()!).Options;
            await using var model = new IsolatedLearningBrowseDbContext(new DbContextOptionsBuilder<IsolatedLearningBrowseDbContext>().UseNpgsql(source.Database.GetConnectionString()!).Options);
            await model.Database.ExecuteSqlRawAsync(model.Database.GenerateCreateScript());
            await model.Database.ExecuteSqlRawAsync("""
ALTER TABLE andril_learning_browse_isolated.discovery_evidence ENABLE ROW LEVEL SECURITY;
ALTER TABLE andril_learning_browse_isolated.discovery_evidence FORCE ROW LEVEL SECURITY;
CREATE POLICY learning_browse_owner ON andril_learning_browse_isolated.discovery_evidence
USING(module='learning' AND account_id::text=current_setting('andril.account',true) AND andril_learning.scope_visible(profile_id,'learning'))
WITH CHECK(module='learning' AND account_id::text=current_setting('andril.account',true) AND andril_learning.scope_visible(profile_id,'learning'));
REVOKE ALL ON SCHEMA andril_learning_browse_isolated FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA andril_learning_browse_isolated FROM PUBLIC;
""");
            await using var runtime = f.Activities.Shared.OpenRuntime();
            f.role = await runtime.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
            using var identifiers = new NpgsqlCommandBuilder(); var role = identifiers.QuoteIdentifier(f.role);
            await using var grant = new NpgsqlCommand($"GRANT USAGE ON SCHEMA andril_learning_browse_isolated TO {role}; GRANT SELECT ON andril_learning_browse_isolated.discovery_evidence TO {role}", (NpgsqlConnection)source.Database.GetDbConnection());
            await grant.ExecuteNonQueryAsync(); return f;
        }
        catch { await f.DisposeAsync(); throw; }
    }
    internal LearningBrowseRow Evidence(string kind = "People", string name = "visible name") => new()
    {
        AccountId = Activities.Shared.Actor().UserId, ProfileId = Activities.Shared.Actor().ModuleProfileId,
        TargetId = Guid.NewGuid(), Kind = kind, Name = name, Revision = Guid.NewGuid().ToString("N"), EvidenceAt = Activities.Shared.Time.GetUtcNow(),
        Active = true, Eligible = true, Visible = true, BlocksClear = true, OrdinaryNameDisclosure = true, Public = true, MembershipAllowed = true
    };
    internal async Task<IsolatedLearningBrowseDbContext> BoundOwner()
    {
        await using var source = await Activities.Shared.BoundOwner();
        var db = new IsolatedLearningBrowseDbContext(new DbContextOptionsBuilder<IsolatedLearningBrowseDbContext>().UseNpgsql(source.Database.GetConnectionString()!).Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('andril.subject',{Activities.Shared.Owner.Subject.ToString()},false),set_config('andril.account',{Activities.Shared.Actor().UserId.ToString()},false),set_config('andril.module','learning',false)");
        return db;
    }
    public async ValueTask DisposeAsync()
    {
        if (!string.IsNullOrEmpty(role))
        {
            await using var db = await Activities.Shared.BoundOwner(); using var identifiers = new NpgsqlCommandBuilder(); var quoted = identifiers.QuoteIdentifier(role);
            await using var revoke = new NpgsqlCommand($"REVOKE ALL ON ALL TABLES IN SCHEMA andril_learning_browse_isolated FROM {quoted}; REVOKE ALL ON SCHEMA andril_learning_browse_isolated FROM {quoted}", (NpgsqlConnection)db.Database.GetDbConnection()); await revoke.ExecuteNonQueryAsync();
        }
        await Activities.DisposeAsync();
    }
}
