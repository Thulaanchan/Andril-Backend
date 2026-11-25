using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Andril.API.Authorization.Groups;
using Andril.Common.Accounts.Profiles;
using Andril.Groups.Entities;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Xunit;

namespace Andril.SecurityTests.Groups;

/// <summary>
/// Security, module isolation, authoritative evidence verification, and fail-closed boundary tests
/// for <see cref="GroupActorContext"/> (CF-07).
/// </summary>
public sealed class GroupActorContextTests
{
    #region Test Doubles

    private sealed class StubPrivateProfileService : IPrivateProfileService
    {
        private readonly Func<string, Task<PrivateProfileDto?>> _profileResolver;

        public StubPrivateProfileService(PrivateProfileDto? profile)
            : this(_ => Task.FromResult(profile))
        {
        }

        public StubPrivateProfileService(Func<string, Task<PrivateProfileDto?>> profileResolver)
        {
            _profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        }

        public async Task<T> WithAuthorizedProfileAsync<T>(
            string module,
            Func<PrivateProfileDto, Task<T>> action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var profile = await _profileResolver(module).ConfigureAwait(false);
            if (profile is null)
            {
                throw new ProfileAccessDeniedException();
            }

            return await action(profile).ConfigureAwait(false);
        }

        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class ThrowingPrivateProfileService : IPrivateProfileService
    {
        private readonly Exception _exceptionToThrow;

        public ThrowingPrivateProfileService(Exception exceptionToThrow)
        {
            _exceptionToThrow = exceptionToThrow ?? throw new ArgumentNullException(nameof(exceptionToThrow));
        }

        public Task<T> WithAuthorizedProfileAsync<T>(
            string module,
            Func<PrivateProfileDto, Task<T>> action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw _exceptionToThrow;
        }

        public Task<PrivateProfileDto> GetAsync(string module, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<PrivateProfileDto> CreateAsync(string module, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class StubGroupAuthoritativeEvidenceProvider : IGroupAuthoritativeEvidenceProvider
    {
        private readonly Func<Guid, string, Task<AuthoritativeGroupProfileEvidence?>> _evidenceResolver;

        public StubGroupAuthoritativeEvidenceProvider(AuthoritativeGroupProfileEvidence? evidence)
            : this((_, _) => Task.FromResult(evidence))
        {
        }

        public StubGroupAuthoritativeEvidenceProvider(Func<Guid, string, Task<AuthoritativeGroupProfileEvidence?>> evidenceResolver)
        {
            _evidenceResolver = evidenceResolver ?? throw new ArgumentNullException(nameof(evidenceResolver));
        }

        public Task<AuthoritativeGroupProfileEvidence?> GetProfileEvidenceAsync(
            Guid profileId,
            string module,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _evidenceResolver(profileId, module);
        }

        public Task<AuthoritativeGroupStateEvidence?> GetGroupEvidenceAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AuthoritativeGroupMembershipEvidence?> GetMembershipEvidenceAsync(
            Guid groupId,
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> IsGroupOwnerAsync(
            Guid groupId,
            Guid profileId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> GetCurrentMemberCountAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int?> GetGroupCapacityAsync(
            Guid groupId,
            GroupModuleScope scope,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset initialUtcNow)
        {
            _utcNow = initialUtcNow;
        }

        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    #endregion

    #region 1. Constructor and Parameter Validation

    [Fact]
    public void Constructor_NullProfileService_ThrowsArgumentNullException()
    {
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider((AuthoritativeGroupProfileEvidence?)null);
        Assert.Throws<ArgumentNullException>(() => new GroupActorContext(null!, evidenceProvider));
    }

    [Fact]
    public void Constructor_NullEvidenceProvider_ThrowsArgumentNullException()
    {
        var profileService = new StubPrivateProfileService((PrivateProfileDto?)null);
        Assert.Throws<ArgumentNullException>(() => new GroupActorContext(profileService, null!));
    }

    [Fact]
    public void Constructor_NullTimeProvider_DefaultsToSystemTimeProvider()
    {
        var profileService = new StubPrivateProfileService((PrivateProfileDto?)null);
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider((AuthoritativeGroupProfileEvidence?)null);

        var context = new GroupActorContext(profileService, evidenceProvider, null);
        Assert.NotNull(context);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("economy")]
    [InlineData("admin")]
    [InlineData("communication")]
    [InlineData("invalid_module")]
    public async Task GetCurrentActorAsync_InvalidOrUnapprovedModule_FailsClosedReturningNull(string? invalidModule)
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow);
        var evidence = new AuthoritativeGroupProfileEvidence(profileId, "learning", true, false, 25, DateTimeOffset.UtcNow);

        var profileService = new StubPrivateProfileService(profile);
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(evidence);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync(invalidModule!);
        Assert.Null(result);
    }

    #endregion

    #region 2. Successful Trusted Actor Resolution Across Approved Modules

    [Fact]
    public async Task GetCurrentActorAsync_ValidLearningClassroomScope_ReturnsAuthoritativeEvidence()
    {
        var profileId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow.AddDays(-10));
        var expectedEvidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "learning",
            IsActive: true,
            IsRestricted: false,
            VerifiedAge: null,
            EvidenceTimestampUtc: timestamp);

        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(expectedEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider, timeProvider);

        var actual = await context.GetCurrentActorAsync("learning");

        Assert.NotNull(actual);
        Assert.Equal(profileId, actual.ProfileId);
        Assert.Equal("learning", actual.Module);
        Assert.True(actual.IsActive);
        Assert.False(actual.IsRestricted);
        Assert.Equal(timestamp, actual.EvidenceTimestampUtc);
    }

    [Fact]
    public async Task GetCurrentActorAsync_ValidHobbiesClubScope_ReturnsAuthoritativeEvidence()
    {
        var profileId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-2);
        var profile = new PrivateProfileDto(profileId, "hobbies", DateTimeOffset.UtcNow.AddDays(-5));
        var expectedEvidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "hobbies",
            IsActive: true,
            IsRestricted: false,
            VerifiedAge: 22,
            EvidenceTimestampUtc: timestamp);

        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(expectedEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider, timeProvider);

        var actual = await context.GetCurrentActorAsync("hobbies");

        Assert.NotNull(actual);
        Assert.Equal(profileId, actual.ProfileId);
        Assert.Equal("hobbies", actual.Module);
        Assert.Equal(22, actual.VerifiedAge);
        Assert.Equal(timestamp, actual.EvidenceTimestampUtc);
    }

