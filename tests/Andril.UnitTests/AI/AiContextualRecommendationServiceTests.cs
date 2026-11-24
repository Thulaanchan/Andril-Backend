using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Recommendations;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

public sealed class AiContextualRecommendationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static AiAuthorizedActor CreateActor(AiModule module = AiModule.Learning) =>
        new(Id(1), Id(2), module);

    private static AiConsentReference CreateReference(AiModule module = AiModule.Learning) =>
        new(new AiOperationReference(CreateActor(module), Id(3), Id(4)),
            new AiConsentPurpose("study-recommendations", "v1", "current-conversation"),
            Id(5), Id(6));

    private static AiConsentLifecycle CreateLifecycle(AiModule module = AiModule.Learning) =>
        new(CreateReference(module).Operation, Id(5), Id(6), Now, true, true, true, true);

    private static AiConsentSnapshot CreateSnapshot(AiConsentState state, Guid attemptId, Guid revision, AiModule module = AiModule.Learning) =>
        new(CreateReference(module),
            new AiConsentNotice("v1", "Study suggestions", "Use current conversation context"),
            state,
            revision,
            Now,
            state is AiConsentState.GrantedUnused or AiConsentState.Started ? Now : null,
            state == AiConsentState.Started ? Now : null,
            state == AiConsentState.Started ? attemptId : null);

    private sealed class StubActorAuthorization : IAiActorAuthorization
    {
        public AiAuthorizedActor? Actor { get; set; }

        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken cancellationToken = default) =>
            Task.FromResult(Actor);
    }

    private sealed class StubPrivacyNoticeGate : IAiPrivacyNoticeGate
    {
        public AiPrivacyNoticeGateOutcome Outcome { get; set; } = AiPrivacyNoticeGateOutcome.Satisfied;

        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default) =>
            Task.FromResult(Outcome);
    }

    private sealed class StubConsentRepository : IAiContextualConsentRepository
    {
        public AiConsentLifecycle? Lifecycle { get; set; }
        public AiConsentResult Result { get; set; } = null!;

        public Task<AiConsentLifecycle?> ReadLifecycleAsync(AiOperationReference operation, CancellationToken cancellationToken = default) =>
            Task.FromResult(Lifecycle);

        public Task<AiConsentResult> ApplyAsync(AiConsentRequest request, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);

        public Task<AiConsentResult> ReadStatusAsync(AiConsentReference reference, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);

        public Task<AiConsentResult> CheckPendingAsync(AiConsentPendingReference pending, AiConsentLifecycle lifecycle, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);

        public Task<AiConsentSystemResult> InvalidateFromSystemAsync(AiConsentSystemInvalidation evidence, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class StubRecommendationGenerator : IAiContextualRecommendationGenerator
    {
        public string Content { get; set; } = "Recommended: Review Chapter 4 Exercises";
        public bool GeneratorCalled { get; private set; }

        public Task<string> GenerateRecommendationAsync(AiModule module, AiAuthorizedActor actor, AiConsentPurpose purpose, CancellationToken cancellationToken = default)
        {
            GeneratorCalled = true;
            return Task.FromResult(Content);
        }
    }

    private static (AiContextualRecommendationService Service, StubActorAuthorization Auth, StubPrivacyNoticeGate Gate, StubConsentRepository Repo, StubRecommendationGenerator Gen) CreateTestHarness(AiModule module = AiModule.Learning)
    {
        var auth = new StubActorAuthorization { Actor = CreateActor(module) };
        var gate = new StubPrivacyNoticeGate();
        var privacyGuard = new AiPrivacyNoticeGuard(auth, gate);
        var repo = new StubConsentRepository { Lifecycle = CreateLifecycle(module) };
        var consentService = new AiContextualConsentService(auth, privacyGuard, repo);
        var generator = new StubRecommendationGenerator();

        var service = new AiContextualRecommendationService(auth, privacyGuard, consentService, generator);
        return (service, auth, gate, repo, generator);
    }

    [Fact]
    public async Task ProcessRecommendationAsync_ExplicitConsentVerified_DeliversRecommendation()
    {
        var (service, _, _, repo, gen) = CreateTestHarness(AiModule.Learning);
        var reference = CreateReference(AiModule.Learning);
        var attemptId = Id(8);
        var revision = Id(7);

        var pending = new AiConsentPendingReference(reference, revision, attemptId);
        var snapshot = CreateSnapshot(AiConsentState.Started, attemptId, revision, AiModule.Learning);

        repo.Result = new AiConsentResult(AiConsentOutcome.ContinuationVerified, snapshot, CreateLifecycle(AiModule.Learning));

        var result = await service.ProcessRecommendationAsync(pending, AiModule.Learning, CancellationToken.None);

        Assert.Equal(AiContextualRecommendationDeliveryStatus.Delivered, result.Status);
        Assert.True(result.IsDelivered);
        Assert.False(result.IsSuppressed);
        Assert.Equal("Recommended: Review Chapter 4 Exercises", result.RecommendationContent);
        Assert.True(gen.GeneratorCalled);
    }

    [Theory]
    [InlineData(AiConsentState.Revoked)]
    [InlineData(AiConsentState.Declined)]
    [InlineData(AiConsentState.Expired)]
    public async Task ProcessRecommendationAsync_WithdrawnOrDeclinedConsent_ThrowsInvalidOperationException(AiConsentState state)
    {
        var (service, _, _, repo, gen) = CreateTestHarness(AiModule.Learning);
        var reference = CreateReference(AiModule.Learning);
        var attemptId = Id(8);
        var revision = Id(7);

        var pending = new AiConsentPendingReference(reference, revision, attemptId);
        var snapshot = CreateSnapshot(state, attemptId, revision, AiModule.Learning);

        repo.Result = new AiConsentResult(AiConsentOutcome.ContinuationVerified, snapshot, CreateLifecycle(AiModule.Learning));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProcessRecommendationAsync(pending, AiModule.Learning, CancellationToken.None));
        Assert.False(gen.GeneratorCalled);
    }

    [Fact]
    public async Task ProcessRecommendationAsync_CrossModuleTarget_StrictlyRejected()
    {
        var (service, _, _, _, _) = CreateTestHarness(AiModule.Learning);
        var reference = CreateReference(AiModule.Learning);
        var pending = new AiConsentPendingReference(reference, Id(7), Id(8));

        // Operation is Learning, but targetModule is Hobbies
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ProcessRecommendationAsync(pending, AiModule.Hobbies, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessRecommendationAsync_ActorMismatch_ThrowsUnauthorizedAccessException()
    {
        var (service, auth, _, _, _) = CreateTestHarness(AiModule.Learning);
        var reference = CreateReference(AiModule.Learning);
        var pending = new AiConsentPendingReference(reference, Id(7), Id(8));

        // Different user ID
        auth.Actor = new AiAuthorizedActor(Guid.NewGuid(), Id(2), AiModule.Learning);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ProcessRecommendationAsync(pending, AiModule.Learning, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessRecommendationAsync_PrivacyNoticeNotSatisfied_ThrowsUnauthorizedAccessException()
    {
        var (service, _, gate, _, _) = CreateTestHarness(AiModule.Learning);
        var reference = CreateReference(AiModule.Learning);
        var pending = new AiConsentPendingReference(reference, Id(7), Id(8));

        gate.Outcome = AiPrivacyNoticeGateOutcome.NoticeRequired;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ProcessRecommendationAsync(pending, AiModule.Learning, CancellationToken.None));
    }

    [Fact]
    public async Task ProcessRecommendationAsync_InvalidPendingOutcome_SuppressesDelivery()
    {
        var (service, _, _, repo, gen) = CreateTestHarness(AiModule.Learning);
        var reference = CreateReference(AiModule.Learning);
        var attemptId = Id(8);
        var revision = Id(7);

        var pending = new AiConsentPendingReference(reference, revision, attemptId);
        repo.Result = new AiConsentResult(AiConsentOutcome.Denied, null, null);

        var result = await service.ProcessRecommendationAsync(pending, AiModule.Learning, CancellationToken.None);

        Assert.Equal(AiContextualRecommendationDeliveryStatus.Suppressed, result.Status);
        Assert.True(result.IsSuppressed);
        Assert.Equal(AiContextualSuppressionReason.ConsentNotVerified, result.SuppressionReason);
        Assert.False(gen.GeneratorCalled);
    }

    [Fact]
    public async Task ProcessRecommendationAsync_CancellationPropagates()
    {
        var (service, _, _, _, _) = CreateTestHarness(AiModule.Learning);
        var reference = CreateReference(AiModule.Learning);
        var pending = new AiConsentPendingReference(reference, Id(7), Id(8));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.ProcessRecommendationAsync(pending, AiModule.Learning, cts.Token));
    }

    [Fact]
    public void AiContextualRecommendationDeliveryResult_JsonSerialization_ConcealsSensitiveState()
    {
        var delivered = AiContextualRecommendationDeliveryResult.Delivered("Secret payload");
        var suppressed = AiContextualRecommendationDeliveryResult.Suppressed(AiContextualSuppressionReason.ConsentWithdrawn);

        Assert.Equal("{}", JsonSerializer.Serialize(delivered));
        Assert.Equal("{}", JsonSerializer.Serialize(suppressed));

        Assert.Equal("AiContextualRecommendationDeliveryResult [private]", delivered.ToString());
        Assert.Equal("AiContextualRecommendationDeliveryResult [private]", suppressed.ToString());
    }
}
