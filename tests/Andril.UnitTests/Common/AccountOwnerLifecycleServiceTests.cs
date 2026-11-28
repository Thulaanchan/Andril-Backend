using Andril.Common.Accounts;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class AccountOwnerLifecycleServiceTests
{
    private static readonly Guid Subject = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeactivationAndReactivationProduceScopedAuditWithoutGrantingModulePermissions()
    {
        var f = new Fixture();
        var inactive = await f.Service.ExecuteAsync(Command(AccountOwnerOperation.Deactivate));
        Assert.Equal(AccountLifecycleState.Deactivated, inactive.State);
        Assert.Equal(2, inactive.Revision);
        Assert.False(AccountLifecyclePolicy.AllowsProtectedAccess(inactive.State));
        var active = await f.Service.ExecuteAsync(Command(AccountOwnerOperation.Reactivate, 2));
        Assert.Equal(AccountLifecycleState.Active, active.State);
        Assert.Equal(3, active.Revision);
        Assert.Equal(2, f.Store.Audits.Count);
        Assert.All(f.Store.Audits, audit => Assert.Equal(f.Store.Account.Id, audit.AccountId));
        Assert.Equal(AccountLifecycleState.Deactivated, f.Store.Audits[1].PreviousState);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task OwnerDeletionIsTerminalAndCannotBeReactivated(int state)
    {
        var f = new Fixture((AccountLifecycleState)state);
        var deleted = await f.Service.ExecuteAsync(Command(AccountOwnerOperation.Delete, confirm: true));
        Assert.Equal(AccountLifecycleState.Deleted, deleted.State);
        Assert.False(AccountLifecyclePolicy.AllowsProtectedAccess(deleted.State));
        await Denied(f, Command(AccountOwnerOperation.Reactivate, 2), AccountOwnerLifecycleDenial.Denied);
        Assert.Single(f.Store.Audits);
    }

    [Fact]
    public async Task MissingDeletionConfirmationDoesNotReachStore()
    {
        var f = new Fixture();
        await Denied(f, Command(AccountOwnerOperation.Delete), AccountOwnerLifecycleDenial.ConfirmationRequired);
        Assert.Equal(0, f.Store.Calls);
    }

    [Fact]
    public async Task SuspendedOwnerCannotDeactivateReactivateOrDelete()
    {
        var f = new Fixture(AccountLifecycleState.Suspended);
        foreach (var operation in Enum.GetValues<AccountOwnerOperation>())
            await Denied(f, Command(operation, confirm: operation == AccountOwnerOperation.Delete), AccountOwnerLifecycleDenial.Denied);
        Assert.Empty(f.Store.Audits);
    }

    [Fact]
    public async Task ForeignAccountSnapshotNeverChangesState()
    {
        var f = new Fixture();
        f.Store.Account = Account.Create(Guid.NewGuid(), Now);
        await Denied(f, Command(AccountOwnerOperation.Deactivate), AccountOwnerLifecycleDenial.Denied);
        Assert.Empty(f.Store.Audits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnauthenticatedOrUnconfirmedIdentityDoesNotReachStore(bool unconfirmed)
    {
        var f = new Fixture();
        if (unconfirmed) f.User.Confirmed = false;
        else f.User.IsAuthenticated = false;
        await Denied(f, Command(AccountOwnerOperation.Deactivate), AccountOwnerLifecycleDenial.AuthenticationRequired);
        Assert.Equal(0, f.Store.Calls);
    }

    [Fact]
    public async Task IdentityChangeInsideWriteBoundaryPreventsMutation()
    {
        var f = new Fixture();
        f.Store.BeforeDecision = () => f.User.UserId = Guid.NewGuid();
        await Denied(f, Command(AccountOwnerOperation.Deactivate), AccountOwnerLifecycleDenial.AuthenticationRequired);
        Assert.Empty(f.Store.Audits);
    }

    [Fact]
    public async Task MissingOrStaleAuthorityFailsClosed()
    {
        var f = new Fixture();
        f.Store.Authority = AccountOwnerAuthorityOutcome.Unavailable;
        await Denied(f, Command(AccountOwnerOperation.Deactivate), AccountOwnerLifecycleDenial.Unavailable);
        f.Store.Authority = AccountOwnerAuthorityOutcome.Satisfied;
        f.Store.ObservedVersion = Guid.NewGuid();
        await Denied(f, Command(AccountOwnerOperation.Deactivate), AccountOwnerLifecycleDenial.Denied);
        Assert.Empty(f.Store.Audits);
    }

    [Fact]
    public async Task ConcurrentIdenticalDeletionHasOneTransitionReceiptAndAudit()
    {
        var f = new Fixture();
        var command = Command(AccountOwnerOperation.Delete, confirm: true);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.Service.ExecuteAsync(command)));
        Assert.All(results, result => Assert.Equal(results[0], result));
        Assert.Single(f.Store.Receipts);
        Assert.Single(f.Store.Audits);
        Assert.Equal(2, f.Store.Current.Revision);
    }

    [Fact]
    public async Task ConcurrentDifferentCommandsCannotBothUseAnEarlierRevision()
    {
        var f = new Fixture();
        var commands = new[] { Command(AccountOwnerOperation.Deactivate), Command(AccountOwnerOperation.Delete, confirm: true) };
        var outcomes = await Task.WhenAll(commands.Select(async command =>
        {
            try { await f.Service.ExecuteAsync(command); return true; }
            catch (AccountOwnerLifecycleException) { return false; }
        }));
        Assert.Single(outcomes, success => success);
        Assert.Single(f.Store.Audits);
    }

    [Fact]
    public async Task ReplayRequiresFreshAuthorityAndCannotReportSupersededState()
    {
        var f = new Fixture();
        var command = Command(AccountOwnerOperation.Deactivate);
        await f.Service.ExecuteAsync(command);
        f.Store.Authority = AccountOwnerAuthorityOutcome.Denied;
        await Denied(f, command, AccountOwnerLifecycleDenial.Denied);
        f.Store.Authority = AccountOwnerAuthorityOutcome.Satisfied;
        await f.Service.ExecuteAsync(Command(AccountOwnerOperation.Delete, 2, true));
        await Denied(f, command, AccountOwnerLifecycleDenial.Conflict);
        Assert.Equal(AccountLifecycleState.Deleted, f.Store.Current.State);
    }

    [Fact]
    public async Task ReusingRequestKeyForDifferentIntentConflicts()
    {
        var f = new Fixture();
        var command = Command(AccountOwnerOperation.Deactivate);
        await f.Service.ExecuteAsync(command);
        await Denied(f, command with { Operation = AccountOwnerOperation.Delete, ConfirmDeletion = true }, AccountOwnerLifecycleDenial.Conflict);
        Assert.Single(f.Store.Audits);
    }

    [Fact]
    public async Task FailedAtomicCommitLeavesStateReceiptAndAuditUnchangedAndIsNotRetried()
    {
        var f = new Fixture();
        f.Store.FailCommit = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.ExecuteAsync(Command(AccountOwnerOperation.Delete, confirm: true)));
        Assert.Equal(AccountLifecycleState.Active, f.Store.Current.State);
        Assert.Empty(f.Store.Receipts);
        Assert.Empty(f.Store.Audits);
        Assert.Equal(1, f.Store.Calls);
    }

    [Fact]
    public async Task CancellationBeforeWriteDoesNotReachStore()
    {
        var f = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ExecuteAsync(Command(AccountOwnerOperation.Deactivate), cancellation.Token));
        Assert.Equal(0, f.Store.Calls);
    }

    private static AccountOwnerLifecycleCommand Command(AccountOwnerOperation operation, long revision = 1, bool confirm = false) =>
        new(operation, Guid.NewGuid(), revision, confirm);

    private static async Task Denied(Fixture f, AccountOwnerLifecycleCommand command, AccountOwnerLifecycleDenial expected) =>
        Assert.Equal(expected, (await Assert.ThrowsAsync<AccountOwnerLifecycleException>(() => f.Service.ExecuteAsync(command))).Denial);

    private sealed class User : ICurrentUser, IOnboardingIdentity
    {
        public Guid? UserId { get; set; } = Subject;
        public bool IsAuthenticated { get; set; } = true;
        internal bool Confirmed { get; set; } = true;
        public bool IsConfirmedFor(Guid subject) => Confirmed && subject == UserId;
    }

    private sealed class Fixture(AccountLifecycleState state = AccountLifecycleState.Active)
    {
        internal User User { get; } = new();
        internal MemoryStore Store { get; } = new(state);
        internal AccountOwnerLifecycleService Service => new(User, User, Store);
    }

    // Synthetic transactional contract test only. It proves coordinator behavior,
    // not PostgreSQL locks/RLS, production authority, HTTP access or provider revocation.
    private sealed class MemoryStore(AccountLifecycleState state) : IAccountOwnerLifecycleStore
    {
        private readonly SemaphoreSlim fence = new(1, 1);
        internal Account Account { get; set; } = Account.Create(Subject, Now);
        internal AccountOwnerLifecycleResult Current { get; private set; } = new(state, 1, Now);
        internal Dictionary<Guid, AccountOwnerLifecycleReceipt> Receipts { get; } = [];
        internal List<AccountOwnerLifecycleAudit> Audits { get; } = [];
        internal Guid Version { get; } = Guid.NewGuid();
        internal Guid? ObservedVersion { get; set; }
        internal AccountOwnerAuthorityOutcome Authority { get; set; } = AccountOwnerAuthorityOutcome.Satisfied;
        internal Action? BeforeDecision { get; set; }
        internal bool FailCommit { get; set; }
        internal int Calls { get; private set; }

        public async Task<AccountOwnerLifecycleResult> ExecuteAsync(Guid subject, Guid requestKey, AccountOwnerOperation operation,
            Func<AccountOwnerLifecycleSnapshot, AccountOwnerLifecycleDecision> decide, CancellationToken cancellationToken)
        {
            await fence.WaitAsync(cancellationToken);
            try
            {
                // Force overlapping callers to queue on the synthetic fence instead
                // of completing each async operation synchronously during enumeration.
                await Task.Yield();
                Calls++;
                BeforeDecision?.Invoke();
                Receipts.TryGetValue(requestKey, out var receipt);
                var decision = decide(new(Account, Current, Authority, ObservedVersion ?? Version, Version, Now.AddMinutes(1), receipt));
                if (FailCommit) throw new InvalidOperationException("Synthetic atomic commit failure.");
                if (decision.Write is { } write)
                {
                    Current = decision.Result;
                    Receipts.Add(requestKey, write.Receipt);
                    Audits.Add(write.Audit);
                }
                return decision.Result;
            }
            finally { fence.Release(); }
        }
    }
}
