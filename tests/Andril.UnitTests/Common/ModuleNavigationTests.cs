using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class ModuleNavigationTests
{
    private static readonly Guid Subject = new("12674070-51a2-4e20-ab66-2f8191d58d68");
    private static readonly Guid ForeignSubject = new("dfcbb783-f5b6-4140-94eb-14b918306a9a");
    private static readonly Guid Version = new("4b97883d-b984-4da9-8ffc-bb88e82d471c");
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [Trait("PartC", "M2-P3C-C-QA-01")]
    [Trait("PartC", "M2-P3C-C-QA-03")]
    [Trait("PartC", "M2-P3C-C-QA-08")]
    [InlineData("learning", "hobbies")]
    [InlineData("learning", "social")]
    [InlineData("hobbies", "learning")]
    [InlineData("hobbies", "social")]
    [InlineData("social", "learning")]
    [InlineData("social", "hobbies")]
    public async Task EachDirectionReadsTheDestinationAndLeavesBothMembershipsUnchanged(string from, string to)
    {
        var reader = new Fixture();
        var origin = reader.Set(from, ModuleNavigationState.Active, 4);
        var destination = reader.Set(to, ModuleNavigationState.Active, 9);

        Assert.Equal(new(ModuleNavigationState.Active, from, 4), await reader.Service.ReadAsync(from));
        Assert.Equal(new(ModuleNavigationState.Active, to, 9), await reader.Service.ReadAsync(to));

        Assert.Equal(new[] { (Subject, from), (Subject, to) }, reader.Requests);
        Assert.Same(origin, reader.Snapshots[from].Membership);
        Assert.Same(destination, reader.Snapshots[to].Membership);
        Assert.Equal(2, reader.Snapshots.Count);
    }

    [Theory]
    [Trait("PartC", "M2-P3C-C-QA-02")]
    [InlineData("Social")]
    [InlineData("dating")]
    [InlineData(" learning")]
    [InlineData("")]
    public async Task InvalidModuleIsDeniedBeforeRead(string module)
    {
        var reader = new Fixture();
        Assert.Equal(new(ModuleNavigationState.Denied), await reader.Service.ReadAsync(module));
        Assert.Empty(reader.Requests);
    }

    [Fact]
    [Trait("PartC", "M2-P3C-C-QA-04")]
    [Trait("PartC", "M2-P3C-C-QA-05")]
    public async Task InactiveAndAbsentMembershipsRemainSeparate()
    {
        var reader = new Fixture();
        reader.Set("learning", ModuleNavigationState.Inactive, 2);
        reader.Set("hobbies", ModuleNavigationState.NeverActivated);

        Assert.Equal(new(ModuleNavigationState.Inactive, "learning", 2), await reader.Service.ReadAsync("learning"));
        Assert.Equal(new(ModuleNavigationState.NeverActivated, "hobbies"), await reader.Service.ReadAsync("hobbies"));
        Assert.Null(reader.Snapshots["hobbies"].Membership);
    }

    [Fact]
    [Trait("PartC", "M2-P3C-C-QA-06")]
    [Trait("PartC", "M2-P3C-C-QA-10")]
    public async Task UnverifiedAndMismatchedScopesDiscloseNoStatus()
    {
        var reader = new Fixture();
        reader.Set("learning", ModuleNavigationState.Active, 4);
        var original = reader.Snapshots["learning"];
        reader.User.Authenticated = false;
        Assert.Equal(new(ModuleNavigationState.Denied), await reader.Service.ReadAsync("learning"));
        Assert.Empty(reader.Requests);
        reader.User.Authenticated = true;

        var foreign = Account.Create(ForeignSubject, Now);
        reader.Snapshots["learning"] = original with
        {
            Evidence = original.Evidence with { Account = foreign, ConfirmedSubject = ForeignSubject }
        };
        Assert.Equal(new(ModuleNavigationState.Denied), await reader.Service.ReadAsync("learning"));
        reader.Snapshots["learning"] = original with
        {
            Membership = ActiveModuleMembership.Restore(new(foreign.Id, "learning"), 4)
        };
        Assert.Equal(new(ModuleNavigationState.Denied), await reader.Service.ReadAsync("learning"));
        reader.Snapshots["learning"] = original with
        {
            Membership = ActiveModuleMembership.Restore(original.Evidence.Scope with { Module = "social" }, 4)
        };
        Assert.Equal(new(ModuleNavigationState.Denied), await reader.Service.ReadAsync("learning"));
        reader.Snapshots["learning"] = original with
        {
            Evidence = original.Evidence with { ConfirmedSubject = null }
        };
        Assert.Equal(new(ModuleNavigationState.Denied), await reader.Service.ReadAsync("learning"));
    }

    [Fact]
    [Trait("PartC", "M2-P3C-C-QA-07")]
    [Trait("PartC", "M2-P3C-C-QA-14")]
    public async Task DeniedUnavailableOrStaleAuthorityCannotDiscloseAnActiveSocialMembership()
    {
        var reader = new Fixture();
        reader.Set("social", ModuleNavigationState.Active, 3);
        var active = reader.Snapshots["social"];
        foreach (var (evidence, result) in new[]
        {
            (active.Evidence with { SourceOutcome = ModuleAuthorizationOutcome.Denied }, ModuleNavigationState.Denied),
            (active.Evidence with { SourceOutcome = ModuleAuthorizationOutcome.Unavailable }, ModuleNavigationState.Unavailable),
            (active.Evidence with { CurrentEvidenceVersion = ForeignSubject }, ModuleNavigationState.Denied),
            (active.Evidence with { SocialOptIn = false }, ModuleNavigationState.Denied),
            (active.Evidence with { RestrictionsAllowActivation = false }, ModuleNavigationState.Denied),
            (active.Evidence with { Demographics = AccountDemographics.Create(active.Evidence.Scope.AccountId,
                new DateOnly(2010, 1, 1), "prefer_not_to_say", Now) }, ModuleNavigationState.Denied)
        })
        {
            reader.Snapshots["social"] = active with { Evidence = evidence };
            Assert.Equal(new(result), await reader.Service.ReadAsync("social"));
        }
    }

    [Theory]
    [Trait("PartC", "M2-P3C-C-QA-11")]
    [Trait("PartC", "M2-P3C-C-QA-12")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NeitherActiveNorStaleNavigationStatusGrantsHome(bool profileComplete)
    {
        var reader = new Fixture();
        reader.Set("learning", ModuleNavigationState.Active, 1);
        var earlier = await reader.Service.ReadAsync("learning");
        var home = new PrivateModuleAccess(new Completion(profileComplete));
        await Assert.ThrowsAsync<ProfileContractPendingException>(() => home.RequireAsync("learning"));

        reader.Set("learning", ModuleNavigationState.Inactive, 2);
        Assert.Equal(ModuleNavigationState.Active, earlier.State);
        Assert.Equal(new(ModuleNavigationState.Inactive, "learning", 2), await reader.Service.ReadAsync("learning"));
        await Assert.ThrowsAsync<ProfileContractPendingException>(() => home.RequireAsync("learning"));
    }

    [Fact]
    [Trait("PartC", "M2-P3C-C-QA-13")]
    public async Task ADelayedResponseDoesNotReplaceCurrentStateOrSurviveAnAccountChange()
    {
        var reader = new Fixture();
        reader.Set("learning", ModuleNavigationState.Active, 1);
        reader.Set("hobbies", ModuleNavigationState.Active, 1);
        reader.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = reader.Service.ReadAsync("learning");
        reader.Set("learning", ModuleNavigationState.Inactive, 2);
        reader.Set("social", ModuleNavigationState.NeverActivated);
        var release = reader.ReadGate;
        reader.ReadGate = null;

        Assert.Equal(new(ModuleNavigationState.Active, "hobbies", 1), await reader.Service.ReadAsync("hobbies"));
        Assert.Equal(new(ModuleNavigationState.NeverActivated, "social"), await reader.Service.ReadAsync("social"));
        Assert.Equal(new(ModuleNavigationState.Inactive, "learning", 2), await reader.Service.ReadAsync("learning"));
        reader.User.SubjectId = ForeignSubject;
        release.SetResult();

        Assert.Equal(new(ModuleNavigationState.Denied), await pending);
    }

    [Fact]
    [Trait("PartC", "M2-P3C-C-QA-15")]
    [Trait("PartC", "M2-P3C-C-QA-16")]
    public async Task FailureAndCancellationDoNotExposeEvidenceOrState()
    {
        var reader = new Fixture();
        reader.Set("learning", ModuleNavigationState.Active, 1);
        reader.Failure = new IOException("Private dependency details");
        Assert.Equal(new(ModuleNavigationState.Unavailable), await reader.Service.ReadAsync("learning"));
        Assert.Equal(new[] { "Module", "Revision", "State" },
            typeof(ModuleNavigationStatus).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));

        reader.Failure = null;
        reader.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var pending = reader.Service.ReadAsync("learning", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, reader.Snapshots["learning"].Membership!.Revision);
    }

    private sealed class Completion(bool isComplete) : IPrivateProfileCompletionReader
    {
        public Task<ProfileCompletionStatus> ReadAsync(string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProfileCompletionStatus(null, isComplete));
    }

    private sealed class CurrentUser : ICurrentUser
    {
        public bool Authenticated { get; set; } = true;
        public Guid? SubjectId { get; set; } = Subject;
        public Guid? UserId => SubjectId;
        public bool IsAuthenticated => Authenticated;
    }

    private sealed class Fixture : IModuleNavigationReader
    {
        private readonly Account account = Account.Create(Subject, Now);
        public CurrentUser User { get; } = new();
        public ModuleNavigationService Service { get; }
        public Dictionary<string, ModuleNavigationSnapshot> Snapshots { get; } = new(StringComparer.Ordinal);
        public List<(Guid Subject, string Module)> Requests { get; } = [];
        public TaskCompletionSource? ReadGate { get; set; }
        public Exception? Failure { get; set; }
        public Fixture() => Service = new(User, this);

        public ModuleMembership? Set(string module, ModuleNavigationState state, long revision = 0)
        {
            var scope = new ModuleMembershipScope(account.Id, module);
            var policy = new PolicyDocument("test-policy", "https://example.invalid", "en");
            var evidence = new ModuleActivationEvidence(scope, ModuleAuthorizationOutcome.Satisfied, account, Subject,
                AccountDemographics.Create(account.Id, new DateOnly(2000, 1, 1), "prefer_not_to_say", Now),
                new(policy, policy, policy),
                [
                    AccountPolicyRecord.Create(account.Id, PolicyActions.TermsAgreed, policy.Version, Now),
                    AccountPolicyRecord.Create(account.Id, PolicyActions.PrivacyAcknowledged, policy.Version, Now)
                ], true, module == "social", Version, Version, Now);
            ModuleMembership? membership = state switch
            {
                ModuleNavigationState.Active => ActiveModuleMembership.Restore(scope, revision),
                ModuleNavigationState.Inactive => new InactiveModuleMembership(scope, revision),
                _ => null
            };
            Snapshots[module] = new(evidence, membership);
            return membership;
        }

        public async Task<ModuleNavigationSnapshot> ReadNavigationAsync(Guid subject, string module, CancellationToken cancellationToken)
        {
            Requests.Add((subject, module));
            if (Failure is not null) throw Failure;
            var snapshot = Snapshots[module];
            if (ReadGate is { } gate)
                await gate.Task.WaitAsync(cancellationToken);
            return snapshot;
        }
    }
}
