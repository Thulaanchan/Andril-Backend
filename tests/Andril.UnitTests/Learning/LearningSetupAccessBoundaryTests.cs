using System.Text.Json;
using Andril.Learning.Interfaces;
using Andril.Learning.Services;
using Andril.Shared.Authentication;
using Xunit;

namespace Andril.UnitTests.Learning;

public sealed class LearningSetupAccessBoundaryTests
{
    private sealed class User : ICurrentUser
    {
        public Guid? UserId { get; set; } = Guid.NewGuid();
        public bool IsAuthenticated { get; set; } = true;
    }
    // Synthetic authority is confined to this test assembly; never used by production DI.
    private sealed class Authority : ILearningSetupAuthorityReader
    {
        internal int Calls;
        internal Guid Subject;
        internal LearningSetupOperation Operation;
        internal CancellationToken Token;
        internal Func<Task<LearningSetupAccessEvidence?>> Read = () => Task.FromResult<LearningSetupAccessEvidence?>(null);
        public Task<LearningSetupAccessEvidence?> ReadCurrentAsync(Guid subject, LearningSetupOperation operation, CancellationToken cancellationToken = default)
        {
            Calls++; Subject = subject; Operation = operation; Token = cancellationToken;
            return Read();
        }
    }
    private static LearningSetupAccessEvidence Valid(User user, LearningSetupOperation operation, bool complete = false)
    {
        var account = Guid.NewGuid(); var version = Guid.NewGuid();
        return new(user.UserId!.Value, account, Guid.NewGuid(), account, "learning", operation,
            true, true, true, true, true, false, complete, version, version);
    }

    [Theory]
    [InlineData(LearningSetupOperation.ReadSetup, false)]
    [InlineData(LearningSetupOperation.UpdateSetup, false)]
    [InlineData(LearningSetupOperation.ReadSetup, true)]
    [InlineData(LearningSetupOperation.UpdateSetup, true)]
    [InlineData(LearningSetupOperation.EnterHome, true)]
    public async Task ValidOwnerUsesServerEvidenceAndSetupDoesNotRequireCompletion(LearningSetupOperation operation, bool complete)
    {
        var user = new User(); var evidence = Valid(user, operation, complete);
        var authority = new Authority { Read = () => Task.FromResult<LearningSetupAccessEvidence?>(evidence) };
        var result = await new LearningSetupAccessBoundary(user, authority).CheckAsync(operation);
        Assert.Equal(LearningSetupAccessState.Allowed, result.State); Assert.NotNull(result.Scope);
        Assert.Equal(evidence.AccountId, result.Scope.AccountId); Assert.Equal(evidence.LearningProfileId, result.Scope.LearningProfileId);
        Assert.Equal("learning", result.Scope.Module); Assert.Equal(user.UserId, authority.Subject);
        Assert.Equal(operation, authority.Operation); Assert.Equal(1, authority.Calls);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("owner")]
    [InlineData("module")]
    [InlineData("module-case")]
    [InlineData("operation")]
    [InlineData("session")]
    [InlineData("inactive")]
    [InlineData("restricted")]
    [InlineData("consent")]
    [InlineData("permission")]
    [InlineData("revoked")]
    [InlineData("stale")]
    [InlineData("missing-observed")]
    [InlineData("missing-current")]
    public async Task InvalidAuthorityNeverReleasesScope(string defect)
    {
        var user = new User(); var good = Valid(user, LearningSetupOperation.UpdateSetup);
        var evidence = defect switch
        {
            "subject" => good with { SubjectId = Guid.NewGuid() }, "account" => good with { AccountId = Guid.Empty },
            "profile" => good with { LearningProfileId = Guid.Empty }, "owner" => good with { ProfileOwnerAccountId = Guid.NewGuid() },
            "module" => good with { Module = "social" }, "module-case" => good with { Module = "Learning" },
            "operation" => good with { Operation = LearningSetupOperation.ReadSetup }, "session" => good with { SessionCurrent = false },
            "inactive" => good with { MembershipActive = false }, "restricted" => good with { StandingAllows = false },
            "consent" => good with { ConsentSatisfied = false }, "permission" => good with { OperationAllowed = false },
            "revoked" => good with { Revoked = true }, "stale" => good with { CurrentVersion = Guid.NewGuid() },
            "missing-observed" => good with { ObservedVersion = Guid.Empty }, _ => good with { CurrentVersion = Guid.Empty }
        };
        var authority = new Authority { Read = () => Task.FromResult<LearningSetupAccessEvidence?>(evidence) };
        var result = await new LearningSetupAccessBoundary(user, authority).CheckAsync(LearningSetupOperation.UpdateSetup);
        Assert.Equal(LearningSetupAccessState.Denied, result.State); Assert.Null(result.Scope);
        Assert.Equal(1, authority.Calls);
    }

