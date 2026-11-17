
using System;
using System.Threading;
using System.Threading.Tasks;
using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.API.Integrations.AI;
using Xunit;

namespace Andril.SecurityTests.AI;

/// <summary>
/// AI-BE-04.1: verifies the deny-all privacy gate with synthetic actors.
/// Does not prove real consent, profile eligibility or HTTP enforcement.
/// </summary>
public sealed class DenyAllAiPrivacyNoticeGateTests
{
    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task WellFormedActor_PolicyNotVerified(AiModule module)
    {
        IAiPrivacyNoticeGate gate = new DenyAllAiPrivacyNoticeGate();
        var actor = CreateActor(module);

        var outcome = await gate.EvaluateAsync(actor);

        Assert.Equal(AiPrivacyNoticeGateOutcome.PolicyNotVerified, outcome);
    }

    [Theory]
    [InlineData(AiModule.Learning, true, false)]
    [InlineData(AiModule.Learning, false, true)]
    [InlineData(AiModule.Learning, true, true)]
    [InlineData(AiModule.Hobbies, true, false)]
    [InlineData(AiModule.Hobbies, false, true)]
    [InlineData(AiModule.Hobbies, true, true)]
    [InlineData(AiModule.Social, true, false)]
    [InlineData(AiModule.Social, false, true)]
    [InlineData(AiModule.Social, true, true)]
    public async Task EmptyIdentityFields_AreDenied(
        AiModule module, bool emptyAccount, bool emptyProfile)
    {
        IAiPrivacyNoticeGate gate = new DenyAllAiPrivacyNoticeGate();
        var actor = new AiAuthorizedActor(
            emptyAccount ? Guid.Empty : Guid.NewGuid(),
            emptyProfile ? Guid.Empty : Guid.NewGuid(),
            module);

        Assert.Equal(
            AiPrivacyNoticeGateOutcome.Denied,
            await gate.EvaluateAsync(actor));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task UndefinedModule_IsDenied(int value)
    {
        IAiPrivacyNoticeGate gate = new DenyAllAiPrivacyNoticeGate();
        var actor = CreateActor((AiModule)value);

        Assert.Equal(
            AiPrivacyNoticeGateOutcome.Denied,
            await gate.EvaluateAsync(actor));
    }

    [Fact]
    public async Task NullActor_IsDenied()
    {
        IAiPrivacyNoticeGate gate = new DenyAllAiPrivacyNoticeGate();

        Assert.Equal(
            AiPrivacyNoticeGateOutcome.Denied,
            await gate.EvaluateAsync(null!));
    }

    [Fact]
    public async Task RepeatedAndChangedIdentities_NeverUnlockGate()
    {
        IAiPrivacyNoticeGate gate = new DenyAllAiPrivacyNoticeGate();
        var learning = CreateActor(AiModule.Learning);
        var hobbies = learning with
        {
            Module = AiModule.Hobbies,
            ModuleProfileId = Guid.NewGuid()
        };
        var social = learning with
        {
            Module = AiModule.Social,
            ModuleProfileId = Guid.NewGuid()
        };
        var otherAccount = learning with { UserId = Guid.NewGuid() };
        var otherProfile = learning with { ModuleProfileId = Guid.NewGuid() };

        using var cts = new CancellationTokenSource();

        foreach (var actor in new[]
        {
            learning, hobbies, social, otherAccount, otherProfile, learning
        })
        {
            Assert.Equal(
                AiPrivacyNoticeGateOutcome.PolicyNotVerified,
                await gate.EvaluateAsync(actor, cts.Token));
        }
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task CancelledWellFormedActor_PropagatesCancellation(
        AiModule module)
    {
        IAiPrivacyNoticeGate gate = new DenyAllAiPrivacyNoticeGate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => { await gate.EvaluateAsync(CreateActor(module), cts.Token); });

        Assert.Equal(cts.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledInvalidActor_PropagatesCancellation(
        bool nullActor)
    {
        IAiPrivacyNoticeGate gate = new DenyAllAiPrivacyNoticeGate();
        var actor = nullActor ? null : new AiAuthorizedActor(
            Guid.Empty, Guid.Empty, (AiModule)0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => { await gate.EvaluateAsync(actor!, cts.Token); });

        Assert.Equal(cts.Token, error.CancellationToken);
    }

    private static AiAuthorizedActor CreateActor(AiModule module) =>
        new(Guid.NewGuid(), Guid.NewGuid(), module);
}
