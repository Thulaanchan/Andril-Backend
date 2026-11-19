using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupModuleScopeTests
{
    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public void Constructor_WithValidModuleAndGroupKind_ConstructsSuccessfully(
        string module, GroupKind groupKind)
    {
        var scope = new GroupModuleScope(module, groupKind);

        Assert.Equal(module, scope.Module);
        Assert.Equal(groupKind, scope.GroupKind);
    }

    [Theory]
    [InlineData("learning", GroupKind.Club)]
    [InlineData("learning", GroupKind.Zone)]
    [InlineData("hobbies", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Zone)]
    [InlineData("social", GroupKind.Classroom)]
    [InlineData("social", GroupKind.Club)]
    public void Constructor_WithIncompatibleModuleAndGroupKind_ThrowsArgumentException(
        string module, GroupKind groupKind)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new GroupModuleScope(module, groupKind));

        Assert.Contains("compatible", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("unknown")]
    [InlineData("dating")]
    public void Constructor_WithNullEmptyOrUnknownModule_ThrowsArgumentExceptionForEveryGroupKind(
        string? module)
    {
        foreach (var groupKind in Enum.GetValues<GroupKind>())
        {
            Assert.Throws<ArgumentException>(
                () => new GroupModuleScope(module, groupKind));
        }
    }

    [Theory]
    [InlineData("Learning", GroupKind.Classroom)]
    [InlineData("LEARNING", GroupKind.Classroom)]
    [InlineData(" learning ", GroupKind.Classroom)]
    [InlineData("Hobbies", GroupKind.Club)]
    [InlineData("HOBBIES", GroupKind.Club)]
    [InlineData(" hobbies ", GroupKind.Club)]
    [InlineData("Social", GroupKind.Zone)]
    [InlineData("SOCIAL", GroupKind.Zone)]
    [InlineData(" social ", GroupKind.Zone)]
    public void Constructor_WithIncorrectlyCasedOrPaddedModule_ThrowsArgumentExceptionWithoutFallback(
        string module, GroupKind groupKind)
    {
        Assert.Throws<ArgumentException>(
            () => new GroupModuleScope(module, groupKind));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Constructor_WithDefaultOrUndefinedGroupKind_ThrowsArgumentException(int value)
    {
        var groupKind = (GroupKind)value;

        foreach (var module in new[] { "learning", "hobbies", "social" })
        {
            Assert.Throws<ArgumentException>(
                () => new GroupModuleScope(module, groupKind));
        }
    }

    [Fact]
    public void Properties_AreReadOnlyAndImmutable()
    {
        var moduleProperty = typeof(GroupModuleScope).GetProperty(nameof(GroupModuleScope.Module));
        var groupKindProperty = typeof(GroupModuleScope).GetProperty(nameof(GroupModuleScope.GroupKind));

        Assert.NotNull(moduleProperty);
        Assert.True(moduleProperty.CanRead);
        Assert.Null(moduleProperty.SetMethod);

        Assert.NotNull(groupKindProperty);
        Assert.True(groupKindProperty.CanRead);
        Assert.Null(groupKindProperty.SetMethod);

        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var copy = scope with { };

        Assert.Equal(scope.Module, copy.Module);
        Assert.Equal(scope.GroupKind, copy.GroupKind);
        Assert.Equal(scope, copy);
    }

    [Fact]
    public void ValueEquality_IdenticalScopes_AreEqual()
    {
        var first = new GroupModuleScope("learning", GroupKind.Classroom);
        var second = new GroupModuleScope("learning", GroupKind.Classroom);

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.True(first.Equals(second));
        Assert.True(first.Equals((object)second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void ValueInequality_DifferentScopes_AreNotEqual()
    {
        var learning = new GroupModuleScope("learning", GroupKind.Classroom);
        var hobbies = new GroupModuleScope("hobbies", GroupKind.Club);
        var social = new GroupModuleScope("social", GroupKind.Zone);

        Assert.NotEqual(learning, hobbies);
        Assert.NotEqual(hobbies, social);
        Assert.NotEqual(learning, social);

        Assert.True(learning != hobbies);
        Assert.False(learning == hobbies);
        Assert.False(learning.Equals(null));
        Assert.False(learning.Equals(new object()));
    }

    [Fact]
    public void HashCode_IsConsistentAcrossMultipleInvocations()
    {
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);

        var initialHashCode = scope.GetHashCode();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(initialHashCode, scope.GetHashCode());
        }
    }

    [Fact]
    public void RepeatedConstruction_IsDeterministicWithoutStateLeakageOrFallback()
    {
        for (var repetition = 0; repetition < 3; repetition++)
        {
            var classroom = new GroupModuleScope("learning", GroupKind.Classroom);
            var club = new GroupModuleScope("hobbies", GroupKind.Club);
            var zone = new GroupModuleScope("social", GroupKind.Zone);

            Assert.Equal("learning", classroom.Module);
            Assert.Equal(GroupKind.Classroom, classroom.GroupKind);

            Assert.Equal("hobbies", club.Module);
            Assert.Equal(GroupKind.Club, club.GroupKind);

            Assert.Equal("social", zone.Module);
            Assert.Equal(GroupKind.Zone, zone.GroupKind);

            Assert.Throws<ArgumentException>(() => new GroupModuleScope("learning", GroupKind.Club));
            Assert.Throws<ArgumentException>(() => new GroupModuleScope("social", GroupKind.Classroom));
            Assert.Throws<ArgumentException>(() => new GroupModuleScope(null, GroupKind.Zone));
        }
    }
}
