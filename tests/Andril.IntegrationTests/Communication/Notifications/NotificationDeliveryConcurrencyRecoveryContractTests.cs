using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andril.Persistence.DbContexts;
using Xunit;

namespace Andril.IntegrationTests.Communication.Notifications;

/// <summary>
/// ANDRIL M4 D14-E — PROPOSED OFFLINE delivery concurrency and recovery contract.
///
/// The in-memory, lock-protected store is TEST-ONLY. Its shared lifetime simulates
/// workers restarting against one durable authority, but it is NOT PostgreSQL,
/// an EF repository, a real crash/restart, an RLS test, or a push provider.
/// This harness exercises the real D14 internal delivery-attempt record through
/// reflection without changing its visibility or production wiring.
///
/// No retry limit, retry interval, claim lease, or automatic Unknown recovery is
/// approved. This prototype deliberately does not invent those policies.
/// </summary>
public sealed class NotificationDeliveryConcurrencyRecoveryContractTests
{
    private static readonly Assembly PersistenceAssembly =
        typeof(NotificationsDbContext).Assembly;

    private static readonly Type RecordType =
        PersistenceAssembly.GetType(
            "Andril.Persistence.Repositories.Communication.NotificationDeliveryAttemptRecord",
            throwOnError: true)!;

    private static readonly Type OutcomeType =
        PersistenceAssembly.GetType(
            "Andril.Persistence.Repositories.Communication.NotificationDeliveryAttemptOutcome",
            throwOnError: true)!;

