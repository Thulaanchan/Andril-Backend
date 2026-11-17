using Andril.API.Authorization.Admin;
using Xunit;

namespace Andril.SecurityTests.Authorization.Admin;

public sealed class AdminModuleScopeTests
{
    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public void IsValid_AcceptsCanonicalModules(string module)
    {
        Assert.True(AdminModuleScope.IsValid(module));
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public void RequireValid_ReturnsCanonicalModule(string module)
    {
        var result = AdminModuleScope.RequireValid(module);

        Assert.Equal(module, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("Learning")]
    [InlineData("HOBBIES")]
    [InlineData("social ")]
    [InlineData("dating")]
    [InlineData("admin")]
    public void IsValid_RejectsInvalidScope(string? module)
    {
        Assert.False(AdminModuleScope.IsValid(module));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\t")]
    public void RequireValid_RejectsMissingScope(string? module)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AdminModuleScope.RequireValid(module));

        Assert.Equal("moduleScope", exception.ParamName);
    }

    [Theory]
    [InlineData("Learning")]
    [InlineData("Hobbies")]
    [InlineData("Social")]
    [InlineData("social ")]
    [InlineData("dating")]
    public void RequireValid_RejectsUnsupportedScope(string module)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            AdminModuleScope.RequireValid(module));

        Assert.Equal("moduleScope", exception.ParamName);
    }
}
