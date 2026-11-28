using Andril.Common.Accounts;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Common.DTOs;
using Andril.Common.Entities;
using Andril.Shared.Authentication;
using Xunit;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.UnitTests.Common;

public sealed class ProfileSetupSubmissionTests
{
    [Fact]
    public async Task InvalidLocationIsRejectedBeforeAnyProfileWrite()
    {
        var fixture = new TestFixture("learning", false);
        await Assert.ThrowsAsync<ProfileValidationException>(() => fixture.Service.SubmitProfileAsync("learning", new("Learner", null, "UnapprovedCity", null)));
        Assert.Equal(0, fixture.Profiles.SetupCalls);
    }

    [Fact]
    public async Task OmittedLocationReturnsPreviouslyPersistedLocation()
    {
        var fixture = new TestFixture("learning", false);
        await fixture.Service.SubmitProfileAsync("learning", new("Learner", null, "Colombo", "Western"));
        var response = await fixture.Service.SubmitProfileAsync("learning", new("Learner", "updated", null, null));
        Assert.Equal("Colombo", response.LocationCity);
        Assert.Equal("Western", response.LocationRegion);
        Assert.False(response.Handoff.IsPreferencesComplete);
    }

    [Theory]
    [InlineData("policies")]
    [InlineData("age")]
    [InlineData("membership")]
    [InlineData("social_opt_in")]
    public async Task SubmissionRechecksPersistedPrerequisitesInsteadOfTrustingStarterSummary(string missing)
    {
        var fixture = new TestFixture(missing == "social_opt_in" ? "social" : "learning", true);
        fixture.Profiles.Missing = missing;
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => fixture.Service.SubmitProfileAsync(
            missing == "social_opt_in" ? "social" : "learning", new("Learner", null, null, null)));
    }

    [Fact]
    public async Task SetupStatusPropagatesDependencyFailureInsteadOfClaimingNoProfile()
    {
        var fixture = new TestFixture("learning", false);
        fixture.Profiles.FailRead = true;
        await Assert.ThrowsAsync<AccountStoreUnavailableException>(() => fixture.Service.GetSetupStatusAsync("learning"));
    }

    [Fact]
    public async Task PS_T07_LearningProfileCreation_SucceedsWithoutMainPhoto()
    {
        var fixture = new TestFixture(ModuleNames.Learning, hasMainPhoto: false);

        var request = new ProfileSetupSubmissionRequest("AlexLearner", "Learning C#", "Colombo", "Western");
        var res = await fixture.Service.SubmitProfileAsync(ModuleNames.Learning, request);

        Assert.Equal("AlexLearner", res.DisplayName);
        Assert.False(res.HasMainPhoto);
        Assert.Equal("completed", res.SetupStage);
        Assert.Equal("/learning/preferences", res.NextRoute);
        Assert.True(res.Handoff.IsProfileComplete);
        Assert.False(res.Handoff.IsPreferencesComplete);
        Assert.Equal("M1 - Thusa", res.Handoff.OwnerMember);
    }

    [Fact]
    public async Task PS_T17_AboutMe_Over100Chars_Rejected()
    {
        var fixture = new TestFixture(ModuleNames.Learning, hasMainPhoto: false);

        var longBio = new string('A', 101); // Exceeds 100 character counter
        var request = new ProfileSetupSubmissionRequest("AlexLearner", longBio, "Colombo", "Western");

        await Assert.ThrowsAsync<ProfileBioTooLongException>(() =>
            fixture.Service.SubmitProfileAsync(ModuleNames.Learning, request));
    }

    [Fact]
    public async Task PS_T24_SocialProfile_WithoutRequiredMainPhoto_Rejected()
    {
        var fixture = new TestFixture(ModuleNames.Social, hasMainPhoto: false);

        var request = new ProfileSetupSubmissionRequest("AlexSocial", "Hello world", "Colombo", "Western");

        await Assert.ThrowsAsync<SocialProfileRequiresMainPhotoException>(() =>
            fixture.Service.SubmitProfileAsync(ModuleNames.Social, request));
    }

    [Fact]
    public async Task PS_T24_SocialProfile_WithRequiredMainPhoto_Succeeds()
    {
        var fixture = new TestFixture(ModuleNames.Social, hasMainPhoto: true);

        var request = new ProfileSetupSubmissionRequest("AlexSocial", "Hello world", "Colombo", "Western");
        var res = await fixture.Service.SubmitProfileAsync(ModuleNames.Social, request);

        Assert.Equal("AlexSocial", res.DisplayName);
        Assert.True(res.HasMainPhoto);
        Assert.Equal("completed", res.SetupStage);
        Assert.Equal("/social/preferences", res.NextRoute);
        Assert.Equal("M5 - Thulaanchan", res.Handoff.OwnerMember);
    }

    private sealed class TestFixture
    {
        public ProfileSetupService Service { get; }
        public Account Account { get; }
        public FakeProfileRepo Profiles { get; }

        public TestFixture(string module, bool hasMainPhoto)
        {
            var userId = Guid.NewGuid();
            Account = Account.Create(userId, DateTimeOffset.UtcNow);
            var user = new FakeUser(userId, true);
            var identity = new FakeIdentity(true);
            var accounts = new FakeAccountRepo(Account);
            var starter = new FakeStarterService(module);
            var profiles = Profiles = new FakeProfileRepo(Account.Id, module);
            var photoStore = new FakePhotoStore(hasMainPhoto);
            var addPhotos = new FakeAdditionalPhotosRepo();
            var locations = new FakeLocationRepo();

            Service = new ProfileSetupService(
                user, identity, accounts, starter, profiles, photoStore, addPhotos, locations, TimeProvider.System, new Catalog());
        }
    }

    private sealed record FakeUser(Guid? UserId, bool IsAuthenticated) : ICurrentUser;

    private sealed class FakeIdentity(bool confirmed) : IOnboardingIdentity
    {
        public bool IsConfirmedFor(Guid subject) => confirmed;
    }

    private sealed class FakeAccountRepo(Account account) : IAccountRepository
    {
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default) =>
            Task.FromResult<Account?>(subject == account.SupabaseUserId ? account : null);

        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult(account);
    }

    private sealed class FakeStarterService(string activeModule) : IStarterModuleService
    {
        public Task<StarterModuleStatus> GetAsync(CancellationToken cancellationToken = default)
        {
            var eligibilities = new List<StarterModuleEligibility>
            {
                new(ModuleNames.Learning, true, false, true),
                new(ModuleNames.Hobbies, true, false, true),
                new(ModuleNames.Social, true, true, true)
            };
            var membership = new StarterMembershipDto(activeModule, DateTimeOffset.UtcNow, activeModule == ModuleNames.Social);
            return Task.FromResult(new StarterModuleStatus(true, true, eligibilities, membership, "profile_setup"));
        }

        public Task<StarterModuleStatus> SelectAsync(string? module, bool? socialOptIn, CancellationToken cancellationToken = default) =>
            GetAsync(cancellationToken);
    }

    private sealed class Catalog : IPolicyCatalogProvider
    {
        public PolicyCatalog GetCurrent() => new(new("v1", "https://example.test/terms", "en"),
            new("v1", "https://example.test/privacy", "en"), new("v1", "https://example.test/community", "en"));
    }

    private sealed class FakeProfileRepo : IPrivateProfileRepository
    {
        public async Task<T> WithProfileSetupAsync<T>(Guid accountId, string module, ProfileEdit edit,
            Action<StarterModuleState> validate, TimeProvider clock, Func<PrivateModuleProfile, Task<T>> action,
            CancellationToken cancellationToken)
        {
            SetupCalls++;
            validate(new(AccountDemographics.Create(accountId, Missing == "age" ? new(2020, 1, 1) : new(2000, 1, 1), "other", clock.GetUtcNow()),
                Missing == "policies" ? [] : [AccountPolicyRecord.Create(accountId, PolicyActions.TermsAgreed, "v1", clock.GetUtcNow()),
                 AccountPolicyRecord.Create(accountId, PolicyActions.PrivacyAcknowledged, "v1", clock.GetUtcNow())],
                Missing == "membership" ? null : StarterMembership.Create(accountId, Missing == "social_opt_in" ? "learning" : module, module == "social" && Missing != "social_opt_in", clock.GetUtcNow())));
            _profile.SetDraftFields(edit);
            return await action(_profile);
        }

        public int SetupCalls;
        public string? Missing;
        public bool FailRead;
        private readonly PrivateModuleProfile _profile;

        public FakeProfileRepo(Guid accountId, string module)
        {
            _profile = PrivateModuleProfile.Create(accountId, module, DateTimeOffset.UtcNow);
        }

        public Task<PrivateModuleProfile?> ReadAsync(Guid accountId, string module, Action<StarterModuleState> validate, CancellationToken cancellationToken = default) =>
            FailRead ? Task.FromException<PrivateModuleProfile?>(new AccountStoreUnavailableException()) : Task.FromResult<PrivateModuleProfile?>(_profile);

        public Task<PrivateModuleProfile> GetOrCreateAsync(Guid accountId, string module, Action<StarterModuleState> validate, TimeProvider clock, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profile);

        public Task<PrivateModuleProfile> UpdateAsync(Guid accountId, string module, ProfileEdit edit, Action<StarterModuleState> validate, CancellationToken cancellationToken = default)
        {
            _profile.SetDraftFields(edit);
            return Task.FromResult(_profile);
        }
    }

    private sealed class FakePhotoStore(bool hasCurrentPhoto) : IPrivateProfilePhotoStore
    {
        public Task<StoredProfilePhoto?> ReadCurrentAsync(PhotoScope scope, CancellationToken ct = default) =>
            Task.FromResult(hasCurrentPhoto
                ? new StoredProfilePhoto(new PhotoObjectAddress(scope, Guid.NewGuid()), PhotoState.Stored, true, new string('0', 64), "image/jpeg", 1024, 500, 500)
                : null);

        public Task<PrivateProfilePhoto?> FindRequestAsync(PhotoScope scope, Guid requestId, CancellationToken ct) => Task.FromResult<PrivateProfilePhoto?>(null);
        public Task<PrivateProfilePhoto> ReserveAsync(PhotoScope scope, Guid requestId, string originalHash, PhotoUploadEvidence? evidence, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> OwnsLeaseAsync(PhotoScope scope, Guid id, Guid token, CancellationToken ct) => Task.FromResult(true);
        public Task<PrivatePhotoStatus> FinalizeAsync(PhotoScope scope, Guid id, Guid token, CancellationToken ct) => throw new NotImplementedException();
        public Task<PrivatePhotoStatus> FailAsync(PhotoScope scope, Guid id, Guid token, PhotoReason reason, CancellationToken ct) => throw new NotImplementedException();
        public Task<PrivatePhotoStatus?> ReadAsync(PhotoScope scope, Guid id, CancellationToken ct) => Task.FromResult<PrivatePhotoStatus?>(null);
        public Task<PhotoCleanupClaim?> ClaimCleanupAsync(PhotoScope scope, CancellationToken ct) => Task.FromResult<PhotoCleanupClaim?>(null);
        public Task<bool> RecordCleanupAsync(PhotoCleanupClaim claim, bool removed, TimeSpan retryDelay, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class FakeAdditionalPhotosRepo : IAdditionalProfilePhotoRepository
    {
        private readonly List<AdditionalProfilePhotoRecord> _photos = [];
        public Task<IReadOnlyList<AdditionalProfilePhotoRecord>> GetByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AdditionalProfilePhotoRecord>>(_photos);

        public Task<AdditionalProfilePhotoRecord?> GetByProfileAndSlotAsync(Guid profileId, int slotIndex, CancellationToken cancellationToken = default) =>
            Task.FromResult(_photos.FirstOrDefault(p => p.ProfileId == profileId && p.SlotIndex == slotIndex));

        public Task<AdditionalProfilePhotoRecord> UpsertSlotPhotoAsync(AdditionalProfilePhotoRecord record, CancellationToken cancellationToken = default)
        {
            _photos.RemoveAll(p => p.ProfileId == record.ProfileId && p.SlotIndex == record.SlotIndex);
            _photos.Add(record);
            return Task.FromResult(record);
        }

        public Task<bool> DeleteSlotPhotoAsync(Guid profileId, int slotIndex, CancellationToken cancellationToken = default) =>
            Task.FromResult(_photos.RemoveAll(p => p.ProfileId == profileId && p.SlotIndex == slotIndex) > 0);
    }

    private sealed class FakeLocationRepo : IProfileLocationRepository
    {
        private readonly Dictionary<Guid, ProfileLocationRecord> _locations = [];

        public Task<ProfileLocationRecord?> GetByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_locations.GetValueOrDefault(profileId));

        public Task<ProfileLocationRecord?> GetByAccountAndModuleAsync(Guid accountId, string module, CancellationToken cancellationToken = default) =>
            Task.FromResult(_locations.Values.FirstOrDefault(l => l.AccountId == accountId && l.Module == module));

        public Task<ProfileLocationRecord> UpsertAsync(ProfileLocationRecord record, CancellationToken cancellationToken = default)
        {
            _locations[record.ProfileId] = record;
            return Task.FromResult(record);
        }

        public Task<bool> DeleteByProfileIdAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_locations.Remove(profileId));
    }
}
