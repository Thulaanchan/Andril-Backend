using System;
using System.Linq;
using Andril.API.Authorization.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Xunit;

namespace Andril.SecurityTests.Authorization.Admin;

public sealed class AdminModulePermissionPolicyRegistrationTests
{
    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public void ValidModule_RegistersCorrectRequirements(string module)
    {
        var options = new AuthorizationOptions();

        AdminModulePermissionPolicyRegistration.AddModulePolicy(
            options, "Admin.Moderation.Read",
            "Moderation.Read", module);

        var policy = Assert.IsType<AuthorizationPolicy>(
            options.GetPolicy("Admin.Moderation.Read"));

        Assert.Contains(policy.Requirements,
            r => r is DenyAnonymousAuthorizationRequirement);

        var requirement = Assert.Single(
            policy.Requirements.OfType<AdminModulePermissionRequirement>());

        Assert.Equal("Moderation.Read", requirement.Permission);
        Assert.Equal(module, requirement.ModuleScope);
    }

    [Fact]
    public void Registration_TrimsPolicyNameAndPermission()
    {
        var options = new AuthorizationOptions();

        AdminModulePermissionPolicyRegistration.AddModulePolicy(
            options, "  Admin.Moderation.Read  ",
            "  Moderation.Read  ", "learning");

        var policy = Assert.IsType<AuthorizationPolicy>(
            options.GetPolicy("Admin.Moderation.Read"));

        var requirement = Assert.Single(
            policy.Requirements.OfType<AdminModulePermissionRequirement>());

        Assert.Equal("Moderation.Read", requirement.Permission);
        Assert.Equal("learning", requirement.ModuleScope);
        Assert.Null(options.GetPolicy("  Admin.Moderation.Read  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidPolicyName_IsRejected(string? policyName)
    {
        var options = new AuthorizationOptions();

        var exception = Assert.Throws<ArgumentException>(() =>
            AdminModulePermissionPolicyRegistration.AddModulePolicy(
                options, policyName!, "Moderation.Read", "learning"));

        Assert.Equal("policyName", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidPermission_IsRejected(string? permission)
    {
        var options = new AuthorizationOptions();

        var exception = Assert.Throws<ArgumentException>(() =>
            AdminModulePermissionPolicyRegistration.AddModulePolicy(
                options, "Admin.Moderation.Read",
                permission!, "learning"));

        Assert.Equal("permission", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void MissingModuleScope_IsRejected(string? module)
    {
        var options = new AuthorizationOptions();

        var exception = Assert.Throws<ArgumentException>(() =>
            AdminModulePermissionPolicyRegistration.AddModulePolicy(
                options, "Admin.Moderation.Read",
                "Moderation.Read", module!));

        Assert.Equal("moduleScope", exception.ParamName);
        Assert.Null(options.GetPolicy("Admin.Moderation.Read"));
    }

    [Theory]
    [InlineData("Learning")]
    [InlineData("Hobbies")]
    [InlineData("Social")]
    [InlineData("dating")]
    [InlineData("admin")]
    [InlineData("learning ")]
    public void UnsupportedModuleScope_IsRejected(string module)
    {
        var options = new AuthorizationOptions();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            AdminModulePermissionPolicyRegistration.AddModulePolicy(
                options, "Admin.Moderation.Read",
                "Moderation.Read", module));

        Assert.Equal("moduleScope", exception.ParamName);
        Assert.Null(options.GetPolicy("Admin.Moderation.Read"));
    }

    [Fact]
    public void DuplicatePolicyName_IsRejected()
    {
        var options = new AuthorizationOptions();

        AdminModulePermissionPolicyRegistration.AddModulePolicy(
            options, "Admin.Moderation.Read",
            "Moderation.Read", "learning");

        Assert.Throws<InvalidOperationException>(() =>
            AdminModulePermissionPolicyRegistration.AddModulePolicy(
                options, " Admin.Moderation.Read ",
                "Finance.Read", "hobbies"));

        var policy = Assert.IsType<AuthorizationPolicy>(
            options.GetPolicy("Admin.Moderation.Read"));

        var requirement = Assert.Single(
            policy.Requirements.OfType<AdminModulePermissionRequirement>());

        Assert.Equal("Moderation.Read", requirement.Permission);
        Assert.Equal("learning", requirement.ModuleScope);
    }

    [Fact]
    public void DifferentPolicyNames_CanBeRegisteredIndependently()
    {
        var options = new AuthorizationOptions();

        AdminModulePermissionPolicyRegistration.AddModulePolicy(
            options, "Admin.Learning.Read",
            "Moderation.Read", "learning");

        AdminModulePermissionPolicyRegistration.AddModulePolicy(
            options, "Admin.Hobbies.Read",
            "Moderation.Read", "hobbies");

        Assert.NotNull(options.GetPolicy("Admin.Learning.Read"));
        Assert.NotNull(options.GetPolicy("Admin.Hobbies.Read"));
    }

    [Fact]
    public void NullAuthorizationOptions_IsRejected()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            AdminModulePermissionPolicyRegistration.AddModulePolicy(
                null!, "Admin.Moderation.Read",
                "Moderation.Read", "learning"));

        Assert.Equal("options", exception.ParamName);
    }
}
