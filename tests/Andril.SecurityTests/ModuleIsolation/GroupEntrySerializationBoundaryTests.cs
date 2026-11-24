using System;
using System.Text.Json;
using Andril.Groups.DTOs;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.SecurityTests.ModuleIsolation;

/// <summary>
/// Security boundary and module isolation tests verifying that raw <see cref="GroupEntry"/> domain entities
/// enforce transport serialization privacy, conceal private identifiers and group names in diagnostics,
/// preserve internal field access for authorized business operations, maintain module-scope isolation across
/// Learning, Hobbies, and Social, and ensure that DTO projections function correctly without conveying authorization grants (CF-07 / CF-11).
/// </summary>
public sealed class GroupEntrySerializationBoundaryTests
{
    private static readonly Guid LearningGroupId = Guid.Parse("11111111-aaaa-1111-aaaa-111111111111");
    private static readonly Guid HobbiesGroupId = Guid.Parse("22222222-bbbb-2222-bbbb-222222222222");
    private static readonly Guid SocialGroupId = Guid.Parse("33333333-cccc-3333-cccc-333333333333");

    private const string LearningGroupName = "Advanced Algorithms Classroom";
    private const string HobbiesGroupName = "Robotics & Hardware Club";
    private const string SocialGroupName = "City Central Social Zone";

    #region 1. Raw Entity Transport Serialization Privacy

    [Theory]
    [InlineData("learning", GroupKind.Classroom, "11111111-aaaa-1111-aaaa-111111111111", LearningGroupName)]
    [InlineData("hobbies", GroupKind.Club, "22222222-bbbb-2222-bbbb-222222222222", HobbiesGroupName)]
    [InlineData("social", GroupKind.Zone, "33333333-cccc-3333-cccc-333333333333", SocialGroupName)]
    public void RawGroupEntrySerialization_ConcealsAllPropertiesAndIdentities(
        string module,
        GroupKind groupKind,
        string groupIdString,
        string groupName)
    {
        var groupId = Guid.Parse(groupIdString);
        var scope = new GroupModuleScope(module, groupKind);
        var entry = new GroupEntry(groupId, scope, groupName);

        var json = JsonSerializer.Serialize(entry);

        // Security boundary: Default JSON serialization of raw domain entity must not expose any data
        Assert.Equal("{}", json);
        Assert.DoesNotContain(groupIdString, json);
        Assert.DoesNotContain(groupName, json);
        Assert.DoesNotContain(module, json);
        Assert.DoesNotContain(groupKind.ToString(), json);
        Assert.DoesNotContain("Id", json);
        Assert.DoesNotContain("Name", json);
        Assert.DoesNotContain("Scope", json);
    }

    #endregion

    #region 2. Diagnostic String Concealment

    [Theory]
    [InlineData("learning", GroupKind.Classroom, "11111111-aaaa-1111-aaaa-111111111111", LearningGroupName)]
    [InlineData("hobbies", GroupKind.Club, "22222222-bbbb-2222-bbbb-222222222222", HobbiesGroupName)]
    [InlineData("social", GroupKind.Zone, "33333333-cccc-3333-cccc-333333333333", SocialGroupName)]
    public void ToString_ConcealsPrivateIdentifiersAndGroupNames(
        string module,
        GroupKind groupKind,
        string groupIdString,
        string groupName)
    {
        var groupId = Guid.Parse(groupIdString);
        var scope = new GroupModuleScope(module, groupKind);
        var entry = new GroupEntry(groupId, scope, groupName);

        var diagnostic = entry.ToString();

        Assert.Equal("GroupEntry [private]", diagnostic);
        Assert.DoesNotContain(groupIdString, diagnostic);
        Assert.DoesNotContain(groupName, diagnostic);
    }

    #endregion

    #region 3. Internal C# Business Access & Field Retention

