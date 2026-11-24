using Andril.Learning.Interfaces;
using Andril.Shared.Authentication;

namespace Andril.Learning.Services;

/// <summary>Unregistered provisional Learning preflight using the existing verified-subject
/// interface. No repository dependency, mutation, production M2 adapter or authorization cache.</summary>
public sealed class LearningSetupAccessBoundary : ILearningSetupAccessBoundary
{
    private readonly ICurrentUser user;
    private readonly ILearningSetupAuthorityReader authority;
    public LearningSetupAccessBoundary(ICurrentUser user, ILearningSetupAuthorityReader authority)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(authority);
        this.user = user; this.authority = authority;
    }

    public async Task<LearningSetupAccessResult> CheckAsync(LearningSetupOperation operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(operation) || !user.IsAuthenticated || user.UserId is not Guid subject || subject == Guid.Empty)
            return LearningSetupAccessResult.Denied();
        var evidence = await authority.ReadCurrentAsync(subject, operation, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!user.IsAuthenticated || user.UserId != subject) return LearningSetupAccessResult.Denied();
        if (evidence is null) return LearningSetupAccessResult.Unavailable();
        if (evidence.SubjectId != subject || evidence.AccountId == Guid.Empty || evidence.LearningProfileId == Guid.Empty ||
            evidence.ProfileOwnerAccountId != evidence.AccountId || evidence.Module != "learning" || evidence.Operation != operation ||
            !evidence.SessionCurrent || !evidence.MembershipActive || !evidence.StandingAllows || !evidence.ConsentSatisfied ||
            !evidence.OperationAllowed || evidence.Revoked || evidence.ObservedVersion == Guid.Empty ||
            evidence.CurrentVersion == Guid.Empty || evidence.ObservedVersion != evidence.CurrentVersion ||
            (operation == LearningSetupOperation.EnterHome && !evidence.SetupComplete))
            return LearningSetupAccessResult.Denied();
        return LearningSetupAccessResult.Allowed(new(evidence.AccountId, evidence.LearningProfileId));
    }
}
