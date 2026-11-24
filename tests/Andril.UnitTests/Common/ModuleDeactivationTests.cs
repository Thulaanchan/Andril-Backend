using System.Security.Cryptography;
using System.Text;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Common.Accounts.Lifecycle;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class ModuleDeactivationTests
{
    private static readonly Guid Subject = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Version = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly ModuleMembershipScope Scope = new(AccountId, "learning");
    private static ModuleLifecycleCommand Command => new("learning", Guid.Parse("44444444-4444-4444-4444-444444444444"),
        false, 4, ModuleLifecycleOperation.Deactivate, Scope);

    [Fact]
    public async Task TransitionAndExactRetryPreserveFirstTimeAndCommitOneAudit()
    {
        var store = new Store();
        var originalFirst = store.Current!.FirstActivatedAtUtc;
        var profile = store.Profile;
        var starter = store.Starter;
        var otherModule = store.OtherModule;
        var otherAccount = store.OtherAccount;
        var first = await Execute(store);
        var replay = await Execute(store);
        Assert.Equal(first, replay);
        Assert.Equal("inactive", first.State);
        Assert.Equal(5, first.Revision);
        Assert.Equal(Now, first.StateChangedAtUtc);
        Assert.IsType<InactiveModuleMembership>(store.Current.Membership);
        Assert.Equal(originalFirst, store.Current.FirstActivatedAtUtc);
        Assert.Equal("active", Assert.Single(store.Audits).PreviousState);
        Assert.Equal("inactive", store.Audits[0].Result.State);
        Assert.Single(store.Receipts);
        Assert.Equal(1, store.Commits);
        Assert.Same(profile, store.Profile);
        Assert.Same(starter, store.Starter);
        Assert.Equal("learning", store.Starter.Module);
        Assert.Equal("private", store.Profile.Visibility);
        Assert.Same(otherModule, store.OtherModule);
        Assert.Same(otherAccount, store.OtherAccount);
        var normalized = $"v2\n{AccountId:D}\nlearning\nDeactivate\n4\nFalse";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))), store.Receipts.Values.Single().IntentHash);
    }

    [Theory]
    [InlineData((int)ModuleAuthorizationOutcome.Denied, (int)ModuleLifecycleDenial.AuthorityDenied)]
    [InlineData((int)ModuleAuthorizationOutcome.Unavailable, (int)ModuleLifecycleDenial.EvidenceUnavailable)]
    public async Task OperationEvidenceFailsClosed(int outcomeValue, int denialValue)
    {
        var store = new Store();
        store.Evidence = store.Evidence with { Outcome = (ModuleAuthorizationOutcome)outcomeValue };
        await RejectUnchanged(store, Command, (ModuleLifecycleDenial)denialValue);
    }

    [Fact]
    public async Task MissingAndAlreadyInactiveHaveDistinctOfflineClassifications()
    {
        var missing = new Store { Current = null };
        await RejectUnchanged(missing, Command, ModuleLifecycleDenial.MembershipMissing);
        var inactive = new Store { Current = new(new InactiveModuleMembership(Scope, 4), Now.AddDays(-1), Now.AddDays(-5)) };
        await RejectUnchanged(inactive, Command, ModuleLifecycleDenial.AlreadyInactive);
    }

    [Theory]
    [InlineData("learning")]
    [InlineData("hobbies")]
    [InlineData("social")]
    public void EachCanonicalModuleUsesOperationSpecificEvidence(string module)
    {
        var scope = new ModuleMembershipScope(AccountId, module);
        var evidence = new ModuleDeactivationEvidence(scope, Subject, ModuleAuthorizationOutcome.Satisfied, Version, Version, Now);
        var result = ModuleDeactivation.Deactivate(scope, Subject, evidence, ActiveModuleMembership.Restore(scope, 4), 4);
        Assert.Equal(scope, result.Scope);
        Assert.Equal(5, result.Revision);
    }

    [Fact]
    public async Task StaleAndForeignEvidenceNeverMutates()
    {
        var variants = new[]
        {
            new Store { Evidence = new(Scope, Subject, ModuleAuthorizationOutcome.Satisfied, Guid.Empty, Version, Now) },
            new Store { Evidence = new(new(Guid.NewGuid(), "learning"), Subject, ModuleAuthorizationOutcome.Satisfied, Version, Version, Now) },
            new Store { Evidence = new(new(AccountId, "hobbies"), Subject, ModuleAuthorizationOutcome.Satisfied, Version, Version, Now) },
            new Store { Evidence = new(Scope, Guid.NewGuid(), ModuleAuthorizationOutcome.Satisfied, Version, Version, Now) }
        };
        foreach (var store in variants)
        {
            var before = store.Current;
            await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(store));
            Assert.Same(before, store.Current);
            Assert.Empty(store.Receipts);
            Assert.Empty(store.Audits);
        }
        await RejectUnchanged(new Store(), Command with { ExpectedRevision = 3 }, ModuleLifecycleDenial.ConflictingTransition);
        await RejectUnchanged(new Store(), Command with { Module = "Learning" }, ModuleLifecycleDenial.InvalidScope);
    }

    [Fact]
    public async Task ReplayRequiresCurrentAuthorityAndRejectsChangedIntentOrSupersededState()
    {
        var store = new Store();
        await Execute(store);
        var inactive = store.Current;
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(store, Command with { ExpectedRevision = 3 }));
        Assert.Same(inactive, store.Current);
        store.Evidence = store.Evidence with { Outcome = ModuleAuthorizationOutcome.Denied };
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(store));
        store.Evidence = store.Evidence with { Outcome = ModuleAuthorizationOutcome.Satisfied };
        store.Current = new(ActiveModuleMembership.Restore(Scope, 6), Now.AddMinutes(1), inactive!.FirstActivatedAtUtc);
        var superseding = store.Current;
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(store));
        Assert.Same(superseding, store.Current);
        Assert.Single(store.Receipts);
        Assert.Single(store.Audits);
    }

    [Fact]
    public async Task InvalidRevisionsAndMissingExplicitScopeNeverWrite()
    {
        foreach (var revision in new long?[] { null, 0, -1, long.MaxValue })
        {
            var store = new Store();
            await Assert.ThrowsAsync<ArgumentException>(() => Execute(store, Command with { ExpectedRevision = revision }));
            Assert.Equal(0, store.Writes);
        }
        await RejectUnchanged(new Store(), Command with { TargetScope = null }, ModuleLifecycleDenial.InvalidScope);
        var evidence = new Store().Evidence;
        Assert.Throws<ModuleLifecycleException>(() => ModuleDeactivation.Deactivate(Scope, Subject, evidence,
            ActiveModuleMembership.Restore(Scope, long.MaxValue), long.MaxValue));
    }

    [Fact]
    public async Task ConcurrentRetriesAndCompetingRequestsSerializeWithoutLostUpdates()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { ReleaseWrite = release };
        var first = Execute(store);
        await store.WriteEntered.Task;
        var retries = Enumerable.Range(0, 7).Select(_ => Execute(store)).ToArray();
        Assert.All(retries, task => Assert.False(task.IsCompleted));
        release.SetResult();
        var results = await Task.WhenAll(retries.Prepend(first));
        Assert.All(results, r => Assert.Equal(results[0], r));
        Assert.Equal(1, store.Commits);
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(store, Command with { RequestKey = Guid.NewGuid() }));
        Assert.Equal(1, store.Commits);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AmbiguousCommitReconcilesWithoutAnotherWrite(bool committed)
    {
        var store = new Store { LoseAcknowledgement = committed, UnknownBeforeCommit = !committed };
        if (committed) Assert.Equal("inactive", (await Execute(store)).State);
        else await Assert.ThrowsAsync<ModuleLifecycleCommitUnknownException>(() => Execute(store));
        Assert.Equal(1, store.Writes);
        Assert.Equal(1, store.Reads);
        Assert.Equal(committed ? 1 : 0, store.Commits);
    }

    [Fact]
    public async Task CancellationAndFailureBeforeCommitPreserveAllState()
    {
        var store = new Store();
        var before = store.Current;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(store, ct: cancel.Token));
        using var during = new CancellationTokenSource();
        store.BeforeCommit = during.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(store, ct: during.Token));
        store.BeforeCommit = () => throw new IOException("Synthetic rollback");
        await Assert.ThrowsAsync<IOException>(() => Execute(store));
        Assert.Same(before, store.Current);
        Assert.Empty(store.Receipts);
        Assert.Empty(store.Audits);
    }

    [Fact]
    public async Task ActivationEvidenceIsNotDeactivationAuthorityAndLegacyStoreCannotDeactivate()
    {
        var store = new Store(); // Its activation evidence is intentionally unavailable and incomplete.
        Assert.Equal("inactive", (await Execute(store)).State);
        var legacy = new LegacyStore();
        await Assert.ThrowsAsync<ModuleLifecycleException>(() => new OfflineModuleLifecycleService(legacy)
            .ExecuteAsync(Subject, Command, Version));
        Assert.Equal(0, legacy.Writes);
    }

    private static Task<ModuleLifecycleResult> Execute(Store store, ModuleLifecycleCommand? command = null, CancellationToken ct = default) =>
        new OfflineModuleLifecycleService(store).ExecuteAsync(Subject, command ?? Command, Version, ct);

    private static async Task RejectUnchanged(Store store, ModuleLifecycleCommand command, ModuleLifecycleDenial denial)
    {
        var before = store.Current;
        var error = await Assert.ThrowsAsync<ModuleLifecycleException>(() => Execute(store, command));
        Assert.Equal(denial, error.Denial);
        Assert.Same(before, store.Current);
        Assert.Empty(store.Receipts);
        Assert.Empty(store.Audits);
    }

    private sealed class Store : IOfflineModuleDeactivationStore
    {
        private readonly SemaphoreSlim gate = new(1);
        internal StoredModuleMembership? Current = new(ActiveModuleMembership.Restore(Scope, 4), Now.AddDays(-1), Now.AddDays(-5));
        internal ModuleDeactivationEvidence Evidence = new(Scope, Subject, ModuleAuthorizationOutcome.Satisfied, Version, Version, Now);
        internal Dictionary<Guid, ModuleLifecycleReceipt> Receipts = [];
        internal List<ModuleLifecycleAudit> Audits = [];
        internal readonly PrivateModuleProfile Profile = PrivateModuleProfile.Create(AccountId, "learning", Now.AddDays(-5));
        internal readonly StarterMembership Starter = StarterMembership.Create(AccountId, "learning", false, Now.AddDays(-5));
        internal readonly ModuleMembership OtherModule = ActiveModuleMembership.Restore(new(AccountId, "hobbies"), 2);
        internal readonly ModuleMembership OtherAccount = ActiveModuleMembership.Restore(new(Version, "learning"), 3);
        internal int Writes, Reads, Commits;
        internal bool LoseAcknowledgement, UnknownBeforeCommit;
        internal Action? BeforeCommit;
        internal TaskCompletionSource? ReleaseWrite;
        internal TaskCompletionSource WriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ModuleLifecycleResult> WriteAsync(Guid subject, string module, Guid key,
            Func<ModuleLifecycleSnapshot, ModuleLifecycleDecision> decide, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                Writes++;
                WriteEntered.TrySetResult();
                if (ReleaseWrite is not null) await ReleaseWrite.Task.WaitAsync(ct);
                var decision = decide(Snapshot(key));
                BeforeCommit?.Invoke();
                ct.ThrowIfCancellationRequested();
                if (UnknownBeforeCommit) throw new ModuleLifecycleCommitUnknownException();
                if (decision.Write is { } write)
                {
                    Current = write.ChangedMembership!;
                    Receipts.Add(key, write.Receipt);
                    Audits.Add(write.Audit!);
                    Commits++;
                }
                if (LoseAcknowledgement) throw new ModuleLifecycleCommitUnknownException();
                return decision.Result;
            }
            finally { gate.Release(); }
        }
        public async Task<ModuleLifecycleSnapshot> ReadAsync(Guid subject, string module, Guid key, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try { Reads++; return Snapshot(key); }
            finally { gate.Release(); }
        }
        private ModuleLifecycleSnapshot Snapshot(Guid key) => new(
            new(Scope, ModuleAuthorizationOutcome.Unavailable, null, null, null, null, null, null, null, Guid.Empty, Guid.Empty, Now),
            Current, Receipts.GetValueOrDefault(key), Evidence);
    }

    private sealed class LegacyStore : IModuleLifecycleStore
    {
        internal int Writes;
        public Task<ModuleLifecycleResult> WriteAsync(Guid subject, string module, Guid key,
            Func<ModuleLifecycleSnapshot, ModuleLifecycleDecision> decide, CancellationToken ct)
        { Writes++; throw new InvalidOperationException(); }
        public Task<ModuleLifecycleSnapshot> ReadAsync(Guid subject, string module, Guid key, CancellationToken ct) =>
            throw new InvalidOperationException();
    }
}
