using System.Text.Json.Serialization;

namespace Andril.Learning.Activities;

public enum LearningActivityKind { SkillExchange = 1, Pact = 2, GrowthSelfReport = 3, MentorshipOffer = 4, MentorshipRequest = 5, Circle = 6 }
public enum LearningActivityAction { Read = 1, Create = 2, Accept = 3, Decline = 4, Cancel = 5, ConfirmCompletion = 6, Withdraw = 7, CreateGoal = 8, EditGoal = 9, UpdateGoalProgress = 10, List = 11 }
public enum LearningActivityOutcome { Unavailable, Available, Saved, Denied, Invalid, Conflict, NotFound, CommitUnknown }
public enum LearningActivityPermissionOutcome { Unavailable, Denied, Allowed }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningActivityMember(Guid AccountId, Guid ProfileId)
{
    public override string ToString() => "Learning participant [private references omitted]";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningActivityRevision(Guid Generation, long Version);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateLearningActivityRequest(Guid[]? TargetProfiles = null, Guid OfferedSkillId = default,
    Guid WantedSkillId = default, string[]? Commitments = null, Guid[]? SkillIds = null,
    Guid? RelatedOfferId = null, Guid? CircleId = null)
{
    public override string ToString() => "Private Learning creation [content and references omitted]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeLearningActivityRequest(LearningActivityAction Action, LearningActivityRevision Expected,
    Guid? GoalId = null, string? Text = null, int? ProgressPercent = null)
{
    public override string ToString() => "Private Learning command [content and references omitted]";
}

/// <summary>Server-resolved participants, never request identity/eligibility flags.</summary>
public sealed record LearningActivityPermission(LearningActivityPermissionOutcome Outcome,
    IReadOnlyList<LearningActivityMember> Participants);

/// <summary>
/// New, provisional cross-owner integration contract. Authorize current M2 account/profile
/// standing and operation-specific rights on every invocation, including commit/read release.
/// Resolve selected target profiles to their real internal accounts; verify their current
/// Learning eligibility. For mentorship, verify the exact voluntary offer and owner; for
/// Circles verify the actual M5 resource binding and operation rights, never infer ownership
/// or membership from the domain ID. Implementations must coordinate revocation with the
/// enclosing transaction. Unknown policy, mapping or evidence returns Unavailable/Denied.
/// No production implementation or permissive default is supplied.
/// </summary>
public interface ILearningActivityAuthority
{
    Task<LearningActivityPermission> AuthorizeAsync(LearningActivityMember actor, LearningActivityKind kind,
        LearningActivityAction action, Guid? resourceId, CreateLearningActivityRequest? creation,
        CancellationToken cancellationToken = default);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningActivityCommitment(Guid Id, string Text)
{
    public override string ToString() => "Private commitment [content omitted]";
}
public sealed record LearningActivityGoal(Guid Id, string Text, int ProgressPercent)
{
    public override string ToString() => "Private shared goal [content omitted]";
}
public sealed record LearningActivityView(Guid Id, LearningActivityKind Kind, LearningActivityRevision Revision,
    string? Status, Guid? OfferedSkillId, Guid? WantedSkillId, IReadOnlyList<Guid> SkillIds,
    IReadOnlyList<LearningActivityCommitment> Commitments, IReadOnlyList<LearningActivityGoal> Goals)
{
    public override string ToString() => "Private Learning activity [content and identities omitted]";
}
public sealed record LearningActivityResult(LearningActivityOutcome Outcome, LearningActivityView? Activity = null,
    IReadOnlyList<LearningActivityView>? Activities = null)
{
    public override string ToString() => $"Learning activity outcome: {Outcome}";
}
public interface ILearningActivitiesRepository
{
    Task<LearningActivityResult> CreateAsync(LearningActivityKind kind, CreateLearningActivityRequest request, CancellationToken cancellationToken = default);
    Task<LearningActivityResult> ReadAsync(LearningActivityKind kind, Guid id, CancellationToken cancellationToken = default);
    Task<LearningActivityResult> ListAsync(LearningActivityKind kind, Guid? after = null, int limit = 50, CancellationToken cancellationToken = default);
    Task<LearningActivityResult> ChangeAsync(LearningActivityKind kind, Guid id, ChangeLearningActivityRequest request, CancellationToken cancellationToken = default);
}
