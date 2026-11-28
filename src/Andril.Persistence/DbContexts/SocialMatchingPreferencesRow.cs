using Andril.Persistence.Repositories.Social;
using Andril.Social.Matching;
using Andril.Social.Personality;

namespace Andril.Persistence.DbContexts;

internal sealed class SocialMatchingPreferencesRow
{
    public Guid ProfileId { get; private set; }
    public string Module { get; private set; } = null!;
    public bool Discoverable { get; private set; }
    public string ShowMe { get; private set; } = null!;
    public int MinAge { get; private set; }
    public int MaxAge { get; private set; }
    public int MinDistanceKm { get; private set; }
    public int MaxDistanceKm { get; private set; }
    public string[] Languages { get; private set; } = null!;
    public string[] ConnectionGoals { get; private set; } = null!;
    public string? PartnerSocial { get; private set; }
    public string? PartnerCommunication { get; private set; }
    public string? PartnerPlanning { get; private set; }
    public long Revision { get; private set; }

    public SocialMatchingPreferences ToPreferences()
    {
        var filters = new SocialExploreRangeFilters(MinAge, MaxAge, MinDistanceKm, MaxDistanceKm);
        var partner = PartnerSocial is null ? null : new PersonalityPartnerPreferences(
            Enum.Parse<SocialEngagementPreference>(PartnerSocial), Enum.Parse<CommunicationPreference>(PartnerCommunication!),
            Enum.Parse<PlanningPreference>(PartnerPlanning!));
        return new(Discoverable, filters, ShowMe, Array.AsReadOnly(Languages), Array.AsReadOnly(ConnectionGoals), partner);
    }
}
