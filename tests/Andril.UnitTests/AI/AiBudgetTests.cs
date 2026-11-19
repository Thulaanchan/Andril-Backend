using Andril.AI.ChatBox;
using Andril.AI.DTOs;
using Andril.AI.Interfaces;
using Andril.AI.Policies;
using Andril.AI.Services;
using Xunit;

namespace Andril.UnitTests.AI;

/// <summary>Synthetic prices and evidence only. Fakes do not prove PostgreSQL safety.</summary>
public sealed class AiBudgetTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
    private static AiAuthorizedActor Actor(AiModule module = AiModule.Learning) => new(Id(1), Id(10 + (int)module), module);
    private static AiGenerationAttemptReference Attempt(AiModule module = AiModule.Learning) => new(new(Actor(module), Id(20), Id(21)), Id(22));
    private static AiBudgetWindow Window() => new(Id(30), Start, Start.AddDays(14));
    // FICTIONAL fixture rates, NOT live provider pricing.
    private static AiPricingEvidence Price() => new("fictional-text", "fixture-v1", 1m, 2m, true, AiBillingCoverage.ApprovedComplete);
    private static AiTokenEvidence Tokens() => new("fictional-text", new string('A', 64), 100, 100, 100, 100, 100, 500, 500, true, true);
    private static AiBudgetPreparation Preparation(AiModule module = AiModule.Learning) => new(Attempt(module),
        new(Attempt(module).Operation, AiOperationStatus.Reserved, AiQuotaState.Reserved, AiDispatchState.NotDispatched,
            Start, DateOnly.FromDateTime(Start.UtcDateTime), Id(40), Attempt(module).AttemptId),
        Window(), Start, AiExecutionMode.PaidText, Tokens(), Price(), Measurement(), Binding());
    private static AiBudgetReservation Reservation(AiModule module = AiModule.Learning) => new(Attempt(module), Window(),
        Price().Model, Price().Revision, Tokens().RequestFingerprint, AiBudgetPolicy.Estimate(AiExecutionMode.PaidText, Tokens(), Price()), AiBillingState.Reserved, Measurement: Measurement());
    private static AiProviderBillingEvidence Bill(decimal actual = 0.001m) => new(Id(50), Attempt(), Window().Id,
        Price().Model, Price().Revision, AiBillingEvidenceKind.Final, 500, 250, actual, true,
        new(500, 250, 750, null, null, null, null, null), AiBillingCoverage.ApprovedComplete);

    private static AiProviderPayloadBinding Binding() => new(Tokens().RequestFingerprint, null,
        new string('B', 64), Price().Model, "synthetic-framing-v1", "synthetic-config", 500);
    private static AiTokenCountResult Measurement() => new(AiTokenCountState.Measured, Tokens(),
        new("synthetic-counter", "fixture-v1", "synthetic-framing-v1"), Binding());

    [Theory]
    [InlineData("payload")]
    [InlineData("configuration")]
    [InlineData("provenance")]
    public void Correction_ReservationReplayCannotChangeRetainedMeasurement(string defect)
    {
        var stored = Reservation();
        Assert.True(AiBudgetPolicy.SameReservation(stored, Preparation()));
        var measured = Measurement();
        measured = defect switch
        {
            "payload" => measured with { PayloadBinding = Binding() with { PayloadSha256 = new string('C', 64) } },
            "configuration" => measured with { PayloadBinding = Binding() with { ExecutionConfiguration = "different" } },
            _ => measured with { Provenance = measured.Provenance! with { CounterRevision = "different" } }
        };
        var changed = Preparation() with { Measurement = measured, ExpectedPayloadBinding = measured.PayloadBinding };
        AiBudgetPolicy.ValidatePreparation(changed, Attempt());
        Assert.False(AiBudgetPolicy.SameReservation(stored, changed));
        Assert.Equal(Measurement(), stored.Measurement);
    }

    [Theory]
    [InlineData("measurement")]
    [InlineData("payload")]
    [InlineData("provenance")]
    [InlineData("configuration")]
    [InlineData("coverage")]
    public void Correction_PaidReservationRejectsIncompleteBindings(string defect)
    {
        var p = Preparation();
        p = defect switch
        {
            "measurement" => p with { Measurement = null },
            "payload" => p with { ExpectedPayloadBinding = Binding() with { PayloadSha256 = new string('C', 64) } },
            "provenance" => p with { Measurement = Measurement() with { Provenance = null } },
            "configuration" => p with { ExpectedPayloadBinding = Binding() with { ExecutionConfiguration = "other" } },
            _ => p with { Pricing = Price() with { Coverage = AiBillingCoverage.Unknown } }
        };
        Assert.Throws<InvalidOperationException>(() => AiBudgetPolicy.ValidatePreparation(p, Attempt()));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unsupported")]
    [InlineData("total")]
    [InlineData("reasoning")]
    [InlineData("cache")]
    public void Correction_IncompleteBillingCannotReleaseReservation(string defect)
    {
        var bill = Bill();
        bill = defect switch
        {
            "missing" => bill with { Usage = null },
            "unsupported" => bill with { Coverage = AiBillingCoverage.Incomplete,
                Usage = bill.Usage! with { CacheWriteTokens = 30 } },
            "total" => bill with { Usage = bill.Usage! with { TotalTokens = 1 } },
            "reasoning" => bill with { Usage = bill.Usage! with { ReasoningTokens = 251 } },
            _ => bill with { Usage = bill.Usage! with { CacheWriteTokens = 100, CacheWrite30MinuteTokens = 50, CacheWrite12HourTokens = 51 } }
        };
        var original = Reservation();
        Assert.Throws<InvalidOperationException>(() => AiBudgetPolicy.Settle(original, bill));
        Assert.Equal(AiBillingState.Reserved, original.State);
    }

    [Fact]
    public void Correction_ActualCacheAndReasoningObservationsAreRetainedWithoutClamping()
    {
        var bill = Bill(6m) with { InputTokens = 3000, BillableOutputTokens = 700,
            Usage = new(3000, 700, 3700, 100, 500, 300, 100, 200) };
        var settled = AiBudgetPolicy.Settle(Reservation(), bill);
        Assert.Equal(6m, settled.AdditionalCommittedUsd);
        Assert.Equal(bill.Usage, settled.Reservation.FinalEvidence!.Usage);
        Assert.Equal(700, settled.Reservation.FinalEvidence.BillableOutputTokens);
    }

    [Fact]
    public void A_ApprovedLimits_MatchExistingPolicies()
    {
        Assert.Equal(6, AiPrototypeLimits.AllowlistedAccounts);
        Assert.Equal(20, AiPrototypeLimits.DailyQuota);
        Assert.Equal(3, AiPrototypeLimits.AccountPerMinute);
        Assert.Equal(10, AiPrototypeLimits.GlobalPerMinute);
        Assert.Equal(500, AiPrototypeLimits.MaxUserTextElements);
        Assert.Equal(2000, AiPrototypeLimits.MaxInputTokens);
        Assert.Equal(500, AiPrototypeLimits.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromDays(14), AiPrototypeLimits.Duration);
        Assert.Equal(5m, AiPrototypeLimits.BudgetUsd);
        Assert.Equal(2.5m, AiPrototypeLimits.FirstAlertUsd);
        Assert.Equal(4m, AiPrototypeLimits.SecondAlertUsd);
        Assert.Equal(AiOperationPolicy.TesterCount, AiPrototypeLimits.AllowlistedAccounts);
        Assert.Equal(AiOperationPolicy.DailySlots, AiPrototypeLimits.DailyQuota);
        Assert.Equal(AiOperationPolicy.AccountPerMinute, AiPrototypeLimits.AccountPerMinute);
        Assert.Equal(AiOperationPolicy.GlobalPerMinute, AiPrototypeLimits.GlobalPerMinute);
        Assert.Equal(AiOperationPolicy.MaxTextElements, AiPrototypeLimits.MaxUserTextElements);
        Assert.Equal(AiConversationContextPolicy.MaxInputTokens, AiPrototypeLimits.MaxInputTokens);
    }

    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(2000, 500, true)]
    [InlineData(2001, 500, false)]
    [InlineData(2000, 501, false)]
    [InlineData(-1, 500, false)]
    [InlineData(0, 500, false)]
    [InlineData(2000, -1, false)]
    [InlineData(2000, 0, false)]
    public void B_TokenBoundaries_AreIndependentOfCharacters(int input, int output, bool valid)
    {
        var tokens = Tokens() with { SystemTokens = 0, UserTokens = input - 1, HistoryTokens = 0, FramingTokens = 1,
            OtherInputTokens = 0, FullInputTokens = input, MaxBillableOutputTokens = output };
        Assert.Equal(valid, AiBudgetPolicy.ValidTokens(tokens, Price().Model));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("model")]
    [InlineData("fingerprint")]
    [InlineData("unverified")]
    [InlineData("reasoning")]
    [InlineData("components")]
    [InlineData("negative")]
    [InlineData("overflow")]
    public void B_FullRequestEvidence_MustBeVerifiable(string defect)
    {
        var tokens = Tokens();
        tokens = defect switch
        {
            "missing" => null,
            "model" => tokens with { Model = "other-model" },
            "fingerprint" => tokens with { RequestFingerprint = "" },
            "unverified" => tokens with { FullRequestVerified = false },
            "reasoning" => tokens with { OutputIncludesReasoning = false },
            "components" => tokens with { FullInputTokens = 100 },
            "negative" => tokens with { HistoryTokens = -1 },
            _ => tokens with { SystemTokens = int.MaxValue, HistoryTokens = int.MaxValue }
        };
        Assert.False(AiBudgetPolicy.ValidTokens(tokens, Price().Model));
        Assert.Throws<InvalidOperationException>(() => AiBudgetPolicy.Estimate(AiExecutionMode.PaidText, tokens, Price()));
    }

    [Fact]
    public void C_Estimate_UsesSeparateRatesMaximumOutputAndUpwardRounding()
    {
        Assert.Equal(0.0015m, AiBudgetPolicy.Estimate(AiExecutionMode.PaidText, Tokens(), Price()));
        Assert.Equal(0.000001m, AiBudgetPolicy.RoundUp(0.00000001m));
        Assert.Equal(0.123457m, AiBudgetPolicy.RoundUp(0.12345601m));
        Assert.Equal(0.123456m, AiBudgetPolicy.RoundUp(0.123456m));
        Assert.Equal(0m, AiBudgetPolicy.Estimate(AiExecutionMode.Mock, null, null));
        Assert.Throws<InvalidOperationException>(() => AiBudgetPolicy.RoundUp(-1));
        Assert.Throws<InvalidOperationException>(() => AiBudgetPolicy.RoundUp(decimal.MaxValue));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("model")]
    [InlineData("revision")]
    [InlineData("tools")]
    [InlineData("overflow")]
    public void C_InvalidPricing_BlocksPaidEstimate(string defect)
    {
        var price = Price();
        price = defect switch
        {
            "missing" => null,
            "zero" => price with { InputUsdPerMillion = 0 },
            "negative" => price with { OutputUsdPerMillion = -1 },
            "model" => price with { Model = "" },
            "revision" => price with { Revision = "" },
            "tools" => price with { SupportedTextOnly = false },
            _ => price with { InputUsdPerMillion = decimal.MaxValue }
        };
        Assert.Throws<InvalidOperationException>(() => AiBudgetPolicy.Estimate(AiExecutionMode.PaidText, Tokens(), price));
    }

    [Theory]
    [InlineData("0", "0", "5", true)]
    [InlineData("4.90", "0", "0.10", true)]
    [InlineData("4.90", "0", "0.11", false)]
    [InlineData("4", "1", "0.01", false)]
    [InlineData("5", "0", "0.01", false)]
    [InlineData("6", "0", "0.01", false)]
    [InlineData("-1", "0", "0.01", false)]
    [InlineData("0", "-1", "0.01", false)]
    [InlineData("0", "0", "0", false)]
    public void D_Capacity_UsesSharedCommittedAndOutstanding(string committed, string reserved, string amount, bool expected)
    {
        decimal Parse(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, AiBudgetPolicy.CanReserve(new(Parse(committed), Parse(reserved)), Parse(amount), Window(), Start));
    }

    [Fact]
    public void D_OverflowAndOverCapBalances_DoNotCreateCapacity()
    {
        Assert.False(AiBudgetPolicy.CanReserve(new(decimal.MaxValue, 1), 0.1m, Window(), Start));
        Assert.False(AiBudgetPolicy.CanReserve(null, 0.1m, Window(), Start));
        Assert.Equal(-1m, AiBudgetPolicy.Available(new(6, 0)));
        Assert.False(AiBudgetPolicy.CanReserve(new(5, 0), 1, Window(), Start.AddDays(1)));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(1209599, true)]
    [InlineData(1209600, false)]
    [InlineData(1209601, false)]
    public void E_Window_IsStartInclusiveEndExclusive(int seconds, bool expected)
    {
        Assert.Equal(expected, AiBudgetPolicy.WindowOpen(Window(), Start.AddSeconds(seconds)));
    }

    [Fact]
    public void E_InvalidWindowAndMidnight_DoNotResetBudget()
    {
        Assert.False(AiBudgetPolicy.ValidWindow(null));
        Assert.False(AiBudgetPolicy.ValidWindow(Window() with { Id = Guid.Empty }));
        Assert.False(AiBudgetPolicy.ValidWindow(Window() with { EndUtc = Start.AddDays(15) }));
        Assert.False(AiBudgetPolicy.ValidWindow(Window() with { StartUtc = Start.ToOffset(TimeSpan.FromHours(1)) }));
        Assert.False(AiBudgetPolicy.CanReserve(new(5, 0), 0.001m, Window(), Start.AddDays(1)));
        // Settlement has no new-admission window-open gate: historical liability survives expiry.
        Assert.Equal(AiBudgetOutcome.Settled, AiBudgetPolicy.Settle(Reservation(), Bill()).Outcome);
    }

    [Theory]
    [InlineData("same", true)]
    [InlineData("revision", false)]
    [InlineData("tokens", false)]
    [InlineData("attempt", false)]
    [InlineData("window", false)]
    public void F_ReservationIdentity_RequiresImmutableDetails(string defect, bool expected)
    {
        var preparation = Preparation();
        preparation = defect switch
        {
            "revision" => preparation with { Pricing = Price() with { Revision = "fixture-v2" } },
            "tokens" => preparation with { Tokens = Tokens() with { RequestFingerprint = "changed" } },
            "attempt" => preparation with { Attempt = Attempt() with { AttemptId = Id(99) } },
            "window" => preparation with { Window = Window() with { Id = Id(99) } },
            _ => preparation
        };
        Assert.Equal(expected, AiBudgetPolicy.SameReservation(Reservation(), preparation));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.001")]
    [InlineData("0.0015")]
    [InlineData("0.1")]
    [InlineData("6")]
    public void G_FinalCost_IsRecordedWithoutClamping(string actualText)
    {
        var actual = decimal.Parse(actualText, System.Globalization.CultureInfo.InvariantCulture);
        var result = AiBudgetPolicy.Settle(Reservation(), Bill(actual));
        Assert.Equal(AiBudgetOutcome.Settled, result.Outcome);
        Assert.Equal(actual, result.AdditionalCommittedUsd);
        Assert.Equal(actual, result.Reservation.ActualUsd);
        Assert.Equal(Math.Max(0, Reservation().ReservedUsd - actual), result.ReleasedExposureUsd);
        Assert.True(AiBudgetPolicy.ValidReservation(result.Reservation));
    }

    [Theory]
    [InlineData("same", AiBudgetOutcome.Existing)]
    [InlineData("cost", AiBudgetOutcome.Conflict)]
    [InlineData("usage", AiBudgetOutcome.Conflict)]
    [InlineData("event", AiBudgetOutcome.Conflict)]
    public void G_DuplicateBilling_DoesNotDoubleCharge(string change, AiBudgetOutcome expected)
    {
        var bill = Bill();
        var settled = AiBudgetPolicy.Settle(Reservation(), bill).Reservation;
        bill = change switch
        {
            "cost" => bill with { ActualUsd = 2m },
            "usage" => bill with { InputTokens = 501, Usage = bill.Usage! with { InputTokens = 501, TotalTokens = 751 } },
            "event" => bill with { EventId = Id(99) },
            _ => bill
        };
        var duplicate = AiBudgetPolicy.Settle(settled, bill);
        Assert.Equal(expected, duplicate.Outcome);
        Assert.Equal(0m, duplicate.AdditionalCommittedUsd);
        Assert.Equal(0m, duplicate.ReleasedExposureUsd);
        Assert.Equal(settled, duplicate.Reservation);
    }

    [Fact]
    public void G_UncertainBilling_HoldsAllExposure()
    {
        var result = AiBudgetPolicy.Settle(Reservation(), Bill() with { Kind = AiBillingEvidenceKind.Uncertain, ActualUsd = null, UsageVerified = false });
        Assert.Equal(AiBudgetOutcome.Pending, result.Outcome);
        Assert.Equal(AiBillingState.Pending, result.Reservation.State);
        Assert.Equal(Reservation().ReservedUsd, result.Reservation.ReservedUsd);
        Assert.Equal(0m, result.ReleasedExposureUsd);
        Assert.Null(result.Reservation.ActualUsd);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unverified")]
    [InlineData("negative")]
    [InlineData("scope")]
    [InlineData("revision")]
    [InlineData("missing-cost")]
    public void G_UntrustedBilling_FailsClosed(string defect)
    {
        var bill = defect switch
        {
            "unknown" => Bill() with { Kind = (AiBillingEvidenceKind)99 },
            "unverified" => Bill() with { UsageVerified = false },
            "negative" => Bill() with { ActualUsd = -1 },
            "scope" => Bill() with { Attempt = Attempt() with { AttemptId = Id(99) } },
            "revision" => Bill() with { PricingRevision = "wrong" },
            _ => Bill() with { ActualUsd = null }
        };
        Assert.Throws<InvalidOperationException>(() => AiBudgetPolicy.Settle(Reservation(), bill));
    }

    [Theory]
    [InlineData("0", "2.49", false, false, false)]
    [InlineData("0", "2.50", true, false, false)]
    [InlineData("2.50", "4", false, true, false)]
    [InlineData("0", "5", true, true, true)]
    [InlineData("4", "4", false, false, false)]
    [InlineData("5", "2", false, false, false)]
    [InlineData("2", "2.5", true, false, false)]
    public void H_Warnings_AreUpwardExposureCrossings(string before, string after, bool first, bool second, bool cap)
    {
        decimal Parse(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new AiBudgetWarnings(first, second, cap), AiBudgetPolicy.Crossings(new(Parse(before), 0), new(0, Parse(after))));
    }

    [Theory]
    [InlineData("privacy")]
    [InlineData("authorization")]
    [InlineData("evidence")]
    [InlineData("repository")]
    public void I_NullDependencies_RejectWithParameter(string name)
    {
        var setup = new Setup();
        var error = Assert.Throws<ArgumentNullException>(() => new AiBudgetService(name == "privacy" ? null! : setup.Privacy,
            name == "authorization" ? null! : setup.Auth, name == "evidence" ? null! : setup.Evidence, name == "repository" ? null! : setup.Repo));
        Assert.Equal(name, error.ParamName);
    }

    [Theory]
    [InlineData(AiModule.Learning)]
    [InlineData(AiModule.Hobbies)]
    [InlineData(AiModule.Social)]
    public async Task F_ServiceReservation_ForwardsTrustedEvidenceAndScope(AiModule module)
    {
        var setup = new Setup();
        setup.Auth.Actor = Actor(module);
        setup.Evidence.Preparation = Preparation(module);
        setup.Repo.Result = new(AiBudgetOutcome.Reserved, Reservation(module));
        using var cts = new CancellationTokenSource();

        Assert.Equal(setup.Repo.Result, await setup.Service.ReserveAsync(Attempt(module), Id(60), cts.Token));

        Assert.Equal((Attempt(module), Id(60), cts.Token), Assert.Single(setup.Evidence.Preparations));
        Assert.Equal((Preparation(module), Reservation(module).ReservedUsd, cts.Token), Assert.Single(setup.Repo.Reservations));
        Assert.Equal(2, setup.Gate.Calls.Count);
        Assert.All(setup.Auth.Calls, call => Assert.Equal((module, cts.Token), call));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("module")]
    [InlineData("attempt")]
    [InlineData("admission")]
    [InlineData("price")]
    [InlineData("tokens")]
    [InlineData("binding")]
    [InlineData("window")]
    [InlineData("missing")]
    public async Task F_InvalidPreparation_NeverReachesReservation(string defect)
    {
        var setup = new Setup();
        var preparation = Preparation();
        setup.Evidence.Preparation = defect switch
        {
            "missing" => null,
            "account" => preparation with { Attempt = Attempt() with { Operation = Attempt().Operation with { Actor = Actor() with { UserId = Id(99) } } } },
            "profile" => preparation with { Attempt = Attempt() with { Operation = Attempt().Operation with { Actor = Actor() with { ModuleProfileId = Id(99) } } } },
            "module" => preparation with { Attempt = Attempt(AiModule.Social) },
            "attempt" => preparation with { Attempt = Attempt() with { AttemptId = Guid.Empty } },
            "admission" => preparation with { Operation = preparation.Operation with { Status = AiOperationStatus.Unknown } },
            "price" => preparation with { Pricing = null },
            "tokens" => preparation with { Tokens = null },
            "binding" => preparation with { Operation = preparation.Operation with { AttemptId = null } },
            _ => preparation with { NowUtc = Window().EndUtc }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ReserveAsync(Attempt(), Id(60)));
        Assert.Empty(setup.Repo.Reservations);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("module")]
    [InlineData("privacy")]
    public async Task I_IdentityAndPrivacyDenial_BlocksEvidenceAccess(string defect)
    {
        var setup = new Setup();
        setup.Auth.Actor = defect switch
        {
            "denied" => null,
            "account" => Actor() with { UserId = Guid.Empty },
            "profile" => Actor() with { ModuleProfileId = Guid.Empty },
            "module" => Actor(AiModule.Social),
            _ => Actor()
        };
        if (defect == "privacy") setup.Gate.Outcome = AiPrivacyNoticeGateOutcome.NoticeRequired;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.Service.ReserveAsync(Attempt(), Id(60)));
        Assert.Empty(setup.Evidence.Preparations);
        Assert.Empty(setup.Repo.Reservations);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("undefined")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("price")]
    [InlineData("cost")]
    [InlineData("denial-payload")]
    public async Task I_MalformedRepositoryResults_AreRejected(string defect)
    {
        var setup = new Setup();
        setup.Repo.Result = defect switch
        {
            "unknown" => new(AiBudgetOutcome.Unknown),
            "undefined" => new((AiBudgetOutcome)99),
            "missing" => new(AiBudgetOutcome.Reserved),
            "foreign" => new(AiBudgetOutcome.Reserved, Reservation(AiModule.Social)),
            "price" => new(AiBudgetOutcome.Reserved, Reservation() with { PricingRevision = "wrong" }),
            "cost" => new(AiBudgetOutcome.Reserved, Reservation() with { ReservedUsd = 1 }),
            _ => new(AiBudgetOutcome.Denied, Reservation())
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ReserveAsync(Attempt(), Id(60)));
        Assert.Single(setup.Repo.Reservations);
    }

    [Theory]
    [InlineData(AiBudgetOutcome.Insufficient)]
    [InlineData(AiBudgetOutcome.Denied)]
    [InlineData(AiBudgetOutcome.Conflict)]
    public async Task I_AuthoritativeDenial_IsReturnedWithoutRetry(AiBudgetOutcome outcome)
    {
        var setup = new Setup();
        setup.Repo.Result = new(outcome);
        Assert.Equal(outcome, (await setup.Service.ReserveAsync(Attempt(), Id(60))).Outcome);
        Assert.Single(setup.Repo.Reservations);
    }

    [Fact]
    public async Task F_DuplicateReservation_IsExistingWithoutAutomaticExecution()
    {
        var setup = new Setup();
        setup.Repo.Result = new(AiBudgetOutcome.Existing, Reservation());
        Assert.Equal(AiBudgetOutcome.Existing, (await setup.Service.ReserveAsync(Attempt(), Id(60))).Outcome);
        Assert.Equal(AiBudgetOutcome.Existing, (await setup.Service.ReserveAsync(Attempt(), Id(60))).Outcome);
        Assert.Equal(2, setup.Repo.Reservations.Count); // Explicit requests; fake proves no storage uniqueness.
    }

    [Fact]
    public async Task C_MockMode_CreatesNoPaidReservation()
    {
        var setup = new Setup();
        setup.Evidence.Preparation = Preparation() with { Mode = AiExecutionMode.Mock, Pricing = null, Tokens = null };
        Assert.Equal(AiBudgetOutcome.MockNoSpend, (await setup.Service.ReserveAsync(Attempt(), Id(60))).Outcome);
        Assert.Empty(setup.Repo.Reservations);
    }

    [Fact]
    public async Task G_SystemBilling_WorksAfterUserRevocationAndWindowClosure()
    {
        var setup = new Setup();
        setup.Auth.Actor = null;
        setup.Evidence.Preparation = Preparation() with { NowUtc = Window().EndUtc.AddDays(10) };
        setup.Evidence.Billing = Bill(6m);
        setup.Repo.Result = new(AiBudgetOutcome.Settled, AiBudgetPolicy.Settle(Reservation(), Bill(6m)).Reservation);
        using var cts = new CancellationTokenSource();

        Assert.Equal(6m, (await setup.Service.ReconcileSystemAsync(Id(61), cts.Token)).Reservation!.ActualUsd);

        Assert.Empty(setup.Auth.Calls);
        Assert.Empty(setup.Gate.Calls);
        Assert.Equal((Id(61), cts.Token), Assert.Single(setup.Evidence.BillingReads));
        Assert.Equal((Bill(6m), cts.Token), Assert.Single(setup.Repo.Billings));
        // No conversation content or daily-quota mutation is part of the billing API.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G_UntrustedSystemCallerOrEvidence_CannotSettle(bool throws)
    {
        var setup = new Setup();
        setup.Evidence.Billing = null;
        var denial = new UnauthorizedAccessException("Synthetic internal caller denial");
        if (throws) setup.Evidence.Failure = denial;
        var error = await Record.ExceptionAsync(() => setup.Service.ReconcileSystemAsync(Id(61)));
        if (throws) Assert.Same(denial, error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.Empty(setup.Repo.Billings);
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("during")]
    [InlineData("after")]
    public async Task I_Cancellation_DoesNotRefundOrRetry(string stage)
    {
        var setup = new Setup();
        using var cts = new CancellationTokenSource();
        if (stage == "pre") cts.Cancel();
        else setup.Repo.Handler = token =>
        {
            Assert.Equal(cts.Token, token);
            cts.Cancel();
            return stage == "during" ? Task.FromCanceled<AiBudgetResult>(token)
                : Task.FromResult(new AiBudgetResult(AiBudgetOutcome.Reserved, Reservation()));
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Service.ReserveAsync(Attempt(), Id(60), cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Equal(stage == "pre" ? 0 : 1, setup.Repo.Reservations.Count);
        Assert.Empty(setup.Repo.Billings);
    }

    [Theory]
    [InlineData("auth")]
    [InlineData("privacy")]
    [InlineData("evidence")]
    [InlineData("repository")]
    public async Task I_DependencyFailures_PropagateOriginalWithoutRetry(string stage)
    {
        var setup = new Setup();
        var failure = new InvalidOperationException("Synthetic dependency failure");
        if (stage == "auth") setup.Auth.Failure = failure;
        if (stage == "privacy") setup.Gate.Failure = failure;
        if (stage == "evidence") setup.Evidence.Failure = failure;
        if (stage == "repository") setup.Repo.Handler = _ => Task.FromException<AiBudgetResult>(failure);
        Assert.Same(failure, await Record.ExceptionAsync(() => setup.Service.ReserveAsync(Attempt(), Id(60))));
        Assert.Equal(stage == "repository" ? 1 : 0, setup.Repo.Reservations.Count);
    }

    [Fact]
    public async Task F_StatusLookup_RequiresOwnerButNoNewBudget()
    {
        var setup = new Setup();
        setup.Gate.Outcome = AiPrivacyNoticeGateOutcome.NoticeRequired;
        setup.Repo.Result = new(AiBudgetOutcome.Status, Reservation());
        Assert.Equal(setup.Repo.Result, await setup.Service.ReadStatusAsync(Attempt()));
        setup.Auth.Actor = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.Service.ReadStatusAsync(Attempt()));
        Assert.Single(setup.Repo.Statuses);
        Assert.Empty(setup.Gate.Calls);
    }

    [Fact]
    public void C_SubMicroDecimalRemainder_IsNeverRoundedAway()
    {
        var price = Price() with { InputUsdPerMillion = 0.0020000000000000000000000001m, OutputUsdPerMillion = 0.002m };
        // Numerator is just above 2 micro-USD; ordinary decimal multiplication can lose that fraction.
        Assert.Equal(0.000003m, AiBudgetPolicy.Estimate(AiExecutionMode.PaidText, Tokens(), price));
        Assert.Throws<InvalidOperationException>(() => AiBudgetPolicy.Estimate((AiExecutionMode)99, Tokens(), Price()));
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("operation")]
    [InlineData("conversation")]
    [InlineData("evidence")]
    public async Task F_MissingReferences_AreRejectedBeforeEvidenceAccess(string defect)
    {
        var setup = new Setup();
        var attempt = defect switch
        {
            "attempt" => Attempt() with { AttemptId = Guid.Empty },
            "operation" => Attempt() with { Operation = Attempt().Operation with { OperationId = Guid.Empty } },
            "conversation" => Attempt() with { Operation = Attempt().Operation with { ConversationId = Guid.Empty } },
            _ => Attempt()
        };
        await Assert.ThrowsAsync<ArgumentException>(() => setup.Service.ReserveAsync(attempt, defect == "evidence" ? Guid.Empty : Id(60)));
        Assert.Empty(setup.Evidence.Preparations);
    }

    [Fact]
    public async Task G_ServicePendingBilling_RetainsReservation()
    {
        var setup = new Setup();
        var uncertain = Bill() with { Kind = AiBillingEvidenceKind.Uncertain, ActualUsd = null, UsageVerified = false };
        setup.Evidence.Billing = uncertain;
        setup.Repo.Result = new(AiBudgetOutcome.Pending, AiBudgetPolicy.Settle(Reservation(), uncertain).Reservation);
        var result = await setup.Service.ReconcileSystemAsync(Id(61));
        Assert.Equal(Reservation().ReservedUsd, result.Reservation!.ReservedUsd);
        Assert.Equal(AiBillingState.Pending, result.Reservation.State);
        Assert.Single(setup.Repo.Billings);
    }

    [Theory]
    [InlineData("cost")]
    [InlineData("usage")]
    [InlineData("scope")]
    [InlineData("state")]
    [InlineData("unknown")]
    public async Task G_ServiceRejectsMismatchedSettlement(string defect)
    {
        var setup = new Setup();
        setup.Evidence.Billing = Bill();
        var reservation = AiBudgetPolicy.Settle(Reservation(), Bill()).Reservation;
        if (defect == "cost") reservation = AiBudgetPolicy.Settle(Reservation(), Bill(1m)).Reservation;
        if (defect == "usage") reservation = AiBudgetPolicy.Settle(Reservation(), Bill() with { InputTokens = 501, Usage = Bill().Usage! with { InputTokens = 501, TotalTokens = 751 } }).Reservation;
        if (defect == "scope") reservation = reservation with { Attempt = Attempt(AiModule.Social) };
        if (defect == "state") reservation = Reservation();
        setup.Repo.Result = new(defect == "unknown" ? AiBudgetOutcome.Unknown : AiBudgetOutcome.Settled, reservation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ReconcileSystemAsync(Id(61)));
        Assert.Single(setup.Repo.Billings);
    }

    [Fact]
    public void G_DailyQuotaRefund_DoesNotEraseProviderLiability()
    {
        var failed = Preparation().Operation with
        {
            Status = AiOperationStatus.ConfirmedFailed,
            Dispatch = AiDispatchState.ConclusivelyFailed
        };
        var refunded = AiOperationPolicy.ApplyAccounting(failed, AiAccountingEvent.ConfirmedUnrecoverableFailure);
        var billed = AiBudgetPolicy.Settle(Reservation(), Bill());

        Assert.Equal(AiQuotaState.Released, refunded.Quota);
        Assert.Equal(Bill().ActualUsd, billed.AdditionalCommittedUsd);
        Assert.Equal(AiBillingState.Settled, billed.Reservation.State);
    }

    [Theory]
    [InlineData("pre")]
    [InlineData("during")]
    [InlineData("after")]
    public async Task I_SystemCancellation_DoesNotRetrySettlement(string stage)
    {
        var setup = new Setup();
        setup.Evidence.Billing = Bill();
        using var cts = new CancellationTokenSource();
        if (stage == "pre") cts.Cancel();
        else setup.Repo.Handler = token =>
        {
            cts.Cancel();
            return stage == "during" ? Task.FromCanceled<AiBudgetResult>(token)
                : Task.FromResult(new AiBudgetResult(AiBudgetOutcome.Settled, AiBudgetPolicy.Settle(Reservation(), Bill()).Reservation));
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Service.ReconcileSystemAsync(Id(61), cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Equal(stage == "pre" ? 0 : 1, setup.Repo.Billings.Count);
    }

    private sealed class Setup
    {
        public Authorization Auth { get; } = new();
        public Gate Gate { get; } = new();
        public EvidenceReader Evidence { get; } = new();
        public Repository Repo { get; } = new();
        public AiPrivacyNoticeGuard Privacy { get; }
        public AiBudgetService Service { get; }
        public Setup()
        {
            Auth.Actor = Actor();
            Gate.Outcome = AiPrivacyNoticeGateOutcome.Satisfied;
            Evidence.Preparation = Preparation();
            Privacy = new(Auth, Gate);
            Service = new(Privacy, Auth, Evidence, Repo);
        }
    }
    private sealed class Authorization : IAiActorAuthorization
    {
        public AiAuthorizedActor? Actor { get; set; }
        public Exception? Failure { get; set; }
        public List<(AiModule Module, CancellationToken Token)> Calls { get; } = new();
        public Task<AiAuthorizedActor?> GetAuthorizedActorAsync(AiModule module, CancellationToken cancellationToken = default)
        {
            Calls.Add((module, cancellationToken));
            return Failure is null ? Task.FromResult(Actor) : Task.FromException<AiAuthorizedActor?>(Failure);
        }
    }
    private sealed class Gate : IAiPrivacyNoticeGate
    {
        public AiPrivacyNoticeGateOutcome Outcome { get; set; } = AiPrivacyNoticeGateOutcome.Unknown;
        public Exception? Failure { get; set; }
        public List<(AiAuthorizedActor Actor, CancellationToken Token)> Calls { get; } = new();
        public Task<AiPrivacyNoticeGateOutcome> EvaluateAsync(AiAuthorizedActor actor, CancellationToken cancellationToken = default)
        {
            Calls.Add((actor, cancellationToken));
            return Failure is null ? Task.FromResult(Outcome) : Task.FromException<AiPrivacyNoticeGateOutcome>(Failure);
        }
    }
    private sealed class EvidenceReader : IAiBudgetEvidenceReader
    {
        public AiBudgetPreparation? Preparation { get; set; }
        public AiProviderBillingEvidence? Billing { get; set; }
        public Exception? Failure { get; set; }
        public List<(AiGenerationAttemptReference Attempt, Guid Evidence, CancellationToken Token)> Preparations { get; } = new();
        public List<(Guid Evidence, CancellationToken Token)> BillingReads { get; } = new();
        public Task<AiBudgetPreparation?> ReadPreparationAsync(AiGenerationAttemptReference attempt, Guid evidenceId, CancellationToken cancellationToken = default)
        {
            Preparations.Add((attempt, evidenceId, cancellationToken));
            return Failure is null ? Task.FromResult(Preparation) : Task.FromException<AiBudgetPreparation?>(Failure);
        }
        public Task<AiProviderBillingEvidence?> ReadSystemBillingAsync(Guid evidenceId, CancellationToken cancellationToken = default)
        {
            BillingReads.Add((evidenceId, cancellationToken));
            return Failure is null ? Task.FromResult(Billing) : Task.FromException<AiProviderBillingEvidence?>(Failure);
        }
    }
    private sealed class Repository : IAiBudgetRepository
    {
        public AiBudgetResult Result { get; set; } = new(AiBudgetOutcome.Unknown);
        public Func<CancellationToken, Task<AiBudgetResult>>? Handler { get; set; }
        public List<(AiBudgetPreparation Evidence, decimal Amount, CancellationToken Token)> Reservations { get; } = new();
        public List<(AiProviderBillingEvidence Evidence, CancellationToken Token)> Billings { get; } = new();
        public List<(AiGenerationAttemptReference Attempt, CancellationToken Token)> Statuses { get; } = new();
        public Task<AiBudgetResult> ReserveAsync(AiBudgetPreparation evidence, decimal estimatedUsd, CancellationToken cancellationToken = default)
        {
            Reservations.Add((evidence, estimatedUsd, cancellationToken));
            return Handler?.Invoke(cancellationToken) ?? Task.FromResult(Result);
        }
        public Task<AiBudgetResult> ReadStatusAsync(AiGenerationAttemptReference attempt, CancellationToken cancellationToken = default)
        {
            Statuses.Add((attempt, cancellationToken));
            return Handler?.Invoke(cancellationToken) ?? Task.FromResult(Result);
        }
        public Task<AiBudgetResult> ReconcileSystemAsync(AiProviderBillingEvidence evidence, CancellationToken cancellationToken = default)
        {
            Billings.Add((evidence, cancellationToken));
            return Handler?.Invoke(cancellationToken) ?? Task.FromResult(Result);
        }
    }
}
