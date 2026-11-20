using Andril.Groups.DTOs;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupScopeProjectorTests
{
    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public void Project_WithMixedModules_ReturnsOnlyMatchingEntryAndPreservesExactProperties(
        string module, GroupKind groupKind)
    {
        var entries = new[]
        {
            new GroupEntry(Guid.Parse("00000000-0000-0000-0000-000000000001"),
                new GroupModuleScope("learning", GroupKind.Classroom), "  Dummy Classroom  "),
            new GroupEntry(Guid.Parse("00000000-0000-0000-0000-000000000002"),
                new GroupModuleScope("hobbies", GroupKind.Club), "  Dummy Club  "),
            new GroupEntry(Guid.Parse("00000000-0000-0000-0000-000000000003"),
                new GroupModuleScope("social", GroupKind.Zone), "  Dummy Zone  ")
        };
        var expected = entries.Single(entry => entry.Scope.Module == module);

        var result = GroupScopeProjector.Project(entries, new GroupModuleScope(module, groupKind));

        var dto = Assert.Single(result);
        Assert.Equal(expected.Id, dto.Id);
        Assert.Equal(module, dto.Module);
        Assert.Equal(groupKind, dto.GroupKind);
        Assert.Equal(expected.Name, dto.Name);
    }

    [Fact]
    public void Project_WithEmptyCollection_ReturnsEmptyCollection()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);

        var result = GroupScopeProjector.Project(Array.Empty<GroupEntry>(), scope);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public void Project_WithNoMatchingEntries_ReturnsEmptyWithoutCrossModuleFallback(
        string module, GroupKind groupKind)
    {
        var scopes = new[]
        {
            new GroupModuleScope("learning", GroupKind.Classroom),
            new GroupModuleScope("hobbies", GroupKind.Club),
            new GroupModuleScope("social", GroupKind.Zone)
        };
        var entries = scopes.Where(scope => scope.Module != module)
            .Select(scope => new GroupEntry(
                Guid.Parse("00000000-0000-0000-0000-000000000001"), scope, "Dummy Shared Name"))
            .ToArray();

        var result = GroupScopeProjector.Project(entries, new GroupModuleScope(module, groupKind));

        Assert.Empty(result);
    }

    [Fact]
    public void Project_WithNullCollection_ThrowsArgumentNullException()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);

        var exception = Assert.Throws<ArgumentNullException>(
            () => GroupScopeProjector.Project(null, scope));

        Assert.Equal("entries", exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Project_WithNullTargetScope_ThrowsArgumentNullException(bool hasEntry)
    {
        var entries = new List<GroupEntry>();
        if (hasEntry)
        {
            entries.Add(new GroupEntry(Guid.Parse("00000000-0000-0000-0000-000000000001"),
                new GroupModuleScope("learning", GroupKind.Classroom), "Dummy Classroom"));
        }

        var exception = Assert.Throws<ArgumentNullException>(
            () => GroupScopeProjector.Project(entries, null));

        Assert.Equal("targetScope", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Project_WithNullElementAtAnyPosition_ThrowsArgumentException(int nullIndex)
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var entries = new List<GroupEntry>
        {
            new(Guid.Parse("00000000-0000-0000-0000-000000000001"), scope, "Dummy Classroom"),
            new(Guid.Parse("00000000-0000-0000-0000-000000000002"),
                new GroupModuleScope("hobbies", GroupKind.Club), "Dummy Club")
        };
        entries.Insert(nullIndex, null!);

        var exception = Assert.Throws<ArgumentException>(
            () => GroupScopeProjector.Project(entries, scope));

        Assert.Equal("entries", exception.ParamName);
    }

    [Fact]
    public void Project_WithInterleavedEntries_PreservesMatchingEntryOrder()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var thirdId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var entries = new[]
        {
            new GroupEntry(firstId, scope, "Dummy Z"),
            new GroupEntry(Guid.Parse("00000000-0000-0000-0000-000000000004"),
                new GroupModuleScope("hobbies", GroupKind.Club), "Dummy Club"),
            new GroupEntry(secondId, scope, "Dummy A"),
            new GroupEntry(Guid.Parse("00000000-0000-0000-0000-000000000005"),
                new GroupModuleScope("social", GroupKind.Zone), "Dummy Zone"),
            new GroupEntry(thirdId, scope, "Dummy M")
        };

        var result = GroupScopeProjector.Project(entries, scope);

        Assert.Equal(new[] { firstId, secondId, thirdId }, result.Select(dto => dto.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Project_ResultCollection_RejectsMutation(bool hasEntry)
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var entry = new GroupEntry(Guid.Parse("00000000-0000-0000-0000-000000000001"),
            scope, "Dummy Classroom");
        var entries = hasEntry ? new[] { entry } : Array.Empty<GroupEntry>();
        var result = GroupScopeProjector.Project(entries, scope);
        var replacement = new GroupEntryDto(
            Guid.Parse("00000000-0000-0000-0000-000000000002"),
            "learning", GroupKind.Classroom, "Dummy Replacement");

        var collection = Assert.IsAssignableFrom<ICollection<GroupEntryDto>>(result);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Add(replacement));
        Assert.Throws<NotSupportedException>(() => collection.Clear());
        if (hasEntry)
        {
            var list = Assert.IsAssignableFrom<IList<GroupEntryDto>>(result);
            Assert.Throws<NotSupportedException>(() => list[0] = replacement);
            Assert.Throws<NotSupportedException>(() => list.RemoveAt(0));
        }

        Assert.Equal(hasEntry ? 1 : 0, result.Count);
        if (hasEntry)
        {
            Assert.Equal(entry.Id, Assert.Single(result).Id);
        }
    }

    [Fact]
    public void Project_AfterInputCollectionChanges_PreservesOriginalSnapshot()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var original = new GroupEntry(Guid.Parse("00000000-0000-0000-0000-000000000001"),
            scope, "Dummy Original");
        var replacement = new GroupEntry(Guid.Parse("00000000-0000-0000-0000-000000000002"),
            scope, "Dummy Replacement");
        var entries = new List<GroupEntry> { original };
        var expected = new GroupEntryDto(original.Id, "learning", GroupKind.Classroom, original.Name);

        var result = GroupScopeProjector.Project(entries, scope);
        entries[0] = replacement;
        entries.Add(original);
        entries.Reverse();

        Assert.Equal(expected, Assert.Single(result));
        entries.Clear();
        Assert.Equal(expected, Assert.Single(result));
    }

    [Fact]
    public void Project_RepeatedCallsWithDifferentScopes_ReturnsDeterministicResults()
    {
        var learning = new GroupModuleScope("learning", GroupKind.Classroom);
        var hobbies = new GroupModuleScope("hobbies", GroupKind.Club);
        var entries = new[]
        {
            new GroupEntry(Guid.Parse("00000000-0000-0000-0000-000000000001"), learning, "Dummy Classroom"),
            new GroupEntry(Guid.Parse("00000000-0000-0000-0000-000000000002"), hobbies, "Dummy Club")
        };
        var expectedLearning = new GroupEntryDto(entries[0].Id, "learning", GroupKind.Classroom, "Dummy Classroom");
        var expectedHobbies = new GroupEntryDto(entries[1].Id, "hobbies", GroupKind.Club, "Dummy Club");

        for (var repetition = 0; repetition < 5; repetition++)
        {
            Assert.Equal(expectedLearning, Assert.Single(GroupScopeProjector.Project(entries, learning)));
            Assert.Equal(expectedHobbies, Assert.Single(GroupScopeProjector.Project(entries, hobbies)));
        }
    }

    [Fact]
    public void Project_WithMixedEntries_LeavesSourceEntitiesAndCollectionUnchanged()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var entries = new List<GroupEntry>
        {
            new(Guid.Parse("00000000-0000-0000-0000-000000000002"), scope, "  Dummy Classroom  "),
            new(Guid.Parse("00000000-0000-0000-0000-000000000001"),
                new GroupModuleScope("social", GroupKind.Zone), "  Dummy Zone  ")
        };
        var originalReferences = entries.ToArray();
        var originalValues = entries.Select(entry =>
            (entry.Id, entry.Scope.Module, entry.Scope.GroupKind, entry.Name)).ToArray();
        var originalScopes = entries.Select(entry => entry.Scope).ToArray();

        GroupScopeProjector.Project(entries, scope);

        Assert.Equal(originalValues, entries.Select(entry =>
            (entry.Id, entry.Scope.Module, entry.Scope.GroupKind, entry.Name)));
        Assert.Equal(originalReferences.Length, entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            Assert.Same(originalReferences[index], entries[index]);
            Assert.Same(originalScopes[index], entries[index].Scope);
        }
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public void Project_WithSeparatelyConstructedValueEqualScope_MatchesEntry(
        string module, GroupKind groupKind)
    {
        var entryScope = new GroupModuleScope(module, groupKind);
        var targetScope = new GroupModuleScope(module, groupKind);
        var entry = new GroupEntry(Guid.Parse("00000000-0000-0000-0000-000000000001"),
            entryScope, "Dummy Group");
        Assert.NotSame(entryScope, targetScope);
        Assert.Equal(entryScope, targetScope);

        var result = GroupScopeProjector.Project(new[] { entry }, targetScope);

        Assert.Equal(new GroupEntryDto(entry.Id, module, groupKind, entry.Name), Assert.Single(result));
    }
}
