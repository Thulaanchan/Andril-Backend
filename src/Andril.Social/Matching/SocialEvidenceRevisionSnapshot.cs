using System;
using System.Text.Json.Serialization;

namespace Andril.Social.Matching;

/// <summary>
/// Immutable snapshot representing the committed state and revision markers of a profile's Social matching evidence.
/// Only committed transactions produce valid revision snapshots (Task 152).
/// </summary>
public sealed class SocialEvidenceRevisionSnapshot : IEquatable<SocialEvidenceRevisionSnapshot>
{
    [JsonIgnore]
    public Guid ProfileId { get; }

    [JsonIgnore]
    public string PolicyVersion { get; }

    [JsonIgnore]
    public long QuestionnaireRevision { get; }

    [JsonIgnore]
    public bool IsQuestionnaireWithdrawn { get; }

    [JsonIgnore]
    public long ArchetypeConfirmationRevision { get; }

    [JsonIgnore]
    public long PartnerPreferencesRevision { get; }

    [JsonIgnore]
    public long DemographicsRevision { get; }

    [JsonIgnore]
    public long LocationDistanceRevision { get; }

    [JsonIgnore]
    public long LanguagesRevision { get; }

    [JsonIgnore]
    public long ConnectionGoalsRevision { get; }

    [JsonIgnore]
    public long SocialInterestsRevision { get; }

    [JsonIgnore]
    public long BlockingRevision { get; }

    [JsonIgnore]
    public long UnmatchingRevision { get; }

    [JsonIgnore]
    public bool IsProfileActive { get; }

    [JsonIgnore]
    public bool IsAccountRestricted { get; }

    [JsonIgnore]
    public DateTimeOffset CommittedAtUtc { get; }

    public SocialEvidenceRevisionSnapshot(
        Guid profileId,
        string policyVersion = SocialCompatibilityScoringEngine.PolicyVersion,
        long questionnaireRevision = 1,
        bool isQuestionnaireWithdrawn = false,
        long archetypeConfirmationRevision = 1,
        long partnerPreferencesRevision = 1,
        long demographicsRevision = 1,
        long locationDistanceRevision = 1,
        long languagesRevision = 1,
        long connectionGoalsRevision = 1,
        long socialInterestsRevision = 1,
        long blockingRevision = 1,
        long unmatchingRevision = 1,
        bool isProfileActive = true,
        bool isAccountRestricted = false,
        DateTimeOffset? committedAtUtc = null)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile ID cannot be empty.", nameof(profileId));
        }

        ProfileId = profileId;
        PolicyVersion = policyVersion ?? SocialCompatibilityScoringEngine.PolicyVersion;
        QuestionnaireRevision = questionnaireRevision;
        IsQuestionnaireWithdrawn = isQuestionnaireWithdrawn;
        ArchetypeConfirmationRevision = archetypeConfirmationRevision;
        PartnerPreferencesRevision = partnerPreferencesRevision;
        DemographicsRevision = demographicsRevision;
        LocationDistanceRevision = locationDistanceRevision;
        LanguagesRevision = languagesRevision;
        ConnectionGoalsRevision = connectionGoalsRevision;
        SocialInterestsRevision = socialInterestsRevision;
        BlockingRevision = blockingRevision;
        UnmatchingRevision = unmatchingRevision;
        IsProfileActive = isProfileActive;
        IsAccountRestricted = isAccountRestricted;
        CommittedAtUtc = committedAtUtc ?? DateTimeOffset.UtcNow;
    }

    public bool Equals(SocialEvidenceRevisionSnapshot? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return ProfileId == other.ProfileId &&
               string.Equals(PolicyVersion, other.PolicyVersion, StringComparison.Ordinal) &&
               QuestionnaireRevision == other.QuestionnaireRevision &&
               IsQuestionnaireWithdrawn == other.IsQuestionnaireWithdrawn &&
               ArchetypeConfirmationRevision == other.ArchetypeConfirmationRevision &&
               PartnerPreferencesRevision == other.PartnerPreferencesRevision &&
               DemographicsRevision == other.DemographicsRevision &&
               LocationDistanceRevision == other.LocationDistanceRevision &&
               LanguagesRevision == other.LanguagesRevision &&
               ConnectionGoalsRevision == other.ConnectionGoalsRevision &&
               SocialInterestsRevision == other.SocialInterestsRevision &&
               BlockingRevision == other.BlockingRevision &&
               UnmatchingRevision == other.UnmatchingRevision &&
               IsProfileActive == other.IsProfileActive &&
               IsAccountRestricted == other.IsAccountRestricted;
    }

    public override bool Equals(object? obj) => obj is SocialEvidenceRevisionSnapshot other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        ProfileId,
        PolicyVersion,
        QuestionnaireRevision,
        ArchetypeConfirmationRevision,
        PartnerPreferencesRevision,
        DemographicsRevision,
        LanguagesRevision,
        SocialInterestsRevision);

    public override string ToString() => "SocialEvidenceRevisionSnapshot [private]";
}
