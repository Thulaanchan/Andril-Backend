using System.Collections.ObjectModel;
using System.Data.Common;
using System.Text.Encodings.Web;
using System.Text.Json;
using Andril.AI.ChatBox;
using Andril.AI.Services;
using Andril.AI.Tools;
using Andril.Groups.Interfaces;
using Andril.Groups.Policies;

namespace Andril.API.Integrations.AI;

public enum LearningClassroomAiSearchOutcome { Unavailable, Available, Denied, InvalidRequest }

/// <summary>The sole currently source-backed AI field. Contains no group/owner/profile references.</summary>
public sealed record LearningClassroomAiCard(string DisplayName)
{
    public override string ToString() => "Classroom AI card [untrusted display text omitted]";
}

public sealed record LearningClassroomAiSearchResult(LearningClassroomAiSearchOutcome Outcome,
    IReadOnlyList<LearningClassroomAiCard> Classrooms)
{
    // Explicit output contract: do not inherit a web host's relaxed JSON escaping settings.
    public string ToToolJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Default
    });

    public override string ToString() => $"Classroom AI search outcome: {Outcome}";
}

/// <summary>
/// Unregistered, read-only TC-01 adapter. Requires an actual authoritative M5 reader and existing
/// AI privacy/actor authority. No discovery evidence, classification or permission is inferred
/// from GroupEntry.Name, generic lookup, a public group or client input. No provider is invoked.
/// </summary>
public sealed class LearningClassroomAiSearchAdapter(AiPrivacyNoticeGuard privacy, ILearningClassroomDiscoveryReader reader)
{
    public async Task<LearningClassroomAiSearchResult> SearchAsync(AiModule module, string? arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (module != AiModule.Learning) return Closed(LearningClassroomAiSearchOutcome.Denied);
        if (!AiLearningReadToolQuery.TryParse(module, "search_classrooms", arguments, out var query))
            return Closed(LearningClassroomAiSearchOutcome.InvalidRequest);

        try
        {
            var actor = await privacy.RequireSatisfiedAsync(AiModule.Learning, cancellationToken);
            LearningClassroomAiSearchResult? projected = null;
            var callbackCount = 0;
            var result = await reader.WithAuthorizedSearchAsync(actor.UserId, actor.ModuleProfileId, query!.Query,
                async (snapshot, token) =>
                {
                    callbackCount++;
                    token.ThrowIfCancellationRequested();
                    if (snapshot.AccountId != actor.UserId || snapshot.LearningProfileId != actor.ModuleProfileId)
                        return projected = Closed(LearningClassroomAiSearchOutcome.Denied);
                    if (snapshot.Classrooms is null || snapshot.Classrooms.Any(x => x is null || x.Group is null))
                        return projected = Closed(LearningClassroomAiSearchOutcome.Unavailable);
                    // Conflicting/duplicate source records do not establish an authoritative projection.
                    if (snapshot.Classrooms.GroupBy(x => x.Group.Id).Any(x => x.Count() != 1))
                        return projected = Closed(LearningClassroomAiSearchOutcome.Unavailable);
                    var cards = new List<LearningClassroomAiCard>();
                    foreach (var evidence in snapshot.Classrooms)
                    {
                        var group = evidence.Group;
                        if (group.Scope.Module != "learning" || group.Scope.GroupKind != GroupKind.Classroom ||
                            evidence.Lifecycle != ClassroomDiscoveryLifecycle.Active ||
                            evidence.Discoverability != ClassroomDiscoveryDecision.Allowed ||
                            evidence.Visibility != ClassroomDiscoveryDecision.Allowed ||
                            evidence.MembershipRestrictions != ClassroomDiscoveryDecision.Allowed ||
                            evidence.BlockingRestrictions != ClassroomDiscoveryDecision.Allowed ||
                            (evidence.IsPrivate && evidence.PrivateDiscoveryAuthorization != ClassroomDiscoveryDecision.Allowed) ||
                            evidence.NameAiDisclosure != ClassroomDiscoveryDecision.Allowed)
                            continue;
                        cards.Add(new(group.Name));
                    }
                    await privacy.RequireSameActorSatisfiedAsync(actor, token);
                    return projected = new(LearningClassroomAiSearchOutcome.Available,
                        new ReadOnlyCollection<LearningClassroomAiCard>(cards));
                }, cancellationToken);
            // The source must really execute and release the reviewed projection, not return fabricated success.
            if (callbackCount != 1 || projected is null || !ReferenceEquals(result, projected))
                return Closed(LearningClassroomAiSearchOutcome.Unavailable);
            await privacy.RequireSameActorSatisfiedAsync(actor, cancellationToken);
            return result!;
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return Closed(LearningClassroomAiSearchOutcome.Denied); }
        catch (Exception e) when (e is IOException or TimeoutException or DbException or InvalidOperationException)
        { return Closed(LearningClassroomAiSearchOutcome.Unavailable); }
    }

    private static LearningClassroomAiSearchResult Closed(LearningClassroomAiSearchOutcome outcome)
        => new(outcome, Array.Empty<LearningClassroomAiCard>());
}
