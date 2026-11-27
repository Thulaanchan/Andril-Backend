using System.Collections.ObjectModel;
using System.Data.Common;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.AI.ChatBox;
using Andril.AI.Services;
using Andril.AI.Tools;
using Andril.Learning.Discovery;

namespace Andril.API.Integrations.AI;

public enum LearningPartnerAiSearchOutcome { Unavailable, Available, Denied, InvalidRequest }
public sealed record LearningPartnerAiCard(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Interests,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Skills,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Goals,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProfessionalBiography)
{
    public override string ToString() => "Learning partner AI card [untrusted content omitted]";
}
public sealed record LearningPartnerAiSearchResult(LearningPartnerAiSearchOutcome Outcome,
    IReadOnlyList<LearningPartnerAiCard> Partners)
{
    public string ToToolJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Encoder = JavaScriptEncoder.Default });
    public override string ToString() => $"Learning partner AI outcome: {Outcome}";
}

/// <summary>Unregistered TC-02 read-only adapter. No provider calls or connection requests.</summary>
public sealed class LearningPartnerAiSearchAdapter(AiPrivacyNoticeGuard privacy, ILearningPartnerDiscoveryReader reader)
{
    public async Task<LearningPartnerAiSearchResult> SearchAsync(AiModule module, string? arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (module != AiModule.Learning) return Closed(LearningPartnerAiSearchOutcome.Denied);
        if (!AiLearningReadToolQuery.TryParse(module, "discover_learning_partners", arguments, out var query))
            return Closed(LearningPartnerAiSearchOutcome.InvalidRequest);
        try
        {
            var actor = await privacy.RequireSatisfiedAsync(module, cancellationToken);
            LearningPartnerAiSearchResult? projected = null; var calls = 0;
            var result = await reader.WithAuthorizedSearchAsync(actor.UserId, actor.ModuleProfileId, query!.Query,
                async (snapshot, ct) =>
                {
                    calls++; ct.ThrowIfCancellationRequested();
                    if (snapshot.AccountId != actor.UserId || snapshot.LearningProfileId != actor.ModuleProfileId)
                        return projected = Closed(LearningPartnerAiSearchOutcome.Denied);
                    if (snapshot.Candidates is null || snapshot.Candidates.Any(x => x is null) ||
                        snapshot.Candidates.GroupBy(x => x.CandidateLearningProfileId).Any(x => x.Count() != 1))
                        return projected = Closed(LearningPartnerAiSearchOutcome.Unavailable);
                    var cards = new List<LearningPartnerAiCard>();
                    foreach (var candidate in snapshot.Candidates)
                    {
                        if (candidate.CandidateLearningProfileId == Guid.Empty || candidate.CandidateLearningProfileId == actor.ModuleProfileId ||
                            candidate.Module != "learning" || candidate.Lifecycle != LearningDiscoveryLifecycle.Active ||
                            candidate.Eligibility != LearningDiscoveryDecision.Allowed || candidate.Visibility != LearningDiscoveryDecision.Allowed ||
                            candidate.Blocking != LearningDiscoveryDecision.Allowed) continue;
                        var card = new LearningPartnerAiCard(Field(candidate.DisplayName), Field(candidate.Interests), Field(candidate.Skills),
                            Field(candidate.Goals), Field(candidate.ProfessionalBiography));
                        if (card.DisplayName is not null || card.Interests is not null || card.Skills is not null ||
                            card.Goals is not null || card.ProfessionalBiography is not null) cards.Add(card);
                    }
                    await privacy.RequireSameActorSatisfiedAsync(actor, ct);
                    return projected = new(LearningPartnerAiSearchOutcome.Available, new ReadOnlyCollection<LearningPartnerAiCard>(cards));
                }, cancellationToken);
            if (calls != 1 || projected is null || !ReferenceEquals(result, projected)) return Closed(LearningPartnerAiSearchOutcome.Unavailable);
            await privacy.RequireSameActorSatisfiedAsync(actor, cancellationToken);
            return result!;
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return Closed(LearningPartnerAiSearchOutcome.Denied); }
        catch (Exception e) when (e is IOException or TimeoutException or DbException or InvalidOperationException)
        { return Closed(LearningPartnerAiSearchOutcome.Unavailable); }
    }
    private static string? Field(LearningPartnerField? field)
        => field?.AiDisclosure == LearningDiscoveryDecision.Allowed && !string.IsNullOrWhiteSpace(field.Text) ? field.Text : null;
    private static LearningPartnerAiSearchResult Closed(LearningPartnerAiSearchOutcome outcome)
        => new(outcome, Array.Empty<LearningPartnerAiCard>());
}
