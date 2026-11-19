
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Andril.Hobbies.Map.Contributions;

/// <summary>
/// Persistence contract for server-managed Andril Map
/// contribution capture sessions.
/// </summary>
/// <remarks>
/// All callers must resolve an authorized Hobbies profile
/// and a canonical attraction through trusted backend
/// services before invoking repository operations.
/// </remarks>
public interface IMapContributionCaptureSessionRepository
{
    /// <summary>
    /// Persists a new capture session.
    /// </summary>
    /// <remarks>
    /// Implementations must reject duplicate session IDs
    /// without overwriting an existing session.
    /// </remarks>
    Task AddAsync(
        MapContributionCaptureSession session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a capture session within its original
    /// Hobbies profile and attraction scope.
    /// </summary>
    /// <remarks>
    /// Returns null when the session does not exist
    /// or the supplied ownership or attraction scope
    /// does not match.
    ///
    /// The returned model must not allow implicit
    /// changes to persisted state.
    /// </remarks>
    Task<MapContributionCaptureSession?> FindOwnedAsync(
        Guid sessionId,
        Guid ownerHobbiesModuleProfileId,
        Guid attractionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to complete an existing capture session
    /// within its original ownership and attraction scope.
    /// </summary>
    /// <remarks>
    /// The implementation must atomically verify that:
    ///
    /// 1. The session exists.
    /// 2. The Hobbies profile matches.
    /// 3. The attraction matches.
    /// 4. The session is not already completed.
    /// 5. Completion does not precede its stored start time.
    ///
    /// Returns true only when completion is persisted.
    /// Returns false when these conditions are not met.
    ///
    /// Persistence failures must not be reported as success.
    ///
    /// This operation does not establish camera freshness,
    /// GPS authenticity or final contribution eligibility.
    /// </remarks>
    Task<bool> TryCompleteAsync(
        Guid sessionId,
        Guid ownerHobbiesModuleProfileId,
        Guid attractionId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);
}
