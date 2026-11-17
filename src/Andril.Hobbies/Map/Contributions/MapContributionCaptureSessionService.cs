
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Hobbies.Map.Contributions;

/// <summary>
/// Resolves the authorized Hobbies profile of the
/// current authenticated contributor.
/// </summary>
public interface IMapCaptureSessionAccessReader
{
    /// <summary>
    /// Returns the authorized active Hobbies profile
    /// when the canonical attraction is eligible.
    /// Returns null when authorization fails.
    /// </summary>
    Task<Guid?> ResolveAuthorizedProfileIdAsync(
        Guid attractionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates server-managed Map contribution
/// capture session creation and completion.
/// </summary>
public sealed class MapContributionCaptureSessionService
{
    private readonly IMapContributionCaptureSessionRepository
        _repository;

    private readonly IMapCaptureSessionAccessReader
        _accessReader;

    private readonly TimeProvider _timeProvider;

    public MapContributionCaptureSessionService(
        IMapContributionCaptureSessionRepository repository,
        IMapCaptureSessionAccessReader accessReader,
        TimeProvider timeProvider)
    {
        _repository = repository
            ?? throw new ArgumentNullException(
                nameof(repository));

        _accessReader = accessReader
            ?? throw new ArgumentNullException(
                nameof(accessReader));

        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(
                nameof(timeProvider));
    }

    public async Task<MapContributionCaptureSession?> StartAsync(
        Guid attractionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (attractionId == Guid.Empty)
        {
            return null;
        }

        var profileId =
            await _accessReader.ResolveAuthorizedProfileIdAsync(
                attractionId,
                cancellationToken);

        if (!profileId.HasValue
            || profileId.Value == Guid.Empty)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var session = MapContributionCaptureSession.Create(
            Guid.NewGuid(),
            profileId.Value,
            attractionId,
            _timeProvider.GetUtcNow());

        await _repository.AddAsync(
            session,
            cancellationToken);

        return session;
    }

    public async Task<bool> CompleteAsync(
        Guid sessionId,
        Guid attractionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (sessionId == Guid.Empty
            || attractionId == Guid.Empty)
        {
            return false;
        }

        var profileId =
            await _accessReader.ResolveAuthorizedProfileIdAsync(
                attractionId,
                cancellationToken);

        if (!profileId.HasValue
            || profileId.Value == Guid.Empty)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var completedAt = _timeProvider.GetUtcNow();

        return await _repository.TryCompleteAsync(
            sessionId,
            profileId.Value,
            attractionId,
            completedAt,
            cancellationToken);
    }
}
