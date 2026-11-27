using Andril.Learning.Circles;
using Andril.Learning.Growth;
using Andril.Learning.Pacts;
using Andril.Learning.SkillExchange;
using System.Text.Json.Serialization;
using Exchange = Andril.Learning.SkillExchange.SkillExchange;

namespace Andril.Learning.Activities;

/// <summary>
/// Private persistence representation, never an HTTP request/response. Replays existing
/// domain operations rather than duplicating their lifecycle rules. Canonical commitment
/// and goal IDs are durable; temporary domain-instance IDs never leave the projection.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LearningActivityState
{
    public Guid Id { get; set; }
    public LearningActivityKind Kind { get; set; }
    public LearningActivityMember Creator { get; set; } = new(Guid.Empty, Guid.Empty);
    public LearningActivityMember[] Members { get; set; } = [];
    public Guid OfferedSkillId { get; set; }
    public Guid WantedSkillId { get; set; }
    public Guid? OfferId { get; set; }
    public Guid[] SkillIds { get; set; } = [];
    public LearningActivityCommitment[] Commitments { get; set; } = [];
    public List<LearningActivityEvent> Events { get; set; } = [];

    public static LearningActivityState Create(LearningActivityKind kind, LearningActivityMember actor,
        LearningActivityMember[] members, CreateLearningActivityRequest request)
    {
        var unsupported = kind switch
        {
            LearningActivityKind.SkillExchange => request.Commitments is not null || request.SkillIds is not null || request.RelatedOfferId is not null || request.CircleId is not null,
            LearningActivityKind.Pact => request.OfferedSkillId != Guid.Empty || request.WantedSkillId != Guid.Empty || request.SkillIds is not null || request.RelatedOfferId is not null || request.CircleId is not null,
            LearningActivityKind.GrowthSelfReport => request.OfferedSkillId != Guid.Empty || request.WantedSkillId != Guid.Empty || request.Commitments is not null || request.RelatedOfferId is not null || request.CircleId is not null,
            LearningActivityKind.MentorshipRequest => request.OfferedSkillId != Guid.Empty || request.WantedSkillId != Guid.Empty || request.Commitments is not null || request.SkillIds is not null || request.CircleId is not null,
            LearningActivityKind.MentorshipOffer => request.OfferedSkillId != Guid.Empty || request.WantedSkillId != Guid.Empty || request.Commitments is not null || request.SkillIds is not null || request.RelatedOfferId is not null || request.CircleId is not null,
            LearningActivityKind.Circle => request.OfferedSkillId != Guid.Empty || request.WantedSkillId != Guid.Empty || request.Commitments is not null || request.SkillIds is not null || request.RelatedOfferId is not null || request.TargetProfiles is { Length: > 0 },
            _ => true
        };
        if (unsupported) throw new ArgumentException("Fields are not supported for this activity kind.", nameof(request));
        if (!Enum.IsDefined(kind) || members.Length == 0 || !members.Contains(actor) ||
            members.Any(x => x is null || x.AccountId == Guid.Empty || x.ProfileId == Guid.Empty) ||
            members.Select(x => x.AccountId).Distinct().Count() != members.Length ||
            members.Select(x => x.ProfileId).Distinct().Count() != members.Length)
            throw new ArgumentException("Valid server-resolved Learning participants are required.");
        var targets = request.TargetProfiles ?? [];
        if (targets.Any(x => x == Guid.Empty) || targets.Distinct().Count() != targets.Length || targets.Contains(actor.ProfileId))
            throw new ArgumentException("Selected Learning profiles must be distinct.");
        if (kind is LearningActivityKind.SkillExchange or LearningActivityKind.MentorshipRequest)
        {
            if (members.Length != 2 || targets.Length != 1 || !members.Any(x => x.ProfileId == targets[0]))
                throw new ArgumentException("An authorized counterpart is required.");
        }
        else if (kind == LearningActivityKind.Pact)
        {
            if (!members.Select(x => x.ProfileId).ToHashSet().SetEquals(targets.Append(actor.ProfileId)))
                throw new ArgumentException("Pact participants must match the selected profiles.");
        }
        else if (kind != LearningActivityKind.Circle && (members.Length != 1 || targets.Length != 0))
            throw new ArgumentException("This activity is private to its authorized owner.");

        var state = new LearningActivityState { Id = Guid.NewGuid(), Kind = kind, Creator = actor, Members = members.ToArray() };
        switch (kind)
        {
            case LearningActivityKind.SkillExchange:
                state.OfferedSkillId = request.OfferedSkillId; state.WantedSkillId = request.WantedSkillId; break;
            case LearningActivityKind.Pact:
                var pact = LearningPact.Propose(state.Id, PactMember(actor), members.Select(PactMember), request.Commitments ?? []);
                state.Commitments = pact.Commitments.Select(x => new LearningActivityCommitment(x.Id, x.Text)).ToArray(); break;
            case LearningActivityKind.GrowthSelfReport: state.SkillIds = (request.SkillIds ?? []).ToArray(); break;
            case LearningActivityKind.MentorshipRequest:
                if (request.RelatedOfferId is not { } offer || offer == Guid.Empty) throw new ArgumentException("A verified offer is required.");
                state.OfferId = offer; break;
            case LearningActivityKind.Circle:
                if (request.CircleId is not { } circle || circle == Guid.Empty)
                    throw new NotSupportedException("An authoritative Circle resource binding is required.");
                state.Id = circle; break;
        }
        _ = state.Project(new(Guid.NewGuid(), 1));
        return state;
    }

    public void Apply(LearningActivityMember actor, ChangeLearningActivityRequest request)
    {
        if (!Members.Contains(actor)) throw new UnauthorizedAccessException("Learning activity access denied.");
        if (request.Action is LearningActivityAction.Read or LearningActivityAction.Create or LearningActivityAction.List || !Enum.IsDefined(request.Action))
            throw new ArgumentException("A supported activity command is required.");
        var invalidFields = Kind == LearningActivityKind.Circle
            ? request.Action switch
            {
                LearningActivityAction.CreateGoal => request.GoalId is not null || request.ProgressPercent is not null,
                LearningActivityAction.EditGoal => request.ProgressPercent is not null,
                LearningActivityAction.UpdateGoalProgress => request.Text is not null,
                _ => request.GoalId is not null || request.Text is not null || request.ProgressPercent is not null
            }
            : request.GoalId is not null || request.Text is not null || request.ProgressPercent is not null;
        if (invalidFields) throw new ArgumentException("Fields are not supported for this command.", nameof(request));
        if (Kind == LearningActivityKind.Circle && (request.Action is LearningActivityAction.EditGoal or LearningActivityAction.UpdateGoalProgress) &&
            (request.GoalId is null || !Project(request.Expected).Goals.Any(x => x.Id == request.GoalId)))
            throw new ArgumentException("An existing goal is required.");
        var goal = request.Action == LearningActivityAction.CreateGoal ? Guid.NewGuid() : request.GoalId;
        Events.Add(new(request.Action, actor, goal, request.Text, request.ProgressPercent));
        try { _ = Project(request.Expected); }
        catch { Events.RemoveAt(Events.Count - 1); throw; }
    }

    public LearningMentorship RestoreMentorship()
    {
        if (Kind != LearningActivityKind.MentorshipRequest || OfferId is not { } offer)
            throw new ArgumentException("A mentorship request is required.");
        var mentor = Members.Single(x => x != Creator);
        var value = LearningMentorship.Request(Id, MentorMember(Creator), LearningMentorshipOffer.Create(offer, MentorMember(mentor)));
        foreach (var e in Events)
            switch (e.Action)
            {
                case LearningActivityAction.Accept: value.Accept(MentorMember(e.Actor)); break;
                case LearningActivityAction.Decline: value.Decline(MentorMember(e.Actor)); break;
                case LearningActivityAction.Cancel: value.Cancel(MentorMember(e.Actor)); break;
                default: throw new ArgumentException("Unsupported mentorship transition.");
            }
        return value;
    }

    public LearningActivityView Project(LearningActivityRevision revision)
    {
        string? status = null; IReadOnlyList<LearningActivityGoal> goals = [];
        switch (Kind)
        {
            case LearningActivityKind.SkillExchange:
                var recipient = Members.Single(x => x != Creator);
                var exchange = Exchange.Propose(Id, ExchangeMember(Creator), ExchangeMember(recipient), OfferedSkillId, WantedSkillId);
                foreach (var e in Events)
                    switch (e.Action)
                    {
                        case LearningActivityAction.Accept: exchange.Accept(ExchangeMember(e.Actor)); break;
                        case LearningActivityAction.Decline: exchange.Decline(ExchangeMember(e.Actor)); break;
                        case LearningActivityAction.Cancel: exchange.Cancel(ExchangeMember(e.Actor)); break;
                        default: throw new ArgumentException("Unsupported exchange transition.");
                    }
                status = exchange.Status.ToString(); break;
            case LearningActivityKind.Pact:
                var pact = LearningPact.Propose(Id, PactMember(Creator), Members.Select(PactMember), Commitments.Select(x => x.Text));
                foreach (var e in Events)
                    switch (e.Action)
                    {
                        case LearningActivityAction.Accept: pact.AcceptInvitation(PactMember(e.Actor)); break;
                        case LearningActivityAction.Decline: pact.DeclineInvitation(PactMember(e.Actor)); break;
                        case LearningActivityAction.Cancel: pact.Cancel(PactMember(e.Actor)); break;
                        case LearningActivityAction.ConfirmCompletion: pact.ConfirmCompletion(PactMember(e.Actor)); break;
                        case LearningActivityAction.Withdraw: pact.Withdraw(PactMember(e.Actor)); break;
                        default: throw new ArgumentException("Unsupported Pact transition.");
                    }
                status = pact.Status.ToString(); break;
            case LearningActivityKind.GrowthSelfReport:
                _ = LearningGrowthEvidence.RecordSelfReport(Id, Creator.AccountId, Creator.ProfileId, SkillIds);
                if (Events.Count != 0) throw new NotSupportedException("The evidence mutation policy is not established.");
                status = LearningEvidenceKind.SelfReported.ToString(); break;
            case LearningActivityKind.MentorshipOffer:
                _ = LearningMentorshipOffer.Create(Id, MentorMember(Creator));
                if (Events.Count != 0) throw new NotSupportedException("The offer mutation policy is not established.");
                break;
            case LearningActivityKind.MentorshipRequest: status = RestoreMentorship().Status.ToString(); break;
            case LearningActivityKind.Circle:
                var circle = new LearningCircle(Id); var ids = new Dictionary<Guid, Guid>();
                foreach (var e in Events)
                {
                    if (e.GoalId is not { } goal || goal == Guid.Empty) throw new ArgumentException("A goal is required.");
                    switch (e.Action)
                    {
                        case LearningActivityAction.CreateGoal:
                            if (!ids.TryAdd(goal, circle.CreateSharedGoal(e.Text!).Id)) throw new ArgumentException("Duplicate goal identity."); break;
                        case LearningActivityAction.EditGoal: circle.EditSharedGoal(ids[goal], e.Text!); break;
                        case LearningActivityAction.UpdateGoalProgress:
                            circle.UpdateSharedGoalProgress(ids[goal], e.ProgressPercent ?? throw new ArgumentException("Progress is required.")); break;
                        default: throw new ArgumentException("Unsupported Circle command.");
                    }
                }
                goals = ids.Select(x => { var g = circle.Goals.Single(y => y.Id == x.Value); return new LearningActivityGoal(x.Key, g.Text, g.ProgressPercent); }).ToArray();
                break;
            default: throw new ArgumentException("Unsupported Learning activity.");
        }
        return new(Id, Kind, revision, status, Kind == LearningActivityKind.SkillExchange ? OfferedSkillId : null,
            Kind == LearningActivityKind.SkillExchange ? WantedSkillId : null, SkillIds.ToArray(), Commitments.ToArray(), goals);
    }

    private static SkillExchangeParticipant ExchangeMember(LearningActivityMember p) => new(p.AccountId, p.ProfileId);
    private static LearningPactParticipant PactMember(LearningActivityMember p) => new(p.AccountId, p.ProfileId);
    private static LearningMentorshipParticipant MentorMember(LearningActivityMember p) => new(p.AccountId, p.ProfileId);
    public override string ToString() => "Private Learning persistence state [content omitted]";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LearningActivityEvent(LearningActivityAction Action, LearningActivityMember Actor,
    Guid? GoalId, string? Text, int? ProgressPercent)
{
    public override string ToString() => "Private Learning command [content omitted]";
}
