using Andril.API.Extensions;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Diary;
using Andril.Learning.Portfolio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Learning;

public sealed class LearningPrivateDocumentRuntimeRegistrationTests
{
    private static IConfiguration Configuration(bool diary,string? role)=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    {{"Learning:"+(diary?"PrivateDiary":"PrivatePortfolios")+":Enabled","true"},{"Learning:"+(diary?"PrivateDiary":"PrivatePortfolios")+":RuntimeRole",role}}).Build();
    private static void Register(IServiceCollection services,bool diary,IConfiguration configuration)
    {if(diary)services.AddVerifiedLearningPrivateDiary(configuration);else services.AddVerifiedLearningPrivatePortfolios(configuration);}
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitActivationWithoutRestrictedRoleIsRejected(bool diary)
    {var services=new ServiceCollection();Assert.Throws<InvalidOperationException>(()=>Register(services,diary,Configuration(diary,null)));Assert.Empty(services);}
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistrationCannotManufactureCurrentIdentityModuleAccessOrProfileAuthority(bool diary)
    {
        var services=new ServiceCollection();Register(services,diary,Configuration(diary,"metadata_only"));
        var contract=diary?typeof(IModuleDiaryRepository):typeof(ILearningPortfolioRepository);
        Assert.Equal(ServiceLifetime.Scoped,Assert.Single(services,s=>s.ServiceType==contract).Lifetime);
        Assert.DoesNotContain(services,s=>s.ServiceType==typeof(IPrivateProfileService)||s.ServiceType==typeof(IPrivateModuleAccess));
        using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();Assert.Throws<InvalidOperationException>(()=>scope.ServiceProvider.GetRequiredService(contract));
    }
}
