using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Persistence.DbContexts;
using Andril.Social.Entities;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Social;

/// <summary>
/// Read adapter contract for querying private Social personality questionnaire responses.
/// Enforces profile-scoped data isolation and eager-loading of answer records.
/// </summary>
public interface ISocialQuestionnaireReadAdapter
{
    /// <summary>
    /// Retrieves a questionnaire response snapshot by profile scope and response identifier.
    /// </summary>
    Task<QuestionnaireResponseEntity?> GetByIdAsync(
        Guid profileId,
        Guid responseId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the most recent questionnaire response snapshot for an authorized Social profile scope.
    /// </summary>
    Task<QuestionnaireResponseEntity?> GetLatestByProfileIdAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a chronological list of questionnaire responses for an authorized Social profile scope.
    /// </summary>
    Task<IReadOnlyList<QuestionnaireResponseEntity>> ListByProfileIdAsync(
        Guid profileId,
        int maxItems = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies at the database boundary that the target profile belongs strictly to the 'social' module.
    /// </summary>
    Task<bool> VerifySocialProfileScopeAsync(
        Guid profileId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Owner-scoped PostgreSQL read adapter for Social questionnaire responses.
/// Queries 'andril_social.social_questionnaire_responses' with eager loading of answers,
/// enforcing profile-scoped isolation and cross-module identity protection.
/// </summary>
public sealed class PostgresSocialQuestionnaireReadAdapter : ISocialQuestionnaireReadAdapter
{
    private readonly SocialDbContext _context;
    private readonly bool _enforceIdentityDiscriminator;

    public PostgresSocialQuestionnaireReadAdapter(
        SocialDbContext context,
        bool enforceIdentityDiscriminator = false)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _enforceIdentityDiscriminator = enforceIdentityDiscriminator;
    }

    public bool EnforceIdentityDiscriminator => _enforceIdentityDiscriminator;

    public async Task<QuestionnaireResponseEntity?> GetByIdAsync(
        Guid profileId,
        Guid responseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (profileId == Guid.Empty || responseId == Guid.Empty)
        {
            return null;
        }

        if (_enforceIdentityDiscriminator)
        {
            var isSocial = await VerifySocialProfileScopeAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (!isSocial)
            {
                return null;
            }
        }

        return await _context.QuestionnaireResponses
            .AsNoTracking()
            .Include(r => r.Answers)
            .Where(r => r.ProfileId == profileId && r.Id == responseId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<QuestionnaireResponseEntity?> GetLatestByProfileIdAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (profileId == Guid.Empty)
        {
            return null;
        }

        if (_enforceIdentityDiscriminator)
        {
            var isSocial = await VerifySocialProfileScopeAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (!isSocial)
            {
                return null;
            }
        }

        return await _context.QuestionnaireResponses
            .AsNoTracking()
            .Include(r => r.Answers)
            .Where(r => r.ProfileId == profileId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QuestionnaireResponseEntity>> ListByProfileIdAsync(
        Guid profileId,
        int maxItems = 10,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (profileId == Guid.Empty)
        {
            return Array.Empty<QuestionnaireResponseEntity>();
        }

        if (maxItems <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), "Maximum items must be greater than zero.");
        }

        if (_enforceIdentityDiscriminator)
        {
            var isSocial = await VerifySocialProfileScopeAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (!isSocial)
            {
                return Array.Empty<QuestionnaireResponseEntity>();
            }
        }

        return await _context.QuestionnaireResponses
            .AsNoTracking()
            .Include(r => r.Answers)
            .Where(r => r.ProfileId == profileId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .Take(maxItems)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> VerifySocialProfileScopeAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (profileId == Guid.Empty)
        {
            return false;
        }

        try
        {
            var matches = await _context.Database.SqlQueryRaw<int>(
                "SELECT 1 FROM andril_identity.private_module_profiles WHERE id = {0} AND module = 'social' LIMIT 1",
                profileId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return matches.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
