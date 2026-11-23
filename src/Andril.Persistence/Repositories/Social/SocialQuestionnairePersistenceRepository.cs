using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Persistence.DbContexts;
using Andril.Social.Entities;
using Andril.Social.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Social;

/// <summary>
/// Concrete PostgreSQL repository for private Social personality questionnaire responses.
/// Implements <see cref="IQuestionnairePersistenceRepository"/>, enforcing strict profile-scoped data isolation,
/// atomic transactional saves, and cross-module profile protection within schema 'andril_social'.
/// </summary>
public sealed class SocialQuestionnairePersistenceRepository : IQuestionnairePersistenceRepository
{
    private readonly SocialDbContext _context;
    private readonly ISocialQuestionnaireReadAdapter _readAdapter;
    private readonly bool _enforceIdentityDiscriminator;

    public SocialQuestionnairePersistenceRepository(
        SocialDbContext context,
        ISocialQuestionnaireReadAdapter? readAdapter = null,
        bool enforceIdentityDiscriminator = false)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _readAdapter = readAdapter ?? new PostgresSocialQuestionnaireReadAdapter(context, enforceIdentityDiscriminator);
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

        return await _readAdapter.GetByIdAsync(profileId, responseId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        Guid profileId,
        QuestionnaireResponseEntity response,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile identifier cannot be empty.", nameof(profileId));
        }

        ArgumentNullException.ThrowIfNull(response);

        if (response.Id == Guid.Empty)
        {
            throw new ArgumentException("Response identifier cannot be empty.", nameof(response));
        }

        if (response.ProfileId != profileId)
        {
            throw new InvalidOperationException(
                "Cross-profile persistence mismatch detected: response.ProfileId does not match the authorized profile scope.");
        }

        if (_enforceIdentityDiscriminator)
        {
            var isSocial = await _readAdapter.VerifySocialProfileScopeAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (!isSocial)
            {
                throw new InvalidOperationException("Profile scope does not belong to the authorized 'social' module.");
            }
        }

        // Check if an existing entity with this response Id is currently tracked by this context
        var tracked = _context.ChangeTracker.Entries<QuestionnaireResponseEntity>()
            .SingleOrDefault(e => e.Entity.Id == response.Id);

        if (tracked is not null)
        {
            tracked.State = EntityState.Detached;
        }

        // Detach any tracked child answer entities to avoid change tracker conflicts
        foreach (var answer in response.Answers)
        {
            var trackedAnswer = _context.ChangeTracker.Entries<QuestionnaireAnswerEntity>()
                .SingleOrDefault(e => e.Entity.Id == answer.Id);
            if (trackedAnswer is not null)
            {
                trackedAnswer.State = EntityState.Detached;
            }
        }

        // Check if already in the database
        var existing = await _context.QuestionnaireResponses
            .Include(r => r.Answers)
            .SingleOrDefaultAsync(r => r.Id == response.Id, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            if (existing.ProfileId != profileId)
            {
                throw new InvalidOperationException(
                    "Security invariant violation: cannot overwrite a questionnaire response belonging to a different profile.");
            }

            // Remove existing child answers if being replaced
            _context.QuestionnaireAnswers.RemoveRange(existing.Answers);

            // Update parent entity state
            _context.Entry(existing).CurrentValues.SetValues(response);

            // Add new answers
            foreach (var answer in response.Answers)
            {
                _context.QuestionnaireAnswers.Add(answer);
            }
        }
        else
        {
            await _context.QuestionnaireResponses.AddAsync(response, cancellationToken).ConfigureAwait(false);
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