    private static readonly MethodInfo CreateMethod =
        RecordType.GetMethod("CreateClaimed", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static readonly MethodInfo RecordOutcomeMethod =
        RecordType.GetMethod("TryRecordOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly DateTimeOffset At =
        new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    private readonly record struct Scope(
        Guid AccountId, string Module, Guid PrivateProfileId, Guid DeviceRegistrationId);

    private readonly record struct Eligibility(
        bool AccountActive, bool ProfileEligible, bool PreferencesAllowPush, bool DeviceAuthorized)
    {
        public bool MayDispatch =>
            AccountActive && ProfileEligible && PreferencesAllowPush && DeviceAuthorized;

        public static Eligibility Allowed => new(true, true, true, true);
    }

    private enum AttemptState
    {
        Pending, Claimed, ProviderAccepted, TemporaryFailure, PermanentFailure, Unknown
    }

    private enum ProviderResult
    {
        ProviderAccepted, TemporaryFailure, PermanentFailure, Unknown
    }

    private sealed record Snapshot(
        AttemptState State, int AttemptCount, Guid? CurrentAttemptId,
        bool DispatchStarted);

    private sealed class SimulatedLostAcknowledgementException : Exception { }
    private sealed class SimulatedPreCommitException : Exception { }

    private sealed class Entry
    {
        public Entry(Scope owner) => Owner = owner;

        public Scope Owner { get; }
        public AttemptState State { get; set; } = AttemptState.Pending;
        public List<object> Attempts { get; } = new();
        public object? CurrentAttempt { get; set; }
        public Guid? CurrentAttemptId { get; set; }
        public bool DispatchStarted { get; set; }
    }

    /// <summary>
    /// Simulates one authoritative persistence boundary with atomic operations.
    /// It does NOT prove that a future PostgreSQL schema has these properties.
    /// </summary>
    private sealed class SharedTestStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, Entry> _notifications = new();

        public bool RegisterTrusted(Guid notificationId, Scope verifiedOwner)
        {
            if (notificationId == Guid.Empty ||
                verifiedOwner.AccountId == Guid.Empty ||
                verifiedOwner.PrivateProfileId == Guid.Empty ||
                verifiedOwner.DeviceRegistrationId == Guid.Empty ||
                verifiedOwner.Module is not ("learning" or "hobbies" or "social"))
            {
                return false;
            }

            lock (_sync)
            {
                return _notifications.TryAdd(notificationId, new Entry(verifiedOwner));
            }
        }

        public Guid? TryClaim(
            Guid notificationId, Scope actor, Eligibility freshEligibility,
            bool simulatePreCommitFailure = false,
            bool simulateLostAcknowledgement = false)
        {
            lock (_sync)
            {
                if (!FindOwned(notificationId, actor, out var row) ||
                    !freshEligibility.MayDispatch || row!.State != AttemptState.Pending)
                {
                    return null;
                }

                if (simulatePreCommitFailure)
                    throw new SimulatedPreCommitException();

                var attemptId = Guid.NewGuid();
                var record = CreateMethod.Invoke(null, new object?[]
                {
                    attemptId, notificationId, actor.AccountId,
                    actor.PrivateProfileId, actor.Module,
                    actor.DeviceRegistrationId, At
                })!;

                row.Attempts.Add(record);
                row.CurrentAttempt = record;
                row.CurrentAttemptId = attemptId;
                row.State = AttemptState.Claimed;
                row.DispatchStarted = false;

                if (simulateLostAcknowledgement)
                    throw new SimulatedLostAcknowledgementException();

                return attemptId;
            }
        }

        /// <summary>
        /// One worker may cross the simulated provider-call boundary once.
        /// Eligibility must be checked again at this boundary.
        /// </summary>
        public bool TryBeginDispatch(
            Guid notificationId, Scope actor, Guid attemptId,
            Eligibility freshEligibility)
        {
            lock (_sync)
            {
                if (!FindOwned(notificationId, actor, out var row) ||
                    !freshEligibility.MayDispatch ||
                    row!.State != AttemptState.Claimed ||
                    row.CurrentAttemptId != attemptId ||
                    row.DispatchStarted)
                {
                    return false;
                }

                row.DispatchStarted = true;
                return true;
            }
        }

        public bool TryRecordResult(
            Guid notificationId, Scope actor, Guid attemptId,
            ProviderResult providerResult)
        {
            lock (_sync)
            {
                if (!FindOwned(notificationId, actor, out var row) ||
                    row!.State != AttemptState.Claimed ||
                    !row.DispatchStarted ||
                    row.CurrentAttemptId != attemptId ||
                    !Enum.IsDefined(providerResult))
                {
                    return false;
                }

                var actualOutcome = Enum.Parse(OutcomeType, providerResult.ToString());
                var recorded = (bool)RecordOutcomeMethod.Invoke(
                    row.CurrentAttempt,
                    new object?[] { actualOutcome, At.AddMinutes(1) })!;

                if (!recorded)
                    return false;

                row.State = providerResult switch
                {
                    ProviderResult.ProviderAccepted => AttemptState.ProviderAccepted,
                    ProviderResult.TemporaryFailure => AttemptState.TemporaryFailure,
                    ProviderResult.PermanentFailure => AttemptState.PermanentFailure,
                    ProviderResult.Unknown => AttemptState.Unknown,
                    _ => throw new InvalidOperationException("Unexpected mock outcome.")
                };
                return true;
            }
        }

        /// <summary>
        /// The boolean is a TEST-ONLY stand-in for a FUTURE reviewed retry policy.
        /// Nothing calls this automatically. Unknown and permanent outcomes are
        /// never made retryable by this simulation.
        /// </summary>
        public bool TryPermitRetry(
            Guid notificationId, Scope actor, Eligibility freshEligibility,
            bool policyExplicitlyPermitsRetry)
        {
            lock (_sync)
            {
                if (!FindOwned(notificationId, actor, out var row) ||
                    !policyExplicitlyPermitsRetry ||
                    !freshEligibility.MayDispatch ||
                    row!.State != AttemptState.TemporaryFailure)
                {
                    return false;
                }

                row.State = AttemptState.Pending;
                row.CurrentAttempt = null;
                row.CurrentAttemptId = null;
                row.DispatchStarted = false;
                // History is retained; a subsequent claim creates a NEW attempt.
                return true;
            }
        }

        public Snapshot? ReadOwned(Guid notificationId, Scope actor)
        {
            lock (_sync)
            {
                if (!FindOwned(notificationId, actor, out var row))
                    return null;

                return new Snapshot(
                    row!.State, row.Attempts.Count,
                    row.CurrentAttemptId, row.DispatchStarted);
            }
        }

        private bool FindOwned(Guid id, Scope actor, out Entry? result)
        {
            if (_notifications.TryGetValue(id, out var row) && row.Owner == actor)
            {
                result = row;
                return true;
            }

            result = null;
            return false;
        }
    }

    /// <summary>
    /// A new instance simulates a worker restart while retaining one shared
    /// test store. This is not process-persistent storage.
    /// </summary>
    private sealed class Worker
    {
        private readonly SharedTestStore _store;
        public Worker(SharedTestStore store) => _store = store;

        public Guid? Claim(Guid id, Scope scope, Eligibility eligibility,
            bool failBeforeCommit = false, bool loseAcknowledgement = false) =>
            _store.TryClaim(id, scope, eligibility, failBeforeCommit, loseAcknowledgement);

        public bool Begin(Guid id, Scope scope, Guid attempt, Eligibility eligibility) =>
            _store.TryBeginDispatch(id, scope, attempt, eligibility);
    }

    private static Scope Owner(string module = "learning") =>
        new(Guid.NewGuid(), module, Guid.NewGuid(), Guid.NewGuid());

    private static (SharedTestStore store, Guid id, Scope owner) Arrange(
        string module = "learning")
    {
        var store = new SharedTestStore();
        var id = Guid.NewGuid();
        var owner = Owner(module);
        Assert.True(store.RegisterTrusted(id, owner));
        return (store, id, owner);
    }

    private static Guid ClaimOne(SharedTestStore store, Guid id, Scope owner)
    {
        var claimed = new Worker(store).Claim(id, owner, Eligibility.Allowed);
        Assert.True(claimed.HasValue);
        return claimed.Value;
    }

    [Fact]
    public async Task ConcurrentWorkers_ClaimExactlyOneAttempt()
    {
        var (store, id, owner) = Arrange();
        using var ready = new CountdownEvent(2);
        using var release = new ManualResetEventSlim(false);

        Task<Guid?> StartWorker() => Task.Run(() =>
        {
            ready.Signal();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Test workers were not released.");
            return new Worker(store).Claim(id, owner, Eligibility.Allowed);
        });

        var a = StartWorker();
        var b = StartWorker();
        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            release.Set();
        }

        var results = await Task.WhenAll(a, b);
        Assert.Single(results, x => x.HasValue);
        Assert.Single(results, x => !x.HasValue);
        Assert.Equal(1, store.ReadOwned(id, owner)!.AttemptCount);
        Assert.Equal(AttemptState.Claimed, store.ReadOwned(id, owner)!.State);
    }

