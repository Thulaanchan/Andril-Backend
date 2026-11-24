using System.Data.Common;
namespace Andril.Hobbies.Map.Places;

internal enum MapAttractionManagementAuthorizationOutcome { Unavailable = 0, Satisfied, Denied }
internal sealed record MapAttractionManagementGrant(Guid ReviewerSubjectId, Guid AttractionId, bool RequestedIsActive);
internal sealed record MapAttractionManagementAuthorizationResult(MapAttractionManagementAuthorizationOutcome Outcome,
    MapAttractionManagementGrant? Grant = null);

/// <summary>Map-owned boundary; no production identity/permission implementation is supplied.</summary>
/// <remarks>Use trusted server identity and the supplied connection/transaction. Authorize each
/// retry and hold M2 permission/revocation fences until transaction completion, before any Map
/// resource lock. A Hobbies profile is not an Admin grant. Do not commit, replace or dispose
/// the caller's transaction. Missing/unavailable evidence must never produce a grant.</remarks>
internal interface IMapAttractionManagementAuthorization
{
    Task<MapAttractionManagementAuthorizationResult> AuthorizeAsync(DbConnection connection,
        DbTransaction transaction, Guid attractionId, bool requestedIsActive,
        CancellationToken cancellationToken = default);
}
