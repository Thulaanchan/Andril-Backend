using Andril.API.Authorization.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Xunit;

namespace Andril.SecurityTests.Authorization.Admin;

public sealed class AdminPermissionPolicyRegistrationTests
{
    [Fact]
    public void AddAccountWidePolicy_RegistersCorrectPermission()
    {
        var options = new AuthorizationOptions();

        AdminPermissionPolicyRegistration.AddAccountWidePolicy(
            options, "Admin.Moderation.Read", "  Moderation.Read  ");

        var policy = options.GetPolicy("Admin.Moderation.Read");

        Assert.NotNull(policy);
        var requirement = Assert.Single(
            policy.Requirements.OfType<AdminPermissionRequirement>());
        Assert.Equal("Moderation.Read", requirement.Permission);
    }

    [Fact]
    public void AddAccountWidePolicy_RequiresAuthenticatedUser()
    {
        var options = new AuthorizationOptions();

        AdminPermissionPolicyRegistration.AddAccountWidePolicy(
            options, "Admin.Moderation.Read", "Moderation.Read");

        var policy = options.GetPolicy("Admin.Moderation.Read");

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements,
            requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void AddAccountWidePolicy_TrimsPolicyName()
    {
        var options = new AuthorizationOptions();

        AdminPermissionPolicyRegistration.AddAccountWidePolicy(
            options, "  Admin.Moderation.Read  ", "Moderation.Read");

        Assert.NotNull(options.GetPolicy("Admin.Moderation.Read"));
        Assert.Null(options.GetPolicy("  Admin.Moderation.Read  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAccountWidePolicy_RejectsInvalidPolicyName(
        string? policyName)
    {
        var options = new AuthorizationOptions();

        var exception = Assert.Throws<ArgumentException>(() =>
            AdminPermissionPolicyRegistration.AddAccountWidePolicy(
                options, policyName!, "Moderation.Read"));

        Assert.Equal("policyName", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddAccountWidePolicy_RejectsInvalidPermission(
        string? permission)
    {
        var options = new AuthorizationOptions();

        var exception = Assert.Throws<ArgumentException>(() =>
            AdminPermissionPolicyRegistration.AddAccountWidePolicy(
                options, "Admin.Moderation.Read", permission!));

        Assert.Equal("permission", exception.ParamName);
    }

    [Fact]
    public void AddAccountWidePolicy_RejectsDuplicatePolicyName()
    {
        var options = new AuthorizationOptions();

        AdminPermissionPolicyRegistration.AddAccountWidePolicy(
            options, "Admin.Moderation.Read", "Moderation.Read");

        Assert.Throws<InvalidOperationException>(() =>
            AdminPermissionPolicyRegistration.AddAccountWidePolicy(
                options, " Admin.Moderation.Read ", "Finance.Read"));

        var policy = options.GetPolicy("Admin.Moderation.Read");
        Assert.NotNull(policy);
        var requirement = Assert.Single(
            policy.Requirements.OfType<AdminPermissionRequirement>());
        Assert.Equal("Moderation.Read", requirement.Permission);
    }

    [Fact]
    public void AddAccountWidePolicy_RejectsNullOptions()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            AdminPermissionPolicyRegistration.AddAccountWidePolicy(
                null!, "Admin.Moderation.Read", "Moderation.Read"));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void AddAccountWidePolicy_AllowsDifferentPolicyNames()
    {
        var options = new AuthorizationOptions();

        AdminPermissionPolicyRegistration.AddAccountWidePolicy(
            options, "Admin.Moderation.Read", "Moderation.Read");
        AdminPermissionPolicyRegistration.AddAccountWidePolicy(
            options, "Admin.Finance.Read", "Finance.Read");

        Assert.NotNull(options.GetPolicy("Admin.Moderation.Read"));
        Assert.NotNull(options.GetPolicy("Admin.Finance.Read"));
    }
}
