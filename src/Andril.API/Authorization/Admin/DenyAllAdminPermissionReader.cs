using System.Threading;
using System.Threading.Tasks;

namespace Andril.API.Authorization.Admin;

/// <summary>
/// Denies all Admin permissions until a trusted grant provider is available.
/// This implementation never grants Admin access.
/// </summary>
public sealed class DenyAllAdminPermissionReader : IAdminPermissionReader
{
    public Task<bool> HasPermissionAsync(
        string verifiedSubject,
        string permission,
        string? moduleScope,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}
