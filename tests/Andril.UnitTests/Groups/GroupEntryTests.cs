using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupEntryTests
{
    [Theory]
    [InlineData("learning", GroupKind.Classroom, "Mathematics 101")]
    [InlineData("hobbies", GroupKind.Club, "Photography Enthusiasts")]
    [InlineData("social", GroupKind.Zone, "Downtown Coffee Walkers")]
    public void Constructor_WithValidParameters_ConstructsSuccessfullyAndPreservesProperties(
        string module, GroupKind groupKind, string name)
    {
        var id = Guid.NewGuid();
        var scope = new GroupModuleScope(module, groupKind);

        var group = new GroupEntry(id, scope, name);

        Assert.Equal(id, group.Id);
        Assert.Same(scope, group.Scope);
        Assert.Equal(name, group.Name);
    }

    [Fact]
    public void Constructor_WithEmptyId_ThrowsArgumentException()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);

        var exception = Assert.Throws<ArgumentException>(
            () => new GroupEntry(Guid.Empty, scope, "Valid Group"));

        Assert.Equal("id", exception.ParamName);
    }

    [Fact]
    public void Constructor_WithNullScope_ThrowsArgumentNullException()
    {
        var id = Guid.NewGuid();

        var exception = Assert.Throws<ArgumentNullException>(
            () => new GroupEntry(id, null!, "Valid Group"));

        Assert.Equal("scope", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Constructor_WithNullEmptyOrWhitespaceName_ThrowsArgumentException(string? name)
    {
        var id = Guid.NewGuid();
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);

        var exception = Assert.Throws<ArgumentException>(
            () => new GroupEntry(id, scope, name!));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void CoreProperties_HaveNoPublicSetters_AndStateRemainsImmutable()
    {
        var idProperty = typeof(GroupEntry).GetProperty(nameof(GroupEntry.Id));
        var scopeProperty = typeof(GroupEntry).GetProperty(nameof(GroupEntry.Scope));
        var nameProperty = typeof(GroupEntry).GetProperty(nameof(GroupEntry.Name));

        Assert.NotNull(idProperty);
        Assert.True(idProperty.CanRead);
        Assert.Null(idProperty.SetMethod);

        Assert.NotNull(scopeProperty);
        Assert.True(scopeProperty.CanRead);
        Assert.Null(scopeProperty.SetMethod);

        Assert.NotNull(nameProperty);
        Assert.True(nameProperty.CanRead);
        Assert.Null(nameProperty.SetMethod);

        var id = Guid.NewGuid();
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        const string name = "Immutable Zone";

        var group = new GroupEntry(id, scope, name);

        Assert.Equal(id, group.Id);
        Assert.Equal(scope, group.Scope);
        Assert.Equal(name, group.Name);
    }

    [Fact]
    public void RepeatedConstruction_IsDeterministicWithoutSideEffects()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var id = Guid.NewGuid();
        const string name = "Deterministic Classroom";

        for (var i = 0; i < 5; i++)
        {
            var group = new GroupEntry(id, scope, name);

            Assert.Equal(id, group.Id);
            Assert.Equal(scope, group.Scope);
            Assert.Equal(name, group.Name);
        }
    }
}
