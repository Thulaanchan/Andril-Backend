using System.Text.Json.Serialization;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Profiles;
using Andril.Persistence.DbContexts;
using Andril.Shared.Authentication;
using Andril.Social.Entities;
using Andril.Social.Matching;
using Andril.Social.Personality;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Social;

public sealed record SocialMatchingPreferences(
    bool Discoverable,
    SocialExploreRangeFilters Ranges,
    string ShowMe,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> ConnectionGoals,
    PersonalityPartnerPreferences? PartnerPreferences = null);

/// <summary>Owner-scoped Social profile evidence for internal matching composition.</summary>
public sealed record SocialCommittedProfileEvidence(
    [property: JsonIgnore] Guid ProfileId,
    [property: JsonIgnore] int Age,
    [property: JsonIgnore] string Gender,
    [property: JsonIgnore] bool IsActive,
    [property: JsonIgnore] bool IsOptedIn,
    [property: JsonIgnore] string Visibility,
    [property: JsonIgnore] long LifecycleRevision,
    [property: JsonIgnore] long PreferencesRevision,
    [property: JsonIgnore] SocialMatchingPreferences? Preferences,
    [property: JsonIgnore] QuestionnaireResponseEntity? Questionnaire)
{
    public override string ToString() => "SocialCommittedProfileEvidence [private]";
}

