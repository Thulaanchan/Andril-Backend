using Andril.AI.ChatBox;
using Andril.AI.Interfaces;
using Andril.API.Integrations.Learning;
using Andril.Learning.Activities;
using Xunit;

namespace Andril.SecurityTests.Learning;

public sealed class CurrentLearningOwnerActivityAuthorityTests
{
    private sealed class Reader(Guid account, Guid profile) : IAiModuleProfileAccessReader
    {
        internal Guid? Profile = profile;
        internal int Calls;
        internal bool Unavailable;
        internal AiModule? LastModule;
        public Task<Guid?> FindAuthorizedProfileIdAsync(Guid accountId, AiModule module, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; LastModule = module;
            if (Unavailable) throw new InvalidOperationException("PRIVATE_OWNER_STORAGE_FAILURE");
            return Task.FromResult(accountId == account && module == AiModule.Learning ? Profile : null);
        }
    }

    [Theory]
    [InlineData(LearningActivityKind.GrowthSelfReport, LearningActivityAction.Create)]
    [InlineData(LearningActivityKind.GrowthSelfReport, LearningActivityAction.Read)]
    [InlineData(LearningActivityKind.MentorshipOffer, LearningActivityAction.Create)]
    [InlineData(LearningActivityKind.MentorshipOffer, LearningActivityAction.Read)]
    [InlineData(LearningActivityKind.GrowthSelfReport, LearningActivityAction.List)]
    [InlineData(LearningActivityKind.MentorshipOffer, LearningActivityAction.List)]
    public async Task SupportedPrivateOwnerOperationsUseFreshVerifiedLearningOwnership(LearningActivityKind kind, LearningActivityAction action)
    {
        var actor = new LearningActivityMember(Guid.NewGuid(), Guid.NewGuid()); var reader = new Reader(actor.AccountId, actor.ProfileId);
        var authority = new CurrentLearningOwnerActivityAuthority(reader);
        var result = await authority.AuthorizeAsync(actor, kind, action, Guid.NewGuid(), null);
        Assert.Equal(LearningActivityPermissionOutcome.Allowed, result.Outcome); Assert.Equal(actor, Assert.Single(result.Participants));
        Assert.Equal(AiModule.Learning, reader.LastModule); Assert.Equal(1, reader.Calls);
    }

    [Theory]
    [InlineData(LearningActivityKind.SkillExchange)]
    [InlineData(LearningActivityKind.Pact)]
    [InlineData(LearningActivityKind.MentorshipRequest)]
    [InlineData(LearningActivityKind.Circle)]
    public async Task OwnerProofDoesNotFabricateCounterpartyOfferVisibilityOrCirclePermission(LearningActivityKind kind)
    {
        var actor = new LearningActivityMember(Guid.NewGuid(), Guid.NewGuid()); var reader = new Reader(actor.AccountId, actor.ProfileId);
        var result = await new CurrentLearningOwnerActivityAuthority(reader).AuthorizeAsync(actor, kind, LearningActivityAction.Create, null, new());
        Assert.Equal(LearningActivityPermissionOutcome.Unavailable, result.Outcome); Assert.Empty(result.Participants); Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task ForeignAndInactiveProfilesAreDeniedWithoutPositiveCaching()
    {
        var actor = new LearningActivityMember(Guid.NewGuid(), Guid.NewGuid()); var reader = new Reader(actor.AccountId, actor.ProfileId);
        var authority = new CurrentLearningOwnerActivityAuthority(reader);
        Assert.Equal(LearningActivityPermissionOutcome.Allowed, (await authority.AuthorizeAsync(actor, LearningActivityKind.GrowthSelfReport, LearningActivityAction.Read, Guid.NewGuid(), null)).Outcome);
        reader.Profile = null;
        Assert.Equal(LearningActivityPermissionOutcome.Denied, (await authority.AuthorizeAsync(actor, LearningActivityKind.GrowthSelfReport, LearningActivityAction.Read, Guid.NewGuid(), null)).Outcome);
        reader.Profile = actor.ProfileId;
        Assert.Equal(LearningActivityPermissionOutcome.Denied, (await authority.AuthorizeAsync(actor with { AccountId = Guid.NewGuid() }, LearningActivityKind.GrowthSelfReport, LearningActivityAction.Read, Guid.NewGuid(), null)).Outcome);
        Assert.Equal(LearningActivityPermissionOutcome.Denied, (await authority.AuthorizeAsync(actor with { ProfileId = Guid.NewGuid() }, LearningActivityKind.GrowthSelfReport, LearningActivityAction.Read, Guid.NewGuid(), null)).Outcome);
        Assert.Equal(4, reader.Calls);
    }

    [Fact]
    public async Task TargetFieldsAndUndefinedMutationsCannotExpandPrivateOwnerAuthority()
    {
        var actor = new LearningActivityMember(Guid.NewGuid(), Guid.NewGuid()); var reader = new Reader(actor.AccountId, actor.ProfileId);
        var authority = new CurrentLearningOwnerActivityAuthority(reader);
        Assert.Equal(LearningActivityPermissionOutcome.Denied, (await authority.AuthorizeAsync(actor, LearningActivityKind.GrowthSelfReport, LearningActivityAction.Create, null, new([Guid.NewGuid()]))).Outcome);
        Assert.Equal(LearningActivityPermissionOutcome.Unavailable, (await authority.AuthorizeAsync(actor, LearningActivityKind.MentorshipOffer, LearningActivityAction.Cancel, Guid.NewGuid(), null)).Outcome);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task UnavailableStorageAndCancellationFailClosedWithoutPrivateExceptionPayload()
    {
        var actor = new LearningActivityMember(Guid.NewGuid(), Guid.NewGuid()); var reader = new Reader(actor.AccountId, actor.ProfileId) { Unavailable = true };
        var authority = new CurrentLearningOwnerActivityAuthority(reader);
        var result = await authority.AuthorizeAsync(actor, LearningActivityKind.GrowthSelfReport, LearningActivityAction.Read, Guid.NewGuid(), null);
        Assert.Equal(LearningActivityPermissionOutcome.Unavailable, result.Outcome); Assert.Empty(result.Participants);
        Assert.DoesNotContain("PRIVATE_OWNER_STORAGE_FAILURE", result.ToString());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => authority.AuthorizeAsync(actor, LearningActivityKind.GrowthSelfReport, LearningActivityAction.Read, Guid.NewGuid(), null, cancel.Token));
    }
}