    [Fact]
    public async Task IncompleteSetupCannotEnterHome()
    {
        var user = new User(); var authority = new Authority { Read = () => Task.FromResult<LearningSetupAccessEvidence?>(Valid(user, LearningSetupOperation.EnterHome)) };
        var result = await new LearningSetupAccessBoundary(user, authority).CheckAsync(LearningSetupOperation.EnterHome);
        Assert.Equal(LearningSetupAccessState.Denied, result.State); Assert.Null(result.Scope);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("missing-subject")]
    [InlineData("empty-subject")]
    [InlineData("invalid-operation")]
    public async Task InvalidCallerNeverReadsAuthority(string defect)
    {
        var user = new User(); var authority = new Authority();
        if (defect == "anonymous") user.IsAuthenticated = false;
        if (defect == "missing-subject") user.UserId = null;
        if (defect == "empty-subject") user.UserId = Guid.Empty;
        var result = await new LearningSetupAccessBoundary(user, authority).CheckAsync(
            defect == "invalid-operation" ? (LearningSetupOperation)0 : LearningSetupOperation.ReadSetup);
        Assert.Equal(LearningSetupAccessState.Denied, result.State); Assert.Null(result.Scope); Assert.Equal(0, authority.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityChangesDuringReadCannotReleaseEarlierScope(bool signedOut)
    {
        var user = new User(); var evidence = Valid(user, LearningSetupOperation.ReadSetup);
        var authority = new Authority { Read = () =>
        {
            if (signedOut) user.IsAuthenticated = false; else user.UserId = Guid.NewGuid();
            return Task.FromResult<LearningSetupAccessEvidence?>(evidence);
        }};
        var result = await new LearningSetupAccessBoundary(user, authority).CheckAsync(LearningSetupOperation.ReadSetup);
        Assert.Equal(LearningSetupAccessState.Denied, result.State); Assert.Null(result.Scope);
    }

    [Fact]
    public async Task EveryIndependentCallReadsAuthorityAgainAndDoesNotCachePositiveAccess()
    {
        var user = new User(); var current = Valid(user, LearningSetupOperation.ReadSetup);
        var authority = new Authority { Read = () => Task.FromResult<LearningSetupAccessEvidence?>(current) };
        var boundary = new LearningSetupAccessBoundary(user, authority);
        Assert.Equal(LearningSetupAccessState.Allowed, (await boundary.CheckAsync(LearningSetupOperation.ReadSetup)).State);
        current = current with { Revoked = true };
        Assert.Equal(LearningSetupAccessState.Denied, (await boundary.CheckAsync(LearningSetupOperation.ReadSetup)).State);
        authority.Read = () => Task.FromResult<LearningSetupAccessEvidence?>(null);
        var unavailable = await boundary.CheckAsync(LearningSetupOperation.ReadSetup);
        Assert.Equal(LearningSetupAccessState.Unavailable, unavailable.State); Assert.Null(unavailable.Scope);
        Assert.Equal(3, authority.Calls);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("during")]
    [InlineData("after")]
    public async Task CancellationPropagatesWithoutFallbackOrRetry(string phase)
    {
        using var cts = new CancellationTokenSource(); var user = new User();
        var authority = new Authority { Read = () =>
        {
            cts.Cancel();
            return phase == "during" ? Task.FromCanceled<LearningSetupAccessEvidence?>(cts.Token)
                : Task.FromResult<LearningSetupAccessEvidence?>(Valid(user, LearningSetupOperation.ReadSetup));
        }};
        if (phase == "before") cts.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LearningSetupAccessBoundary(user, authority).CheckAsync(LearningSetupOperation.ReadSetup, cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken); Assert.Equal(phase == "before" ? 0 : 1, authority.Calls);
        if (phase != "before") Assert.Equal(cts.Token, authority.Token);
    }

    [Fact]
    public async Task UnexpectedAuthorityFailurePropagatesWithoutRetry()
    {
        var failure = new InvalidOperationException("Synthetic authority failure"); var authority = new Authority { Read = () => throw failure };
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LearningSetupAccessBoundary(new User(), authority).CheckAsync(LearningSetupOperation.ReadSetup));
        Assert.Same(failure, observed); Assert.Equal(1, authority.Calls);
    }

    [Fact]
    public async Task PrivateEvidenceAndResultsDoNotSerializeScopeOrAuthorityFacts()
    {
        var user = new User(); var evidence = Valid(user, LearningSetupOperation.ReadSetup);
        var authority = new Authority { Read = () => Task.FromResult<LearningSetupAccessEvidence?>(evidence) };
        var result = await new LearningSetupAccessBoundary(user, authority).CheckAsync(LearningSetupOperation.ReadSetup);
        Assert.Equal("{}", JsonSerializer.Serialize(evidence)); Assert.Equal("{}", JsonSerializer.Serialize(result));
        foreach (var id in new[] { evidence.SubjectId, evidence.AccountId, evidence.LearningProfileId, evidence.CurrentVersion })
        { Assert.DoesNotContain(id.ToString(), evidence.ToString()); Assert.DoesNotContain(id.ToString(), result.ToString()); }
    }

    [Fact]
    public void BoundaryHasNoCallerIdentityInputsOrRepositoryMutationDependency()
    {
        var parameters = typeof(ILearningSetupAccessBoundary).GetMethod("CheckAsync")!.GetParameters();
        Assert.Equal(new[] { typeof(LearningSetupOperation), typeof(CancellationToken) }, parameters.Select(parameter => parameter.ParameterType));
        Assert.Equal(new[] { typeof(ICurrentUser), typeof(ILearningSetupAuthorityReader) },
            Assert.Single(typeof(LearningSetupAccessBoundary).GetConstructors()).GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(typeof(LearningSetupAccessBoundary).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
            field => typeof(ILearningSetupRepository).IsAssignableFrom(field.FieldType));
        // No repository exists in this scope, so neither successful nor denied preflight can mutate one.
        // Actual coordinated release/commit and concurrent revocation remain adapter integration tests.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingDependenciesAreRejected(bool missingUser) =>
        Assert.Throws<ArgumentNullException>(() => new LearningSetupAccessBoundary(missingUser ? null! : new User(),
            missingUser ? new Authority() : null!));
}