    [Fact]
    public void RecreatedWorker_CannotDispatchSameClaimTwice()
    {
        var (store, id, owner) = Arrange();
        var attempt = ClaimOne(store, id, owner);
        Assert.True(new Worker(store).Begin(id, owner, attempt, Eligibility.Allowed));
        Assert.False(new Worker(store).Begin(id, owner, attempt, Eligibility.Allowed));
        Assert.Equal(1, store.ReadOwned(id, owner)!.AttemptCount);
    }

    [Fact]
    public void ForeignAccountModuleProfileOrDevice_CannotReadClaimOrComplete()
    {
        var (store, id, owner) = Arrange("social");
        var outsiders = new[]
        {
            owner with { AccountId = Guid.NewGuid() },
            owner with { Module = "hobbies" },
            owner with { PrivateProfileId = Guid.NewGuid() },
            owner with { DeviceRegistrationId = Guid.NewGuid() }
        };

        foreach (var foreign in outsiders)
        {
            Assert.Null(new Worker(store).Claim(id, foreign, Eligibility.Allowed));
            Assert.Null(store.ReadOwned(id, foreign));
        }

        var attempt = ClaimOne(store, id, owner);
        Assert.True(new Worker(store).Begin(id, owner, attempt, Eligibility.Allowed));

        foreach (var foreign in outsiders)
        {
            Assert.False(store.TryRecordResult(
                id, foreign, attempt, ProviderResult.ProviderAccepted));
        }

        Assert.Equal(AttemptState.Claimed, store.ReadOwned(id, owner)!.State);
    }

