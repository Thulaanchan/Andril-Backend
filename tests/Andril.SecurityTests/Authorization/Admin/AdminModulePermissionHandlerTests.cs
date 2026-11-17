using System.Security.Claims;
using Andril.API.Authorization.Admin;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Andril.SecurityTests.Authorization.Admin;

public sealed class AdminModulePermissionHandlerTests
{
    private const string Subject =
        "11111111-1111-4111-8111-111111111111";

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public async Task MatchingTrustedGrant_Succeeds(string module)
    {
        var reader = new FakeReader
        {
            Enabled = true,
            AllowedModule = module
        };

        var result = await AuthorizeAsync(CreateUser(), reader, module);

        Assert.True(result.HasSucceeded);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(Subject, reader.LastSubject);
        Assert.Equal("Moderation.Read", reader.LastPermission);
        Assert.Equal(module, reader.LastScope);
    }

    [Fact]
    public async Task MissingGrant_FailsClosed()
    {
        var reader = new FakeReader { Enabled = false };

        var result = await AuthorizeAsync(CreateUser(), reader);

        Assert.True(result.HasFailed);
        Assert.False(result.HasSucceeded);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task WrongModuleGrant_FailsClosed()
    {
        var reader = new FakeReader
        {
            Enabled = true,
            AllowedModule = "hobbies"
        };

        var result = await AuthorizeAsync(
            CreateUser(), reader, "learning");

        Assert.True(result.HasFailed);
        Assert.Equal("learning", reader.LastScope);
    }

    [Fact]
    public async Task PermissionReaderException_FailsClosed()
    {
        var reader = new FakeReader { ThrowError = true };

        var result = await AuthorizeAsync(CreateUser(), reader);

        Assert.True(result.HasFailed);
        Assert.False(result.HasSucceeded);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task UnauthenticatedUser_DeniedWithoutLookup()
    {
        var reader = new FakeReader { Enabled = true };

        var result = await AuthorizeAsync(
            CreateUser(authenticated: false), reader);

        Assert.True(result.HasFailed);
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("invalid-guid", "authenticated", "false")]
    [InlineData("00000000-0000-0000-0000-000000000000",
        "authenticated", "false")]
    [InlineData(null, "authenticated", "false")]
    [InlineData(Subject, "guest", "false")]
    [InlineData(Subject, null, "false")]
    [InlineData(Subject, "authenticated", "true")]
    [InlineData(Subject, "authenticated", "invalid")]
    [InlineData(Subject, "authenticated", null)]
    public async Task InvalidClaims_DeniedWithoutLookup(
        string? subject, string? role, string? anonymous)
    {
        var reader = new FakeReader { Enabled = true };

        var result = await AuthorizeAsync(
            CreateUser(subject, role, anonymous), reader);

        Assert.True(result.HasFailed);
        Assert.False(result.HasSucceeded);
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("sub", Subject)]
    [InlineData("role", "authenticated")]
    [InlineData("is_anonymous", "false")]
    public async Task DuplicateClaims_DeniedWithoutLookup(
        string claimType, string claimValue)
    {
        var reader = new FakeReader { Enabled = true };
        var user = CreateUser();

        ((ClaimsIdentity)user.Identity!).AddClaim(
            new Claim(claimType, claimValue));

        var result = await AuthorizeAsync(user, reader);

        Assert.True(result.HasFailed);
        Assert.Equal(0, reader.Calls);
    }

    private static ClaimsPrincipal CreateUser(
        string? subject = Subject,
        string? role = "authenticated",
        string? anonymous = "false",
        bool authenticated = true)
    {
        var claims = new List<Claim>();

        if (subject is not null)
            claims.Add(new Claim("sub", subject));

        if (role is not null)
            claims.Add(new Claim("role", role));

        if (anonymous is not null)
            claims.Add(new Claim("is_anonymous", anonymous));

        var identity = new ClaimsIdentity(
            claims, authenticated ? "TestAuth" : null);

        return new ClaimsPrincipal(identity);
    }

    private static async Task<AuthorizationHandlerContext> AuthorizeAsync(
        ClaimsPrincipal user,
        FakeReader reader,
        string module = "learning")
    {
        var requirement = new AdminModulePermissionRequirement(
            "Moderation.Read", module);

        var context = new AuthorizationHandlerContext(
            new[] { requirement }, user, resource: null);

        await new AdminModulePermissionHandler(reader)
            .HandleAsync(context);

        return context;
    }

    private sealed class FakeReader : IAdminPermissionReader
    {
        public bool Enabled { get; set; }
        public bool ThrowError { get; set; }
        public string AllowedModule { get; set; } = "learning";
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

            var allowed = Enabled
                && verifiedSubject == Subject
                && permission == "Moderation.Read"
                && moduleScope == AllowedModule;

            return Task.FromResult(allowed);
        }
    }
}
