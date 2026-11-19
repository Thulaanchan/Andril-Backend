using Andril.API.Authorization.Admin;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Andril.SecurityTests.Authorization.Admin;

public sealed class AdminPermissionRequirementTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Constructor_RejectsInvalidPermission(string? permission)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new AdminPermissionRequirement(permission!));

        Assert.Equal("permission", exception.ParamName);
    }

    [Fact]
    public void Constructor_TrimsPermission()
    {
        var requirement = new AdminPermissionRequirement("  Moderation.Read  ");

        Assert.Equal("Moderation.Read", requirement.Permission);
    }

    [Fact]
    public void Constructor_PreservesValidPermission()
    {
        const string permission = "Finance.Read";

        var requirement = new AdminPermissionRequirement(permission);

        Assert.Equal(permission, requirement.Permission);
    }

    [Fact]
    public void Requirement_ImplementsAuthorizationRequirement()
    {
        var requirement = new AdminPermissionRequirement("Moderation.Read");

        Assert.IsAssignableFrom<IAuthorizationRequirement>(requirement);
    }
}
