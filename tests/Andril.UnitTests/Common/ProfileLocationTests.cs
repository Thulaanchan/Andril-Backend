using Andril.Common.Accounts.Profiles;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class ProfileLocationTests
{
    [Fact]
    public void PS_T19_ApprovedLocationsCatalog_ContainsValidCities()
    {
        var locations = ApprovedLocationsCatalog.GetApprovedLocations();

        Assert.NotEmpty(locations);
        Assert.Contains(locations, l => l.City == "Colombo" && l.Country == "Sri Lanka");
        Assert.Contains(locations, l => l.City == "Kandy");
        Assert.Contains(locations, l => l.City == "Galle");
        Assert.Contains(locations, l => l.City == "Jaffna");
    }

    [Fact]
    public void PS_T20_IsValidLocation_ChecksCityCorrectly()
    {
        Assert.True(ApprovedLocationsCatalog.IsValidLocation("Colombo"));
        Assert.True(ApprovedLocationsCatalog.IsValidLocation("colombo"));
        Assert.True(ApprovedLocationsCatalog.IsValidLocation(" Kandy "));
        Assert.False(ApprovedLocationsCatalog.IsValidLocation("Atlantis"));
        Assert.False(ApprovedLocationsCatalog.IsValidLocation(""));
        Assert.False(ApprovedLocationsCatalog.IsValidLocation(null));
    }

    [Fact]
    public void PS_T21_ProfileLocationRecord_CreateAndUpdate_EnforcesInvariants()
    {
        var profileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var record = ProfileLocationRecord.Create(profileId, accountId, "learning", "Colombo", "Western Province", now);

        Assert.Equal(profileId, record.ProfileId);
        Assert.Equal(accountId, record.AccountId);
        Assert.Equal("learning", record.Module);
        Assert.Equal("Colombo", record.City);
        Assert.Equal("Western Province", record.Region);

        var updateTime = now.AddMinutes(5);
        record.Update("Kandy", "Central Province", updateTime);

        Assert.Equal("Kandy", record.City);
        Assert.Equal("Central Province", record.Region);
        Assert.Equal(updateTime, record.UpdatedAtUtc);
    }
}
