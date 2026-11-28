using Andril.API.Extensions;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.CV;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Learning;

public sealed class LearningPrivateCvRuntimeRegistrationTests
{
    [Fact]
    public void OrdinaryStartupDoesNotActivatePrivateCvPersistenceOrReplaceAuthorities()
    {
        var services=new ServiceCollection();services.AddVerifiedLearningPrivateCv(new ConfigurationBuilder().Build());
        Assert.Empty(services);
    }
    [Fact]
    public void ExplicitActivationWithoutRestrictedRoleFailsClosed()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Learning:PrivateCv:Enabled","true"}}).Build();
        Assert.Throws<InvalidOperationException>(()=>new ServiceCollection().AddVerifiedLearningPrivateCv(config));
    }
    [Fact]
    public void PrivateCvRegistrationRequiresRealScopedAuthoritiesInsteadOfInstallingPermissiveDefaults()
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Learning:PrivateCv:Enabled","true"},{"Learning:PrivateCv:RuntimeRole","approved_runtime"}}).Build();
        var services=new ServiceCollection();services.AddVerifiedLearningPrivateCv(config);
        Assert.Equal(ServiceLifetime.Scoped,Assert.Single(services,s=>s.ServiceType==typeof(ILearningCvRepository)).Lifetime);
        Assert.DoesNotContain(services,s=>s.ServiceType==typeof(IPrivateProfileService)||s.ServiceType==typeof(IPrivateModuleAccess));
        using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();
        Assert.Throws<InvalidOperationException>(()=>scope.ServiceProvider.GetRequiredService<ILearningCvRepository>());
    }
}
