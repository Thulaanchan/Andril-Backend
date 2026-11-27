using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Social;

/// <summary>Reads only the standing and evidence visible to the verified Social caller.</summary>
public sealed class PostgresSocialMatchingEvidenceReader(PostgresSocialQuestionnaireLifecycleRepository scope)
{
    public Task<List<SocialMatchingEvidenceRecord>> ReadAsync(Guid caller, Guid? target, int maximum, CancellationToken token) =>
        scope.InAuthorizedTransactionAsync(caller, async (db, ct) =>
        {
            // Keep the evidence coherent through the enclosing Accounts authorization transaction and its result release.
            await db.Database.ExecuteSqlRawAsync("SELECT andril_social.social_safety_lock()", ct);
            return await db.Database.SqlQuery<SocialMatchingEvidenceRecord>($"""
                SELECT profile_id AS "ProfileId", profile_name AS "ProfileName", date_of_birth AS "DateOfBirth", gender AS "Gender",
                    demographics_revision AS "DemographicsRevision", preferences_revision AS "PreferencesRevision", discoverable AS "Discoverable",
                    show_me AS "ShowMe", min_age AS "MinAge", max_age AS "MaxAge", min_distance AS "MinDistance", max_distance AS "MaxDistance",
                    languages AS "Languages", goals AS "Goals", partner_social AS "PartnerSocial",
                    partner_communication AS "PartnerCommunication", partner_planning AS "PartnerPlanning",
                    response_id AS "ResponseId", questionnaire_version AS "QuestionnaireVersion", questionnaire_revision AS "QuestionnaireRevision",
                    answers_json AS "AnswersJson", latitude_cell AS "LatitudeCell", longitude_cell AS "LongitudeCell",
                    location_observed AS "LocationObserved", location_revision AS "LocationRevision", safety_revision AS "SafetyRevision",
                    lifecycle_revision AS "LifecycleRevision"
                FROM andril_social.read_social_matching_evidence({caller}, {target}, {maximum})
                """).ToListAsync(ct);
        }, token);
}

public sealed record SocialMatchingEvidenceRecord(
    [property: JsonIgnore] Guid ProfileId,
    [property: JsonIgnore] string? ProfileName,
    [property: JsonIgnore] DateOnly DateOfBirth,
    [property: JsonIgnore] string Gender,
    [property: JsonIgnore] long DemographicsRevision,
    [property: JsonIgnore] long PreferencesRevision,
    [property: JsonIgnore] bool Discoverable,
    [property: JsonIgnore] string? ShowMe,
    [property: JsonIgnore] int? MinAge,
    [property: JsonIgnore] int? MaxAge,
    [property: JsonIgnore] int? MinDistance,
    [property: JsonIgnore] int? MaxDistance,
    [property: JsonIgnore] string[]? Languages,
    [property: JsonIgnore] string[]? Goals,
    [property: JsonIgnore] string? PartnerSocial,
    [property: JsonIgnore] string? PartnerCommunication,
    [property: JsonIgnore] string? PartnerPlanning,
    [property: JsonIgnore] Guid? ResponseId,
    [property: JsonIgnore] string? QuestionnaireVersion,
    [property: JsonIgnore] long QuestionnaireRevision,
    [property: JsonIgnore] string? AnswersJson,
    [property: JsonIgnore] decimal? LatitudeCell,
    [property: JsonIgnore] decimal? LongitudeCell,
    [property: JsonIgnore] DateTimeOffset? LocationObserved,
    [property: JsonIgnore] long LocationRevision,
    [property: JsonIgnore] long SafetyRevision,
    [property: JsonIgnore] long LifecycleRevision)
{
    public override string ToString() => "SocialMatchingEvidenceRecord [private]";
}
