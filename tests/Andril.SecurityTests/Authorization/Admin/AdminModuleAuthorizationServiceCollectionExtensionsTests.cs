using System.Security.Claims;
using Andril.API.Authorization.Admin;
using Andril.API.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Authorization.Admin;

public sealed class AdminModuleAuthorizationServiceCollectionExtensionsTests
{
    [Fact]
    public void Registration_ReturnsSameServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddAdminModuleAuthorization();

        Assert.Same(services, result);
    }

    [Fact]
    public void Registration_RejectsNullServiceCollection()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AdminModuleAuthorizationServiceCollectionExtensions
                .AddAdminModuleAuthorization(null!));
    }

    [Fact]
    public void Registration_ResolvesDenyAllReaderByDefault()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();

        var reader = scope.ServiceProvider
            .GetRequiredService<IAdminPermissionReader>();

        Assert.IsType<DenyAllAdminPermissionReader>(reader);
    }

    [Fact]
    public void Registration_ResolvesModulePermissionHandler()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();

        var handlers = scope.ServiceProvider
            .GetServices<IAuthorizationHandler>()
            .OfType<AdminModulePermissionHandler>();

        Assert.Single(handlers);
    }

    [Fact]
    public void RepeatedRegistration_DoesNotDuplicateHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdminModuleAuthorization();
        services.AddAdminModuleAuthorization();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var handlers = scope.ServiceProvider
            .GetServices<IAuthorizationHandler>()
            .OfType<AdminModulePermissionHandler>();

        Assert.Single(handlers);
    }

    [Fact]
    public void Registration_PreservesExistingReader()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var existingReader = new StubPermissionReader();
        services.AddSingleton<IAdminPermissionReader>(existingReader);
        services.AddAdminModuleAuthorization();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolvedReader = scope.ServiceProvider
            .GetRequiredService<IAdminPermissionReader>();

        Assert.Same(existingReader, resolvedReader);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task DefaultAuthorization_DeniesAllModuleGrants(
        string moduleScope)
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();

        var authorization = scope.ServiceProvider
            .GetRequiredService<IAuthorizationService>();

        var claims = new[]
        {
            new Claim(
                "sub",
                "11111111-1111-4111-8111-111111111111"),
            new Claim("role", "authenticated"),
            new Claim("is_anonymous", "false")
        };

        var user = new ClaimsPrincipal(
            new ClaimsIdentity(claims, "TestAuth"));

        var requirements = new IAuthorizationRequirement[]
        {
            new AdminModulePermissionRequirement(
                "Moderation.Read", moduleScope)
        };

        var result = await authorization.AuthorizeAsync(
            user, null, requirements);

        Assert.False(result.Succeeded);
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAdminModuleAuthorization();

        return services.BuildServiceProvider();
    }

    private sealed class StubPermissionReader
        : IAdminPermissionReader
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
}
