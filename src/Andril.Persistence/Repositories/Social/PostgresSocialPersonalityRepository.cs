using System.Text.Json;
using Andril.Common.Accounts.Profiles;
using Andril.Social.Personality;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Andril.Persistence.Repositories.Social;

public sealed class SocialPersonalityConflictException() : InvalidOperationException("Current Social personality evidence has changed.");

public sealed record SocialPersonalityView(Guid? QuestionnaireResponseId, string? QuestionnaireVersion,
    string[] UnresolvedAxes, bool HasConfirmation, PersonalityPartnerPreferences? PartnerPreferences);

public sealed class PostgresSocialPersonalityRepository(IPrivateProfileService profiles,
    PostgresSocialMatchingEvidenceReader reader, PostgresSocialQuestionnaireLifecycleRepository scope)
{
    public Task<SocialPersonalityView> ReadAsync(CancellationToken token = default) =>
        ForProfile(async (caller, ct) =>
        {
            var row = await ReadEvidenceAsync(caller, ct);
            var partner = row.PartnerSocial is null ? null : new PersonalityPartnerPreferences(
                Enum.Parse<SocialEngagementPreference>(row.PartnerSocial), Enum.Parse<CommunicationPreference>(row.PartnerCommunication!),
                Enum.Parse<PlanningPreference>(row.PartnerPlanning!));
            if (row.AnswersJson is null)
                return new SocialPersonalityView(null, null, [], false, partner);

            var classification = Classify(row, row.Confirmation);
            return new SocialPersonalityView(row.ResponseId, row.QuestionnaireVersion, classification.UnresolvedAxes.ToArray(),
                row.Confirmation is not null, partner);
        }, token);

    public Task SetPartnerAsync(PersonalityPartnerPreferences? preferences, CancellationToken token = default)
    {
        if (preferences is not null && (!Enum.IsDefined(preferences.Social) || !Enum.IsDefined(preferences.Communication) ||
            !Enum.IsDefined(preferences.Planning))) throw new ArgumentException("Invalid Social partner preference.");
        return ForProfile(async (caller, ct) =>
        {
            var applied = await scope.InAuthorizedTransactionAsync(caller, (db, dbToken) => db.Database.SqlQuery<bool>($"""
                SELECT andril_social.set_social_personality_preferences({caller}, {preferences?.Social.ToString()},
                    {preferences?.Communication.ToString()}, {preferences?.Planning.ToString()}) AS "Value"
                """).SingleAsync(dbToken), ct);
            if (!applied) throw new SocialPersonalityConflictException();
            return applied;
        }, token);
    }

    public Task ConfirmAsync(Guid questionnaireResponseId, SocialEngagementTendency? social,
        CommunicationTendency? communication, PlanningTendency? planning, CancellationToken token = default)
    {
        if (questionnaireResponseId == Guid.Empty || (social, communication, planning) == (null, null, null))
            throw new ArgumentException("Explicit Social archetype choices are required.");

        return ForProfile(async (caller, ct) =>
        {
            var row = await ReadEvidenceAsync(caller, ct);
            if (row.AnswersJson is null || row.ResponseId != questionnaireResponseId) throw new SocialPersonalityConflictException();
            var confirmation = new PersonalityArchetypeConfirmation(questionnaireResponseId.ToString("D"), social, communication, planning);
            var classification = Classify(row, confirmation);
            if (!classification.IsSuccess && !classification.HasUnresolvedAxes)
                throw new ArgumentException("Invalid Social archetype confirmation.");

            var committed = await scope.InAuthorizedTransactionAsync(caller, (db, dbToken) => db.Database.SqlQuery<bool>($"""
                SELECT andril_social.set_social_archetype_confirmation({caller}, {questionnaireResponseId}, {(int?)social},
                    {(int?)communication}, {(int?)planning}) AS "Value"
                """).SingleAsync(dbToken), ct);
            if (!committed) throw new SocialPersonalityConflictException();
            return committed;
        }, token);
    }

    private async Task<SocialMatchingEvidenceRecord> ReadEvidenceAsync(Guid caller, CancellationToken ct) =>
        (await reader.ReadAsync(caller, caller, 1, ct)).SingleOrDefault() ?? throw new ProfileAccessDeniedException();

    private static PersonalityArchetypeClassificationResult Classify(SocialMatchingEvidenceRecord row,
        PersonalityArchetypeConfirmation? confirmation) => PersonalityArchetypeClassifier.Classify(
            JsonSerializer.Deserialize<QuestionnaireAnswer[]>(row.AnswersJson!), row.QuestionnaireVersion, row.ResponseId?.ToString("D"), confirmation);

    private Task<T> ForProfile<T>(Func<Guid, CancellationToken, Task<T>> action, CancellationToken token) =>
        profiles.WithAuthorizedProfileAsync("social", async profile =>
        {
            try { return await action(profile.ProfileId, token); }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                throw new ProfileAccessDeniedException();
            }
        }, token);
}