    [Fact]
    public void InternalAccess_PreservesAllDomainFieldsAcrossAllModules()
    {
        var learningScope = new GroupModuleScope("learning", GroupKind.Classroom);
        var hobbiesScope = new GroupModuleScope("hobbies", GroupKind.Club);
        var socialScope = new GroupModuleScope("social", GroupKind.Zone);

        var learningEntry = new GroupEntry(LearningGroupId, learningScope, LearningGroupName);
        var hobbiesEntry = new GroupEntry(HobbiesGroupId, hobbiesScope, HobbiesGroupName);
        var socialEntry = new GroupEntry(SocialGroupId, socialScope, SocialGroupName);

        // Learning Classroom verification
        Assert.Equal(LearningGroupId, learningEntry.Id);
        Assert.Equal(learningScope, learningEntry.Scope);
        Assert.Equal("learning", learningEntry.Scope.Module);
        Assert.Equal(GroupKind.Classroom, learningEntry.Scope.GroupKind);
        Assert.Equal(LearningGroupName, learningEntry.Name);

        // Hobbies Club verification
        Assert.Equal(HobbiesGroupId, hobbiesEntry.Id);
        Assert.Equal(hobbiesScope, hobbiesEntry.Scope);
        Assert.Equal("hobbies", hobbiesEntry.Scope.Module);
        Assert.Equal(GroupKind.Club, hobbiesEntry.Scope.GroupKind);
        Assert.Equal(HobbiesGroupName, hobbiesEntry.Name);

        // Social Zone verification
        Assert.Equal(SocialGroupId, socialEntry.Id);
        Assert.Equal(socialScope, socialEntry.Scope);
        Assert.Equal("social", socialEntry.Scope.Module);
        Assert.Equal(GroupKind.Zone, socialEntry.Scope.GroupKind);
        Assert.Equal(SocialGroupName, socialEntry.Name);
    }

    #endregion

    #region 4. Module Isolation & Scope Compatibility Rules

    [Theory]
    [InlineData("learning", GroupKind.Club)]
    [InlineData("learning", GroupKind.Zone)]
    [InlineData("hobbies", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Zone)]
    [InlineData("social", GroupKind.Classroom)]
    [InlineData("social", GroupKind.Club)]
    [InlineData("Learning", GroupKind.Classroom)]
    [InlineData("Hobbies", GroupKind.Club)]
    [InlineData("Social", GroupKind.Zone)]
    [InlineData("OtherModule", GroupKind.Classroom)]
    public void CrossModuleScope_IncompatibleCombinations_ThrowArgumentException(string module, GroupKind groupKind)
    {
        Assert.Throws<ArgumentException>(() => new GroupModuleScope(module, groupKind));
    }

    [Fact]
    public void Constructor_InvalidArguments_EnforceDomainValidation()
    {
        var validScope = new GroupModuleScope("learning", GroupKind.Classroom);

        Assert.Throws<ArgumentException>(() => new GroupEntry(Guid.Empty, validScope, "Valid Name"));
        Assert.Throws<ArgumentNullException>(() => new GroupEntry(LearningGroupId, null!, "Valid Name"));
        Assert.Throws<ArgumentException>(() => new GroupEntry(LearningGroupId, validScope, ""));
        Assert.Throws<ArgumentException>(() => new GroupEntry(LearningGroupId, validScope, "   "));
    }

    #endregion

    #region 5. DTO Mapping & Non-Authorization Invariants

    [Fact]
    public void DtoMapping_FromEntry_FunctionsCorrectlyWithoutConveyingAuthorization()
    {
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var entry = new GroupEntry(HobbiesGroupId, scope, HobbiesGroupName);

        // Authorized internal projection mapping
        var dto = GroupEntryDto.FromEntry(entry);

        Assert.Equal(HobbiesGroupId, dto.Id);
        Assert.Equal("hobbies", dto.Module);
        Assert.Equal(GroupKind.Club, dto.GroupKind);
        Assert.Equal(HobbiesGroupName, dto.Name);

        // Security invariant: DTO is purely a read model projection and conveys no membership or authorization grant
        Assert.DoesNotContain("Authorization", dto.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Token", dto.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Permission", dto.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
