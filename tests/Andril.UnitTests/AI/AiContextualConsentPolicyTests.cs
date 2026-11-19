using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

public sealed class AiContextualConsentPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    private static AiConsentReference Reference(AiModule module = AiModule.Learning) => new(new(new(Id(1), Id(2), module), Id(3), Id(4)), new("synthetic-purpose", "v1", "current-conversation"), Id(5), Id(6));
    private static AiConsentLifecycle Live(AiConsentReference? reference = null) => new((reference ?? Reference()).Operation, Id(5), Id(6), Now, true, true, true, true);
    private static AiConsentSnapshot Snapshot(AiConsentState state = AiConsentState.GrantedUnused) => new(Reference(), new("notice-v1", "Synthetic recommendation", "Relevant current AI conversation only"), state, Id(7), Now, state == AiConsentState.AwaitingConsent || state == AiConsentState.Declined ? null : Now);
    private static AiConsentSnapshot Started() => Snapshot(AiConsentState.Started) with { StartedAtUtc = Now.AddSeconds(1), AttemptId = Id(8) };

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void A_ValidModuleScopes_AreSupported(AiModule module)
    {
        Assert.True(AiContextualConsentPolicy.ValidReference(Reference(module)));
        Assert.True(AiContextualConsentPolicy.ValidLifecycle(Reference(module), Live(Reference(module))));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("conversation")]
    [InlineData("operation")]
    [InlineData("module")]
    [InlineData("purpose")]
    [InlineData("session")]
    public void A_InvalidScope_FailsClosed(string defect)
    {
        var reference = Reference();
        var operation = reference.Operation;
        reference = defect switch
        {
            "account" => reference with { Operation = operation with { Actor = operation.Actor with { UserId = Guid.Empty } } },
            "profile" => reference with { Operation = operation with { Actor = operation.Actor with { ModuleProfileId = Guid.Empty } } },
            "conversation" => reference with { Operation = operation with { ConversationId = Guid.Empty } },
            "operation" => reference with { Operation = operation with { OperationId = Guid.Empty } },
            "module" => reference with { Operation = operation with { Actor = operation.Actor with { Module = (AiModule)99 } } },
            "purpose" => reference with { Purpose = reference.Purpose with { Version = "" } },
            _ => reference with { SessionEpoch = Guid.Empty }
        };
        Assert.False(AiContextualConsentPolicy.ValidReference(reference));
        Assert.False(AiContextualConsentPolicy.CanStart(Snapshot(), reference, Live()));
    }

    [Theory]
    [InlineData(AiConsentDecision.Allow, true)]
    [InlineData(AiConsentDecision.NotNow, true)]
    [InlineData(AiConsentDecision.Unknown, false)]
    [InlineData((AiConsentDecision)99, false)]
    public void B_OnlyExplicitDecisions_AreSupported(AiConsentDecision decision, bool valid) =>
        Assert.Equal(valid, AiContextualConsentPolicy.ValidDecision(decision));

    [Theory]
    [InlineData("operation")]
    [InlineData("purpose")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("conversation")]
    public void B_ConsentCannotBeReusedForDifferentScope(string defect)
    {
        var expected = Reference();
        expected = defect switch
        {
            "operation" => expected with { Operation = expected.Operation with { OperationId = Id(99) } },
            "purpose" => expected with { Purpose = expected.Purpose with { Id = "different" } },
            "account" => expected with { Operation = expected.Operation with { Actor = expected.Operation.Actor with { UserId = Id(99) } } },
            "profile" => expected with { Operation = expected.Operation with { Actor = expected.Operation.Actor with { ModuleProfileId = Id(99) } } },
            _ => expected with { Operation = expected.Operation with { ConversationId = Id(99) } }
        };
        Assert.False(AiContextualConsentPolicy.CanStart(Snapshot(), expected, Live(expected)));
        Assert.False(AiContextualConsentPolicy.CanStart(null, expected, Live(expected))); // History/audit alone is no runtime grant.
    }

    [Theory]
    [InlineData(0L, true)]
    [InlineData(2990000000L, true)]
    [InlineData(2999999999L, true)]
    [InlineData(3000000000L, false)]
    [InlineData(3000000001L, false)]
    [InlineData(-1L, false)]
    public void C_UnusedGrant_ExpiresAtExactFiveMinuteBoundary(long ticks, bool expected)
    {
        Assert.Equal(Now.AddMinutes(5), AiContextualConsentPolicy.UnusedExpiresAt(Now));
        Assert.Equal(expected, AiContextualConsentPolicy.CanStart(Snapshot(), Reference(), Live() with { NowUtc = Now.AddTicks(ticks) }));
    }

    [Fact]
    public void C_StartedWork_DoesNotInheritUnusedDeadline()
    {
        Assert.True(AiContextualConsentPolicy.CanContinue(Started(), Reference(), Live() with { NowUtc = Now.AddHours(1) }, Id(8), Id(7)));
        Assert.False(AiContextualConsentPolicy.CanStart(Started(), Reference(), Live() with { NowUtc = Now.AddHours(1) }));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("offset")]
    [InlineData("overflow")]
    public void C_InvalidTimeArithmetic_IsRejected(string defect)
    {
        var time = defect switch { "default" => default, "offset" => Now.ToOffset(TimeSpan.FromHours(1)), _ => DateTimeOffset.MaxValue };
        Assert.Throws<InvalidOperationException>(() => AiContextualConsentPolicy.UnusedExpiresAt(time));
        Assert.Throws<InvalidOperationException>(() => AiContextualConsentPolicy.AuditExpiresAt(time));
    }

    [Theory]
    [InlineData(AiConsentState.AwaitingConsent, AiConsentState.GrantedUnused, true)]
    [InlineData(AiConsentState.AwaitingConsent, AiConsentState.Declined, true)]
    [InlineData(AiConsentState.GrantedUnused, AiConsentState.Started, true)]
    [InlineData(AiConsentState.GrantedUnused, AiConsentState.Revoked, true)]
    [InlineData(AiConsentState.GrantedUnused, AiConsentState.Expired, true)]
    [InlineData(AiConsentState.Declined, AiConsentState.GrantedUnused, false)]
    [InlineData(AiConsentState.Expired, AiConsentState.GrantedUnused, false)]
    [InlineData(AiConsentState.Revoked, AiConsentState.GrantedUnused, false)]
    [InlineData(AiConsentState.Completed, AiConsentState.Revoked, false)]
    [InlineData(AiConsentState.Started, AiConsentState.Revoked, true)]
    public void D_Transitions_AreMonotonicAndDoNotInventCompletedWithdrawal(AiConsentState from, AiConsentState to, bool expected)
    {
        var before = from is AiConsentState.Started or AiConsentState.Completed ? Started() with { State = from } : Snapshot(from);
        var after = to == AiConsentState.Started ? Started() with { Revision = Id(9) } :
            before with { State = to, Revision = Id(9), GrantedAtUtc = to == AiConsentState.GrantedUnused ? Now : before.GrantedAtUtc };
        Assert.Equal(expected, AiContextualConsentPolicy.CanTransition(before, after));
        Assert.True(AiContextualConsentPolicy.CanTransition(before, before)); // Replay only; no new processing grant.
    }

    [Theory]
    [InlineData(AiConsentState.Declined)]
    [InlineData(AiConsentState.Expired)]
    [InlineData(AiConsentState.Revoked)]
    [InlineData(AiConsentState.Completed)]
    [InlineData(AiConsentState.Unknown)]
    [InlineData((AiConsentState)99)]
    public void D_NonUnusedState_CannotStart(AiConsentState state)
    {
        Assert.False(AiContextualConsentPolicy.CanStart(Snapshot(state), Reference(), Live()));
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("switch")]
    [InlineData("inactive")]
    [InlineData("eligibility")]
    [InlineData("conversation")]
    [InlineData("recovery")]
    public void E_LiveRestrictions_BlockPendingResults(string defect)
    {
        var lifecycle = defect switch
        {
            "logout" => Live() with { SessionEpoch = Id(99) },
            "switch" => Live() with { ModuleEpoch = Id(99) },
            "inactive" => Live() with { SessionActive = false },
            "eligibility" => Live() with { EligibilityVerified = false },
            "conversation" => Live() with { ConversationAvailable = false },
            _ => Live() with { RecoveryVerified = false }
        };
        Assert.False(AiContextualConsentPolicy.CanContinue(Started(), Reference(), lifecycle with { NowUtc = Now.AddSeconds(2) }, Id(8), Id(7)));
    }

    [Fact]
    public void E_RevokedStateAndWrongAttemptOrRevision_BlockContinuation()
    {
        var live = Live() with { NowUtc = Now.AddSeconds(2) };
        Assert.False(AiContextualConsentPolicy.CanContinue(Started() with { State = AiConsentState.Revoked }, Reference(), live, Id(8), Id(7)));
        Assert.False(AiContextualConsentPolicy.CanContinue(Started(), Reference(), live, Id(99), Id(7)));
        Assert.False(AiContextualConsentPolicy.CanContinue(Started(), Reference(), live, Id(8), Id(99)));
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("unverified", false)]
    [InlineData("same-operation", false)]
    [InlineData("new-request", true)]
    public void F_RefusalRequiresExplicitNewRequestForDifferentOperation(string evidence, bool expected)
    {
        var reference = Reference();
        var suppression = new AiConsentSuppression(reference.Operation.Actor, reference.Operation.ConversationId, Id(50));
        if (evidence == "same-operation") suppression = suppression with { DeclinedOperationId = reference.Operation.OperationId };
        var request = evidence == "none" ? null : new AiExplicitRecommendationRequest(reference, Id(51), evidence != "unverified");
        Assert.Equal(expected, AiContextualConsentPolicy.CanOffer(reference, Live() with { NowUtc = Now.AddDays(1) }, suppression, request));
        // Topic/emotion/time/resumption supply no explicit request evidence and cannot bypass this.
    }

    [Fact]
    public void F_SuppressionIsConversationScoped_AndNewOfferIsNotConsent()
    {
        var suppression = new AiConsentSuppression(Reference().Operation.Actor, Id(99), Id(50));
        Assert.True(AiContextualConsentPolicy.CanOffer(Reference(), Live(), suppression, null));
        Assert.False(AiContextualConsentPolicy.CanStart(Snapshot(AiConsentState.AwaitingConsent), Reference(), Live()));
        Assert.False(AiContextualConsentPolicy.CanOffer(Reference(), Live() with { ConversationAvailable = false }, null, null));
    }

    [Theory]
    [InlineData(-1L, true)]
    [InlineData(0L, false)]
    [InlineData(1L, false)]
    public void G_AuditExpiresIndependentlyAtFourteenDays(long ticks, bool expected)
    {
        var audit = new AiConsentAuditEvent(Id(70), Id(4), AiConsentDecision.Allow, "v1", Now);
        Assert.Equal(Now.AddDays(14), AiContextualConsentPolicy.AuditExpiresAt(audit.OccurredAtUtc));
        Assert.Equal(expected, AiContextualConsentPolicy.CanReadAudit(audit, Now.AddDays(14).AddTicks(ticks)));
        Assert.Equal(Now.AddDays(15), AiContextualConsentPolicy.AuditExpiresAt(audit.OccurredAtUtc.AddDays(1)));
    }

    [Theory]
    [InlineData(AiConsentRevocationReason.Withdrawal)]
    [InlineData(AiConsentRevocationReason.Logout)]
    [InlineData(AiConsentRevocationReason.ModuleSwitch)]
    [InlineData(AiConsentRevocationReason.AccountChange)]
    [InlineData(AiConsentRevocationReason.EligibilityLoss)]
    [InlineData(AiConsentRevocationReason.ConversationDeletion)]
    [InlineData(AiConsentRevocationReason.ConversationExpiry)]
    [InlineData(AiConsentRevocationReason.AccountDeletion)]
    public void E_AllInvalidationReasons_UseSameNonReusableRevokedState(AiConsentRevocationReason reason)
    {
        var revoked = Started() with { State = AiConsentState.Revoked, Revision = Id(9) };
        Assert.True(Enum.IsDefined(reason));
        Assert.True(AiContextualConsentPolicy.CanTransition(Started(), revoked));
        Assert.False(AiContextualConsentPolicy.CanContinue(revoked, Reference(), Live() with { NowUtc = Now.AddSeconds(2) }, Id(8), Id(9)));
    }

    [Fact]
    public void G_AuditReadAndReconciliation_DoNotRefreshOrAuthorize()
    {
        var audit = new AiConsentAuditEvent(Id(70), Id(4), AiConsentDecision.Allow, "v1", Now);
        var expiry = AiContextualConsentPolicy.AuditExpiresAt(audit.OccurredAtUtc);
        Assert.True(AiContextualConsentPolicy.CanReadAudit(audit, Now.AddDays(1)));
        Assert.Equal(expiry, AiContextualConsentPolicy.AuditExpiresAt(audit.OccurredAtUtc));
        Assert.False(AiContextualConsentPolicy.CanStart(null, Reference(), Live()));
        Assert.False(AiContextualConsentPolicy.CanReadAudit(audit with { OccurredAtUtc = DateTimeOffset.MaxValue }, Now));
    }
}
