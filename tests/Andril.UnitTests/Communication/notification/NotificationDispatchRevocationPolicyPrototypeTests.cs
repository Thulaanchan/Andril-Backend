using System;
using System.Threading.Tasks;
using Andril.Communication.Notifications;
using Xunit;

namespace Andril.UnitTests.Communication.Notifications;

/// <summary>
/// D15-D: PROPOSED dispatch-freshness acceptance model, TEST-ONLY.
/// This uses a mutable in-memory simulation rather than the real
/// NotificationPushOrchestrationService, M2 P2, PostgreSQL, or a push provider.
/// Passing tests do NOT establish a production fix for D15-A06.
/// A final check does not eliminate the race before an external provider call.
/// </summary>
public sealed class NotificationDispatchRevocationPolicyPrototypeTests
{
    [Theory]
    [InlineData(NotificationModule.Learning)]
    [InlineData(NotificationModule.Hobbies)]
    [InlineData(NotificationModule.Social)]
    public async Task EligiblePrivateModule_AllowsOneSimulatedDispatch(
        NotificationModule module)
    {
        var state = new TestOnlyDispatchState(module);
        var permitted = await state.TryDispatchAsync();

        Assert.True(permitted);
        Assert.Equal(1, state.Dispatches);
        Assert.Equal(2, state.Checks);
    }

    [Fact]
    public async Task ActiveProfileRevokedBetweenChecks_Denies()
    {
        var state = new TestOnlyDispatchState(NotificationModule.Learning);

        var permitted = await state.TryDispatchAsync(() =>
        {
            state.ProfileActive = false;
            return Task.CompletedTask;
        });

        Assert.False(permitted);
        Assert.Equal(0, state.Dispatches);
    }

    [Fact]
    public async Task ProfileReplacedBetweenChecks_DeniesEvenInSameModule()
    {
        var state = new TestOnlyDispatchState(NotificationModule.Hobbies);

        var permitted = await state.TryDispatchAsync(() =>
        {
            state.AuthoritativeProfileId = Guid.NewGuid();
            return Task.CompletedTask;
        });

        Assert.False(permitted);
        Assert.Equal(0, state.Dispatches);
    }

    [Fact]
    public async Task ModuleChangedBetweenChecks_Denies()
    {
        var state = new TestOnlyDispatchState(NotificationModule.Learning);

        var permitted = await state.TryDispatchAsync(() =>
        {
            state.AuthoritativeModule = NotificationModule.Social;
            return Task.CompletedTask;
        });

        Assert.False(permitted);
        Assert.Equal(0, state.Dispatches);
    }

    [Fact]
    public async Task PushPreferenceDisabledBetweenChecks_Denies()
    {
        var state = new TestOnlyDispatchState(NotificationModule.Hobbies);

        var permitted = await state.TryDispatchAsync(() =>
        {
            state.PushEnabled = false;
            return Task.CompletedTask;
        });

        Assert.False(permitted);
        Assert.Equal(0, state.Dispatches);
    }

    [Fact]
    public async Task DeviceRevokedBetweenChecks_Denies()
    {
        var state = new TestOnlyDispatchState(NotificationModule.Social);

        var permitted = await state.TryDispatchAsync(() =>
        {
            state.DeviceActive = false;
            return Task.CompletedTask;
        });

        Assert.False(permitted);
        Assert.Equal(0, state.Dispatches);
    }

    [Fact]
    public async Task DeviceReassignedToAnotherAccount_Denies()
    {
        var state = new TestOnlyDispatchState(NotificationModule.Learning);

        var permitted = await state.TryDispatchAsync(() =>
        {
            state.DeviceOwnerAccountId = Guid.NewGuid();
            return Task.CompletedTask;
        });

        Assert.False(permitted);
        Assert.Equal(0, state.Dispatches);
    }

    [Fact]
    public async Task NotificationReassignedToForeignProfile_Denies()
    {
        var state = new TestOnlyDispatchState(NotificationModule.Learning);

        var permitted = await state.TryDispatchAsync(() =>
        {
            state.NotificationOwnerProfileId = Guid.NewGuid();
            return Task.CompletedTask;
        });

        Assert.False(permitted);
        Assert.Equal(0, state.Dispatches);
    }

    [Fact]
    public async Task IdentityEvidenceUnavailableAtFinalCheck_Denies()
    {
        var state = new TestOnlyDispatchState(NotificationModule.Learning);

        var permitted = await state.TryDispatchAsync(() =>
        {
            state.IdentityAvailable = false;
            return Task.CompletedTask;
        });

        Assert.False(permitted);
        Assert.Equal(0, state.Dispatches);
    }