public sealed class PostgresSocialProfileEvidenceRepository(
    AccountsDbContext accounts,
    IPrivateProfileService profiles,
    ICurrentUser currentUser,
    PostgresSocialQuestionnaireLifecycleRepository questionnaires,
    TimeProvider clock)
{
    public Task<SocialCommittedProfileEvidence> ReadSelfAsync(CancellationToken cancellationToken = default) =>
        profiles.WithAuthorizedProfileAsync("social", async profile =>
        {
            var facts = await ReadVerifiedIdentityAsync(profile, cancellationToken);
            if (facts.State != "active" || !facts.OptedIn)
                return Project(profile.ProfileId, facts, null, null);

            var preferences = await questionnaires.InAuthorizedTransactionAsync(profile.ProfileId,
                (db, token) => db.Set<SocialMatchingPreferencesRow>().AsNoTracking()
                    .SingleOrDefaultAsync(row => row.ProfileId == profile.ProfileId, token), cancellationToken);
            var response = await questionnaires.GetActiveResponseAsync(profile.ProfileId, cancellationToken);
            return Project(profile.ProfileId, facts, preferences, response);
        }, cancellationToken);

    public Task SetSelfPreferencesAsync(SocialMatchingPreferences preferences, CancellationToken cancellationToken = default,
        bool preservePartnerPreferences = false)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(preferences.Ranges);
        ArgumentNullException.ThrowIfNull(preferences.Languages);
        ArgumentNullException.ThrowIfNull(preferences.ConnectionGoals);
        var showMe = SocialGenderPreferences.Normalize(preferences.ShowMe);
        var languages = preferences.Languages.Select(SocialLanguages.Normalize).Distinct(StringComparer.Ordinal).Order().ToArray();
        var goals = preferences.ConnectionGoals.Select(SocialConnectionGoals.Normalize).Distinct(StringComparer.Ordinal).Order().ToArray();
        var partner = preferences.PartnerPreferences;
        if (partner is not null && (!Enum.IsDefined(partner.Social) || !Enum.IsDefined(partner.Communication) || !Enum.IsDefined(partner.Planning)))
            throw new ArgumentException("Invalid Social partner preferences.", nameof(preferences));

        return profiles.WithAuthorizedProfileAsync("social", async profile =>
        {
            var facts = await ReadVerifiedIdentityAsync(profile, cancellationToken);
            if (facts.State != "active" || !facts.OptedIn) throw new ProfileAccessDeniedException();
            var ranges = preferences.Ranges;
            return await questionnaires.InAuthorizedTransactionAsync(profile.ProfileId, async (db, token) =>
            {
                await db.Database.ExecuteSqlRawAsync("SELECT andril_social.social_safety_lock()", token);
                return await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO andril_social.social_matching_preferences AS saved
                        (profile_id, module, discoverable, show_me, min_age, max_age, min_distance_km, max_distance_km,
                         languages, connection_goals, partner_social, partner_communication, partner_planning, revision)
                    VALUES ({profile.ProfileId}, 'social', {preferences.Discoverable}, {showMe}, {ranges.MinAge}, {ranges.MaxAge},
                        {ranges.MinDistanceKm}, {ranges.MaxDistanceKm}, {languages}, {goals},
                        {partner?.Social.ToString()}, {partner?.Communication.ToString()}, {partner?.Planning.ToString()}, 1)
                    ON CONFLICT (profile_id) DO UPDATE SET discoverable = EXCLUDED.discoverable, show_me = EXCLUDED.show_me,
                        min_age = EXCLUDED.min_age, max_age = EXCLUDED.max_age, min_distance_km = EXCLUDED.min_distance_km,
                        max_distance_km = EXCLUDED.max_distance_km, languages = EXCLUDED.languages, connection_goals = EXCLUDED.connection_goals,
                        partner_social = CASE WHEN {preservePartnerPreferences} THEN saved.partner_social ELSE EXCLUDED.partner_social END,
                        partner_communication = CASE WHEN {preservePartnerPreferences} THEN saved.partner_communication ELSE EXCLUDED.partner_communication END,
                        partner_planning = CASE WHEN {preservePartnerPreferences} THEN saved.partner_planning ELSE EXCLUDED.partner_planning END,
                        revision = saved.revision + 1
                    WHERE (saved.discoverable, saved.show_me, saved.min_age, saved.max_age, saved.min_distance_km, saved.max_distance_km,
                        saved.languages, saved.connection_goals, saved.partner_social, saved.partner_communication, saved.partner_planning)
                        IS DISTINCT FROM (EXCLUDED.discoverable, EXCLUDED.show_me, EXCLUDED.min_age, EXCLUDED.max_age, EXCLUDED.min_distance_km,
                            EXCLUDED.max_distance_km, EXCLUDED.languages, EXCLUDED.connection_goals,
                            CASE WHEN {preservePartnerPreferences} THEN saved.partner_social ELSE EXCLUDED.partner_social END,
                            CASE WHEN {preservePartnerPreferences} THEN saved.partner_communication ELSE EXCLUDED.partner_communication END,
                            CASE WHEN {preservePartnerPreferences} THEN saved.partner_planning ELSE EXCLUDED.partner_planning END)
                    """, token);
            }, cancellationToken);
        }, cancellationToken);
    }

    private async Task<IdentityEvidence> ReadVerifiedIdentityAsync(PrivateProfileDto profile, CancellationToken token)
    {
        if (profile.ProfileId == Guid.Empty || profile.Module != "social" || !currentUser.IsAuthenticated || currentUser.UserId is not Guid subject
            || subject == Guid.Empty || accounts.Database.CurrentTransaction is null)
            throw new ProfileAccessDeniedException();

        var facts = await accounts.Database.SqlQuery<IdentityEvidence>($"""
            SELECT demographics.date_of_birth AS "DateOfBirth", demographics.gender AS "Gender", profile.visibility AS "Visibility",
                lifecycle.state AS "State", lifecycle.revision AS "Revision", (membership.social_opt_in_at_utc IS NOT NULL) AS "OptedIn"
            FROM andril_identity.private_module_profiles profile
            JOIN andril_identity.accounts account ON account.id = profile.account_id
            JOIN andril_identity.account_demographics demographics ON demographics.account_id = account.id
            JOIN andril_identity.account_starter_memberships membership ON membership.account_id = account.id AND membership.module = profile.module
            JOIN andril_identity.account_module_lifecycles lifecycle ON lifecycle.account_id = account.id AND lifecycle.module = profile.module
            WHERE profile.id = {profile.ProfileId} AND profile.module = 'social' AND account.supabase_user_id = {subject}
            """).SingleOrDefaultAsync(token);
        if (facts is null || facts.Revision <= 0 || facts.State is not ("active" or "inactive")) throw new ProfileAccessDeniedException();
        var age = AgeEligibilityPolicy.CalculateAge(facts.DateOfBirth, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
        if (age < SocialExploreRangeFilters.AbsoluteMinAge) throw new ProfileAccessDeniedException();
        return facts with { Age = age };
    }

    private static SocialCommittedProfileEvidence Project(Guid profile, IdentityEvidence facts, SocialMatchingPreferencesRow? stored,
        QuestionnaireResponseEntity? questionnaire) => new(profile, facts.Age, facts.Gender, facts.State == "active", facts.OptedIn,
            facts.Visibility, facts.Revision, stored?.Revision ?? 0, stored?.ToPreferences(), questionnaire);

    private sealed record IdentityEvidence(DateOnly DateOfBirth, string Gender, string Visibility, string State, long Revision, bool OptedIn)
    {
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public int Age { get; init; }
    }
}
