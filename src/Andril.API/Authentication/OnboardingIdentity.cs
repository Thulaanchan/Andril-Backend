using Andril.Common.Accounts.Onboarding;

namespace Andril.API.Authentication;

internal sealed record ConfirmedIdentityFeature(Guid Subject);

public sealed class OnboardingIdentity(IHttpContextAccessor accessor) : IOnboardingIdentity
{
    public bool IsConfirmedFor(Guid subject) => subject != Guid.Empty
        && accessor.HttpContext?.Features.Get<ConfirmedIdentityFeature>()?.Subject == subject;
}

internal static class IdentityProtectedRoutes
{
    internal static bool IsOnboarding(PathString path) =>
        path.StartsWithSegments("/api/v1/onboarding", StringComparison.OrdinalIgnoreCase);

    internal static bool Contains(PathString path) => IsOnboarding(path)
        || path.StartsWithSegments("/api/v1/account", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/v1/social", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/v1/groups", StringComparison.OrdinalIgnoreCase);
}
