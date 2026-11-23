using System.Reflection;
using Andril.API.Authorization.Social;
using Andril.API.Controllers.Social;
using Andril.Social.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Andril.SecurityTests.Authorization.Social;

public sealed class SocialQuestionnaireDefinitionsControllerTests
{
    [Fact]
    public void Controller_HasExpectedAttributes()
    {
        var type = typeof(SocialQuestionnaireDefinitionsController);

        Assert.NotNull(type.GetCustomAttribute<NonControllerAttribute>());
        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());

        var routeAttr = type.GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(routeAttr);
        Assert.Equal("api/v1/social/questionnaires", routeAttr.Template);
    }

    [Fact]
    public void GetActiveDefinition_HasExpectedActionAttributes()
    {
        var method = typeof(SocialQuestionnaireDefinitionsController).GetMethod(nameof(SocialQuestionnaireDefinitionsController.GetActiveDefinition));
        Assert.NotNull(method);

        var httpGet = method.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(httpGet);
        Assert.Equal("active", httpGet.Template);

        var cacheAttr = method.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.NotNull(cacheAttr);
        Assert.True(cacheAttr.NoStore);
        Assert.Equal(ResponseCacheLocation.None, cacheAttr.Location);
    }

    [Fact]
    public async Task GetActiveDefinition_ReturnsOkResultWithDefinition()
    {
        var sampleDto = new QuestionnaireDefinitionDto("v1", [
            new("q01", "Prompt", true, [new("1", "Option")])
        ]);
        var mockAccessService = new StubDefinitionAccessService(sampleDto);
        var controller = new SocialQuestionnaireDefinitionsController(mockAccessService);

        var actionResult = await controller.GetActiveDefinition(CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var returnedDto = Assert.IsType<QuestionnaireDefinitionDto>(okResult.Value);

        Assert.Equal("v1", returnedDto.Version);
        Assert.Single(returnedDto.Questions);
    }

    [Fact]
    public async Task GetActiveDefinition_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var mockAccessService = new StubDefinitionAccessService(new QuestionnaireDefinitionDto("v1", []));
        var controller = new SocialQuestionnaireDefinitionsController(mockAccessService);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.GetActiveDefinition(cts.Token));
    }

    private sealed class StubDefinitionAccessService(QuestionnaireDefinitionDto dto) : ISocialQuestionnaireDefinitionAccessService
    {
        public Task<QuestionnaireDefinitionDto> GetActiveDefinitionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(dto);
        }
    }
}
