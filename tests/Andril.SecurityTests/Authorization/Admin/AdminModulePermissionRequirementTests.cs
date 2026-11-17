using Andril.API.Authorization.Admin;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Andril.SecurityTests.Authorization.Admin;

public sealed class AdminModulePermissionRequirementTests
{
    [Theory]
    [InlineData("Moderation.Read", "learning")]
    [InlineData("Map.Review", "hobbies")]
    [InlineData("Safety.Review", "social")]
    public void Constructor_AcceptsValidPermissionAndModule(
        string permission, string moduleScope)
    {
        var requirement = new AdminModulePermissionRequirement(
            permission, moduleScope);

        Assert.Equal(permission, requirement.Permission);
        Assert.Equal(moduleScope, requirement.ModuleScope);
    }

    [Fact]
    public void Constructor_TrimsPermission()
    {
        var requirement = new AdminModulePermissionRequirement(
            "  Moderation.Read  ", "learning");

        Assert.Equal("Moderation.Read", requirement.Permission);
        Assert.Equal("learning", requirement.ModuleScope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_RejectsMissingPermission(string? permission)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AdminModulePermissionRequirement(permission!, "learning"));

        Assert.Equal("permission", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_RejectsMissingModule(string? moduleScope)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AdminModulePermissionRequirement(
                "Moderation.Read", moduleScope!));

        Assert.Equal("moduleScope", exception.ParamName);
    }

    [Theory]
    [InlineData("Learning")]
    [InlineData("Hobbies")]
    [InlineData("Social")]
    [InlineData("dating")]
    [InlineData("admin")]
    [InlineData("learning ")]
    public void Constructor_RejectsUnsupportedModule(string moduleScope)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AdminModulePermissionRequirement(
                "Moderation.Read", moduleScope));

        Assert.Equal("moduleScope", exception.ParamName);
    }

    [Fact]
    public void Requirement_ImplementsAuthorizationRequirement()
    {
        var requirement = new AdminModulePermissionRequirement(
            "Moderation.Read", "learning");

        Assert.IsAssignableFrom<IAuthorizationRequirement>(requirement);
    }
}
