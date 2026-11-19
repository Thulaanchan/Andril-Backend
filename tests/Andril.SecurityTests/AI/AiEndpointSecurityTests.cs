using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Services;
using Andril.API.Controllers.AI;
using Andril.API.Extensions;
using Andril.API.Integrations.AI;
using Andril.API.Middleware;
using Andril.Common.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Andril.SecurityTests.AI;

/// <summary>Direct dormant-controller calls, real MVC discovery and isolated routing only.
/// Never run Program, activate AI, query a database, or authenticate using client actor claims.</summary>
public sealed class AiEndpointSecurityTests
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
    private const string Secret = "private-prompt-assistant-sk_synthetic-Host=private;Password=secret";
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Fake : IAiActorAuthorization, IAiPrivacyNoticeGate, IAiConversationRepository, IAiOperationRepository
    {
        internal AiAuthorizedActor? Actor = new(Id(1), Id(2), AiModule.Learning);
        internal int AuthCalls, Reads, Deletes, ForbiddenWrites, Reconciles;
        internal AiAuthorizedActor? LastActor;
        internal Action? AfterRead;
        internal AiStoredConversation? Conversation = new(Id(3), Id(1), Id(2), AiModule.Learning, Now.AddDays(-2), Now.AddDays(-1), Now.AddDays(13));
        internal IReadOnlyList<AiStoredConversation>? List;
        internal IReadOnlyList<AiStoredAiMessage> Messages = [new(Id(4), Id(3), AiStoredMessageRole.User, Secret, Now.AddHours(-1))];
        internal AiOperationStatus Status = AiOperationStatus.Reserved;
        internal bool DenyStatus;
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken t = default)
        { t.ThrowIfCancellationRequested(); AuthCalls++; return Task.FromResult(Actor); }
        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken t = default) =>
            Task.FromResult(AiPrivacyNoticeGateOutcome.Satisfied);
        public Task<AiStoredConversation?> FindAsync(AiAuthorizedActor actor, Guid id, CancellationToken t = default)
        { t.ThrowIfCancellationRequested(); Reads++; LastActor = actor; return Task.FromResult(Conversation); }
        public Task<IReadOnlyList<AiStoredConversation>> ListAsync(AiAuthorizedActor actor, int limit, CancellationToken t = default)
        { t.ThrowIfCancellationRequested(); Reads++; LastActor = actor; AfterRead?.Invoke(); return Task.FromResult(List ?? new[] { Conversation! }); }
        public Task<IReadOnlyList<AiStoredAiMessage>> ReadRecentMessagesAsync(AiAuthorizedActor actor, Guid id, int limit, CancellationToken t = default)
        { t.ThrowIfCancellationRequested(); Reads++; LastActor = actor; AfterRead?.Invoke(); return Task.FromResult(Messages); }
        public Task DeleteAsync(AiAuthorizedActor actor, Guid id, CancellationToken t = default)
        { t.ThrowIfCancellationRequested(); Deletes++; LastActor = actor; Conversation = null; return Task.CompletedTask; }
        private Exception Forbidden() { ForbiddenWrites++; return new InvalidOperationException("Forbidden synthetic write"); }
        public Task<AiStoredConversation> CreateWithUserMessageAsync(AiAuthorizedActor a, Guid o, string text, CancellationToken t = default) => throw Forbidden();
        public Task<AiStoredAiMessage> AppendUserMessageAsync(AiAuthorizedActor a, Guid c, Guid o, string text, CancellationToken t = default) => throw Forbidden();
        public Task<AiStoredAiMessage> AppendAssistantMessageAsync(AiAuthorizedActor a, Guid c, Guid o, string text, CancellationToken t = default) => throw Forbidden();
        public Task<AiOperationResult> AdmitAsync(AiAuthorizedActor a, AiOperationAdmissionRequest r, CancellationToken t = default) => throw Forbidden();
        public Task<AiOperationResult> PrepareManualRetryAsync(AiOperationReference o, Guid r, CancellationToken t = default) => throw Forbidden();
        public Task<AiOperationResult> ReconcileAsync(AiOperationReference o, CancellationToken t = default)
        {
            t.ThrowIfCancellationRequested(); Reconciles++;
            if (DenyStatus) return Task.FromResult(new AiOperationResult(AiOperationOutcome.Denied));
            var dispatch = Status switch
            { AiOperationStatus.Reserved => AiDispatchState.NotDispatched, AiOperationStatus.PendingReconciliation => AiDispatchState.Uncertain,
                AiOperationStatus.ConfirmedFailed => AiDispatchState.ConclusivelyFailed, _ => AiDispatchState.Confirmed };
            var quota = Status is AiOperationStatus.Completed or AiOperationStatus.Cancelled ? AiQuotaState.Committed : AiQuotaState.Reserved;
            return Task.FromResult(new AiOperationResult(AiOperationOutcome.AuthorizedStatus,
                new(o, Status, quota, dispatch, Now.AddDays(-1), DateOnly.FromDateTime(Now.AddDays(-1).UtcDateTime), Id(7), Id(8))));
        }
        internal AiChatController Controller() => new(new(this, this), new(this, new(this, this), this), this, new Clock());
    }

    [Fact]
    public async Task HistoryProjectionUsesOnlyServerActorAndExcludesPrivateScopeAndEvidence()
    {
        var f = new Fake(); var result = await f.Controller().ListAsync("learning", 100, default);
        var page = Assert.IsType<AiHistoryPage<AiConversationSummary>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(Id(3), Assert.Single(page.Items).ConversationId); Assert.Equal(f.Actor, f.LastActor);
        var json = JsonSerializer.Serialize(page, JsonSerializerOptions.Web);
        foreach (var forbidden in new[] { "accountId", "moduleProfileId", "attemptId", "pricing", "text" }) Assert.DoesNotContain(forbidden, json);
        Assert.Equal(0, f.ForbiddenWrites); Assert.Equal(0, f.Deletes);
    }

    [Theory]
    [InlineData("missing")] [InlineData("module")] [InlineData("empty-account")] [InlineData("empty-profile")]
    public async Task MissingOrInvalidScopedAuthorizationNeverReads(string defect)
    {
        var f = new Fake(); f.Actor = defect switch
        { "missing" => null, "module" => f.Actor! with { Module = AiModule.Social },
            "empty-account" => f.Actor! with { UserId = Guid.Empty }, _ => f.Actor! with { ModuleProfileId = Guid.Empty } };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Controller().ListAsync("learning", 1, default));
        Assert.Equal(0, f.Reads); Assert.Equal(0, f.ForbiddenWrites);
    }

    [Theory]
    [InlineData("account")] [InlineData("profile")] [InlineData("module")] [InlineData("expired")]
    [InlineData("future")] [InlineData("duration")] [InlineData("duplicate")]
    public async Task ForeignMalformedOrExpiredHistoryCannotBeReturned(string defect)
    {
        var f = new Fake(); var r = f.Conversation!;
        f.Conversation = defect switch
        { "account" => r with { AccountId = Id(99) }, "profile" => r with { ModuleProfileId = Id(99) },
            "module" => r with { Module = AiModule.Hobbies }, "expired" => r with { LastSavedUserMessageAtUtc = Now.AddDays(-14), CreatedAtUtc = Now.AddDays(-15), ExpiresAtUtc = Now },
            "future" => r with { LastSavedUserMessageAtUtc = Now.AddDays(1), ExpiresAtUtc = Now.AddDays(15) },
            "duration" => r with { ExpiresAtUtc = r.ExpiresAtUtc.AddSeconds(1) }, _ => r };
        if (defect == "duplicate") f.List = [r, r];
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Controller().ListAsync("learning", 2, default));
        Assert.Equal(0, f.ForbiddenWrites);
    }

    [Theory]
    [InlineData("Learning", 1)] [InlineData("unknown", 1)] [InlineData("", 1)]
    [InlineData("learning", 0)] [InlineData("learning", 101)]
    public async Task InvalidModuleOrLimitFailsBeforeAuthorization(string module, int limit)
    {
        var f = new Fake(); await Assert.ThrowsAsync<AiChatValidationException>(() => f.Controller().ListAsync(module, limit, default));
        Assert.Equal(0, f.AuthCalls); Assert.Equal(0, f.Reads);
    }

    [Fact]
    public async Task AuthorizedMessageReadPreservesTextAndRechecksActorAfterDependency()
    {
        var f = new Fake(); var result = await f.Controller().ReadMessagesAsync("learning", Id(3), 1, default);
        var page = Assert.IsType<AiHistoryPage<AiHistoryMessage>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(Secret, Assert.Single(page.Items).Text); Assert.Equal("user", page.Items[0].Role);
        Assert.DoesNotContain(Secret, page.Items[0].ToString()); Assert.Equal(0, f.ForbiddenWrites);
        f.AfterRead = () => f.Actor = f.Actor! with { ModuleProfileId = Id(99) };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Controller().ReadMessagesAsync("learning", Id(3), 1, default));
    }

    [Theory]
    [InlineData("foreign")] [InlineData("role")] [InlineData("future")] [InlineData("blank")]
    [InlineData("duplicate")] [InlineData("empty-id")]
    public async Task InvalidMessageRecordsAreWithheld(string defect)
    {
        var f = new Fake(); var m = f.Messages[0];
        f.Messages = [defect switch
        { "foreign" => m with { ConversationId = Id(99) }, "role" => m with { Role = (AiStoredMessageRole)99 },
            "future" => m with { SavedAtUtc = Now.AddSeconds(1) }, "blank" => m with { Text = " " },
            "empty-id" => m with { MessageId = Guid.Empty }, _ => m }];
        if (defect == "duplicate") f.Messages = [m, m];
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Controller().ReadMessagesAsync("learning", Id(3), 2, default));
    }

    [Fact]
    public async Task MissingDeletedAndForeignConversationUseSameDenialWithoutMessageRead()
    {
        var messages = new List<string>();
        foreach (var row in new AiStoredConversation?[] { null, new Fake().Conversation! with { AccountId = Id(99) } })
        {
            var f = new Fake { Conversation = row };
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Controller().ReadMessagesAsync("learning", Id(3), 1, default));
            messages.Add(ex.Message); Assert.Equal(1, f.Reads);
        }
        Assert.Single(messages.Distinct());
        var deletion = new Fake(); var c = deletion.Controller();
        Assert.IsType<NoContentResult>(await c.DeleteAsync("learning", Id(3), default));
        Assert.IsType<NoContentResult>(await c.DeleteAsync("learning", Id(3), default));
        Assert.Equal(2, deletion.Deletes); Assert.Equal(0, deletion.ForbiddenWrites);
    }

    [Theory]
    [InlineData(AiOperationStatus.Reserved, "reserved")] [InlineData(AiOperationStatus.InProgress, "in_progress")]
    [InlineData(AiOperationStatus.PendingReconciliation, "pending_reconciliation")] [InlineData(AiOperationStatus.Completed, "completed")]
    [InlineData(AiOperationStatus.ConfirmedFailed, "confirmed_failed")] [InlineData(AiOperationStatus.Cancelled, "cancelled")]
    public async Task PublicStatusContainsNoAttemptOrCandidate(AiOperationStatus state, string expected)
    {
        var f = new Fake { Status = state }; var result = await f.Controller().ReconcileOperationAsync("learning", Id(3), Id(5), default);
        var value = Assert.IsType<AiPublicOperationStatus>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(expected, value.Status); Assert.Equal(Id(5), value.OperationId); Assert.Equal(1, f.Reconciles);
        var json = JsonSerializer.Serialize(value); Assert.DoesNotContain("Attempt", json); Assert.DoesNotContain(Secret, json);
        Assert.Equal(0, f.ForbiddenWrites);
    }

    [Fact]
    public async Task CancelledAndDeniedCallsCannotUseIncompleteGenerationOrRetry()
    {
        var f = new Fake { DenyStatus = true };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Controller().ReconcileOperationAsync("learning", Id(3), Id(5), default));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Controller().ListAsync("learning", 1, cts.Token));
        Assert.Equal(0, f.ForbiddenWrites);
        var dependencies = typeof(AiChatController).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(IAiResponseProvider), dependencies); Assert.DoesNotContain(typeof(AiChatService), dependencies);
    }

    private static bool IsValid(object value) => Validator.TryValidateObject(value, new(value), [], true);
    [Theory]
    [InlineData("English")] [InlineData("தமிழ்")] [InlineData("Tanglish தமிழ்")] [InlineData("සිංහල")]
    [InlineData("👩🏽‍💻")] [InlineData("e\u0301")]
    public void UnicodeSubmissionUsesTextElementsAndPreservesWireText(string element)
    {
        var atLimit = string.Concat(Enumerable.Repeat(element == "👩🏽‍💻" || element == "e\u0301" ? element : "x", 500));
        Assert.True(IsValid(new AiChatSubmissionRequest(Id(1), atLimit)));
        Assert.False(IsValid(new AiChatSubmissionRequest(Id(1), atLimit + "x")));
        var dto = new AiChatSubmissionRequest(Id(1), "  " + element + "\n");
        var json = JsonSerializer.Serialize(dto, JsonSerializerOptions.Web);
        Assert.Equal(dto, JsonSerializer.Deserialize<AiChatSubmissionRequest>(json, JsonSerializerOptions.Web));
        Assert.DoesNotContain(dto.Text, dto.ToString());
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" \t\n")]
    public void MissingBlankOrInvalidUnicodeCannotValidate(string? text) => Assert.False(IsValid(new AiChatSubmissionRequest(Id(1), text!)));

    [Fact]
    public void InvalidSurrogateIsRejectedWithoutAttributeEncodingReplacement() =>
        Assert.False(IsValid(new AiChatSubmissionRequest(Id(1), new string((char)0xD800, 1))));

    [Fact]
    public void ReferenceValidationRejectsEmptyIdsAndJsonRejectsAuthorityFields()
    {
        foreach (var value in new object[] { new AiChatSubmissionRequest(Guid.Empty, "x"), new AiOperationRequest(Guid.Empty),
            new AiConversationRequest(Guid.Empty), new AiSafetyReportRequest(Guid.Empty, Guid.Empty) }) Assert.False(IsValid(value));
        foreach (var field in new[] { "actor", "model", "attemptId", "budget", "credentials" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AiChatSubmissionRequest>(
                "{\"operationId\":\"" + Id(1) + "\",\"text\":\"x\",\"" + field + "\":\"private\"}", JsonSerializerOptions.Web));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AiConversationRequest>("{\"conversationId\":\"not-a-guid\"}"));
    }

    [Fact]
    public void PublicPageIsBoundedDefensivelyCopiedAndDiagnosticsDoNotSerializePrivateItems()
    {
        var rows = new List<AiHistoryMessage> { new(Id(1), "user", Secret, Now) }; var page = new AiHistoryPage<AiHistoryMessage>(rows);
        rows.Clear(); Assert.Single(page.Items); Assert.DoesNotContain(Secret, page.ToString());
        Assert.Throws<ArgumentException>(() => new AiHistoryPage<string>(Enumerable.Repeat("x", 101)));
        Assert.Throws<ArgumentException>(() => new AiHistoryPage<string>(new string[] { null! }));
        Assert.Throws<ArgumentNullException>(() => new AiHistoryPage<string>(null!));
        Assert.Equal(100, new AiHistoryPage<string>(Enumerable.Repeat("x", 100)).Items.Count);
    }

    [Fact]
    public async Task DisabledRegistrationIsResolvableIdempotentAndCannotActivateLiveOrWorker()
    {
        var services = new ServiceCollection(); services.AddLogging();
        Assert.Same(services, services.AddDisabledAiFoundation()); services.AddDisabledAiFoundation();
        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<AiOptions>));
        Assert.Single(services, d => d.ServiceType == typeof(IAiPrivacyNoticeGate));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Assert.False(provider.GetRequiredService<IOptions<AiOptions>>().Value.LiveExecutionEnabled);
        var reader = provider.GetRequiredService<IAiModuleProfileAccessReader>();
        Assert.IsType<DenyAllAiModuleProfileAccessReader>(reader); Assert.Same(reader, provider.GetRequiredService<IAiModuleProfileAccessReader>());
        Assert.Null(await reader.FindAuthorizedProfileIdAsync(Id(1), AiModule.Social));
        Assert.Equal(AiPrivacyNoticeGateOutcome.PolicyNotVerified, await provider.GetRequiredService<IAiPrivacyNoticeGate>().EvaluateAsync(new(Id(1), Id(2), AiModule.Learning)));
        foreach (var type in new[] { typeof(AiChatController), typeof(AiMaintenanceWorker), typeof(IAiResponseProvider), typeof(IAiConversationRepository), typeof(AiExceptionHandler) })
            Assert.Null(provider.GetService(type));
        Assert.False(provider.GetRequiredService<IValidateOptions<AiOptions>>().Validate(null, new() { LiveExecutionEnabled = true }).Succeeded);
        Assert.Throws<ArgumentNullException>(() => AiServiceCollectionExtensions.AddDisabledAiFoundation(null!));
    }

    [Fact]
    public void DiscoveryAndIsolatedRouteGraphExcludeDormantAiEvenWithLiveFlag()
    {
        var services = new ServiceCollection(); services.AddLogging();
        var mvc = services.AddControllers().AddApplicationPart(typeof(AiChatController).Assembly);
        var feature = new ControllerFeature(); mvc.PartManager.PopulateFeature(feature);
        Assert.NotEmpty(feature.Controllers); Assert.DoesNotContain(feature.Controllers, t => t.AsType() == typeof(AiChatController));
        using var provider = services.BuildServiceProvider();
        Assert.DoesNotContain(provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items,
            a => a is ControllerActionDescriptor c && c.ControllerTypeInfo.AsType() == typeof(AiChatController));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddControllers().AddApplicationPart(typeof(AiChatController).Assembly);
        builder.Services.AddSingleton<IOptions<AiOptions>>(Options.Create(new AiOptions { LiveExecutionEnabled = true }));
        using var app = builder.Build(); app.MapControllers(); // Never Start/Run; no production startup/services.
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToArray();
        Assert.NotEmpty(routes); Assert.DoesNotContain(routes, r => r.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo.AsType() == typeof(AiChatController));
        Assert.DoesNotContain(routes, r => r.RoutePattern.RawText?.StartsWith("api/v1/ai", StringComparison.OrdinalIgnoreCase) == true);
    }

    private sealed class Problems : IProblemDetailsService
    {
        internal ProblemDetailsContext? Last;
        public ValueTask WriteAsync(ProblemDetailsContext context) { Last = context; return ValueTask.CompletedTask; }
        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context) { Last = context; return ValueTask.FromResult(true); }
    }
    [Theory]
    [InlineData("validation", 400)] [InlineData("argument", 400)] [InlineData("auth", 401)]
    [InlineData("denied", 403)] [InlineData("missing", 403)] [InlineData("provider", 503)]
    [InlineData("integration", 503)] [InlineData("cancel", 503)] [InlineData("unexpected", 500)]
    public async Task ErrorMappingUsesActualTypesAndNeverExceptionBodies(string kind, int status)
    {
        Exception exception = kind switch
        { "validation" => new AiChatValidationException(Secret), "argument" => new ArgumentException(Secret),
            "auth" => new AccountAuthenticationRequiredException(), "denied" or "missing" => new UnauthorizedAccessException(Secret),
            "provider" => new InvalidOperationException(Secret), "integration" => new AccountStoreUnavailableException(),
            "cancel" => new OperationCanceledException(Secret), _ => new Exception(Secret, new Exception(Secret)) };
        var p = new Problems(); var context = new DefaultHttpContext(); context.Request.Path = "/api/v1/ai/proposed";
        Assert.True(await new AiExceptionHandler(p).TryHandleAsync(context, exception, default));
        Assert.Equal(status, context.Response.StatusCode); Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.NotNull(p.Last); var json = JsonSerializer.Serialize(p.Last.ProblemDetails);
        Assert.DoesNotContain(Secret, json); Assert.Null(p.Last.ProblemDetails.Detail); Assert.Null(p.Last.ProblemDetails.Instance);
        Assert.Single(p.Last.ProblemDetails.Extensions); Assert.Equal(status, p.Last.ProblemDetails.Status);
        if (status == 401) Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task CurrentSocialAccessUnavailableDoesNotBecomeEligibleBySelectingModule()
    {
        var f = new Fake { Actor = null };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Controller().ListAsync("social", 1, default));
        Assert.Equal(0, f.Reads); Assert.Equal(0, f.ForbiddenWrites);
        // Actual age/opt-in authority is absent; this proves denial, not a substitute eligibility algorithm.
        var reader = new DenyAllAiModuleProfileAccessReader();
        Assert.Null(await reader.FindAuthorizedProfileIdAsync(Id(1), AiModule.Social));
    }

    [Fact]
    public async Task DeniedOperationAndEmptyReferencesNeverReachAdmissionOrDelivery()
    {
        var f = new Fake(); var controller = f.Controller();
        await Assert.ThrowsAsync<AiChatValidationException>(() => controller.DeleteAsync("learning", Guid.Empty, default));
        await Assert.ThrowsAsync<AiChatValidationException>(() => controller.ReconcileOperationAsync("learning", Id(3), Guid.Empty, default));
        Assert.Equal(0, f.AuthCalls); Assert.Equal(0, f.Deletes); Assert.Equal(0, f.Reconciles);
        f.DenyStatus = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.ReconcileOperationAsync("learning", Id(3), Id(5), default));
        Assert.Equal(0, f.ForbiddenWrites);
    }

    [Fact]
    public void DisabledRegistrationPreservesExplicitPriorRegistrationsWithoutCertifyingThem()
    {
        var services = new ServiceCollection(); var prior = new DenyAllAiModuleProfileAccessReader();
        services.AddSingleton<IAiModuleProfileAccessReader>(prior); services.AddDisabledAiFoundation();
        using var provider = services.BuildServiceProvider(); Assert.Same(prior, provider.GetRequiredService<IAiModuleProfileAccessReader>());
        Assert.Single(services, d => d.ServiceType == typeof(IAiModuleProfileAccessReader));
    }

    private sealed class StartedResponse : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 202;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
    [Fact]
    public async Task StartedResponseIsNotOverwrittenAndEnumNamesAreNotParsedAsConflictExceptions()
    {
        var problems = new Problems(); var handler = new AiExceptionHandler(problems);
        var started = new DefaultHttpContext(); started.Request.Path = "/api/v1/ai";
        started.Features.Set<IHttpResponseFeature>(new StartedResponse());
        Assert.False(await handler.TryHandleAsync(started, new Exception(Secret), default));
        Assert.Null(problems.Last); Assert.Equal(202, started.Response.StatusCode);
        var context = new DefaultHttpContext(); context.Request.Path = "/api/v1/ai";
        Assert.True(await handler.TryHandleAsync(context, new InvalidOperationException("IdempotencyConflict " + Secret), default));
        Assert.Equal(503, context.Response.StatusCode); // No typed public conflict exception contract exists.
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(problems.Last!.ProblemDetails));
    }

    [Fact]
    public async Task ForeignPathsAndAbortedRequestsAreNotMisreportedAsSuccessfulReplies()
    {
        var p = new Problems(); var h = new AiExceptionHandler(p); var c = new DefaultHttpContext();
        foreach (var path in new[] { "/api/v1/account", "/api/v1/aievil", "/health" })
        { c.Request.Path = path; Assert.False(await h.TryHandleAsync(c, new Exception(Secret), default)); Assert.Null(p.Last); }
        using var cts = new CancellationTokenSource(); cts.Cancel(); c.Request.Path = "/api/v1/ai"; c.RequestAborted = cts.Token;
        Assert.True(await h.TryHandleAsync(c, new OperationCanceledException(Secret), cts.Token));
        Assert.Equal(503, c.Response.StatusCode); Assert.Null(p.Last);
    }
}
