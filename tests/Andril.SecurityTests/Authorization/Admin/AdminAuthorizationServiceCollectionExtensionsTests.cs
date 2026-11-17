using System.Security.Claims;
using Andril.API.Authorization.Admin;
using Andril.API.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Authorization.Admin;

public sealed class AdminAuthorizationServiceCollectionExtensionsTests
{
    [Fact]
    public void Registration_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddDenyAllAdminAuthorization();

        Assert.Same(services, result);
    }

    [Fact]
    public void Registration_ResolvesDenyAllPermissionReader()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();

        var reader = scope.ServiceProvider
            .GetRequiredService<IAdminPermissionReader>();

        Assert.IsType<DenyAllAdminPermissionReader>(reader);
    }

    [Fact]
    public void Registration_ResolvesAdminPermissionHandler()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();

        var handlers = scope.ServiceProvider
            .GetServices<IAuthorizationHandler>()
            .OfType<AdminPermissionHandler>();

        Assert.Single(handlers);
    }

    [Fact]
    public void RepeatedRegistration_DoesNotDuplicateHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDenyAllAdminAuthorization();
        services.AddDenyAllAdminAuthorization();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var handlers = scope.ServiceProvider
            .GetServices<IAuthorizationHandler>()
            .OfType<AdminPermissionHandler>();

        Assert.Single(handlers);
    }

    [Fact]
    public async Task RegisteredAuthorization_DeniesWithoutTrustedGrant()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();

        var authorization = scope.ServiceProvider
            .GetRequiredService<IAuthorizationService>();

        var claims = new[]
        {
            new Claim("sub", "11111111-1111-4111-8111-111111111111"),
            new Claim("role", "authenticated"),
            new Claim("is_anonymous", "false")
        };

        var user = new ClaimsPrincipal(
            new ClaimsIdentity(claims, "TestAuthentication"));

        var requirements = new IAuthorizationRequirement[]
        {
            new AdminPermissionRequirement("Moderation.Read")
        };

        var result = await authorization.AuthorizeAsync(
            user, null, requirements);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Registration_RejectsNullServices()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AdminAuthorizationServiceCollectionExtensions
                .AddDenyAllAdminAuthorization(null!));
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDenyAllAdminAuthorization();
        return services.BuildServiceProvider();
    }
}
