using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Andril.Persistence.Repositories.AI;

/// <summary>
/// Unregistered durable TD-03 implementation on the existing disposable AI model.
/// Reuses the existing Account/Membership/Profile transaction and isolated connection guard.
/// No policy wording, processing-consent rule, retention or production schema approval is inferred.
/// </summary>
internal sealed class IsolatedLearningAiAcknowledgementRepository : IAiPrivacyAcknowledgementRepository
{
    private readonly IsolatedLearningDocumentBoundary boundary;
    private readonly IAiPrivacyPolicyVersionSource policies;
    private readonly TimeProvider clock;
    internal Func<string, CancellationToken, Task>? TestCheckpoint { get; init; }

    internal IsolatedLearningAiAcknowledgementRepository(DbContextOptions<LearningDbContext> options,
        ICurrentUser user, IPrivateModuleAccess access, IPrivateProfileService profiles,
        IAiPrivacyPolicyVersionSource policies, TimeProvider clock)
    {
        boundary = new(options, user, access, profiles);
        this.policies = policies;
        this.clock = clock;
    }

    public Task<AiAcknowledgementResult> ReadAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default)
        => Run(actor, false, false, null, Guid.Empty, null, cancellationToken);

    public Task<AiAcknowledgementResult> AcknowledgeAsync(AiAuthorizedActor actor, string reviewedPolicyVersion,
        Guid operationId, AiAcknowledgementRevision? expectedRevision, CancellationToken cancellationToken = default)
        => Run(actor, true, true, reviewedPolicyVersion, operationId, expectedRevision, cancellationToken);

    public Task<AiAcknowledgementResult> RevokeAsync(AiAuthorizedActor actor, Guid operationId,
        AiAcknowledgementRevision? expectedRevision, CancellationToken cancellationToken = default)
        => Run(actor, true, false, null, operationId, expectedRevision, cancellationToken);

    private async Task<AiAcknowledgementResult> Run(AiAuthorizedActor actor, bool write, bool acknowledge,
        string? reviewedVersion, Guid operationId, AiAcknowledgementRevision? expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (actor is null || actor.Module != AiModule.Learning || actor.UserId == Guid.Empty || actor.ModuleProfileId == Guid.Empty)
            return new(AiAcknowledgementOutcome.Denied);
        if (write && (operationId == Guid.Empty || (acknowledge && string.IsNullOrWhiteSpace(reviewedVersion))))
            return new(AiAcknowledgementOutcome.Conflict);

        string? observedPolicy = null;
        return await boundary.Run(async (parent, owner, token) =>
        {
            var account = await parent.Database.SqlQueryRaw<string>("SELECT current_setting('andril.account',true) AS \"Value\"").SingleAsync(token);
            if (owner.Profile != actor.ModuleProfileId || account != actor.UserId.ToString())
                throw new ProfileAccessDeniedException();
            await using var db = new IsolatedLearningAiDbContext(new DbContextOptionsBuilder<IsolatedLearningAiDbContext>()
                .UseNpgsql((NpgsqlConnection)parent.Database.GetDbConnection()).Options);
            await db.Database.UseTransactionAsync(parent.Database.CurrentTransaction!.GetDbTransaction(), token);
            var row = await db.Set<IsolatedAiAcknowledgementRow>().SingleOrDefaultAsync(x =>
                x.AccountId == actor.UserId && x.ModuleProfileId == actor.ModuleProfileId && x.Module == AiModule.Learning, token);
            if (!write)
                return new AiAcknowledgementResult(AiAcknowledgementOutcome.Available, row is null ? null : State(row));

            // Revocation must remain possible even while the current notice authority is unavailable.
            if (acknowledge)
            {
                observedPolicy = await policies.ReadCurrentVersionAsync(actor, token);
                if (string.IsNullOrWhiteSpace(observedPolicy)) return new(AiAcknowledgementOutcome.Unavailable);
                if (!StringComparer.Ordinal.Equals(observedPolicy, reviewedVersion)) return new(AiAcknowledgementOutcome.Conflict);
            }
            if (row is not null && row.LastOperationId == operationId)
            {
                if (row.Acknowledged != acknowledge || (acknowledge && row.PolicyVersion != reviewedVersion))
                    return new(AiAcknowledgementOutcome.Conflict);
                return new(AiAcknowledgementOutcome.Saved, State(row));
            }
            if ((row is null && expected is not null) || (row is not null &&
                (expected is null || row.Generation != expected.Generation || row.Version != expected.Version)))
                return new(AiAcknowledgementOutcome.Conflict);

            if (row is null)
            {
                row = new() { AccountId = actor.UserId, ModuleProfileId = actor.ModuleProfileId, Module = AiModule.Learning,
                    Generation = Guid.NewGuid(), Version = 0 };
                db.Add(row);
            }
            row.Version = checked(row.Version + 1);
            row.Acknowledged = acknowledge;
            if (acknowledge) row.PolicyVersion = observedPolicy!;
            row.LastOperationId = operationId;
            row.UpdatedAtUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(token);
            if (TestCheckpoint is not null) await TestCheckpoint("prepared", token);
            return new(AiAcknowledgementOutcome.Saved, State(row));
        }, failure => new AiAcknowledgementResult(failure switch
        {
            LearningDocumentFailure.Denied => AiAcknowledgementOutcome.Denied,
            LearningDocumentFailure.Conflict => AiAcknowledgementOutcome.Conflict,
            LearningDocumentFailure.CommitUnknown => AiAcknowledgementOutcome.CommitUnknown,
            _ => AiAcknowledgementOutcome.Unavailable
        }), ct, write, async (phase, token) =>
        {
            if (TestCheckpoint is not null) await TestCheckpoint(phase, token);
            if (phase == "before-commit" && acknowledge && observedPolicy is not null)
            {
                var current = await policies.ReadCurrentVersionAsync(actor, token);
                if (!StringComparer.Ordinal.Equals(current, observedPolicy)) throw new ProfileContractPendingException();
            }
        });
    }

    private static AiAcknowledgementState State(IsolatedAiAcknowledgementRow row)
        => new(row.PolicyVersion, row.Acknowledged, new(row.Generation, row.Version));
}
