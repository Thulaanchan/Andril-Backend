using System;
using System.Text.Json;
using Andril.Groups.Entities;
using Andril.Groups.JoinRequests;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupJoinRequestTests
{
    private static readonly Guid ValidGroupId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ValidProfileId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime ValidUtcTime = new(2026, 11, 23, 10, 30, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public void Constructor_WithValidParameters_InitializesCorrectly(string module, GroupKind groupKind)
    {
        var scope = new GroupModuleScope(module, groupKind);
        var request = new GroupJoinRequest(ValidGroupId, scope, ValidProfileId, ValidUtcTime);

        Assert.Equal(ValidGroupId, request.GroupId);
        Assert.Equal(scope, request.Scope);
        Assert.Equal(ValidProfileId, request.RequestingProfileId);
        Assert.Equal(ValidUtcTime, request.RequestedAtUtc);
    }

    [Fact]
    public void Create_WithExplicitTimestamp_CreatesExpectedInstance()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var request = GroupJoinRequest.Create(ValidGroupId, scope, ValidProfileId, ValidUtcTime);

        Assert.Equal(ValidGroupId, request.GroupId);
        Assert.Equal(scope, request.Scope);
        Assert.Equal(ValidProfileId, request.RequestingProfileId);
        Assert.Equal(ValidUtcTime, request.RequestedAtUtc);
    }

    [Fact]
    public void Create_WithCurrentTimestamp_SetsUtcKind()
    {
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var before = DateTime.UtcNow;
        var request = GroupJoinRequest.Create(ValidGroupId, scope, ValidProfileId);
        var after = DateTime.UtcNow;

        Assert.Equal(DateTimeKind.Utc, request.RequestedAtUtc.Kind);
        Assert.InRange(request.RequestedAtUtc, before, after);
    }

    [Fact]
    public void Constructor_EmptyGroupId_ThrowsArgumentException()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var ex = Assert.Throws<ArgumentException>(
            () => new GroupJoinRequest(Guid.Empty, scope, ValidProfileId, ValidUtcTime));
        Assert.Equal("groupId", ex.ParamName);
    }

    [Fact]
    public void Constructor_NullScope_ThrowsArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(
            () => new GroupJoinRequest(ValidGroupId, null!, ValidProfileId, ValidUtcTime));
        Assert.Equal("scope", ex.ParamName);
    }

    [Fact]
    public void Constructor_EmptyRequestingProfileId_ThrowsArgumentException()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var ex = Assert.Throws<ArgumentException>(
            () => new GroupJoinRequest(ValidGroupId, scope, Guid.Empty, ValidUtcTime));
        Assert.Equal("requestingProfileId", ex.ParamName);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Constructor_NonUtcTimestamp_ThrowsArgumentException(DateTimeKind kind)
    {
        var nonUtc = new DateTime(2026, 11, 23, 10, 30, 0, kind);
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var ex = Assert.Throws<ArgumentException>(
            () => new GroupJoinRequest(ValidGroupId, scope, ValidProfileId, nonUtc));
        Assert.Equal("requestedAtUtc", ex.ParamName);
        Assert.Contains("UTC DateTimeKind", ex.Message);
    }

    [Fact]
    public void Constructor_DefaultTimestamp_ThrowsArgumentException()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var ex = Assert.Throws<ArgumentException>(
            () => new GroupJoinRequest(ValidGroupId, scope, ValidProfileId, default));
        Assert.Equal("requestedAtUtc", ex.ParamName);
    }

    [Fact]
    public void Equals_StructuralEquality_VerifiesCorrectly()
    {
        var scope1 = new GroupModuleScope("learning", GroupKind.Classroom);
        var scope2 = new GroupModuleScope("learning", GroupKind.Classroom);
        var otherScope = new GroupModuleScope("hobbies", GroupKind.Club);

        var a = new GroupJoinRequest(ValidGroupId, scope1, ValidProfileId, ValidUtcTime);
        var b = new GroupJoinRequest(ValidGroupId, scope2, ValidProfileId, ValidUtcTime);
        var cDifferentGroup = new GroupJoinRequest(Guid.NewGuid(), scope1, ValidProfileId, ValidUtcTime);
        var cDifferentProfile = new GroupJoinRequest(ValidGroupId, scope1, Guid.NewGuid(), ValidUtcTime);
        var cDifferentTime = new GroupJoinRequest(ValidGroupId, scope1, ValidProfileId, ValidUtcTime.AddSeconds(1));
        var cDifferentScope = new GroupJoinRequest(ValidGroupId, otherScope, ValidProfileId, ValidUtcTime);

        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        Assert.False(a.Equals(cDifferentGroup));
        Assert.False(a.Equals(cDifferentProfile));
        Assert.False(a.Equals(cDifferentTime));
        Assert.False(a.Equals(cDifferentScope));
        Assert.False(a.Equals(null));
        Assert.False(a.Equals(new object()));
    }

    [Fact]
    public void ToString_ConcealsConfidentialIdentifiers()
    {
        var scope = new GroupModuleScope("social", GroupKind.Zone);
        var request = new GroupJoinRequest(ValidGroupId, scope, ValidProfileId, ValidUtcTime);

        var representation = request.ToString();

        Assert.Equal("GroupJoinRequest [social:Zone]", representation);
        Assert.DoesNotContain(ValidGroupId.ToString(), representation);
        Assert.DoesNotContain(ValidProfileId.ToString(), representation);
        Assert.DoesNotContain(ValidUtcTime.ToString(), representation);
    }

    [Fact]
    public void Serialization_SystemTextJson_ConcealsPrivateIdentifiers()
    {
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var request = new GroupJoinRequest(ValidGroupId, scope, ValidProfileId, ValidUtcTime);

        var json = JsonSerializer.Serialize(request);

        Assert.DoesNotContain(ValidGroupId.ToString(), json);
        Assert.DoesNotContain(ValidProfileId.ToString(), json);
        Assert.DoesNotContain(ValidUtcTime.ToString("o"), json);
    }
}
