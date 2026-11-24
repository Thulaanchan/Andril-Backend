using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Andril.Social.Entities;
using Andril.Social.Personality;

namespace Andril.Social.Matching;

/// <summary>
/// Immutable, privacy-protected input evidence bundle for a single user evaluated by the Social compatibility engine (P-04).
/// </summary>
public sealed class SocialScoringProfileEvidence : IEquatable<SocialScoringProfileEvidence>
{
    [JsonIgnore]
    public Guid ProfileId { get; }

    [JsonIgnore]
    public int Age { get; }

    [JsonIgnore]
    public string? DemographicGender { get; }

    [JsonIgnore]
    public SocialExplorePreferences Preferences { get; }

    [JsonIgnore]
    public SocialIdealAgeBand? IdealAgeBand { get; }

    [JsonIgnore]
    public IReadOnlySet<string>? ConfirmedLanguages { get; }

    [JsonIgnore]
    public IReadOnlySet<string>? ConfirmedConnectionGoals { get; }

    [JsonIgnore]
    public IReadOnlySet<Guid>? ConfirmedSocialInterestIds { get; }

    [JsonIgnore]
    public IReadOnlyList<QuestionnaireAnswer>? QuestionnaireAnswers { get; }

    [JsonIgnore]
    public string? QuestionnaireVersion { get; }

    [JsonIgnore]
    public string? QuestionnaireRevision { get; }

    [JsonIgnore]
    public QuestionnaireResponseEntity? QuestionnaireResponse { get; }

    [JsonIgnore]
    public PersonalityPartnerPreferences? PartnerPreferences { get; }

    [JsonIgnore]
    public PersonalityArchetypeConfirmation? ArchetypeConfirmation { get; }

    [JsonIgnore]
    public bool IsOptedIn { get; }

    [JsonIgnore]
    public bool IsActive { get; }

    [JsonIgnore]
    public bool IsDiscoverable { get; }

    [JsonIgnore]
    public bool HasAuthoritativeEvidence { get; }

    [JsonIgnore]
    public IReadOnlySet<Guid> BlockedProfileIds { get; }

    public SocialScoringProfileEvidence(
        Guid profileId,
        int age,
        string? demographicGender,
        SocialExplorePreferences preferences,
        SocialIdealAgeBand? idealAgeBand = null,
        IEnumerable<string>? confirmedLanguages = null,
        IEnumerable<string>? confirmedConnectionGoals = null,
        IEnumerable<Guid>? confirmedSocialInterestIds = null,
        QuestionnaireResponseEntity? questionnaireResponse = null,
        IEnumerable<QuestionnaireAnswer>? questionnaireAnswers = null,
        string? questionnaireVersion = null,
        string? questionnaireRevision = null,
        PersonalityPartnerPreferences? partnerPreferences = null,
        PersonalityArchetypeConfirmation? archetypeConfirmation = null,
        bool isOptedIn = true,
        bool isActive = true,
        bool isDiscoverable = true,
        bool hasAuthoritativeEvidence = true,
        IEnumerable<Guid>? blockedProfileIds = null)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile identity cannot be empty.", nameof(profileId));
        }

        ProfileId = profileId;
        Age = age;
        DemographicGender = demographicGender;
        Preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        IdealAgeBand = idealAgeBand;

        ConfirmedLanguages = confirmedLanguages is not null
            ? new HashSet<string>(confirmedLanguages, StringComparer.OrdinalIgnoreCase)
            : null;

        ConfirmedConnectionGoals = confirmedConnectionGoals is not null
            ? new HashSet<string>(confirmedConnectionGoals, StringComparer.OrdinalIgnoreCase)
            : null;

        ConfirmedSocialInterestIds = confirmedSocialInterestIds is not null
            ? new HashSet<Guid>(confirmedSocialInterestIds)
            : null;

        QuestionnaireResponse = questionnaireResponse;
        QuestionnaireAnswers = questionnaireAnswers is not null ? new List<QuestionnaireAnswer>(questionnaireAnswers) : null;
        QuestionnaireVersion = questionnaireVersion;
        QuestionnaireRevision = questionnaireRevision;
        PartnerPreferences = partnerPreferences;
        ArchetypeConfirmation = archetypeConfirmation;
        IsOptedIn = isOptedIn;
        IsActive = isActive;
        IsDiscoverable = isDiscoverable;
        HasAuthoritativeEvidence = hasAuthoritativeEvidence;
        BlockedProfileIds = blockedProfileIds is not null
            ? new HashSet<Guid>(blockedProfileIds)
            : new HashSet<Guid>();
    }

    public PersonalityEvaluationProfile? ToPersonalityEvaluationProfile()
    {
        if (QuestionnaireAnswers is not null)
        {
            return new PersonalityEvaluationProfile(
                QuestionnaireAnswers,
                QuestionnaireVersion ?? QuestionnaireV1Definition.Version,
                QuestionnaireRevision,
                ArchetypeConfirmation,
                PartnerPreferences);
        }

        if (QuestionnaireResponse is not null && QuestionnaireResponse.Answers is not null)
        {
            var answers = new List<QuestionnaireAnswer>();
            foreach (var ans in QuestionnaireResponse.Answers)
            {
                var optionVal = ans.OptionId;
                if (!int.TryParse(optionVal, out _) && optionVal.Contains("_opt", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = optionVal.LastIndexOf("_opt", StringComparison.OrdinalIgnoreCase);
                    optionVal = optionVal.Substring(idx + 4);
                }

                answers.Add(new QuestionnaireAnswer(ans.QuestionId, optionVal));
            }

            return new PersonalityEvaluationProfile(
                answers,
                QuestionnaireResponse.Version ?? QuestionnaireV1Definition.Version,
                QuestionnaireRevision,
                ArchetypeConfirmation,
                PartnerPreferences);
        }

        return null;
    }

    public bool Equals(SocialScoringProfileEvidence? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return ProfileId == other.ProfileId &&
               Age == other.Age &&
               string.Equals(DemographicGender, other.DemographicGender, StringComparison.OrdinalIgnoreCase) &&
               Preferences.Equals(other.Preferences) &&
               Equals(IdealAgeBand, other.IdealAgeBand) &&
               IsOptedIn == other.IsOptedIn &&
               IsActive == other.IsActive &&
               IsDiscoverable == other.IsDiscoverable &&
               HasAuthoritativeEvidence == other.HasAuthoritativeEvidence;
    }

    public override bool Equals(object? obj) => obj is SocialScoringProfileEvidence other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(ProfileId, Age, DemographicGender, Preferences, IsOptedIn, IsActive, IsDiscoverable);

    public override string ToString() => "SocialScoringProfileEvidence [private]";
}
