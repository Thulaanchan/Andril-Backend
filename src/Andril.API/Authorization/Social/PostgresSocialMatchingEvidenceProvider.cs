using System.Text.Json;
using Andril.Common.Accounts.Eligibility;
using Andril.Persistence.Repositories.Social;
using Andril.Social.Matching;
using Andril.Social.Personality;

namespace Andril.API.Authorization.Social;

public sealed class PostgresSocialMatchingEvidenceProvider(PostgresSocialMatchingEvidenceReader reader,
    SocialLocationSettings locationSettings, TimeProvider clock) : ISocialDiscoveryEvidenceProvider, ISocialRelationshipEvidenceProvider
{
    public async Task<SocialDiscoveryCallerBundle?> GetCallerBundleAsync(Guid callerProfileId, CancellationToken cancellationToken = default)
    {
        var caller = await ReadOneAsync(callerProfileId, callerProfileId, cancellationToken);
        if (caller is null || caller.ShowMe is null) return null;
        var revision = CreateRevision(caller);
        return new SocialDiscoveryCallerBundle(Scoring(caller), revision, revision);
    }

    public async Task<IReadOnlyList<SocialAuthoritativeCandidateItem>> GetCandidateBatchAsync(Guid callerProfileId,
        int maxCandidateBatchSize, CancellationToken cancellationToken = default)
    {
        if (maxCandidateBatchSize is < 1 or > SocialRankedDiscoveryService.DefaultMaxCandidateBatchSize)
            throw new ArgumentOutOfRangeException(nameof(maxCandidateBatchSize));

        var caller = await ReadOneAsync(callerProfileId, callerProfileId, cancellationToken);
        if (caller is null || caller.ShowMe is null) return [];
        var candidates = await reader.ReadAsync(callerProfileId, null, maxCandidateBatchSize, cancellationToken);
        var now = clock.GetUtcNow();
        return candidates.Select(candidate =>
        {
            var revision = CreateRevision(candidate);
            var distance = SocialApproximateDistance.Kilometres(caller.LatitudeCell, caller.LongitudeCell, caller.LocationObserved,
                candidate.LatitudeCell, candidate.LongitudeCell, candidate.LocationObserved, now, locationSettings.MaximumAge);
            return new SocialAuthoritativeCandidateItem(Scoring(candidate), revision, revision, candidate.ProfileName!, distance);
        }).ToArray();
    }

    public async Task<SocialDiscoveryCallerEvidence?> GetCallerEvidenceAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var record = await ReadOneAsync(profileId, profileId, cancellationToken);
        return record is null ? null : new(profileId, Age(record), isOptedIn: true, isActive: true);
    }

    public async Task<SocialDiscoveryCandidateEvidence?> GetCandidateEvidenceAsync(Guid candidateProfileId, Guid callerProfileId,
        CancellationToken cancellationToken = default)
    {
        if (candidateProfileId == callerProfileId) return null;
        var record = await ReadOneAsync(callerProfileId, candidateProfileId, cancellationToken);
        return record is null ? null : new(record.ProfileId, Age(record), isOptedIn: true, isActive: true, record.Discoverable,
            isBlockedByCaller: false, isCallerBlockedByCandidate: false);
    }

    private async Task<SocialMatchingEvidenceRecord?> ReadOneAsync(Guid caller, Guid exactProfile, CancellationToken token)
    {
        if (caller == Guid.Empty || exactProfile == Guid.Empty) return null;
        var row = (await reader.ReadAsync(caller, exactProfile, 1, token)).SingleOrDefault();
        return row?.ProfileId == exactProfile ? row : null;
    }

    private int Age(SocialMatchingEvidenceRecord record) => AgeEligibilityPolicy.CalculateAge(record.DateOfBirth,
        DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

    private SocialScoringProfileEvidence Scoring(SocialMatchingEvidenceRecord row)
    {
        var filters = new SocialExploreRangeFilters(row.MinAge!.Value, row.MaxAge!.Value, row.MinDistance!.Value, row.MaxDistance!.Value);
        var preferences = new SocialExplorePreferences(filters, row.ShowMe!, row.Languages!, row.Goals!);
        var partner = row.PartnerSocial is null ? null : new PersonalityPartnerPreferences(
            Enum.Parse<SocialEngagementPreference>(row.PartnerSocial), Enum.Parse<CommunicationPreference>(row.PartnerCommunication!),
            Enum.Parse<PlanningPreference>(row.PartnerPlanning!));
        var answers = row.AnswersJson is null ? null : JsonSerializer.Deserialize<QuestionnaireAnswer[]>(row.AnswersJson);
        return SocialScoringProfileEvidence.CreateAuthoritativeDiscovery(row.ProfileId, Age(row), row.Gender, preferences,
            isOptedIn: true, isActive: true, row.Discoverable, confirmedLanguages: row.Languages, confirmedConnectionGoals: row.Goals,
            questionnaireAnswers: answers, questionnaireVersion: row.QuestionnaireVersion,
            questionnaireRevision: row.ResponseId?.ToString("D"), partnerPreferences: partner);
    }

    private SocialEvidenceRevisionSnapshot CreateRevision(SocialMatchingEvidenceRecord row) => new(row.ProfileId,
        questionnaireRevision: row.QuestionnaireRevision, archetypeConfirmationRevision: 0,
        partnerPreferencesRevision: row.PreferencesRevision, demographicsRevision: row.DemographicsRevision,
        locationDistanceRevision: row.LocationRevision, languagesRevision: row.PreferencesRevision,
        connectionGoalsRevision: row.PreferencesRevision, socialInterestsRevision: 0, blockingRevision: row.SafetyRevision,
        unmatchingRevision: row.LifecycleRevision, committedAtUtc: clock.GetUtcNow());
}
