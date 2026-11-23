using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Policies;
using Xunit;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.UnitTests.Common;

public sealed class OfflineModuleLifecycleServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Subject = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherSubject = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Version = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CorrelationId = new("44444444-4444-4444-4444-444444444444");
    private static readonly ModuleLifecycleCommand Learning = new(ModuleNames.Learning,
        new Guid("55555555-5555-5555-5555-555555555555"), false);

    [Fact]
    public async Task MissingVerifiedSubjectNeverReadsOrWritesLifecycleState()
    {
        var fixture = new Fixture();
        fixture.Store.ReleaseFirstWrite.SetResult();

        var rejected = await Assert.ThrowsAsync<ModuleLifecycleException>(() =>
            fixture.Service.ExecuteAsync(Guid.Empty, Learning, CorrelationId));

        Assert.Equal(ModuleLifecycleDenial.UnconfirmedAccount, rejected.Denial);
        Assert.Equal(0, fixture.Store.WriteAttempts);
        Assert.Equal(0, fixture.Store.ReconciliationReads);
        Assert.Empty(fixture.Store.Memberships);
    }

    [Fact]
    public async Task ConcurrentFirstActivationAndEquivalentRetriesCommitOneEvent()
    {
        var fixture = new Fixture();
        var first = fixture.Execute(Learning);
        await fixture.Store.FirstWriteEntered.Task;
        var retry = fixture.Execute(Learning);
        Assert.False(retry.IsCompleted);
        fixture.Store.ReleaseFirstWrite.SetResult();

        var results = await Task.WhenAll(first, retry);
        Assert.Equal(results[0], results[1]);
        Assert.Equal(new ModuleLifecycleResult(ModuleNames.Learning, 1, Now), results[0]);
        Assert.Single(fixture.Store.Memberships);
        var receipt = Assert.Single(fixture.Store.Receipts).Value;
        Assert.Equal(Learning.RequestKey, receipt.RequestKey);
        Assert.Equal(ModuleLifecycleOperation.Activate, receipt.Operation);
        var audit = Assert.Single(fixture.Store.Audit);
        Assert.Equal(CorrelationId, audit.CorrelationId);
        Assert.Null(audit.PreviousState);
        Assert.Equal(receipt.Result, audit.Result);

        fixture.Store.EvaluatedAtUtc = Now.AddDays(1);
        var differentKey = Learning with { RequestKey = CorrelationId };
        Assert.Equal(results[0], await fixture.Execute(differentKey));
        Assert.Equal(2, fixture.Store.Receipts.Count);
        Assert.Single(fixture.Store.Audit);
        Assert.Equal(1, Assert.Single(fixture.Store.Memberships).Value.Membership.Revision);
    }

    [Fact]
    public async Task ExistingReceiptRejectsDifferentIntentEvenAtTheSameCurrentRevision()
    {
        var fixture = new Fixture();
        fixture.Store.ReleaseFirstWrite.SetResult();
        var command = Learning with { ExpectedRevision = 4 };
        fixture.SeedInactive(command.Module, 4);
        var result = await fixture.Execute(command);
        Assert.Equal(5, result.Revision);
        var receiptKey = Assert.Single(fixture.Store.Receipts).Key;
        var receipt = fixture.Store.Receipts[receiptKey];
        // Simulate a scoped receipt for a different normalized intent with the same result.
        fixture.Store.Receipts[receiptKey] = receipt with { IntentHash = new string('0', 64) };

        var rejection = await Assert.ThrowsAsync<ModuleLifecycleException>(() => fixture.Execute(command));

        Assert.Equal(ModuleLifecycleDenial.ConflictingTransition, rejection.Denial);
        Assert.Equal(5, Assert.Single(fixture.Store.Memberships).Value.Membership.Revision);
        Assert.Equal("inactive", Assert.Single(fixture.Store.Audit).PreviousState);
        Assert.Single(fixture.Store.Receipts);
    }

    [Fact]
    public async Task RetriedActivationStillRequiresCurrentAuthority()
    {
        var fixture = new Fixture();
        fixture.Store.ReleaseFirstWrite.SetResult();
        await fixture.Execute(Learning);
        fixture.Store.Outcome = ModuleAuthorizationOutcome.Unavailable;

        var rejected = await Assert.ThrowsAsync<ModuleLifecycleException>(() => fixture.Execute(Learning));

        Assert.Equal(ModuleAuthorizationOutcome.Unavailable, rejected.Outcome);
        Assert.Equal(1, Assert.Single(fixture.Store.Memberships).Value.Membership.Revision);
        Assert.Single(fixture.Store.Receipts);
        Assert.Single(fixture.Store.Audit);
    }

    [Fact]
    public async Task ForeignAccountEvidenceCannotAuthorizeTheVerifiedCaller()
    {
        var fixture = new Fixture();
        fixture.Store.ReleaseFirstWrite.SetResult();
        fixture.Store.UseForeignEvidence = true;

        var rejected = await Assert.ThrowsAsync<ModuleLifecycleException>(() => fixture.Execute(Learning));

        Assert.Equal(ModuleLifecycleDenial.UnconfirmedAccount, rejected.Denial);
        Assert.Empty(fixture.Store.Memberships);
        Assert.Empty(fixture.Store.Receipts);
        Assert.Empty(fixture.Store.Audit);
    }

    [Fact]
    public async Task ReactivationCannotUseAnEarlierConsentAfterWithdrawal()
    {
        var fixture = new Fixture();
        fixture.Store.ReleaseFirstWrite.SetResult();
        fixture.Store.SocialConsent = false;
        var command = Learning with { Module = ModuleNames.Social, SocialOptIn = true, ExpectedRevision = 2 };
        var inactive = fixture.SeedInactive(command.Module, 2);

        var rejected = await Assert.ThrowsAsync<ModuleLifecycleException>(() => fixture.Execute(command));

        Assert.Equal(ModuleLifecycleDenial.InvalidOptIn, rejected.Denial);
        Assert.Same(inactive, Assert.Single(fixture.Store.Memberships).Value);
        Assert.Empty(fixture.Store.Receipts);
        Assert.Empty(fixture.Store.Audit);
    }

    [Fact]
    public async Task RequestKeyIsIndependentAcrossAccountsAndModules()
    {
        var fixture = new Fixture();
        fixture.Store.ReleaseFirstWrite.SetResult();
        var learning = await fixture.Execute(Learning);
        var hobbies = await fixture.Execute(Learning with { Module = ModuleNames.Hobbies });
        await fixture.Service.ExecuteAsync(OtherSubject, Learning, CorrelationId);

        Assert.Equal(ModuleNames.Learning, learning.Module);
        Assert.Equal(ModuleNames.Hobbies, hobbies.Module);
        Assert.Equal(3, fixture.Store.Memberships.Count);
        Assert.Equal(3, fixture.Store.Receipts.Count);
        Assert.Equal(3, fixture.Store.Audit.Select(item => item.Scope).Distinct().Count());
        Assert.All(fixture.Store.Memberships.Values, item => Assert.Equal(1, item.Membership.Revision));
    }

    [Fact]
    public async Task FailureBeforeCommitLeavesAllArtifactsAbsentAndAllowsSafeRetry()
    {
        var fixture = new Fixture();
        fixture.Store.ReleaseFirstWrite.SetResult();
        fixture.Store.BeforeCommit = () => throw new InvalidOperationException("Simulated atomic write rollback.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Execute(Learning));

        Assert.Empty(fixture.Store.Memberships);
        Assert.Empty(fixture.Store.Receipts);
        Assert.Empty(fixture.Store.Audit);
        fixture.Store.BeforeCommit = null;
        await fixture.Execute(Learning);
        Assert.Single(fixture.Store.Memberships);
        Assert.Single(fixture.Store.Receipts);
        Assert.Single(fixture.Store.Audit);
    }

    [Fact]
    public async Task CancellationBeforeCommitLeavesAllArtifactsAbsent()
    {
        var fixture = new Fixture();
        fixture.Store.ReleaseFirstWrite.SetResult();
        using var cancellation = new CancellationTokenSource();
        fixture.Store.BeforeCommit = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Execute(Learning, cancellation.Token));

        Assert.Empty(fixture.Store.Memberships);
        Assert.Empty(fixture.Store.Receipts);
        Assert.Empty(fixture.Store.Audit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AmbiguousAcknowledgementOnlyReconcilesAndNeverWritesTwice(bool committed)
    {
        var fixture = new Fixture();
        fixture.Store.ReleaseFirstWrite.SetResult();
        if (committed)
            fixture.Store.LoseCommitAcknowledgement = true;
        else
            fixture.Store.BeforeCommit = () => throw new ModuleLifecycleCommitUnknownException();

        if (committed)
            Assert.Equal(new ModuleLifecycleResult(ModuleNames.Learning, 1, Now), await fixture.Execute(Learning));
        else
            await Assert.ThrowsAsync<ModuleLifecycleCommitUnknownException>(() => fixture.Execute(Learning));

        Assert.Equal(1, fixture.Store.WriteAttempts);
        Assert.Equal(1, fixture.Store.ReconciliationReads);
        Assert.Equal(committed ? 1 : 0, fixture.Store.Memberships.Count);
        Assert.Equal(committed ? 1 : 0, fixture.Store.Receipts.Count);
        Assert.Equal(committed ? 1 : 0, fixture.Store.Audit.Count);
    }

    private sealed class Fixture
    {
        public Fixture() => Service = new(Store);
        public FixtureStore Store { get; } = new();
        public OfflineModuleLifecycleService Service { get; }

        public Task<ModuleLifecycleResult> Execute(ModuleLifecycleCommand command, CancellationToken cancellationToken = default) =>
            Service.ExecuteAsync(Subject, command, CorrelationId, cancellationToken);

        public StoredModuleMembership SeedInactive(string module, long revision)
        {
            var scope = Store.ScopeFor(Subject, module);
            var inactive = new StoredModuleMembership(new InactiveModuleMembership(scope, revision), Now.AddDays(-1));
            Store.Memberships.Add(scope, inactive);
            return inactive;
        }
    }

    // Simulates a locked, scoped snapshot in memory. This is not a database adapter.
    private sealed class FixtureStore : IModuleLifecycleStore
    {
        private readonly Account account = Account.Create(Subject, Now);
        private readonly Account other = Account.Create(OtherSubject, Now);
        private readonly SemaphoreSlim gate = new(1);
        public Dictionary<ModuleMembershipScope, StoredModuleMembership> Memberships { get; } = [];
        public Dictionary<(ModuleMembershipScope Scope, Guid Key), ModuleLifecycleReceipt> Receipts { get; } = [];
        public List<ModuleLifecycleAudit> Audit { get; } = [];
        public TaskCompletionSource FirstWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModuleAuthorizationOutcome Outcome { get; set; } = ModuleAuthorizationOutcome.Satisfied;
        public bool SocialConsent { get; set; } = true;
        public DateTimeOffset EvaluatedAtUtc { get; set; } = Now;
        public bool UseForeignEvidence { get; set; }
        public Action? BeforeCommit { get; set; }
        public bool LoseCommitAcknowledgement { get; set; }
        public int WriteAttempts { get; private set; }
        public int ReconciliationReads { get; private set; }

        public ModuleMembershipScope ScopeFor(Guid subject, string module) =>
            new((subject == Subject ? account : other).Id, module);

        public async Task<ModuleLifecycleResult> WriteAsync(Guid subject, string module, Guid requestKey,
            Func<ModuleLifecycleSnapshot, ModuleLifecycleDecision> decide, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                WriteAttempts++;
                FirstWriteEntered.TrySetResult();
                await ReleaseFirstWrite.Task.WaitAsync(cancellationToken);
                var decision = decide(Snapshot(subject, module, requestKey));
                BeforeCommit?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (decision.Write is { } write)
                {
                    Receipts.Add((write.Receipt.Scope, requestKey), write.Receipt);
                    if (write.ChangedMembership is { } changed)
                    {
                        Memberships[changed.Membership.Scope] = changed;
                        Audit.Add(write.Audit!);
                    }
                }
                if (LoseCommitAcknowledgement)
                    throw new ModuleLifecycleCommitUnknownException();
                return decision.Result;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<ModuleLifecycleSnapshot> ReadAsync(Guid subject, string module, Guid requestKey,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                ReconciliationReads++;
                return Snapshot(subject, module, requestKey);
            }
            finally
            {
                gate.Release();
            }
        }

        private ModuleLifecycleSnapshot Snapshot(Guid subject, string module, Guid key)
        {
            var actor = subject == Subject && !UseForeignEvidence ? account : other;
            var scope = new ModuleMembershipScope(actor.Id, module);
            var policy = new PolicyDocument("current", "https://example.test", "en");
            var evidence = new ModuleActivationEvidence(scope, Outcome, actor, actor.SupabaseUserId,
                AccountDemographics.Create(actor.Id, new DateOnly(2000, 1, 1), "other", Now), new(policy, policy, policy),
                [
                    AccountPolicyRecord.Create(actor.Id, PolicyActions.TermsAgreed, policy.Version, Now),
                    AccountPolicyRecord.Create(actor.Id, PolicyActions.PrivacyAcknowledged, policy.Version, Now)
                ],
                true, module == ModuleNames.Social && SocialConsent, Version, Version, EvaluatedAtUtc);
            return new(evidence, Memberships.GetValueOrDefault(scope), Receipts.GetValueOrDefault((scope, key)));
        }
    }
}