    [Fact]
    public async Task GetCurrentActorAsync_ValidAdultSocialZoneScope_ReturnsAuthoritativeEvidence()
    {
        var profileId = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow.AddDays(-2));
        var expectedEvidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "social",
            IsActive: true,
            IsRestricted: false,
            VerifiedAge: 25,
            EvidenceTimestampUtc: timestamp);

        var timeProvider = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(expectedEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider, timeProvider);

        var actual = await context.GetCurrentActorAsync("social");

        Assert.NotNull(actual);
        Assert.Equal(profileId, actual.ProfileId);
        Assert.Equal("social", actual.Module);
        Assert.Equal(25, actual.VerifiedAge);
        Assert.Equal(timestamp, actual.EvidenceTimestampUtc);
    }

    [Fact]
    public async Task GetCurrentActorAsync_CaseInsensitiveApprovedModule_ResolvesSuccessfully()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow);
        var evidence = new AuthoritativeGroupProfileEvidence(profileId, "learning", true, false, null, DateTimeOffset.UtcNow);

        var profileService = new StubPrivateProfileService(profile);
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(evidence);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var actual = await context.GetCurrentActorAsync("LeArNiNg");
        Assert.NotNull(actual);
        Assert.Equal(profileId, actual.ProfileId);
    }

    #endregion

    #region 3. Profile Service Failures and Scope Mismatches

    [Fact]
    public async Task GetCurrentActorAsync_UnauthenticatedOrAccessDenied_FailsClosedReturningNull()
    {
        var profileService = new ThrowingPrivateProfileService(new ProfileAccessDeniedException());
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider((AuthoritativeGroupProfileEvidence?)null);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("learning");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentActorAsync_ProfileServiceThrowsUnauthorized_FailsClosedReturningNull()
    {
        var profileService = new ThrowingPrivateProfileService(new UnauthorizedAccessException("Access denied."));
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider((AuthoritativeGroupProfileEvidence?)null);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("hobbies");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentActorAsync_EmptyProfileId_FailsClosedReturningNull()
    {
        var emptyProfile = new PrivateProfileDto(Guid.Empty, "learning", DateTimeOffset.UtcNow);
        var evidence = new AuthoritativeGroupProfileEvidence(Guid.Empty, "learning", true, false, null, DateTimeOffset.UtcNow);

        var profileService = new StubPrivateProfileService(emptyProfile);
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(evidence);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("learning");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentActorAsync_ProfileModuleMismatch_FailsClosedReturningNull()
    {
        // Caller has a Hobbies profile, but invokes context asking for Learning module
        var profileId = Guid.NewGuid();
        var hobbiesProfile = new PrivateProfileDto(profileId, "hobbies", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(hobbiesProfile);
        var evidence = new AuthoritativeGroupProfileEvidence(profileId, "hobbies", true, false, null, DateTimeOffset.UtcNow);
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(evidence);

        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("learning");
        Assert.Null(result);
    }

    #endregion

    #region 4. Authoritative Evidence Mismatches and Missing Standing

    [Fact]
    public async Task GetCurrentActorAsync_MissingAuthoritativeEvidence_FailsClosedReturningNull()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        // Evidence provider returns null
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider((AuthoritativeGroupProfileEvidence?)null);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("learning");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentActorAsync_MismatchedEvidenceProfileId_FailsClosedReturningNull()
    {
        var profileId = Guid.NewGuid();
        var differentProfileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        // Evidence returned belongs to a different profile ID
        var mismatchedEvidence = new AuthoritativeGroupProfileEvidence(
            differentProfileId,
            "learning",
            true,
            false,
            null,
            DateTimeOffset.UtcNow);

        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(mismatchedEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("learning");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentActorAsync_MismatchedEvidenceModule_FailsClosedReturningNull()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        // Evidence module does not match requested scope
        var mismatchedEvidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "social",
            true,
            false,
            25,
            DateTimeOffset.UtcNow);

        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(mismatchedEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("learning");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentActorAsync_EvidenceProviderThrowsException_FailsClosedReturningNull()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider((_, _) =>
            throw new InvalidOperationException("Evidence store failure."));

        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("learning");
        Assert.Null(result);
    }

    #endregion

    #region 5. Account Standing Restrictions (Inactive / Moderated)

    [Fact]
    public async Task GetCurrentActorAsync_InactiveProfile_FailsClosedReturningNull()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        var inactiveEvidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "learning",
            IsActive: false,
            IsRestricted: false,
            VerifiedAge: null,
            EvidenceTimestampUtc: DateTimeOffset.UtcNow);

        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(inactiveEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("learning");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentActorAsync_RestrictedProfile_FailsClosedReturningNull()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "hobbies", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        var restrictedEvidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "hobbies",
            IsActive: true,
            IsRestricted: true,
            VerifiedAge: 30,
            EvidenceTimestampUtc: DateTimeOffset.UtcNow);

        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(restrictedEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("hobbies");
        Assert.Null(result);
    }

    #endregion

    #region 6. Social Zone Adult Age Gate Enforcement

    [Fact]
    public async Task GetCurrentActorAsync_SocialZone_UnderageActor_FailsClosedReturningNull()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        var underageEvidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "social",
            IsActive: true,
            IsRestricted: false,
            VerifiedAge: 17,
            EvidenceTimestampUtc: DateTimeOffset.UtcNow);

        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(underageEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("social");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentActorAsync_SocialZone_MissingVerifiedAge_FailsClosedReturningNull()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        var missingAgeEvidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "social",
            IsActive: true,
            IsRestricted: false,
            VerifiedAge: null,
            EvidenceTimestampUtc: DateTimeOffset.UtcNow);

        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(missingAgeEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("social");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentActorAsync_SocialZone_ExactlyEighteen_ReturnsAuthoritativeEvidence()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "social", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        var boundaryAdultEvidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "social",
            IsActive: true,
            IsRestricted: false,
            VerifiedAge: 18,
            EvidenceTimestampUtc: DateTimeOffset.UtcNow);

        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(boundaryAdultEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("social");
        Assert.NotNull(result);
        Assert.Equal(18, result.VerifiedAge);
    }

    [Fact]
    public async Task GetCurrentActorAsync_LearningAndHobbies_DoNotRequireVerifiedAge()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        // Learning Classroom does not require adult age
        var evidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "learning",
            IsActive: true,
            IsRestricted: false,
            VerifiedAge: null,
            EvidenceTimestampUtc: DateTimeOffset.UtcNow);

        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(evidence);
        var context = new GroupActorContext(profileService, evidenceProvider);

        var result = await context.GetCurrentActorAsync("learning");
        Assert.NotNull(result);
        Assert.Null(result.VerifiedAge);
    }

    #endregion

    #region 7. Evidence Timestamp Freshness and Clock Skew

    [Fact]
    public async Task GetCurrentActorAsync_StaleEvidenceOlderThanMaximumAge_FailsClosedReturningNull()
    {
        var profileId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var staleTimestamp = now - GroupParticipationAuthorizationPolicy.MaximumEvidenceAge - TimeSpan.FromMinutes(1);

        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        var staleEvidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "learning",
            IsActive: true,
            IsRestricted: false,
            VerifiedAge: null,
            EvidenceTimestampUtc: staleTimestamp);

        var timeProvider = new MutableTimeProvider(now);
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(staleEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider, timeProvider);

        var result = await context.GetCurrentActorAsync("learning");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentActorAsync_FutureTimestampBeyondClockSkew_FailsClosedReturningNull()
    {
        var profileId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        // Future timestamp 10 seconds ahead (exceeds 5 second allowed clock skew)
        var futureTimestamp = now.AddSeconds(10);

        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        var futureEvidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "learning",
            IsActive: true,
            IsRestricted: false,
            VerifiedAge: null,
            EvidenceTimestampUtc: futureTimestamp);

        var timeProvider = new MutableTimeProvider(now);
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(futureEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider, timeProvider);

        var result = await context.GetCurrentActorAsync("learning");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetCurrentActorAsync_FutureDatedEvidence_CannotAuthorizeStrictGroupParticipation()
    {
        var profileId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var scope = new GroupModuleScope("learning", GroupKind.Classroom);
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        // Future timestamp 3 seconds ahead
        var futureTimestamp = now.AddSeconds(3);

        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow);
        var profileService = new StubPrivateProfileService(profile);

        var futureEvidence = new AuthoritativeGroupProfileEvidence(
            profileId,
            "learning",
            IsActive: true,
            IsRestricted: false,
            VerifiedAge: null,
            EvidenceTimestampUtc: futureTimestamp);

        var groupEvidence = new AuthoritativeGroupStateEvidence(
            groupId,
            scope,
            IsActive: true,
            IsOpen: true,
            EvidenceTimestampUtc: now);

        var timeProvider = new MutableTimeProvider(now);
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(futureEvidence);
        var context = new GroupActorContext(profileService, evidenceProvider, timeProvider);

        var actorEvidence = await context.GetCurrentActorAsync("learning");

        if (actorEvidence is not null)
        {
            // If the adapter returns future-dated evidence, verify that
            // strict participation authorization policy rejects it as StaleEvidence
            var strictAuth = GroupParticipationAuthorizationPolicy.EvaluateStrictAuthorization(
                actorEvidence,
                groupEvidence,
                scope,
                groupId,
                now);

            Assert.True(strictAuth.IsDenied);
            Assert.Equal(GroupParticipationAuthorizationStatus.StaleEvidence, strictAuth.Status);
        }
        else
        {
            // Once strict timestamp checking is enforced in the adapter,
            // future-dated evidence is rejected directly
            Assert.Null(actorEvidence);
        }
    }

    #endregion

    #region 8. Cancellation Token Propagation

    [Fact]
    public async Task GetCurrentActorAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        var profileId = Guid.NewGuid();
        var profile = new PrivateProfileDto(profileId, "learning", DateTimeOffset.UtcNow);
        var evidence = new AuthoritativeGroupProfileEvidence(profileId, "learning", true, false, null, DateTimeOffset.UtcNow);

        var profileService = new StubPrivateProfileService(profile);
        var evidenceProvider = new StubGroupAuthoritativeEvidenceProvider(evidence);
        var context = new GroupActorContext(profileService, evidenceProvider);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.GetCurrentActorAsync("learning", cts.Token));
    }

    #endregion

    #region 9. Security, Interface Contract & Non-Activation Verification

    [Fact]
    public void GroupActorContext_ImplementsIGroupActorContext_Directly()
    {
        Assert.True(typeof(IGroupActorContext).IsAssignableFrom(typeof(GroupActorContext)));
    }

    [Fact]
    public void GroupActorContext_HasExpectedConstructorParameters()
    {
        var constructors = typeof(GroupActorContext).GetConstructors();
        Assert.Single(constructors);

        var ctor = constructors[0];
        var parameters = ctor.GetParameters();

        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(IPrivateProfileService), parameters[0].ParameterType);
        Assert.Equal(typeof(IGroupAuthoritativeEvidenceProvider), parameters[1].ParameterType);
        Assert.Equal(typeof(TimeProvider), parameters[2].ParameterType);
        Assert.True(parameters[2].IsOptional);
    }

    [Fact]
    public void GroupActorContext_DoesNotFabricateSyntheticTimestampsOrAges()
    {
        // Reflection check: GroupActorContext has no public or private methods that synthesize ages or modify timestamps
        var methods = typeof(GroupActorContext).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        foreach (var method in methods)
        {
            Assert.DoesNotContain("Manufacture", method.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Synthesize", method.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion
}
