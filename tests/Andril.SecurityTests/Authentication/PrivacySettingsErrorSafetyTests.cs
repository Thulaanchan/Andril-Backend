using Andril.API.Middleware;
using Andril.Common.Accounts.Privacy;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Andril.SecurityTests.Authentication;

public sealed class PrivacySettingsErrorSafetyTests
{
    [Theory]
    [InlineData(false, 404)]
    [InlineData(true, 400)]
    public async Task ErrorResponsesDoNotEchoPrivateInputOrDependencyDetails(bool validation, int status)
    {
        var problems = new Problems();
        var handler = new PrivacyExceptionHandler(problems);
        var context = new DefaultHttpContext();
        const string privateDetail = "synthetic-private-input-and-internal-store-detail";
        Exception exception = validation ? new PrivacyValidationException(privateDetail) : new PrivacyNotFoundException(privateDetail);
        Assert.True(await handler.TryHandleAsync(context, exception, default));
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.NotNull(problems.Last);
        Assert.Null(problems.Last.ProblemDetails.Detail);
        Assert.DoesNotContain(privateDetail, problems.Last.ProblemDetails.Title!);
    }

    private sealed class Problems : IProblemDetailsService
    {
        public ProblemDetailsContext? Last;
        public ValueTask WriteAsync(ProblemDetailsContext context) { Last = context; return ValueTask.CompletedTask; }
        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) { Last = context; return ValueTask.FromResult(true); }
    }
}