    [Fact]
    public void DispatchBoundary_RejectsEachFormOfRevokedEligibility()
    {
        var (store, id, owner) = Arrange("social");
        var attempt = ClaimOne(store, id, owner);
        var forbidden = new[]
        {
            new Eligibility(false, true, true, true),
            new Eligibility(true, false, true, true),
            new Eligibility(true, true, false, true),
            new Eligibility(true, true, true, false)
        };

        foreach (var eligibility in forbidden)
            Assert.False(new Worker(store).Begin(id, owner, attempt, eligibility));

        var state = store.ReadOwned(id, owner)!;
        Assert.Equal(AttemptState.Claimed, state.State);
        Assert.False(state.DispatchStarted);
    }

    [Fact]
    public void UnknownProviderResult_BlocksAllAutomaticRetries()
    {
        var (store, id, owner) = Arrange();
        var attempt = ClaimOne(store, id, owner);
        Assert.True(new Worker(store).Begin(id, owner, attempt, Eligibility.Allowed));
        Assert.True(store.TryRecordResult(id, owner, attempt, ProviderResult.Unknown));

        Assert.False(store.TryPermitRetry(id, owner, Eligibility.Allowed, true));
        Assert.Null(new Worker(store).Claim(id, owner, Eligibility.Allowed));
        Assert.Equal(AttemptState.Unknown, store.ReadOwned(id, owner)!.State);
        Assert.Equal(1, store.ReadOwned(id, owner)!.AttemptCount);
    }

    [Theory]
    [InlineData("ProviderAccepted")]
    [InlineData("PermanentFailure")]
    public void AcceptedOrPermanentResult_CannotRequeue(string resultName)
    {
        var (store, id, owner) = Arrange();
        var attempt = ClaimOne(store, id, owner);
        Assert.True(new Worker(store).Begin(id, owner, attempt, Eligibility.Allowed));
        var result = Enum.Parse<ProviderResult>(resultName);
        Assert.True(store.TryRecordResult(id, owner, attempt, result));
        Assert.False(store.TryPermitRetry(id, owner, Eligibility.Allowed, true));
        Assert.Null(new Worker(store).Claim(id, owner, Eligibility.Allowed));
    }

    [Fact]
    public void TemporaryFailure_RequiresExplicitReviewedPermissionAndFreshEligibility()
    {
        var (store, id, owner) = Arrange();
        var first = ClaimOne(store, id, owner);
        Assert.True(new Worker(store).Begin(id, owner, first, Eligibility.Allowed));
        Assert.True(store.TryRecordResult(id, owner, first, ProviderResult.TemporaryFailure));

        Assert.False(store.TryPermitRetry(id, owner, Eligibility.Allowed, false));
        Assert.False(store.TryPermitRetry(
            id, owner, new Eligibility(true, false, true, true), true));
        Assert.True(store.TryPermitRetry(id, owner, Eligibility.Allowed, true));

        var second = ClaimOne(store, id, owner);
        Assert.NotEqual(first, second);
        Assert.Equal(2, store.ReadOwned(id, owner)!.AttemptCount);
    }

