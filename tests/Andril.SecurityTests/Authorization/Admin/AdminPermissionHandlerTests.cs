using System.Security.Claims;
using Andril.API.Authorization.Admin;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Andril.SecurityTests.Authorization.Admin;

public sealed class AdminPermissionHandlerTests
{
    private const string Subject = "11111111-1111-4111-8111-111111111111";

    [Fact]
    public async Task ValidUser_WithTrustedGrant_Succeeds()
    {
        var reader = new FakeReader { Allowed = true };
        var result = await AuthorizeAsync(CreateUser(), reader);

        Assert.True(result.HasSucceeded);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(Subject, reader.LastSubject);
        Assert.Equal("Moderation.Read", reader.LastPermission);
        Assert.Null(reader.LastScope);
    }

    [Fact]
    public async Task MissingGrant_FailsClosed()
    {
        var reader = new FakeReader();
        var result = await AuthorizeAsync(CreateUser(), reader);

        Assert.True(result.HasFailed);
        Assert.False(result.HasSucceeded);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task ReaderException_FailsClosed()
    {
        var reader = new FakeReader { ThrowError = true };
        var result = await AuthorizeAsync(CreateUser(), reader);

        Assert.True(result.HasFailed);
        Assert.False(result.HasSucceeded);
    }

    [Fact]
    public async Task UnauthenticatedUser_IsDeniedWithoutLookup()
    {
        var reader = new FakeReader { Allowed = true };
        var result = await AuthorizeAsync(
            CreateUser(authenticated: false), reader);

        Assert.True(result.HasFailed);
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("guest", "false")]
    [InlineData("authenticated", "true")]
    [InlineData("authenticated", "invalid")]
    public async Task InvalidIdentityClaims_AreDenied(
        string role, string anonymous)
    {
        var reader = new FakeReader { Allowed = true };
        var result = await AuthorizeAsync(
            CreateUser(role: role, anonymous: anonymous), reader);

        Assert.True(result.HasFailed);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task InvalidSubject_IsDenied()
    {
        var reader = new FakeReader { Allowed = true };
        var result = await AuthorizeAsync(
            CreateUser(subject: "invalid-guid"), reader);

        Assert.True(result.HasFailed);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task DuplicateSubject_IsDenied()
    {
        var reader = new FakeReader { Allowed = true };
        var user = CreateUser();
        ((ClaimsIdentity)user.Identity!).AddClaim(
            new Claim("sub", Subject));

        var result = await AuthorizeAsync(user, reader);

        Assert.True(result.HasFailed);
        Assert.Equal(0, reader.Calls);
    }

    private static ClaimsPrincipal CreateUser(
        bool authenticated = true,
        string role = "authenticated",
        string anonymous = "false",
        string subject = Subject)
    {
        var claims = new[]
        {
            new Claim("sub", subject),
            new Claim("role", role),
            new Claim("is_anonymous", anonymous)
        };

        var identity = new ClaimsIdentity(
            claims, authenticated ? "TestAuth" : null);
        return new ClaimsPrincipal(identity);
    }

    private static async Task<AuthorizationHandlerContext> AuthorizeAsync(
        ClaimsPrincipal user, FakeReader reader)
    {
        var requirement = new AdminPermissionRequirement("Moderation.Read");
        var context = new AuthorizationHandlerContext(
            new[] { requirement }, user, resource: null);

        await new AdminPermissionHandler(reader).HandleAsync(context);
        return context;
    }

    private sealed class FakeReader : IAdminPermissionReader
    {
        public bool Allowed { get; set; }
        public bool ThrowError { get; set; }
        public int Calls { get; private set; }
        public string? LastSubject { get; private set; }
        public string? LastPermission { get; private set; }
        public string? LastScope { get; private set; }

        public Task<bool> HasPermissionAsync(
            string verifiedSubject,
            string permission,
            string? moduleScope,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastSubject = verifiedSubject;
            LastPermission = permission;
            LastScope = moduleScope;

            if (ThrowError)
                throw new InvalidOperationException("Unavailable");

            return Task.FromResult(Allowed);
        }
    }
}
