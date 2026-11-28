using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Xunit;

namespace Andril.UnitTests.Groups;

public sealed class GroupLimitPolicyTests
{
    [Theory]
    [InlineData("learning", GroupKind.Classroom)]
    [InlineData("hobbies", GroupKind.Club)]
    [InlineData("social", GroupKind.Zone)]
    public void ApprovedLimitsApplyPerModuleAndDowngradeUsesFreeLimits(string module, GroupKind kind)
    {
        var scope = new GroupModuleScope(module, kind);
        foreach (var standing in new[] { GroupEntitlementStanding.Free, GroupEntitlementStanding.Downgraded })
        {
            var limits = Assert.IsType<GroupLimits>(GroupLimitPolicy.Resolve(scope, standing));
            Assert.Equal(5, limits.MaximumActiveCreatedGroupsPerModule);
            Assert.Equal(50, limits.MembershipCapacity);
            Assert.True(limits.CanCreate(4));
            Assert.False(limits.CanCreate(5));
            Assert.True(limits.CanAdmit(49));
            Assert.False(limits.CanAdmit(50));
        }
        var premium = Assert.IsType<GroupLimits>(GroupLimitPolicy.Resolve(scope, GroupEntitlementStanding.Premium));
        Assert.Null(premium.MaximumActiveCreatedGroupsPerModule);
        Assert.True(premium.CanCreate(int.MaxValue));
        Assert.False(premium.CanCreate(-1));
        Assert.True(premium.CanAdmit(99));
        Assert.False(premium.CanAdmit(100));
        Assert.False(premium.CanAdmit(-1));
        Assert.Null(GroupLimitPolicy.Resolve(scope, GroupEntitlementStanding.Unknown));
        Assert.Null(GroupLimitPolicy.Resolve(scope, (GroupEntitlementStanding)99));
        Assert.Null(GroupLimitPolicy.Resolve(null, GroupEntitlementStanding.Free));
    }
}
