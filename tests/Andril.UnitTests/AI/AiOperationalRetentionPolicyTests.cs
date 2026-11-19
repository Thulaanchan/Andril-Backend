using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Persistence;
using Andril.AI.Policies;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Pure production policy tests, synthetic approved-for-test evidence only.
/// No claim of real approval authenticity, durable cleanup, legal compliance or transaction safety.</summary>
public sealed class AiOperationalRetentionPolicyTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    private static AiOperationReference Operation(AiModule module = AiModule.Learning) => new(new(Id(1), Id(2), module), Id(3), Id(4));
    private static AiOperationalRecordReference Record(AiOperationalCategory category = AiOperationalCategory.OperationIdempotency) =>
        new(Id(5), category, category == AiOperationalCategory.SecurityAudit ? new AiRestrictedSecurityScope(Id(6), Id(1)) : new AiModuleOperationalScope(Operation()), Id(7));
    private static AiOperationalRetentionEvidence Evidence(AiOperationalRecordReference record)
    {
        // Explicit fictional approval/anchor rule, not a production retention clock decision.
        var approval = new AiOperationalAnchorApproval(record.Category, Id(10), "fixture-approved-revision", Id(11), Id(12));
        var identifiable = record.Scope is AiModuleOperationalScope or AiAccountOperationalScope || record.Scope is AiRestrictedSecurityScope { AccountId: not null };
        return new(approval, new(record, approval.PolicyId, approval.Revision, approval.ApprovalEvidenceId, approval.AnchorRuleId, Anchor),
            new(record, Id(13), AiOperationalResolutionState.TerminalReconciled, AiOperationalPendingWork.None),
            new(record, Id(14), Id(15), "fixture-replay-strategy"),
            new(record, Id(16), identifiable ? AiOperationalAccountState.NoConfirmedDeletion : AiOperationalAccountState.NotApplicable));
    }
    private static AiOperationalRetentionDecision Evaluate(AiOperationalRecordReference record, AiOperationalRetentionEvidence? evidence = null,
        DateTimeOffset? now = null) => AiOperationalRetentionPolicy.Evaluate(record, evidence ?? Evidence(record), now ?? Anchor.AddDays(40));

    [Theory]
    [InlineData(AiOperationalCategory.Quota, 14)]
    [InlineData(AiOperationalCategory.OperationIdempotency, 14)]
    [InlineData(AiOperationalCategory.Billing, 30)]
    [InlineData(AiOperationalCategory.SecurityAudit, 30)]
    public void A_ApprovedDuration_IsCategorySpecific(AiOperationalCategory category, int days) =>
        Assert.Equal(TimeSpan.FromDays(days), AiOperationalRetentionPolicy.Duration(category));

    [Theory]
    [InlineData(AiOperationalCategory.Unknown)]
    [InlineData((AiOperationalCategory)(-1))]
    [InlineData((AiOperationalCategory)99)]
    public void A_UnsupportedCategory_CannotAuthorizeCleanup(AiOperationalCategory category)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AiOperationalRetentionPolicy.Duration(category));
        var result = Evaluate(Record(category));
        Assert.Equal(AiOperationalRetentionOutcome.RetentionPolicyUnavailable, result.Outcome);
        Assert.True(result.OrdinaryAccessMustBeRestricted);
    }

    [Theory]
    [InlineData("missing-approval")]
    [InlineData("missing-anchor")]
    [InlineData("unapproved")]
    [InlineData("category")]
    [InlineData("revision")]
    [InlineData("rule")]
    [InlineData("record")]
    [InlineData("future")]
    [InlineData("default")]
    [InlineData("offset")]
    public void B_UnverifiedAnchor_NeverFallsBackToAnotherClock(string defect)
    {
        var record = Record();
        var evidence = Evidence(record);
        evidence = defect switch
        {
            "missing-approval" => evidence with { Approval = null },
            "missing-anchor" => evidence with { Anchor = null },
            "unapproved" => evidence with { Approval = evidence.Approval! with { ApprovalEvidenceId = Guid.Empty } },
            "category" => evidence with { Approval = evidence.Approval! with { Category = AiOperationalCategory.Billing } },
            "revision" => evidence with { Anchor = evidence.Anchor! with { PolicyRevision = "not-approved" } },
            "rule" => evidence with { Anchor = evidence.Anchor! with { AnchorRuleId = Id(99) } },
            "record" => evidence with { Anchor = evidence.Anchor! with { Record = record with { RecordId = Id(99) } } },
            "future" => evidence with { Anchor = evidence.Anchor! with { AnchorUtc = Anchor.AddDays(41) } },
            "default" => evidence with { Anchor = evidence.Anchor! with { AnchorUtc = default } },
            _ => evidence with { Anchor = evidence.Anchor! with { AnchorUtc = Anchor.ToOffset(TimeSpan.FromHours(1)) } }
        };
        var result = Evaluate(record, evidence);
        Assert.Equal(AiOperationalRetentionOutcome.RetentionPolicyUnavailable, result.Outcome);
        Assert.True(result.ReviewRequired);
        Assert.Null(result.ExpiresAtUtc);
    }

    [Fact]
    public void B_MissingEvidenceAndDateOverflow_BlockCleanup()
    {
        var record = Record();
        Assert.Equal(AiOperationalRetentionOutcome.RetentionPolicyUnavailable,
            AiOperationalRetentionPolicy.Evaluate(record, null, Anchor).Outcome);
        var evidence = Evidence(record);
        evidence = evidence with { Anchor = evidence.Anchor! with { AnchorUtc = DateTimeOffset.MaxValue.AddDays(-1) } };
        Assert.Equal(AiOperationalRetentionOutcome.RetentionPolicyUnavailable, Evaluate(record, evidence, DateTimeOffset.MaxValue).Outcome);
        Assert.Equal(AiOperationalRetentionOutcome.RetentionPolicyUnavailable, Evaluate(record, now: default(DateTimeOffset)).Outcome);
    }

    [Theory]
    [InlineData(AiOperationalCategory.Quota, -1L, AiOperationalRetentionOutcome.NotYetExpired)]
    [InlineData(AiOperationalCategory.Quota, 0L, AiOperationalRetentionOutcome.EligibleForCleanup)]
    [InlineData(AiOperationalCategory.Quota, 1L, AiOperationalRetentionOutcome.EligibleForCleanup)]
    [InlineData(AiOperationalCategory.OperationIdempotency, -1L, AiOperationalRetentionOutcome.NotYetExpired)]
    [InlineData(AiOperationalCategory.OperationIdempotency, 0L, AiOperationalRetentionOutcome.EligibleForCleanup)]
    [InlineData(AiOperationalCategory.OperationIdempotency, 1L, AiOperationalRetentionOutcome.EligibleForCleanup)]
    [InlineData(AiOperationalCategory.Billing, -1L, AiOperationalRetentionOutcome.NotYetExpired)]
    [InlineData(AiOperationalCategory.Billing, 0L, AiOperationalRetentionOutcome.EligibleForCleanup)]
    [InlineData(AiOperationalCategory.Billing, 1L, AiOperationalRetentionOutcome.EligibleForCleanup)]
    [InlineData(AiOperationalCategory.SecurityAudit, -1L, AiOperationalRetentionOutcome.NotYetExpired)]
    [InlineData(AiOperationalCategory.SecurityAudit, 0L, AiOperationalRetentionOutcome.EligibleForCleanup)]
    [InlineData(AiOperationalCategory.SecurityAudit, 1L, AiOperationalRetentionOutcome.EligibleForCleanup)]
    public void C_Expiry_IsInclusiveAndStable(AiOperationalCategory category, long ticks, AiOperationalRetentionOutcome expected)
    {
        var record = Record(category);
        var expiry = Anchor.Add(AiOperationalRetentionPolicy.Duration(category));
        var evidence = Evidence(record);
        var now = expiry.AddTicks(ticks);
        var first = Evaluate(record, evidence, now);
        Assert.Equal(expected, first.Outcome);
        Assert.Equal(expiry, first.ExpiresAtUtc);
        Assert.Equal(first, Evaluate(record, evidence, now));
        Assert.Equal(expiry, Evaluate(record, evidence, now.AddDays(1)).ExpiresAtUtc);
        Assert.Equal(AiOperationalRetentionOutcome.RetentionPolicyUnavailable, Evaluate(record, evidence, now.ToOffset(TimeSpan.FromHours(5.5))).Outcome);
        // Authority supplies normalized UTC; device timezone never selects a clock or renews it.
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public void D_ModuleOperationScopes_AreIndependent(AiModule module)
    {
        var record = Record() with { Scope = new AiModuleOperationalScope(Operation(module)) };
        Assert.True(AiOperationalRetentionPolicy.ValidReference(record));
        Assert.Equal(AiOperationalRetentionOutcome.EligibleForCleanup, Evaluate(record).Outcome);
    }

    [Fact]
    public void D_AggregatesDoNotNeedFabricatedModuleProfiles()
    {
        var account = Record(AiOperationalCategory.Quota) with { Scope = new AiAccountOperationalScope(Id(1)) };
        var app = Record(AiOperationalCategory.Billing) with { Scope = new AiApplicationOperationalScope(Id(20)) };
        var security = Record(AiOperationalCategory.SecurityAudit) with { Scope = new AiRestrictedSecurityScope(Id(21)) };
        foreach (var record in new[] { account, app, security })
            Assert.Equal(AiOperationalRetentionOutcome.EligibleForCleanup, Evaluate(record).Outcome);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("module")]
    [InlineData("account")]
    [InlineData("application-category")]
    [InlineData("account-category")]
    [InlineData("security-category")]
    public void D_InvalidOrContradictoryScope_FailsClosed(string defect)
    {
        var record = defect switch
        {
            "missing" => Record() with { Scope = null! },
            "module" => Record() with { Scope = new AiModuleOperationalScope(Operation((AiModule)99)) },
            "account" => Record(AiOperationalCategory.Quota) with { Scope = new AiAccountOperationalScope(Guid.Empty) },
            "application-category" => Record() with { Scope = new AiApplicationOperationalScope(Id(20)) },
            "account-category" => Record(AiOperationalCategory.Billing) with { Scope = new AiAccountOperationalScope(Id(1)) },
            _ => Record() with { Scope = new AiRestrictedSecurityScope(Id(21)) }
        };
        Assert.False(AiOperationalRetentionPolicy.ValidReference(record));
        Assert.Equal(AiOperationalRetentionOutcome.RetentionPolicyUnavailable, Evaluate(record).Outcome);
    }

    [Theory]
    [InlineData(AiOperationalPendingWork.ActiveGeneration)]
    [InlineData(AiOperationalPendingWork.QuotaReservation)]
    [InlineData(AiOperationalPendingWork.BillingReconciliation)]
    [InlineData(AiOperationalPendingWork.UncertainProvider)]
    [InlineData(AiOperationalPendingWork.IdempotencyReconciliation)]
    [InlineData(AiOperationalPendingWork.AtomicCompletion)]
    [InlineData(AiOperationalPendingWork.UncertainCancellation)]
    [InlineData(AiOperationalPendingWork.RecoveryRevocation)]
    [InlineData((AiOperationalPendingWork)1024)]
    public void E_UnresolvedDependencies_BlockOldRecordCleanup(AiOperationalPendingWork pending)
    {
        var record = Record();
        var evidence = Evidence(record);
        evidence = evidence with { Resolution = evidence.Resolution! with { State = AiOperationalResolutionState.Unresolved, Pending = pending } };
        var result = Evaluate(record, evidence);
        Assert.Equal(AiOperationalRetentionOutcome.DeferredUnresolvedOperation, result.Outcome);
        Assert.True(result.ReviewRequired);
        Assert.True(result.OrdinaryAccessMustBeRestricted);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("contradiction")]
    [InlineData("revision")]
    public void E_UnknownOrStaleResolution_CannotAuthorizeCleanup(string defect)
    {
        var record = Record();
        var evidence = Evidence(record);
        evidence = evidence with { Resolution = defect switch
        {
            "missing" => null,
            "unknown" => evidence.Resolution! with { State = AiOperationalResolutionState.Unknown },
            "contradiction" => evidence.Resolution! with { Pending = AiOperationalPendingWork.QuotaReservation },
            _ => evidence.Resolution! with { Record = record with { SourceRevision = Id(99) } }
        } };
        Assert.Equal(AiOperationalRetentionOutcome.DeferredUnresolvedOperation, Evaluate(record, evidence).Outcome);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("approval")]
    [InlineData("revision")]
    [InlineData("source")]
    public void F_ReplayProof_IsRequiredAfterExpiry(string defect)
    {
        var record = Record();
        var evidence = Evidence(record);
        evidence = evidence with { Replay = defect switch
        {
            "missing" => null,
            "approval" => evidence.Replay! with { ApprovedStrategyId = Guid.Empty },
            "revision" => evidence.Replay! with { StrategyRevision = "" },
            _ => evidence.Replay! with { Record = record with { SourceRevision = Id(99) } }
        } };
        Assert.Equal(AiOperationalRetentionOutcome.DeferredReplayProtection, Evaluate(record, evidence).Outcome);
    }

    [Fact]
    public void F_RepeatedEligibility_IsPureAndDoesNotReleaseLiabilities()
    {
        var record = Record();
        var evidence = Evidence(record);
        var before = evidence;
        Assert.Equal(Evaluate(record, evidence), Evaluate(record, evidence));
        Assert.Equal(before, evidence);
        Assert.Equal(AiOperationalResolutionState.TerminalReconciled, evidence.Resolution!.State);
        // Actual cleanup versus quota/budget settlement/retry races require integration tests.
    }

    [Theory]
    [InlineData(AiOperationalDispositionKind.Erase, AiOperationalRetentionOutcome.EligibleForCleanup)]
    [InlineData(AiOperationalDispositionKind.Anonymize, AiOperationalRetentionOutcome.EligibleForAnonymization)]
    public void G_ApprovedSyntheticErasureDisposition_DoesNotWaitForOrdinaryRetention(AiOperationalDispositionKind kind, AiOperationalRetentionOutcome expected)
    {
        var record = Record();
        var evidence = Evidence(record);
        evidence = evidence with { Erasure = evidence.Erasure! with { State = AiOperationalAccountState.ConfirmedDeleted,
            Disposition = new(record, Id(30), "fixture-erasure-policy", kind, Anchor.AddDays(50)) } };
        var result = Evaluate(record, evidence, Anchor.AddDays(1));
        Assert.Equal(expected, result.Outcome);
        Assert.True(result.OrdinaryAccessMustBeRestricted);
        // Synthetic approval establishes contract behavior only, not legal erasure authorization.
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unverified")]
    [InlineData("scope")]
    [InlineData("expired")]
    [InlineData("unknown")]
    public void G_DeletedAccountNeedsSpecificDisposition(string defect)
    {
        var record = Record();
        var evidence = Evidence(record);
        var disposition = new AiOperationalDisposition(record, Id(30), "fixture-erasure-policy", AiOperationalDispositionKind.Erase, Anchor.AddDays(50));
        disposition = defect switch
        {
            "missing" => null,
            "unverified" => disposition with { ApprovalEvidenceId = Guid.Empty },
            "scope" => disposition with { Record = record with { RecordId = Id(99) } },
            "expired" => disposition with { ValidUntilUtc = Anchor },
            _ => disposition with { Kind = (AiOperationalDispositionKind)99 }
        };
        evidence = evidence with { Erasure = evidence.Erasure! with { State = AiOperationalAccountState.ConfirmedDeleted, Disposition = disposition } };
        Assert.Equal(AiOperationalRetentionOutcome.ErasureDispositionRequired, Evaluate(record, evidence).Outcome);
    }

    private static AiOperationalExceptionEvidence Exception(AiOperationalRecordReference record) =>
        new(record, Id(31), Id(32), Id(33), Id(34), Id(35), "fictional-approved-exception", Anchor.AddDays(50));

    [Theory]
    [InlineData("valid", AiOperationalRetentionOutcome.RestrictedRetentionException)]
    [InlineData("basis", AiOperationalRetentionOutcome.RetentionPolicyUnavailable)]
    [InlineData("fields", AiOperationalRetentionOutcome.RetentionPolicyUnavailable)]
    [InlineData("access", AiOperationalRetentionOutcome.RetentionPolicyUnavailable)]
    [InlineData("audit", AiOperationalRetentionOutcome.RetentionPolicyUnavailable)]
    [InlineData("expired", AiOperationalRetentionOutcome.RetentionPolicyUnavailable)]
    public void G_ExceptionNeedsNarrowFiniteEvidence(string defect, AiOperationalRetentionOutcome expected)
    {
        var record = Record();
        var exception = defect switch
        {
            "basis" => Exception(record) with { BasisId = Guid.Empty },
            "fields" => Exception(record) with { RetainedFieldSetId = Guid.Empty },
            "access" => Exception(record) with { AccessPolicyId = Guid.Empty },
            "audit" => Exception(record) with { AuditReviewEvidenceId = Guid.Empty },
            "expired" => Exception(record) with { RetainUntilUtc = Anchor },
            _ => Exception(record)
        };
        var result = Evaluate(record, Evidence(record) with { Exception = exception });
        Assert.Equal(expected, result.Outcome);
        Assert.True(result.ReviewRequired);
        Assert.True(result.OrdinaryAccessMustBeRestricted);
    }

    [Fact]
    public void G_ContradictoryRetentionAndErasure_RequireReview()
    {
        var record = Record();
        var evidence = Evidence(record);
        evidence = evidence with { Exception = Exception(record), Erasure = evidence.Erasure! with { State = AiOperationalAccountState.ConfirmedDeleted,
            Disposition = new(record, Id(30), "fixture-policy", AiOperationalDispositionKind.Erase, Anchor.AddDays(50)) } };
        Assert.Equal(AiOperationalRetentionOutcome.ErasureDispositionRequired, Evaluate(record, evidence).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void G_DeletedAccountRetention_RequiresCompleteExceptionEvidence(bool approvedExceptionPresent)
    {
        var record = Record();
        var evidence = Evidence(record);
        evidence = evidence with
        {
            Erasure = evidence.Erasure! with
            {
                State = AiOperationalAccountState.ConfirmedDeleted,
                Disposition = new(record, Id(30), "fixture-policy", AiOperationalDispositionKind.RetainUntil, Anchor.AddDays(50), Anchor.AddDays(50))
            },
            Exception = approvedExceptionPresent ? Exception(record) : null
        };
        Assert.Equal(approvedExceptionPresent ? AiOperationalRetentionOutcome.RestrictedRetentionException : AiOperationalRetentionOutcome.ErasureDispositionRequired,
            Evaluate(record, evidence).Outcome);
        var expired = Evaluate(record, evidence, Anchor.AddDays(50));
        Assert.True(expired.ReviewRequired);
        Assert.True(expired.OrdinaryAccessMustBeRestricted);
        Assert.NotEqual(AiOperationalRetentionOutcome.EligibleForCleanup, expired.Outcome);
    }

    [Theory]
    [InlineData("record")]
    [InlineData("revision")]
    [InlineData("erasure")]
    [InlineData("unapproved-account-state")]
    public void I_MalformedIdentityOrErasureEvidence_CannotAuthorizeCleanup(string defect)
    {
        var record = Record();
        var evidence = Evidence(record);
        if (defect == "record") record = record with { RecordId = Guid.Empty };
        if (defect == "revision") record = record with { SourceRevision = Guid.Empty };
        if (defect == "erasure") evidence = evidence with { Erasure = null };
        if (defect == "unapproved-account-state") evidence = evidence with { Erasure = evidence.Erasure! with { State = AiOperationalAccountState.Unknown } };
        var decision = Evaluate(record, evidence);
        Assert.True(decision.OrdinaryAccessMustBeRestricted);
        Assert.True(decision.ReviewRequired);
        Assert.NotEqual(AiOperationalRetentionOutcome.EligibleForCleanup, decision.Outcome);
    }

    [Fact]
    public void H_ProductionConversationAndConsentPolicies_RemainIndependent()
    {
        var assembly = typeof(AiConversationEntity).Assembly;
        Assert.Same(assembly, typeof(AiConversationRetentionPolicy).Assembly);
        Assert.Equal(assembly, typeof(AiOperationalRetentionPolicy).Assembly);
        Assert.Equal(TimeSpan.FromHours(24), AiConversationRetentionPolicy.ActiveDataPurgeTarget);
        Assert.Equal(Anchor.AddDays(14), AiContextualConsentPolicy.AuditExpiresAt(Anchor));
        Assert.Equal(Anchor.AddDays(14), AiConversationExpiryPolicy.GetExpiresAtUtc(Anchor));
        var actor = Operation().Actor;
        var conversation = AiConversationEntity.CreateForFirstUserMessage(actor, Id(90), Anchor);
        var expiry = conversation.ExpiresAtUtc;
        Evaluate(Record(AiOperationalCategory.Billing));
        Assert.Equal(expiry, conversation.ExpiresAtUtc);
        conversation.MarkDeleted(Anchor.AddHours(1));
        Assert.True(AiConversationRetentionPolicy.MustHideFromHistory(conversation, Anchor.AddHours(1)));
        Assert.Equal(Anchor.AddHours(25), AiConversationRetentionPolicy.GetActivePurgeTargetUtc(conversation, Anchor.AddHours(1)));
        Assert.False(AiContextualConsentPolicy.CanStart(null, null!, null));
    }

    [Fact]
    public void I_DiagnosticsOmitSensitiveIdentifiers_AndMissingEvidenceRestrictsAccess()
    {
        var record = Record();
        var evidence = Evidence(record);
        foreach (var value in new object[] { record, record.Scope, evidence, evidence.Approval!, evidence.Anchor!, evidence.Resolution!,
            evidence.Replay!, evidence.Erasure!, Exception(record), new AiOperationalCleanupCandidate(record) })
        {
            Assert.DoesNotContain(Id(1).ToString(), value.ToString());
            Assert.DoesNotContain(Id(5).ToString(), value.ToString());
            Assert.DoesNotContain(Id(10).ToString(), value.ToString());
        }
        var result = AiOperationalRetentionPolicy.Evaluate(record, null, Anchor);
        Assert.True(result.OrdinaryAccessMustBeRestricted);
        Assert.True(result.ReviewRequired);
        Assert.Equal(AiOperationalRetentionOutcome.RetentionPolicyUnavailable, result.Outcome);
    }
}
