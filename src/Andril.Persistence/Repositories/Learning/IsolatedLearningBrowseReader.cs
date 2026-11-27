using System.Collections.Concurrent;
using Andril.Common.Accounts.Profiles;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;
using Andril.Learning.Discovery;
using Andril.Learning.Interfaces;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>Unregistered local-only persistence reader. No synthetic evidence is promoted to production.</summary>
public sealed class IsolatedLearningBrowseReader : ILearningPeopleDiscoveryReader, IGroupDiscoveryReadRepository
{
    private readonly IsolatedLearningDocumentBoundary boundary;
    private readonly ILearningBrowseSourceAuthority source;
    private readonly ConcurrentDictionary<GroupDiscoverySnapshot, GroupDiscoveryQuery> active = new();
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; set; }
    public IsolatedLearningBrowseReader(DbContextOptions<LearningDbContext> options, ICurrentUser user,
        IPrivateModuleAccess access, IPrivateProfileService profiles, ILearningBrowseSourceAuthority source)
    { boundary = new(options, user, access, profiles); this.source = source; }

    private async Task<T?> Read<T>(string kind, string query, int limit, LearningSetupScope? expected, Guid? profile,
        Func<LearningSetupScope, IReadOnlyList<LearningBrowseRow>, CancellationToken, Task<T?>> project, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 500 || query.Any(char.IsControl) || limit is < 1 or > 100) return null;
        LearningSetupScope? resolved = null; LearningBrowseSourceReference[] references = [];
        try
        {
            return await boundary.Run(async (parent, owner, token) =>
            {
                var account = await parent.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(token);
                resolved = new(Guid.Parse(account), owner.Profile);
                if ((expected is not null && (expected.AccountId != resolved.AccountId || expected.LearningProfileId != owner.Profile)) ||
                    (profile is not null && profile != owner.Profile)) throw new ProfileAccessDeniedException();
                await using var db = new IsolatedLearningBrowseDbContext(new DbContextOptionsBuilder<IsolatedLearningBrowseDbContext>()
                    .UseNpgsql((NpgsqlConnection)parent.Database.GetDbConnection()).Options);
                await db.Database.UseTransactionAsync(parent.Database.CurrentTransaction!.GetDbTransaction(), token);
                // Filter before search: hidden/unclassified content must not leak through match presence.
                var rows = await db.Set<LearningBrowseRow>().AsNoTracking().Where(x => x.AccountId == resolved.AccountId &&
                    x.ProfileId == owner.Profile && x.Kind == kind && x.Module == "learning" && x.TargetId != owner.Profile &&
                    x.Active && x.Eligible && x.Visible && x.BlocksClear && x.OrdinaryNameDisclosure && x.Revision != "" &&
                    (kind != "Classroom" || (x.MembershipAllowed && (x.Public || x.PrivateDiscoveryAllowed))) && x.Name.Contains(query))
                    .OrderBy(x => x.TargetId).Take(limit).ToListAsync(token);
                references = rows.Select(x => new LearningBrowseSourceReference(x.TargetId, x.Revision)).ToArray();
                if (!await source.RevalidateAsync(resolved, kind, references, token)) throw new ProfileAccessDeniedException();
                return await project(resolved, rows, token);
            }, _ => (T?)null, ct, checkpoint: async (phase, token) =>
            {
                if (TestCheckpoint is not null) await TestCheckpoint(phase, token);
                if (resolved is null || !await source.RevalidateAsync(resolved, kind, references, token)) throw new ProfileAccessDeniedException();
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    public Task<T?> WithAuthorizedSearchAsync<T>(LearningSetupScope scope, string query, int limit,
        Func<LearningPeopleSnapshot, CancellationToken, Task<T>> project, CancellationToken cancellationToken = default) where T : class
        => Read<T>("People", query, limit, scope, null, async (owner, rows, ct) => await project(new(owner.AccountId, owner.LearningProfileId,
            Guid.NewGuid().ToString("N"), rows.Select(x => new LearningPeopleEvidence(x.TargetId, x.Module, x.Name,
                LearningDiscoveryLifecycle.Active, LearningDiscoveryDecision.Allowed, LearningDiscoveryDecision.Allowed,
                LearningDiscoveryDecision.Allowed, LearningDiscoveryDecision.Allowed)).ToArray()), ct), cancellationToken);

    public Task<T?> WithAuthorizedReadAsync<T>(GroupDiscoveryQuery query,
        Func<GroupDiscoverySnapshot, CancellationToken, Task<T?>> project, CancellationToken cancellationToken = default) where T : class
    {
        if (query.Scope.Module != "learning" || query.Scope.GroupKind != GroupKind.Classroom) return Task.FromResult<T?>(null);
        return Read("Classroom", query.SearchText, query.MaximumCandidates, null, query.ActorProfileId, async (owner, rows, ct) =>
        {
            var snapshot = new GroupDiscoverySnapshot(query.ActorProfileId, query.Scope, rows.Select(x => new GroupDiscoveryEvidence(
                new(x.TargetId, query.Scope, x.Name), GroupDiscoveryLifecycle.Active, GroupDiscoveryDecision.Allowed,
                x.Public ? GroupDiscoveryPrivacy.Public : GroupDiscoveryPrivacy.Private,
                x.PrivateDiscoveryAllowed ? GroupDiscoveryDecision.Allowed : GroupDiscoveryDecision.Denied,
                GroupDiscoveryDecision.Allowed, GroupDiscoveryDecision.Allowed, GroupDiscoveryDecision.Allowed, x.Revision, x.EvidenceAt)).ToArray(), Guid.NewGuid().ToString("N"));
            if (!active.TryAdd(snapshot, query)) return null;
            try { return await project(snapshot, ct); }
            finally { active.TryRemove(snapshot, out _); }
        }, cancellationToken);
    }
    public Task<GroupDiscoveryDecision> RevalidateAsync(GroupDiscoveryQuery query, GroupDiscoverySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Not a transferable positive cache: only the exact live callback can revalidate;
        // underlying source revocation is checked again before the M2 transaction releases.
        return Task.FromResult(active.TryGetValue(snapshot, out var expected) && expected == query
            ? GroupDiscoveryDecision.Allowed : GroupDiscoveryDecision.Denied);
    }
}
