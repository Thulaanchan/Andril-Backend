using System;

namespace Andril.Social.Personality;

public enum SocialEngagementPreference
{
    Either,
    Reserved,
    Outgoing
}

public enum CommunicationPreference
{
    Either,
    LessExpressive,
    Expressive
}

public enum PlanningPreference
{
    Either,
    Spontaneous,
    Structured
}

/// <summary>
/// Optional ranking preferences for partner interaction tendencies under SOC-02.
/// These preferences serve as ranking evidence rather than hard exclusionary filters.
/// </summary>
public sealed record PersonalityPartnerPreferences(
    SocialEngagementPreference Social = SocialEngagementPreference.Either,
    CommunicationPreference Communication = CommunicationPreference.Either,
    PlanningPreference Planning = PlanningPreference.Either
)
{
    public static readonly PersonalityPartnerPreferences Default = new();
}
