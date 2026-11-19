using Andril.Groups.DTOs;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupEntryDtoTests
{
    [Fact]
    public void Constructor_DirectInstantiation_PreservesAllProperties()
    {
        var id = Guid.NewGuid();
        const string module = "learning";
        const GroupKind groupKind = GroupKind.Classroom;
        const string name = "Geometry Advanced";

        var dto = new GroupEntryDto(id, module, groupKind, name);

        Assert.Equal(id, dto.Id);
        Assert.Equal(module, dto.Module);
        Assert.Equal(groupKind, dto.GroupKind);
        Assert.Equal(name, dto.Name);
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom, "Algebra I")]
    [InlineData("hobbies", GroupKind.Club, "Woodworking Guild")]
    [InlineData("social", GroupKind.Zone, "Weekend Board Gamers")]
    public void FromEntry_WithValidEntry_CorrectlyMapsAllApprovedScopes(
        string module, GroupKind groupKind, string name)
    {
        var id = Guid.NewGuid();
        var scope = new GroupModuleScope(module, groupKind);
        var entry = new GroupEntry(id, scope, name);

        var dto = GroupEntryDto.FromEntry(entry);

        Assert.Equal(entry.Id, dto.Id);
        Assert.Equal(entry.Scope.Module, dto.Module);
        Assert.Equal(entry.Scope.GroupKind, dto.GroupKind);
        Assert.Equal(entry.Name, dto.Name);
    }

    [Fact]
    public void FromEntry_WithNullEntry_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => GroupEntryDto.FromEntry(null!));

        Assert.Equal("entry", exception.ParamName);
    }

    [Fact]
    public void FromEntry_PreservesExactValuesWithoutNormalization()
    {
        var id = Guid.NewGuid();
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        const string exactName = "  Spaces Preserved Name  ";
        var entry = new GroupEntry(id, scope, exactName);

        var dto = GroupEntryDto.FromEntry(entry);

        Assert.Equal(exactName, dto.Name);
        Assert.Equal("social", dto.Module);
        Assert.Equal(GroupKind.Zone, dto.GroupKind);
    }

    [Fact]
    public void RecordEquality_IdenticalValues_AreEqualAndHaveMatchingHashCodes()
    {
        var id = Guid.NewGuid();
        var first = new GroupEntryDto(id, "hobbies", GroupKind.Club, "Painting Circle");
        var second = new GroupEntryDto(id, "hobbies", GroupKind.Club, "Painting Circle");

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.True(first.Equals(second));
        Assert.True(first.Equals((object)second));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void RecordInequality_DifferentValues_AreNotEqual()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var first = new GroupEntryDto(id1, "learning", GroupKind.Classroom, "Science 101");
        var second = new GroupEntryDto(id2, "learning", GroupKind.Classroom, "Science 101");
        var third = new GroupEntryDto(id1, "social", GroupKind.Zone, "Science 101");
        var fourth = new GroupEntryDto(id1, "learning", GroupKind.Classroom, "Different Name");

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, third);
        Assert.NotEqual(first, fourth);
        Assert.True(first != second);
        Assert.False(first == second);
        Assert.False(first.Equals(null));
        Assert.False(first.Equals(new object()));
    }

    [Fact]
    public void WithExpression_CreatesModifiedCopy_LeavingOriginalUnchanged()
    {
        var originalId = Guid.NewGuid();
        var original = new GroupEntryDto(originalId, "learning", GroupKind.Classroom, "Original Classroom");

        var modified = original with { Name = "Modified Classroom" };

        Assert.Equal("Original Classroom", original.Name);
        Assert.Equal("Modified Classroom", modified.Name);
        Assert.Equal(originalId, modified.Id);
        Assert.Equal(original.Module, modified.Module);
        Assert.Equal(original.GroupKind, modified.GroupKind);
        Assert.NotEqual(original, modified);
    }

    [Fact]
    public void RepeatedMapping_IsDeterministicAndIdempotent()
    {
        var entry = new GroupEntry(
            Guid.NewGuid(),
            new GroupModuleScope("learning", GroupKind.Classroom),
            "Repeated Mapping Group");

        var firstDto = GroupEntryDto.FromEntry(entry);

        for (var i = 0; i < 5; i++)
        {
            var subsequentDto = GroupEntryDto.FromEntry(entry);

            Assert.Equal(firstDto, subsequentDto);
            Assert.Equal(firstDto.GetHashCode(), subsequentDto.GetHashCode());
        }
    }

    [Fact]
    public void DtoContract_DoesNotExposePrivateOrAuthorizationFields()
    {
        var properties = typeof(GroupEntryDto).GetProperties();
        var propertyNames = properties.Select(p => p.Name).OrderBy(n => n).ToArray();

        var expectedProperties = new[] { "GroupKind", "Id", "Module", "Name" };

        Assert.Equal(expectedProperties, propertyNames);
    }
}
