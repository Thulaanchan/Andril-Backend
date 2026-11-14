using System.Security.Claims;
using Andril.Shared.Authentication;

namespace Andril.API.Authentication;

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId => VerifiedUserSubject.GetId(accessor.HttpContext?.User);
    public bool IsAuthenticated => UserId.HasValue;
}

internal static class VerifiedUserSubject
{
    internal static Guid? GetId(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return null;

        var subjects = principal.FindAll("sub").ToArray();
        var roles = principal.FindAll("role").ToArray();
        var anonymous = principal.FindAll("is_anonymous").ToArray();
        return subjects.Length == 1 && Guid.TryParse(subjects[0].Value, out var id) && id != Guid.Empty &&
            roles.Length == 1 && roles[0].Value == "authenticated" &&
            anonymous.Length == 1 && bool.TryParse(anonymous[0].Value, out var isAnonymous) && !isAnonymous
            ? id : null;
    }
}
