using System.Text.Json;
using Andril.API.Controllers.Learning;
using Andril.API.Extensions;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.CV;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Learning;

public sealed class LearningApiAuditTests
{
    private static IConfiguration Config(bool cv=true,bool portfolio=true,string? role="metadata_only")=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
    {{"Learning:ProjectImports:Enabled","true"},{"Learning:PrivateCv:Enabled",cv.ToString()},{"Learning:PrivatePortfolios:Enabled",portfolio.ToString()},{"Learning:ProjectImports:RuntimeRole",role},{"Learning:PrivateCv:RuntimeRole",role}}).Build();
    [Fact]
    public void ImportsRemainDefaultDisabledWithoutInstallingAuthorities()
    {var services=new ServiceCollection();services.AddVerifiedLearningProjectImports(new ConfigurationBuilder().Build());Assert.Empty(services);}
    [Theory]
    [InlineData(false,true,"restricted")]
    [InlineData(true,false,"restricted")]
    [InlineData(true,true,null)]
    public void ImportsRequireBothPrivateStoresAndAnExplicitRestrictedRole(bool cv,bool portfolio,string? role)
    {var services=new ServiceCollection();Assert.Throws<InvalidOperationException>(()=>services.AddVerifiedLearningProjectImports(Config(cv,portfolio,role)));Assert.Empty(services);}
    [Fact]
    public void ImportRegistrationCannotFabricateM2Authority()
    {
        var services=new ServiceCollection();services.AddVerifiedLearningProjectImports(Config());
        Assert.Equal(ServiceLifetime.Scoped,Assert.Single(services,x=>x.ServiceType==typeof(ILearningProjectImportRepository)).Lifetime);
        Assert.DoesNotContain(services,x=>x.ServiceType==typeof(IPrivateProfileService)||x.ServiceType==typeof(IPrivateModuleAccess));
        using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();Assert.Throws<InvalidOperationException>(()=>scope.ServiceProvider.GetRequiredService<ILearningProjectImportRepository>());
    }
    [Fact]
    public async Task EnabledCvAndImportOpenApiDefinesActualBodiesResponsesAndBearerSecurity()
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Testing"});builder.WebHost.UseTestServer();builder.Configuration.AddConfiguration(Config());
        builder.Services.AddAuthorization();builder.Services.AddVerifiedLearningPrivateCv(builder.Configuration);builder.Services.AddVerifiedLearningProjectImports(builder.Configuration);builder.Services.AddControlledLearningSetupOpenApi();
        await using var app=builder.Build();app.MapVerifiedLearningPrivateCv();app.MapVerifiedLearningProjectImports();app.MapOpenApi();await app.StartAsync();
        using var http=app.GetTestClient();using var document=JsonDocument.Parse(await http.GetStringAsync("/openapi/v1.json"));var root=document.RootElement;var paths=root.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/learning/cvs/{id}").TryGetProperty("get",out var open));Assert.True(open.GetProperty("responses").TryGetProperty("200",out var response));Assert.True(response.GetProperty("content").TryGetProperty("application/json",out _));
        Assert.True(paths.GetProperty("/api/learning/cvs/{id}/template").GetProperty("put").TryGetProperty("requestBody",out _));
        Assert.True(paths.GetProperty("/api/learning/cvs/{id}/sections/{category}/structured").GetProperty("post").TryGetProperty("requestBody",out _));
        Assert.True(paths.GetProperty("/api/learning/cvs/{id}/pdf").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("application/pdf",out _));
        foreach(var path in new[]{"/api/v1/learning/cv-imports/reviews","/api/v1/learning/cv-imports/{reviewId}/confirm"})Assert.True(paths.GetProperty(path).GetProperty("post").TryGetProperty("requestBody",out _));
        Assert.True(root.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("supabaseBearer",out _));Assert.NotEmpty(root.GetProperty("security").EnumerateArray());
        Assert.DoesNotContain(paths.EnumerateObject(),p=>p.Name.Contains("cleanup",StringComparison.OrdinalIgnoreCase)||p.Name.Contains("ai",StringComparison.OrdinalIgnoreCase)||p.Name.Contains("xp/award",StringComparison.OrdinalIgnoreCase));
    }
}
