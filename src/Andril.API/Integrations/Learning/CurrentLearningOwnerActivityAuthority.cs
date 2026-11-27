using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.Learning.Activities;
using Andril.Learning.Interfaces;

namespace Andril.API.Integrations.Learning;

/// <summary>
/// Reuses the verified account-backed current-module ownership reader for the two
/// existing private owner-only foundations. This grants no AI processing permission,
/// verified evidence status, public offer visibility, target access or M5 membership.
/// Supply the real current-owner reader explicitly; a denied default remains denied.
/// Multi-party/Circle operations stay unavailable pending their authoritative policies.
/// Unregistered; does not change the existing AI registrations or activate endpoints.
/// </summary>
public sealed class CurrentLearningOwnerActivityAuthority : ILearningActivityAuthority
{
    private readonly IAiModuleProfileAccessReader? currentOwner;
    private readonly ICurrentLearningOwnerBoundary? sharedOwner;
    public CurrentLearningOwnerActivityAuthority(IAiModuleProfileAccessReader currentOwner)=>this.currentOwner=currentOwner;
    public CurrentLearningOwnerActivityAuthority(ICurrentLearningOwnerBoundary sharedOwner)=>this.sharedOwner=sharedOwner;
    private sealed record Profile(Guid Id);
    public async Task<LearningActivityPermission> AuthorizeAsync(LearningActivityMember actor, LearningActivityKind kind,
        LearningActivityAction action, Guid? resourceId, CreateLearningActivityRequest? creation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (actor is null || actor.AccountId == Guid.Empty || actor.ProfileId == Guid.Empty)
            return new(LearningActivityPermissionOutcome.Denied, []);
        if (kind is not (LearningActivityKind.GrowthSelfReport or LearningActivityKind.MentorshipOffer) ||
            action is not (LearningActivityAction.Create or LearningActivityAction.Read or LearningActivityAction.List))
            return new(LearningActivityPermissionOutcome.Unavailable, []);
        if (creation?.TargetProfiles is { Length: > 0 }) return new(LearningActivityPermissionOutcome.Denied, []);
        try
        {
            var profile = sharedOwner is null ? await currentOwner!.FindAuthorizedProfileIdAsync(actor.AccountId, AiModule.Learning, cancellationToken)
                : (await sharedOwner.WithCurrentOwnerAsync<Profile>((owner,_)=>Task.FromResult<Profile?>(new(owner.ProfileId)),cancellationToken,actor.AccountId))?.Id;
            return profile == actor.ProfileId
                ? new(LearningActivityPermissionOutcome.Allowed, Array.AsReadOnly(new[] { actor }))
                : new(LearningActivityPermissionOutcome.Denied, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(LearningActivityPermissionOutcome.Unavailable, []); }
    }
}
