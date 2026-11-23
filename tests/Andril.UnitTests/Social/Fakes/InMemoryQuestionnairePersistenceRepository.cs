using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Andril.Social.Entities;
using Andril.Social.Interfaces;

namespace Andril.UnitTests.Social.Fakes;

/// <summary>
/// Thread-safe in-memory test double implementing <see cref="IQuestionnairePersistenceRepository"/>.
/// Exists strictly for test harness and integration testing of Social questionnaire workflows.
/// Not suitable for production use or durable persistence.
/// </summary>
public sealed class InMemoryQuestionnairePersistenceRepository : IQuestionnairePersistenceRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<(Guid ProfileId, Guid ResponseId), QuestionnaireResponseEntity> _storage = new();
    private readonly HashSet<string>? _approvedVersions;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryQuestionnairePersistenceRepository"/> class.
    /// </summary>
    /// <param name="approvedVersions">Optional set of approved questionnaire versions. If provided, unapproved versions will be rejected.</param>
    public InMemoryQuestionnairePersistenceRepository(IEnumerable<string>? approvedVersions = null)
    {
        if (approvedVersions is not null)
        {
            _approvedVersions = new HashSet<string>(approvedVersions, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Gets the count of currently stored questionnaire responses across all profiles.
    /// </summary>
    public int StoredCount
    {
        get
        {
            lock (_lock)
            {
                return _storage.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task<QuestionnaireResponseEntity?> GetByIdAsync(
        Guid profileId,
        Guid responseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (profileId == Guid.Empty || responseId == Guid.Empty)
        {
            return Task.FromResult<QuestionnaireResponseEntity?>(null);
        }

        lock (_lock)
        {
            _storage.TryGetValue((profileId, responseId), out var response);
            return Task.FromResult(response);
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(
        Guid profileId,
        QuestionnaireResponseEntity response,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(response);

        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Profile identifier cannot be empty.", nameof(profileId));
        }

        if (response.ProfileId != profileId)
        {
            throw new ArgumentException(
                "Response profile identity does not match authorized profile scope.",
                nameof(response));
        }

        if (_approvedVersions is not null && !_approvedVersions.Contains(response.Version))
        {
            throw new ArgumentException(
                $"Questionnaire version '{response.Version}' is not an approved version.",
                nameof(response));
        }

        lock (_lock)
        {
            _storage[(profileId, response.Id)] = response;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears all stored questionnaire responses.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _storage.Clear();
        }
    }

    /// <summary>
    /// Determines whether a response exists for the specified profile scope and response identity.
    /// </summary>
    public bool Contains(Guid profileId, Guid responseId)
    {
        lock (_lock)
        {
            return _storage.ContainsKey((profileId, responseId));
        }
    }
}
