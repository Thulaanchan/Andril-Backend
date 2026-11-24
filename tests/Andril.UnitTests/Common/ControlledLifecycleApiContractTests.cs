using System.Net;
using System.Text.Json;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Lifecycle;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Common;

public sealed class ControlledLifecycleApiContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Subject = new("5a89c7bb-4897-4b0d-a315-db6b3fdfd52b");
    private static readonly Guid Version = new("92c554d7-4940-48c2-84f6-a7a8edf0b964");
    private static readonly Guid Key = new("4b4415b8-b1c1-48cb-9cd5-d26643359289");
    private static readonly Guid Correlation = new("45362bed-677f-4351-96c8-c7863cf608fd");
    private const string Valid = """{"expectedRevision":1}""";

    [Theory]
    [InlineData("{}", "learning")]
    [InlineData("[]", "learning")]
    [InlineData("""{"expectedRevision":0}""", "learning")]
    [InlineData("""{"expectedRevision":9223372036854775807}""", "learning")]
    [InlineData("""{"expectedRevision":"1"}""", "learning")]
    [InlineData("""{"expectedRevision":1,"expectedRevision":1}""", "learning")]
    [InlineData("""{"expectedRevision":1,"accountId":"foreign"}""", "learning")]
    [InlineData("""{"expectedRevision":1,"socialOptIn":true}""", "learning")]
    [InlineData(Valid, "Learning")]
    public async Task InvalidInputNeverReachesTheAccountRepositoryOrWriter(string body, string module)
    {
        var f = new Fixture();
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Stop(body, module)).Status);
        Assert.Equal(0, f.AccountReads);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task MissingAndRepeatedKeysAreRejectedBeforeLookup()
    {
        var f = new Fixture();
        foreach (var keys in new string[][] { [], ["invalid"], [Guid.Empty.ToString()], [Key.ToString(), Key.ToString()] })
            Assert.Equal(HttpStatusCode.BadRequest, (await f.Stop(keys: keys)).Status);
        Assert.Equal(0, f.AccountReads);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task UnauthenticatedUnconfirmedAndMismatchedAccountFailClosed()
    {
        var f = new Fixture { UserId = null };
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Stop()).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Api.ReadStatusesAsync()).Status);
        f.UserId = Subject;
        f.Confirmed = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Stop()).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Api.ReadStatusesAsync()).Status);
        Assert.Equal(0, f.AccountReads);

        f.Confirmed = true;
        f.LookupOverride = Account.Create(Guid.NewGuid(), Now);
        var response = await f.Stop();
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Null(response.Body);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task DeactivationExactReplayPreservesOneAuditAndConflictsDiscloseNoState()
    {
        var f = new Fixture();
        var first = await f.Stop();
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(new LifecycleDeactivationResponse("learning", "inactive", 2, Now, "deactivated"), first.Body);
        Assert.Equal(first, await f.Stop());
        Assert.Single(f.Audits);
        Assert.Equal(Correlation, f.Audits[0].CorrelationId);

        var conflict = await f.Stop("""{"expectedRevision":2}""");
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Null(conflict.Body);
        Assert.Equal("no-store", conflict.CacheControl);
        Assert.Single(f.Audits);
        Assert.Equal("no-store", first.CacheControl);
    }

    [Theory]
    [InlineData("inactive", HttpStatusCode.Conflict)]
    [InlineData("revision", HttpStatusCode.Conflict)]
    [InlineData("stale", HttpStatusCode.ServiceUnavailable)]
    [InlineData("unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("denied", HttpStatusCode.Forbidden)]
    public async Task FailuresAreSanitizedAndLeaveStateUnchanged(string fault, HttpStatusCode expected)
    {
        var f = new Fixture();
        f.Snapshot = fault switch
        {
            "inactive" => f.Snapshot with { Current = new(new InactiveModuleMembership(f.Scope, 2), Now) },
            "revision" => f.Snapshot with { Current = new(ActiveModuleMembership.Restore(f.Scope, 3), Now) },
            "stale" => f.Snapshot with { DeactivationEvidence = f.Snapshot.DeactivationEvidence! with { CurrentVersion = Guid.NewGuid() } },
            "unavailable" => f.Snapshot with { DeactivationEvidence = null },
            _ => f.Snapshot with { DeactivationEvidence = f.Snapshot.DeactivationEvidence! with { Outcome = ModuleAuthorizationOutcome.Denied } }
        };
        var before = f.Snapshot;
        var response = await f.Stop();
        Assert.Equal(expected, response.Status);
        Assert.Null(response.Body);
        Assert.Same(before, f.Snapshot);
        Assert.Empty(f.Audits);
    }

    [Fact]
    public async Task StatusProjectionOmitsPrivateEvidenceAndWithholdsPartialResultsOnOutage()
    {
        var f = new Fixture();
        var response = await f.Api.ReadStatusesAsync();
        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("""{"learning":{"state":"active","revision":1},"hobbies":{"state":"neverActivated"},"social":{"state":"neverActivated"}}""",
            JsonSerializer.Serialize(response.Body));
        Assert.Equal(new[] { "learning", "hobbies", "social" }, f.StatusReads);
        Assert.Equal("no-store", response.CacheControl);
        Assert.Equal(0, f.Writes);

        f.UnavailableModule = "hobbies";
        var unavailable = await f.Api.ReadStatusesAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.Status);
        Assert.Null(unavailable.Body);
    }

    [Fact]
    public async Task IdentityChangeDuringAKnownCommitWithholdsTheOriginalAccountsResult()
    {
        var f = new Fixture { ChangeSubjectOnWrite = true };
        var response = await f.Stop();
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Null(response.Body);
        Assert.Single(f.Audits);
    }

    [Fact]
    public async Task IdentityChangeDuringAReadDiscardsEarlierStatus()
    {
        var f = new Fixture { ChangeSubjectOn = "hobbies" };
        var response = await f.Api.ReadStatusesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Null(response.Body);
        Assert.Equal(new[] { "learning", "hobbies" }, f.StatusReads);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutAWriteOrPartialStatus()
    {
        var f = new Fixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Stop(ct: canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Api.ReadStatusesAsync(canceled.Token));
        Assert.Equal(0, f.AccountReads);
        Assert.Equal(0, f.Writes);
        Assert.Empty(f.StatusReads);
    }

    private sealed class Fixture : ICurrentUser, IOnboardingIdentity, IAccountRepository, IOfflineModuleDeactivationStore, IModuleNavigationReader
    {
        private readonly Account account = Account.Create(Subject, Now);
        public Guid? UserId { get; set; } = Subject;
        public bool IsAuthenticated => UserId.HasValue;
        internal bool Confirmed { get; set; } = true;
        internal Account? LookupOverride { get; set; }
        internal int AccountReads { get; private set; }
        internal int Writes { get; private set; }
        internal string? UnavailableModule { get; set; }
        internal string? ChangeSubjectOn { get; set; }
        internal bool ChangeSubjectOnWrite { get; init; }
        internal List<string> StatusReads { get; } = [];
        internal List<ModuleLifecycleAudit> Audits { get; } = [];
        internal ModuleMembershipScope Scope => new(account.Id, "learning");
        internal ModuleLifecycleSnapshot Snapshot { get; set; }
        internal ControlledLifecycleApiContracts Api { get; }

        internal Fixture()
        {
            Snapshot = new(Evidence("learning"), new(ActiveModuleMembership.Restore(Scope, 1), Now), null,
                new(Scope, Subject, ModuleAuthorizationOutcome.Satisfied, Version, Version, Now));
            Api = new(this, this, (subject, _, ct) => FindBySubjectAsync(subject, ct), new(this), new(this, this));
        }

        private ModuleActivationEvidence Evidence(string module)
        {
            var policy = new PolicyDocument("test-version", "https://example.invalid", "en");
            return new(new(account.Id, module), ModuleAuthorizationOutcome.Satisfied, account, Subject,
                AccountDemographics.Create(account.Id, new DateOnly(2000, 1, 1), "prefer_not_to_say", Now),
                new(policy, policy, policy),
                [
                    AccountPolicyRecord.Create(account.Id, PolicyActions.TermsAgreed, policy.Version, Now),
                    AccountPolicyRecord.Create(account.Id, PolicyActions.PrivacyAcknowledged, policy.Version, Now)
                ], true, false, Version, Version, Now);
        }

        internal async Task<LifecycleApiResult<LifecycleDeactivationResponse>> Stop(string body = Valid, string module = "learning",
            string[]? keys = null, CancellationToken ct = default)
        {
            using var json = JsonDocument.Parse(body);
            return await Api.DeactivateAsync(module, keys ?? [Key.ToString("D")], json.RootElement, Correlation, ct);
        }

        public bool IsConfirmedFor(Guid subject) => Confirmed && subject == Subject;
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Subject, subject);
            AccountReads++;
            return Task.FromResult<Account?>(LookupOverride ?? account);
        }

        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Lifecycle does not create accounts.");

        public Task<ModuleLifecycleResult> WriteAsync(Guid subject, string module, Guid requestKey,
            Func<ModuleLifecycleSnapshot, ModuleLifecycleDecision> decide, CancellationToken cancellationToken)
        {
            Assert.Equal(Subject, subject);
            Assert.Equal("learning", module);
            Assert.Equal(Key, requestKey);
            Writes++;
            var decision = decide(Snapshot);
            if (decision.Write is { } write)
            {
                Snapshot = Snapshot with { Current = write.ChangedMembership ?? Snapshot.Current, Receipt = write.Receipt };
                if (write.Audit is { } audit) Audits.Add(audit);
            }
            if (ChangeSubjectOnWrite)
                UserId = Guid.NewGuid();
            return Task.FromResult(decision.Result);
        }

        public Task<ModuleLifecycleSnapshot> ReadAsync(Guid subject, string module, Guid requestKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fixture reports a known commit result.");

        public Task<ModuleNavigationSnapshot> ReadNavigationAsync(Guid subject, string module, CancellationToken cancellationToken)
        {
            Assert.Equal(Subject, subject);
            StatusReads.Add(module);
            if (module == ChangeSubjectOn)
                UserId = Guid.NewGuid();
            var evidence = Evidence(module);
            if (module == UnavailableModule)
                evidence = evidence with { SourceOutcome = ModuleAuthorizationOutcome.Unavailable };
            return Task.FromResult(new ModuleNavigationSnapshot(evidence, module == "learning" ? Snapshot.Current?.Membership : null));
        }
    }
}
