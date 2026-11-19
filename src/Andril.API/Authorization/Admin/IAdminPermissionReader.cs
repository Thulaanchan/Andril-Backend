using System.Threading;
using System.Threading.Tasks;

namespace Andril.API.Authorization.Admin;

/// <summary>
/// Reads trusted server-side Admin permission grants.
/// Does not grant or provision permissions.
/// </summary>
public interface IAdminPermissionReader
{
    /// <summary>
    /// Checks a verified subject against a trusted permission source.
    /// Null moduleScope is not an implicit all-module grant.
    /// Missing, expired, revoked or uncertain grants must fail closed.
    /// Callers must deny access if the reader throws an exception.
    /// </summary>
    Task<bool> HasPermissionAsync(
        string verifiedSubject,
        string permission,
        string? moduleScope,
        CancellationToken cancellationToken = default);
}
