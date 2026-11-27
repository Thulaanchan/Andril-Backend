using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Andril.API.Extensions;
using Andril.Learning.CV;
using Andril.Learning.Progress;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andril.IntegrationTests.Database;

public sealed class LearningGroupCHttpPostgresTests
{
    [LocalPostgresFact]
    public async Task UnicodePdfHttpUsesEmbeddedFontAndStillRequiresCurrentOwner()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();var cv=(await f.Cv.Service.CreateAsync(CvTemplate.Professional)).Cv!;
        var section=cv.GetSection(CvSectionCategory.Projects);
        await f.Cv.Repository().SaveAsync(cv.Id,CvSaveChange.ReviewedFields(section.Category,[CvStructuredFields.ReviewedEntry(Guid.NewGuid(),section.Id,section.Category,new Dictionary<string,string>{{"ProjectTitle","Éducation Ελληνικά Привет"}})]));
        await using var app=await Start(f);using var client=Client(app);
        var response=await client.GetAsync($"/api/learning/cvs/{cv.Id}/pdf");Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var pdf=Encoding.Latin1.GetString(await response.Content.ReadAsByteArrayAsync());Assert.Contains("/FontFile2",pdf);Assert.Contains("/ToUnicode",pdf);Assert.DoesNotContain("/URI",pdf);
        f.Cv.Owner.Denied=true;Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync($"/api/learning/cvs/{cv.Id}/pdf")).StatusCode);
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder)
        :AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()=>Task.FromResult(Request.Headers.Authorization.ToString()!="Fixture" ? AuthenticateResult.NoResult():
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,"isolated-group-c-owner")],Scheme.Name)),Scheme.Name)));
    }
    private static async Task<WebApplication> Start(LearningGroupCPostgresFixture f)
    {
        var b=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Production"});b.WebHost.UseTestServer();
        b.Services.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions,Authentication>("Fixture",_=>{});b.Services.AddAuthorization();
        b.Services.AddSingleton<ILearningXpRepository>(f.Repository());b.Services.AddSingleton<ILearningCvRepository>(f.Cv.Repository());
        var app=b.Build();app.UseAuthentication();app.UseAuthorization();app.MapControlledLearningXp();app.MapControlledStructuredCv();await app.StartAsync();return app;
    }
    private static HttpClient Client(WebApplication app){var client=app.GetTestClient();client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Fixture");return client;}
    [LocalPostgresFact]
    public async Task XpHttpIsOwnerReadOnlyAndExposesNoPublicRewardOrClientAmountRoute()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();await using var app=await Start(f);
        using(var anonymous=app.GetTestClient())Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/learning/xp")).StatusCode);
        using var client=Client(app);var empty=await client.GetAsync("/api/learning/xp");Assert.Equal(HttpStatusCode.OK,empty.StatusCode);
        await f.ConfigureSyntheticPolicy();Assert.Equal(LearningXpOutcome.Credited,(await f.Repository().CreditAsync(f.VerifiedSource())).Outcome);
        var read=await client.GetAsync("/api/learning/xp?limit=1");Assert.Equal(HttpStatusCode.OK,read.StatusCode);using var json=JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(7,json.RootElement.GetProperty("total").GetInt64());Assert.DoesNotContain(f.Cv.Owner.Account.ToString(),json.RootElement.GetRawText());
        Assert.Equal(HttpStatusCode.MethodNotAllowed,(await client.PostAsJsonAsync("/api/learning/xp",new{amount=999,verified=true})).StatusCode);
        f.Authority.Denied=true;Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/learning/xp")).StatusCode);
        f.Authority.Denied=false;f.Authority.Unavailable=true;var unavailable=await client.GetAsync("/api/learning/xp");Assert.Equal(HttpStatusCode.ServiceUnavailable,unavailable.StatusCode);
        Assert.DoesNotContain("PRIVATE_XP_SOURCE_FAILURE",await unavailable.Content.ReadAsStringAsync());
    }
    [LocalPostgresFact]
    public async Task StructuredCvHttpPersistsReviewedFieldsAndReleasesOnlyCurrentConsentedPrivateOutput()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();var cv=(await f.Cv.Service.CreateAsync(CvTemplate.Creative)).Cv!;await using var app=await Start(f);using var client=Client(app);
        var payload=new ReviewedCvSectionRequest([new(Guid.NewGuid(),new Dictionary<string,string>{{"ProfessionalDisplayName","<script>inert name</script>"},{"Email","private@example.test"}})]);
        var save=await client.PostAsJsonAsync($"/api/learning/cvs/{cv.Id}/sections/PersonalInformation/structured",payload);Assert.Equal(HttpStatusCode.OK,save.StatusCode);
        var html=await client.GetStringAsync($"/api/learning/cvs/{cv.Id}/preview");Assert.DoesNotContain("<script>",html);Assert.Contains("inert name",html);Assert.DoesNotContain("private@example.test",html);
        await f.Cv.Service.SetContactConsentAsync(cv.Id,true);var pdf=await client.GetAsync($"/api/learning/cvs/{cv.Id}/pdf");Assert.Equal(HttpStatusCode.OK,pdf.StatusCode);Assert.Equal("application/pdf",pdf.Content.Headers.ContentType!.MediaType);
        Assert.Contains("private@example.test",Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync()));
        await f.Cv.Service.SetContactConsentAsync(cv.Id,false);Assert.DoesNotContain("private@example.test",Encoding.ASCII.GetString(await client.GetByteArrayAsync($"/api/learning/cvs/{cv.Id}/pdf")));
        f.Cv.Owner.Denied=true;Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync($"/api/learning/cvs/{cv.Id}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync($"/api/learning/cvs/{cv.Id}/pdf")).StatusCode);
    }
    [LocalPostgresFact]
    public async Task CvHttpRejectsUnapprovedFieldsClientIdentityAndUnsafeLinksBeforeSaving()
    {
        await using var f=await LearningGroupCPostgresFixture.Create();var cv=(await f.Cv.Service.CreateAsync(CvTemplate.Modern)).Cv!;await using var app=await Start(f);using var client=Client(app);
        foreach(var fields in new[]{new Dictionary<string,string>{{"Password","private"}},new Dictionary<string,string>{{"ProfessionalProfileLinks","javascript:alert(1)"}},new Dictionary<string,string>{{"Email","invalid"}}})
            Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync($"/api/learning/cvs/{cv.Id}/sections/PersonalInformation/structured",new ReviewedCvSectionRequest([new(Guid.NewGuid(),fields)]))).StatusCode);
        using var body=new StringContent("{\"accountId\":\""+Guid.NewGuid()+"\",\"entries\":[]}",Encoding.UTF8,"application/json");
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync($"/api/learning/cvs/{cv.Id}/sections/PersonalInformation/structured",body)).StatusCode);
        Assert.Empty((await f.Cv.Service.ReopenAsync(cv.Id)).Cv!.GetSection(CvSectionCategory.PersonalInformation).Entries);
    }
}
