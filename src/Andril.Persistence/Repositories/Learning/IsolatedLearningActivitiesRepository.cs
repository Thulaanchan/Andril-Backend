using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Activities;
using Andril.Learning.Growth;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.Learning;

/// <summary>
/// Unregistered local-only component. Real M2 owner/lifecycle boundary and FORCE RLS;
/// operation/target/Circle/offer authority is explicitly required, never fabricated.
/// Production multi-party authority and schema/permission review remain separate gates.
/// </summary>
public sealed class IsolatedLearningActivitiesRepository : ILearningActivitiesRepository
{
    private readonly IsolatedLearningDocumentBoundary boundary;
    private readonly ILearningActivityAuthority authority;
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; set; }

    public IsolatedLearningActivitiesRepository(DbContextOptions<LearningDbContext> options, ICurrentUser user,
        IPrivateModuleAccess access, IPrivateProfileService profiles, ILearningActivityAuthority authority)
    {
        boundary = new(options, user, access, profiles);
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public Task<LearningActivityResult> CreateAsync(LearningActivityKind kind, CreateLearningActivityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Run(kind, LearningActivityAction.Create, null, request, true, async (db, actor, permission, ct) =>
        {
            var state = Domain(() => LearningActivityState.Create(kind, actor, permission.Participants.ToArray(), request));
            var view = Domain(() => state.Project(new(Guid.NewGuid(), 1)));
            var members = state.Members.Select(x => x.ProfileId).OrderBy(x => x.ToString("N"), StringComparer.Ordinal).ToArray();
            Guid? low = members.Length == 2 ? members[0] : null, high = members.Length == 2 ? members[1] : null;
            if (kind == LearningActivityKind.MentorshipRequest)
            {
                var pending = await db.Set<LearningActivityRow>().Where(x => x.Kind == kind && x.Status == "Pending" && x.PairLow == low && x.PairHigh == high).ToListAsync(ct);
                foreach (var row in pending)
                {
                    var prior = ReadState(row);
                    var comparison = Domain(() => state.RestoreMentorship().ComparePendingRequest(prior.RestoreMentorship()));
                    if (comparison == PendingMentorshipComparison.ExactDuplicate) return new(LearningActivityOutcome.Conflict);
                    if (comparison == PendingMentorshipComparison.PolicyDecisionRequired) return new(LearningActivityOutcome.Unavailable);
                }
            }
            db.Add(new LearningActivityRow { Id = state.Id, Kind = kind, Generation = view.Revision.Generation, Version = 1,
                Status = view.Status, PairLow = low, PairHigh = high, Members = JsonSerializer.Serialize(state.Members), Content = JsonSerializer.Serialize(state) });
            await db.SaveChangesAsync(ct);
            return new(LearningActivityOutcome.Saved, view);
        }, cancellationToken);
    }

    public Task<LearningActivityResult> ReadAsync(LearningActivityKind kind, Guid id, CancellationToken cancellationToken = default)
        => Run(kind, LearningActivityAction.Read, id, null, false, async (db, actor, permission, ct) =>
        {
            var row = await db.Set<LearningActivityRow>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Kind == kind, ct);
            if (row is null) return new(LearningActivityOutcome.NotFound);
            var state = ReadState(row);
            if (!state.Members.Contains(actor) || !SameMembers(state.Members, permission.Participants)) return new(LearningActivityOutcome.Denied);
            return new(LearningActivityOutcome.Available, state.Project(new(row.Generation, row.Version)));
        }, cancellationToken);

    public Task<LearningActivityResult> ChangeAsync(LearningActivityKind kind, Guid id, ChangeLearningActivityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Expected is null || request.Expected.Generation == Guid.Empty || request.Expected.Version <= 0)
            return Task.FromResult(new LearningActivityResult(LearningActivityOutcome.Invalid));
        return Run(kind, request.Action, id, null, true, async (db, actor, permission, ct) =>
        {
            var row = await db.Set<LearningActivityRow>().SingleOrDefaultAsync(x => x.Id == id && x.Kind == kind, ct);
            if (row is null) return new(LearningActivityOutcome.NotFound);
            if (row.Generation != request.Expected.Generation || row.Version != request.Expected.Version) return new(LearningActivityOutcome.Conflict);
            var state = ReadState(row);
            if (!SameMembers(state.Members, permission.Participants)) return new(LearningActivityOutcome.Denied);
            Domain(() => { state.Apply(actor, request); return true; });
            if (row.Version == long.MaxValue) return new(LearningActivityOutcome.Conflict);
            row.Version++;
            var view = Domain(() => state.Project(new(row.Generation, row.Version)));
            row.Status = view.Status; row.Content = JsonSerializer.Serialize(state);
            await db.SaveChangesAsync(ct);
            return new(LearningActivityOutcome.Saved, view);
        }, cancellationToken);
    }

    private sealed record ReadLease(Guid Id, LearningActivityMember[] Members);
    public Task<LearningActivityResult> ListAsync(LearningActivityKind kind, Guid? after = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        // Bounded technical page size, not a product/record-count quota.
        if (limit is < 1 or > 100 || after == Guid.Empty) return Task.FromResult(new LearningActivityResult(LearningActivityOutcome.Invalid));
        var leases = new List<ReadLease>();
        return Run(kind, LearningActivityAction.List, null, null, false, async (db, actor, _, ct) =>
        {
            var rows = after is { } cursor
                ? await db.Set<LearningActivityRow>().FromSqlInterpolated($"SELECT * FROM andril_learning_activities_isolated.activities WHERE kind={(int)kind} AND id>{cursor} ORDER BY id LIMIT {limit}").AsNoTracking().ToListAsync(ct)
                : await db.Set<LearningActivityRow>().FromSqlInterpolated($"SELECT * FROM andril_learning_activities_isolated.activities WHERE kind={(int)kind} ORDER BY id LIMIT {limit}").AsNoTracking().ToListAsync(ct);
            var result = new List<LearningActivityView>();
            foreach (var row in rows)
            {
                var state = ReadState(row);
                var permission = await authority.AuthorizeAsync(actor, kind, LearningActivityAction.Read, row.Id, null, ct);
                if (permission is null || permission.Participants is null || permission.Outcome == LearningActivityPermissionOutcome.Unavailable) return new(LearningActivityOutcome.Unavailable);
                if (permission.Outcome != LearningActivityPermissionOutcome.Allowed || !state.Members.Contains(actor) ||
                    !SameMembers(state.Members, permission.Participants)) return new(LearningActivityOutcome.Denied);
                result.Add(state.Project(new(row.Generation, row.Version)));
                leases.Add(new(row.Id, state.Members.ToArray()));
            }
            return new(LearningActivityOutcome.Available, Activities: result.AsReadOnly());
        }, cancellationToken, leases);
    }

    private static LearningActivityState ReadState(LearningActivityRow row)
    {
        var state = JsonSerializer.Deserialize<LearningActivityState>(row.Content) ?? throw new JsonException();
        var members = JsonSerializer.Deserialize<LearningActivityMember[]>(row.Members) ?? throw new JsonException();
        if (state.Id != row.Id || state.Kind != row.Kind || !state.Members.SequenceEqual(members)) throw new JsonException();
        return state;
    }
    private static bool SameMembers(IEnumerable<LearningActivityMember> stored, IEnumerable<LearningActivityMember> resolved)
        => stored.OrderBy(x => x.ProfileId).SequenceEqual(resolved.OrderBy(x => x.ProfileId));

    private sealed class AuthorityUnavailableException : Exception { }
    private sealed class DomainFailureException(LearningActivityOutcome outcome) : Exception
    {
        internal LearningActivityOutcome Outcome { get; } = outcome;
    }
    private static T Domain<T>(Func<T> action)
    {
        try { return action(); }
        catch (NotSupportedException) { throw new DomainFailureException(LearningActivityOutcome.Unavailable); }
        catch (ArgumentException) { throw new DomainFailureException(LearningActivityOutcome.Invalid); }
        catch (KeyNotFoundException) { throw new DomainFailureException(LearningActivityOutcome.Invalid); }
        catch (UnauthorizedAccessException) { throw new DomainFailureException(LearningActivityOutcome.Denied); }
        catch (InvalidOperationException) { throw new DomainFailureException(LearningActivityOutcome.Conflict); }
    }
    private async Task<LearningActivityResult> Run(LearningActivityKind kind, LearningActivityAction action, Guid? id,
        CreateLearningActivityRequest? creation, bool write,
        Func<IsolatedLearningActivitiesDbContext, LearningActivityMember, LearningActivityPermission, CancellationToken, Task<LearningActivityResult>> work,
        CancellationToken ct, IReadOnlyList<ReadLease>? readLeases = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(action) || id == Guid.Empty) return new(LearningActivityOutcome.Invalid);
        LearningActivityMember? actor = null; LearningActivityPermission? permission = null;
        async Task<LearningActivityPermission> Require(CancellationToken token)
        {
            var current = await authority.AuthorizeAsync(actor!, kind, action, id, creation, token);
            if (current is null || current.Outcome == LearningActivityPermissionOutcome.Unavailable) throw new AuthorityUnavailableException();
            if (current.Outcome != LearningActivityPermissionOutcome.Allowed) throw new ProfileAccessDeniedException();
            var participants = current.Participants?.ToArray() ?? [];
            if (participants.Length == 0 || !participants.Contains(actor!) || participants.Any(x => x is null || x.AccountId == Guid.Empty || x.ProfileId == Guid.Empty) ||
                participants.Select(x => x.AccountId).Distinct().Count() != participants.Length || participants.Select(x => x.ProfileId).Distinct().Count() != participants.Length)
                throw new ProfileAccessDeniedException();
            if (permission is not null && !permission.Participants.OrderBy(x => x.ProfileId).SequenceEqual(participants.OrderBy(x => x.ProfileId)))
                throw new ProfileAccessDeniedException();
            return new(current.Outcome, Array.AsReadOnly(participants));
        }
        try
        {
            return await boundary.Run(async (parent, owner, token) =>
            {
                var account = await parent.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(token);
                actor = new(Guid.Parse(account), owner.Profile);
                permission = await Require(token);
                await using var db = new IsolatedLearningActivitiesDbContext(new DbContextOptionsBuilder<IsolatedLearningActivitiesDbContext>()
                    .UseNpgsql((NpgsqlConnection)parent.Database.GetDbConnection()).Options);
                await db.Database.UseTransactionAsync(parent.Database.CurrentTransaction!.GetDbTransaction(), token);
                return await work(db, actor, permission, token);
            }, failure => new LearningActivityResult(failure switch
            {
                LearningDocumentFailure.Denied => LearningActivityOutcome.Denied,
                LearningDocumentFailure.Conflict => LearningActivityOutcome.Conflict,
                LearningDocumentFailure.CommitUnknown => LearningActivityOutcome.CommitUnknown,
                _ => LearningActivityOutcome.Unavailable
            }), ct, write, async (phase, token) =>
            {
                if (TestCheckpoint is not null) await TestCheckpoint(phase, token);
                if (actor is not null) _ = await Require(token);
                if (actor is not null && readLeases is not null)
                    foreach (var lease in readLeases)
                    {
                        var current = await authority.AuthorizeAsync(actor, kind, LearningActivityAction.Read, lease.Id, null, token);
                        if (current is null || current.Participants is null || current.Outcome == LearningActivityPermissionOutcome.Unavailable) throw new AuthorityUnavailableException();
                        if (current.Outcome != LearningActivityPermissionOutcome.Allowed || !current.Participants.Contains(actor) || !SameMembers(lease.Members, current.Participants))
                            throw new ProfileAccessDeniedException();
                    }
            }, ex => ex is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } ||
                ex is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } });
        }
        catch (AuthorityUnavailableException) { return new(LearningActivityOutcome.Unavailable); }
        catch (DomainFailureException ex) { return new(ex.Outcome); }
        catch (NotSupportedException) { return new(LearningActivityOutcome.Unavailable); }
        catch (JsonException) { return new(LearningActivityOutcome.Unavailable); }
        catch (ArgumentException) { return new(LearningActivityOutcome.Unavailable); }
        catch (KeyNotFoundException) { return new(LearningActivityOutcome.Unavailable); }
        catch (InvalidOperationException) { return new(LearningActivityOutcome.Unavailable); }
    }
}
