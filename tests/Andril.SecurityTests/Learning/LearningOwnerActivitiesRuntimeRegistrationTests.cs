using Andril.API.Extensions;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Activities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Learning;

public sealed class LearningOwnerActivitiesRuntimeRegistrationTests
{
    [Fact]
    public void PrivateOwnerRegistrationDoesNotInstallPeerOrPermissiveM2Authorities()
    {
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {["Learning:OwnerActivities:Enabled"]="true",["Learning:OwnerActivities:RuntimeRole"]="approved_owner_runtime"}).Build();
        var services=new ServiceCollection();services.AddVerifiedLearningOwnerActivities(configuration);
        Assert.Equal(ServiceLifetime.Scoped,Assert.Single(services,s=>s.ServiceType==typeof(ILearningActivitiesRepository)).Lifetime);
        Assert.DoesNotContain(services,s=>s.ServiceType==typeof(ILearningActivityAuthority)||s.ServiceType==typeof(IPrivateProfileService)||s.ServiceType==typeof(IPrivateModuleAccess));
        using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();Assert.Throws<InvalidOperationException>(()=>scope.ServiceProvider.GetRequiredService<ILearningActivitiesRepository>());
    }
    [Fact]
    public void MissingRestrictedRoleCannotActivatePrivateOwnerRuntime()
    {
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Learning:OwnerActivities:Enabled"]="true"}).Build();
        Assert.Throws<InvalidOperationException>(()=>new ServiceCollection().AddVerifiedLearningOwnerActivities(configuration));
    }
}
