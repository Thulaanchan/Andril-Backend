using Andril.API.Extensions;
using Andril.API.Integrations.Learning;
using Andril.Groups.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Learning;

public sealed class LearningClassroomRuntimeRegistrationTests
{
    private static IConfiguration Configuration(bool enabled,bool persistence,bool learning)=>new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Learning:Classrooms:Enabled"]=enabled.ToString(),
            ["SharedGroups:EnablePersistence"]=persistence.ToString(),
            ["SharedGroups:EnableLearning"]=learning.ToString()
        }).Build();
    [Fact]
    public void DisabledByDefaultInstallsNoServicesOrImplicitAuthority()
    {
        var services=new ServiceCollection();services.AddVerifiedLearningClassrooms(new ConfigurationBuilder().Build());
        Assert.Empty(services);
    }
    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)]
    public void EnablingWithoutBothSharedPersistenceAndLearningScopeFailsAtComposition(bool persistence,bool learning)
    {
        var services=new ServiceCollection();
        Assert.Throws<InvalidOperationException>(()=>services.AddVerifiedLearningClassrooms(Configuration(true,persistence,learning)));
        Assert.Empty(services);
    }
    [Fact]
    public void EnabledCompositionIsScopedIdempotentAndRequiresActualGroupDependencies()
    {
        var services=new ServiceCollection();var configuration=Configuration(true,true,true);
        services.AddVerifiedLearningClassrooms(configuration);services.AddVerifiedLearningClassrooms(configuration);
        var service=Assert.Single(services,x=>x.ServiceType==typeof(LearningClassroomDiscoveryService));Assert.Equal(ServiceLifetime.Scoped,service.Lifetime);
        Assert.Equal(3,services.Count); // Enable marker, application and read-only source-readiness guard.
        Assert.DoesNotContain(services,x=>x.ServiceType==typeof(IGroupActorContext)||x.ServiceType==typeof(GroupDiscoveryService));
        Assert.DoesNotContain(services,x=>x.ServiceType.Namespace?.StartsWith("Andril.AI",StringComparison.Ordinal)==true);
        var readiness=Assert.Single(services,x=>x.ServiceType.FullName=="Microsoft.Extensions.Hosting.IHostedService");
        Assert.EndsWith("LearningClassroomReadiness",readiness.ImplementationType!.Name);
        using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();
        Assert.Throws<InvalidOperationException>(()=>scope.ServiceProvider.GetRequiredService<LearningClassroomDiscoveryService>());
    }
}
