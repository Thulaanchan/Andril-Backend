using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupModuleScopePolicyTests
{
    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public void ApprovedModuleAndGroupKind_AreCompatible(
        string module, GroupKind groupKind)
    {
        Assert.True(GroupModuleScopePolicy.IsCompatible(module, groupKind));
    }

    [Theory]
    [InlineData("learning", GroupKind.Club)]
    [InlineData("learning", GroupKind.Zone)]
    [InlineData("hobbies", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Zone)]
    [InlineData("social", GroupKind.Classroom)]
    [InlineData("social", GroupKind.Club)]
    public void CrossModuleGroupKind_IsRejected(
        string module, GroupKind groupKind)
    {
        Assert.False(GroupModuleScopePolicy.IsCompatible(module, groupKind));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("unknown")]
    [InlineData("Learning")]
    [InlineData("Hobbies")]
    [InlineData("Social")]
    [InlineData(" learning ")]
    [InlineData("dating")]
    public void NoncanonicalModule_IsRejectedForEveryGroupKind(string? module)
    {
        foreach (var groupKind in Enum.GetValues<GroupKind>())
        {
            Assert.False(GroupModuleScopePolicy.IsCompatible(module, groupKind));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void DefaultAndUndefinedGroupKinds_AreRejectedForEveryModule(int value)
    {
        var groupKind = (GroupKind)value;

        foreach (var module in new[] { "learning", "hobbies", "social" })
        {
            Assert.False(GroupModuleScopePolicy.IsCompatible(module, groupKind));
        }
    }

    [Fact]
    public void RepeatedEvaluation_IsDeterministicWithoutScopeFallback()
    {
        for (var repetition = 0; repetition < 3; repetition++)
        {
            Assert.True(GroupModuleScopePolicy.IsCompatible("learning", GroupKind.Classroom));
            Assert.False(GroupModuleScopePolicy.IsCompatible("hobbies", GroupKind.Classroom));
            Assert.True(GroupModuleScopePolicy.IsCompatible("hobbies", GroupKind.Club));
            Assert.False(GroupModuleScopePolicy.IsCompatible("social", GroupKind.Club));
            Assert.True(GroupModuleScopePolicy.IsCompatible("social", GroupKind.Zone));
            Assert.False(GroupModuleScopePolicy.IsCompatible(null, GroupKind.Zone));
            Assert.False(GroupModuleScopePolicy.IsCompatible("learning", default));
        }
    }
}
