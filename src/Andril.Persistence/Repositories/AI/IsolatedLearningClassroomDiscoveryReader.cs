using Andril.Common.Accounts.Profiles;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.AI;

/// <summary>
/// New isolated, server-provisioned evidence record. Not an inferred production M5 policy.
/// The provisioner must coordinate every evidence/content change through the existing
/// account/membership/resource boundary. Runtime has SELECT only. No client writes exist.
/// </summary>
internal sealed class IsolatedClassroomDiscoveryRow
{
    public Guid AccountId { get; set; }
    public Guid ModuleProfileId { get; set; }
    public Guid ClassroomId { get; set; }
    public string Name { get; set; } = "";
    public ClassroomDiscoveryLifecycle Lifecycle { get; set; }
    public ClassroomDiscoveryDecision Discoverability { get; set; }
    public bool IsPrivate { get; set; }
    public ClassroomDiscoveryDecision PrivateAuthorization { get; set; }
    public ClassroomDiscoveryDecision Visibility { get; set; }
    public ClassroomDiscoveryDecision Membership { get; set; }
    public ClassroomDiscoveryDecision Blocking { get; set; }
    public ClassroomDiscoveryDecision NameAiDisclosure { get; set; }
    public override string ToString() => "Classroom source evidence [private content omitted]";
}

/// <summary>
/// Actual PostgreSQL reader for the explicitly isolated evidence model, unregistered.
/// Missing evidence never becomes a discovery permission. Production M5 source ingestion,
/// classification and coordinated evidence writers remain separate integration dependencies.
/// </summary>
internal sealed class IsolatedLearningClassroomDiscoveryReader : ILearningClassroomDiscoveryReader
{
    private readonly IsolatedLearningDocumentBoundary boundary;
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; init; }
    internal IsolatedLearningClassroomDiscoveryReader(DbContextOptions<LearningDbContext> options,
        ICurrentUser user, IPrivateModuleAccess access, IPrivateProfileService profiles)
        => boundary = new(options, user, access, profiles);

    public Task<T?> WithAuthorizedSearchAsync<T>(Guid accountId, Guid learningProfileId, string query,
        Func<LearningClassroomDiscoverySnapshot, CancellationToken, Task<T>> project,
        CancellationToken cancellationToken = default) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(project);
        if (accountId == Guid.Empty || learningProfileId == Guid.Empty || string.IsNullOrWhiteSpace(query))
            return Task.FromResult<T?>(null);
        return boundary.Run<T?>(async (parent, owner, ct) =>
        {
            var account = await parent.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(ct);
            if (owner.Profile != learningProfileId || account != accountId.ToString()) throw new ProfileAccessDeniedException();
            await using var db = new IsolatedLearningAiDbContext(new DbContextOptionsBuilder<IsolatedLearningAiDbContext>()
                .UseNpgsql((NpgsqlConnection)parent.Database.GetDbConnection()).Options);
            await db.Database.UseTransactionAsync(parent.Database.CurrentTransaction!.GetDbTransaction(), ct);
            // All predicates and query text are translated to parameterized SQL. Unknown enum values cannot pass.
            var rows = await db.Set<IsolatedClassroomDiscoveryRow>().AsNoTracking().Where(x =>
                x.AccountId == accountId && x.ModuleProfileId == learningProfileId &&
                x.Lifecycle == ClassroomDiscoveryLifecycle.Active &&
                x.Discoverability == ClassroomDiscoveryDecision.Allowed && x.Visibility == ClassroomDiscoveryDecision.Allowed &&
                x.Membership == ClassroomDiscoveryDecision.Allowed && x.Blocking == ClassroomDiscoveryDecision.Allowed &&
                (!x.IsPrivate || x.PrivateAuthorization == ClassroomDiscoveryDecision.Allowed) &&
                x.NameAiDisclosure == ClassroomDiscoveryDecision.Allowed && x.Name.Contains(query))
                .OrderBy(x => x.ClassroomId).ToListAsync(ct);
            var evidence = rows.Select(x => new LearningClassroomDiscoveryEvidence(
                new GroupEntry(x.ClassroomId, new GroupModuleScope("learning", GroupKind.Classroom), x.Name),
                x.Lifecycle, x.Discoverability, x.IsPrivate, x.PrivateAuthorization, x.Visibility,
                x.Membership, x.Blocking, x.NameAiDisclosure)).ToArray();
            return await project(new(accountId, learningProfileId, Array.AsReadOnly(evidence)), ct);
        }, failure => failure == LearningDocumentFailure.Denied ? null : throw new InvalidOperationException("Classroom discovery unavailable."),
            cancellationToken, checkpoint: TestCheckpoint);
    }
}