    [Fact]
    public void DelayedOldProviderResponse_CannotOverwriteNewAttempt()
    {
        var (store, id, owner) = Arrange();
        var first = ClaimOne(store, id, owner);
        Assert.True(new Worker(store).Begin(id, owner, first, Eligibility.Allowed));
        Assert.True(store.TryRecordResult(id, owner, first, ProviderResult.TemporaryFailure));
        Assert.True(store.TryPermitRetry(id, owner, Eligibility.Allowed, true));
        var second = ClaimOne(store, id, owner);
        Assert.True(new Worker(store).Begin(id, owner, second, Eligibility.Allowed));

        Assert.False(store.TryRecordResult(id, owner, first, ProviderResult.ProviderAccepted));
        Assert.Equal(AttemptState.Claimed, store.ReadOwned(id, owner)!.State);
        Assert.True(store.TryRecordResult(id, owner, second, ProviderResult.ProviderAccepted));
        Assert.Equal(AttemptState.ProviderAccepted, store.ReadOwned(id, owner)!.State);
    }

    [Fact]
    public void SimulatedFailureBeforeCommit_LeavesNoPartialAttempt()
    {
        var (store, id, owner) = Arrange();
        Assert.Throws<SimulatedPreCommitException>(() =>
            new Worker(store).Claim(id, owner, Eligibility.Allowed, failBeforeCommit: true));

        var before = store.ReadOwned(id, owner)!;
        Assert.Equal(AttemptState.Pending, before.State);
        Assert.Equal(0, before.AttemptCount);
        Assert.Null(before.CurrentAttemptId);

        Assert.NotEqual(Guid.Empty, ClaimOne(store, id, owner));
        Assert.Equal(1, store.ReadOwned(id, owner)!.AttemptCount);
    }

    [Fact]
    public void SimulatedLostAcknowledgement_ReconcilesWithoutDuplicateDispatch()
    {
        var (store, id, owner) = Arrange();
        Assert.Throws<SimulatedLostAcknowledgementException>(() =>
            new Worker(store).Claim(id, owner, Eligibility.Allowed, loseAcknowledgement: true));

        // A recreated worker sees the same test store, as it would see a
        // previously committed attempt after performing a real DB lookup.
        var restarted = new Worker(store);
        var prior = store.ReadOwned(id, owner)!;
        Assert.Equal(AttemptState.Claimed, prior.State);
        Assert.Equal(1, prior.AttemptCount);
        Assert.True(prior.CurrentAttemptId.HasValue);
        Assert.Null(restarted.Claim(id, owner, Eligibility.Allowed));
        Assert.True(restarted.Begin(id, owner, prior.CurrentAttemptId.Value, Eligibility.Allowed));
        Assert.False(new Worker(store).Begin(
            id, owner, prior.CurrentAttemptId.Value, Eligibility.Allowed));
    }

    [Fact]
    public void ProviderResultBeforeDispatch_IsRejected()
    {
        var (store, id, owner) = Arrange();
        var attempt = ClaimOne(store, id, owner);
        Assert.False(store.TryRecordResult(id, owner, attempt, ProviderResult.ProviderAccepted));
        Assert.Equal(AttemptState.Claimed, store.ReadOwned(id, owner)!.State);
        Assert.False(store.ReadOwned(id, owner)!.DispatchStarted);
    }

    [Fact]
    public void SeparateModulesAndPrivateProfiles_NeverShareAttemptState()
    {
        var store = new SharedTestStore();
        var learning = Owner("learning");
        var social = Owner("social");
        var learningId = Guid.NewGuid();
        var socialId = Guid.NewGuid();
        Assert.True(store.RegisterTrusted(learningId, learning));
        Assert.True(store.RegisterTrusted(socialId, social));
        var first = ClaimOne(store, learningId, learning);

        Assert.Null(store.ReadOwned(learningId, social));
        Assert.Equal(AttemptState.Pending, store.ReadOwned(socialId, social)!.State);
        Assert.True(new Worker(store).Begin(learningId, learning, first, Eligibility.Allowed));
        Assert.True(store.TryRecordResult(
            learningId, learning, first, ProviderResult.ProviderAccepted));
        Assert.Equal(0, store.ReadOwned(socialId, social)!.AttemptCount);
    }
}