    [Fact]
    public async Task DeviceEvidenceUnavailableAtFinalCheck_Denies()
    {
        var state = new TestOnlyDispatchState(NotificationModule.Hobbies);

        var permitted = await state.TryDispatchAsync(() =>
        {
            state.DeviceEvidenceAvailable = false;
            return Task.CompletedTask;
        });

        Assert.False(permitted);
        Assert.Equal(0, state.Dispatches);
    }

    [Fact]
    public async Task ForeignAccountNeverAuthorizesInitialDispatch()
    {
        var state = new TestOnlyDispatchState(NotificationModule.Learning)
        {
            AuthoritativeAccountId = Guid.NewGuid()
        };

        var permitted = await state.TryDispatchAsync();

        Assert.False(permitted);
        Assert.Equal(1, state.Checks);
        Assert.Equal(0, state.Dispatches);
    }

    [Fact]
    public async Task SocialOptInRevokedBetweenChecks_Denies()
    {
        var state = new TestOnlyDispatchState(NotificationModule.Social);

        var permitted = await state.TryDispatchAsync(() =>
        {
            state.SocialOptedIn = false;
            return Task.CompletedTask;
        });

        Assert.False(permitted);
        Assert.Equal(0, state.Dispatches);
    }

    [Fact]
    public async Task SocialAgeEligibilityRevokedBetweenChecks_Denies()
    {
        var state = new TestOnlyDispatchState(NotificationModule.Social);

        var permitted = await state.TryDispatchAsync(() =>
        {
            state.SocialAdultEligible = false;
            return Task.CompletedTask;
        });

        Assert.False(permitted);
        Assert.Equal(0, state.Dispatches);
    }

    /// <summary>
    /// Local toy authorization state: not backed by trusted M2 identity evidence.
    /// The two checks express a candidate acceptance expectation only.
    /// No database uniqueness, dispatch lease, or provider atomicity exists here.
    /// </summary>
    private sealed class TestOnlyDispatchState
    {
        private readonly Guid _requestedAccountId = Guid.NewGuid();
        private readonly Guid _requestedProfileId = Guid.NewGuid();
        private readonly NotificationModule _requestedModule;

        public TestOnlyDispatchState(NotificationModule module)
        {
            _requestedModule = module;
            AuthoritativeAccountId = _requestedAccountId;
            AuthoritativeProfileId = _requestedProfileId;
            AuthoritativeModule = module;
            NotificationOwnerProfileId = _requestedProfileId;
            NotificationOwnerModule = module;
            DeviceOwnerAccountId = _requestedAccountId;
        }

        public Guid AuthoritativeAccountId { get; set; }
        public Guid AuthoritativeProfileId { get; set; }
        public NotificationModule AuthoritativeModule { get; set; }
        public Guid NotificationOwnerProfileId { get; set; }
        public NotificationModule NotificationOwnerModule { get; set; }
        public Guid DeviceOwnerAccountId { get; set; }
        public bool ProfileActive { get; set; } = true;
        public bool PushEnabled { get; set; } = true;
        public bool DeviceActive { get; set; } = true;
        public bool IdentityAvailable { get; set; } = true;
        public bool DeviceEvidenceAvailable { get; set; } = true;
        public bool SocialAdultEligible { get; set; } = true;
        public bool SocialOptedIn { get; set; } = true;
        public int Checks { get; private set; }
        public int Dispatches { get; private set; }

        public async Task<bool> TryDispatchAsync(Func<Task>? betweenChecks = null)
        {
            if (!CheckCurrentEligibility())
                return false;

            if (betweenChecks is not null)
                await betweenChecks();

            // Candidate final validation before simulated dispatch.
            // Not an atomic authorization/provider handoff.
            if (!CheckCurrentEligibility())
                return false;

            Dispatches++;
            return true;
        }

        private bool CheckCurrentEligibility()
        {
            Checks++;

            return IdentityAvailable &&
                   DeviceEvidenceAvailable &&
                   ProfileActive &&
                   PushEnabled &&
                   DeviceActive &&
                   AuthoritativeAccountId == _requestedAccountId &&
                   AuthoritativeProfileId == _requestedProfileId &&
                   AuthoritativeModule == _requestedModule &&
                   NotificationOwnerProfileId == _requestedProfileId &&
                   NotificationOwnerModule == _requestedModule &&
                   DeviceOwnerAccountId == _requestedAccountId &&
                   (_requestedModule != NotificationModule.Social ||
                       (SocialAdultEligible && SocialOptedIn));
        }
    }
}
