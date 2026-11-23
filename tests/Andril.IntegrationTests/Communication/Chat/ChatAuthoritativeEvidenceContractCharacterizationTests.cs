using System.Security.Claims;
using Andril.API.Authentication;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Eligibility;
using Andril.Common.Accounts.Onboarding;
using Andril.Common.Accounts.Policies;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.StarterModules;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.Groups.Entities;
using Andril.Groups.Interfaces;
using Andril.Groups.Memberships;
using Andril.Groups.Policies;
using Andril.Groups.Services;
using Andril.Social.Interfaces;
using Andril.Social.Matching;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Andril.IntegrationTests.Communication.Chat;

/// <summary>
/// AND-M4-CHAT-P09B-CLEARANCE-20261123: test-only characterization of real partial
/// owner services and the existing Chat policy. All identities and repositories here
/// are synthetic. HttpCurrentUser receives an already-authenticated test principal;
/// this does not verify JWT signatures or contact Supabase. No positive production
/// Chat handoff is claimed. Repository callbacks simulate prerequisite revalidation,
/// not PostgreSQL locks, cross-context atomicity, RLS or distributed revocation.
/// The existing reflection fixture is test-only; production visibility is unchanged.
/// </summary>
public sealed class ChatAuthoritativeEvidenceContractCharacterizationTests
{
    private static readonly Guid ConversationId = Guid.Parse("10000000-0000-4000-8000-000000000091");
    private static readonly Guid OtherId = Guid.Parse("90000000-0000-4000-8000-000000000091");

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("anonymous")]
    [InlineData("role")]
    [InlineData("unauthenticated")]
    public async Task RealHttpIdentityRejectsInvalidSubjectsBeforeOwnedProfileCallback(string fault)
    {
        var owner = new SyntheticM2Boundary("social");
        var claims = owner.Context.User.Claims.ToList();
        if (fault is "missing" or "empty" or "malformed")
        {
            claims.RemoveAll(x => x.Type == "sub");
            if (fault != "missing") claims.Add(new("sub", fault == "empty" ? Guid.Empty.ToString() : "invalid-subject"));
        }
        if (fault == "duplicate") claims.Add(new("sub", OtherId.ToString()));
        if (fault is "anonymous" or "role")
        {
            var type = fault == "anonymous" ? "is_anonymous" : "role";
            claims.RemoveAll(x => x.Type == type);
            claims.Add(new(type, fault == "anonymous" ? "true" : "service_role"));
        }
        owner.Context.User = new(new ClaimsIdentity(claims, fault == "unauthenticated" ? null : "SyntheticValidatedPrincipal"));
        Assert.False(owner.User.IsAuthenticated);
        Assert.Null(owner.User.UserId);
        await Assert.ThrowsAsync<AccountAuthenticationRequiredException>(() => owner.Service.WithAuthorizedProfileAsync<bool>(
            "social", _ => throw new InvalidOperationException("Callback must not run."), CancellationToken.None));
        Assert.Equal(0, owner.RepositoryReads);
    }

    [Theory]
    [InlineData("learning", ChatModule.Learning)]
    [InlineData("hobbies", ChatModule.Hobbies)]
    [InlineData("social", ChatModule.Social)]
    public async Task RealM2DerivesOwnedProfileFromSubjectAndDoesNotGrantChat(string module, ChatModule chatModule)
    {
        var owner = new SyntheticM2Boundary(module);
        owner.Context.Request.Headers["X-Account-Id"] = OtherId.ToString();
        owner.Context.Request.QueryString = new("?profileId=" + OtherId);
        var profile = await owner.Service.WithAuthorizedProfileAsync(module, Task.FromResult<PrivateProfileDto>, CancellationToken.None);
        Assert.Equal(owner.Account.SupabaseUserId, owner.LastSubject);
        Assert.Equal(owner.Account.Id, owner.LastAccount);
        Assert.Equal(module, owner.LastModule);
        Assert.Equal(owner.Profile!.Id, profile.ProfileId);
        Assert.NotEqual(OtherId, profile.ProfileId);
        // This operation permits a draft; no universal setup-completion rule is invented.
        Assert.False(profile.IsComplete);
        using var chat = new ChatSyntheticAuthorizationFixture();
        var policy = chat.CreatePolicy(owner.User, missingReader: true);
        Assert.Null(await policy.AuthorizeConversationAsync(chatModule, ConversationId));
        Assert.Equal(0, chat.Reads);
        Assert.Null(await new DenyAllChatAccessAuthorizer().AuthorizeConversationAsync(chatModule, ConversationId));
    }

    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("owner")]
    [InlineData("module")]
    [InlineData("inactive")]
    [InlineData("membership-owner")]
    [InlineData("membership-module")]
    [InlineData("underage")]
    [InlineData("opt-in")]
    [InlineData("privacy")]
    public async Task RealM2RejectsUnavailableOrConflictingAuthorityBeforeCallback(string fault)
    {
        var owner = new SyntheticM2Boundary("social");
        Type expected = typeof(ProfileAccessDeniedException);
        switch (fault)
        {
            case "unconfirmed": owner.Confirmed = false; expected = typeof(OnboardingDependencyException); break;
            case "account": owner.AccountAvailable = false; expected = typeof(StarterModuleAccountMissingException); break;
            case "profile": owner.Profile = null; expected = typeof(ProfileMissingException); break;
            case "owner": owner.Profile = PrivateModuleProfile.Create(OtherId, "social", owner.GetUtcNow()); break;
            case "module": owner.Profile = PrivateModuleProfile.Create(owner.Account.Id, "learning", owner.GetUtcNow()); break;
            case "inactive": owner.State = owner.State with { Membership = null }; break;
            case "membership-owner": owner.State = owner.State with { Membership = StarterMembership.Create(OtherId, "social", true, owner.GetUtcNow()) }; break;
            case "membership-module": owner.State = owner.State with { Membership = StarterMembership.Create(owner.Account.Id, "hobbies", false, owner.GetUtcNow()) }; break;
            case "underage": owner.State = owner.State with { Demographics = AccountDemographics.Create(owner.Account.Id, new(2013, 1, 1), "other", owner.GetUtcNow()) }; break;
            // Historical repository evidence, not a supported public activation operation.
            case "opt-in": typeof(StarterMembership).GetProperty(nameof(StarterMembership.SocialOptInAtUtc))!.SetValue(owner.State.Membership, null); break;
            case "privacy": owner.State = owner.State with { Policies = owner.State.Policies.Where(x => x.Action != PolicyActions.PrivacyAcknowledged).ToArray() }; break;
        }
        var callbacks = 0;
        var error = await Record.ExceptionAsync(() => owner.Service.WithAuthorizedProfileAsync("social", _ =>
        {
            callbacks++;
            return Task.FromResult(true);
        }, CancellationToken.None));
        Assert.Equal(expected, error?.GetType());
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task RealSocialCompletionAndActivationStillDoNotProvideChatPermission()
    {
        var owner = new SyntheticM2Boundary("social");
        owner.Profile!.SetDraftFields(new("Synthetic Person", null, "private"));
        var completion = new PrivateProfileCompletionReader(owner.Service);
        Assert.True((await completion.ReadAsync("social")).IsComplete);
        await Assert.ThrowsAsync<ProfileContractPendingException>(() => new PrivateModuleAccess(completion).RequireAsync("social"));
        var dto = await owner.Service.WithAuthorizedProfileAsync("social", Task.FromResult<PrivateProfileDto>, CancellationToken.None);
        using var chat = new ChatSyntheticAuthorizationFixture { AccountId = owner.Account.Id };
        chat.Transform = evidence =>
        {
            ChatSyntheticAuthorizationFixture.Set(evidence, "ModuleProfileId", dto.ProfileId);
            ChatSyntheticAuthorizationFixture.Set(evidence, "Conversation", ChatConversation.Create(ChatModule.Social, ConversationId,
                [ChatParticipantReference.Create(ChatModule.Social, owner.Account.Id, dto.ProfileId)]));
            // Actual M2 identity/profile output cannot fill missing communication/match authority.
            ChatSyntheticAuthorizationFixture.Set(evidence, "IsModuleCommunicationEligible", null);
            ChatSyntheticAuthorizationFixture.Set(evidence, "HasMutualMatch", null);
            return evidence;
        };
        Assert.Null(await chat.CreatePolicy(owner.User).AuthorizeConversationAsync(ChatModule.Social, ConversationId));
        Assert.Equal(1, chat.Reads);
    }

    [Fact]
    public async Task RealM2ValidatorRechecksRevokedPrerequisitesAtSyntheticRepositoryBoundary()
    {
        var owner = new SyntheticM2Boundary("social");
        await owner.Service.WithAuthorizedProfileAsync("social", Task.FromResult<PrivateProfileDto>, CancellationToken.None);
        owner.BeforeValidation = () => owner.State = owner.State with { Membership = null };
        var callbacks = 0;
        await Assert.ThrowsAsync<ProfileAccessDeniedException>(() => owner.Service.WithAuthorizedProfileAsync("social", _ =>
        {
            callbacks++;
            return Task.FromResult(true);
        }, CancellationToken.None));
        Assert.Equal(2, owner.RepositoryReads);
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(null, SocialReciprocalInterestStatus.Indeterminate)]
    [InlineData(false, SocialReciprocalInterestStatus.NotMutual)]
    [InlineData(true, SocialReciprocalInterestStatus.MutualInterestObserved)]
    public async Task RealM5InterestObservationDoesNotSupplyCurrentMatchAuthority(bool? secondPositive, SocialReciprocalInterestStatus expected)
    {
        var pair = new SocialProfilePairKey(Guid.Parse("50000000-0000-4000-8000-000000000001"), OtherId);
        var repository = new SyntheticInterestRepository
        {
            First = new(pair, pair.FirstProfileId, true, ChatSyntheticAuthorizationFixture.Now.UtcDateTime),
            Second = secondPositive is bool positive ? new(pair, pair.SecondProfileId, positive, ChatSyntheticAuthorizationFixture.Now.UtcDateTime) : null
        };
        var observation = await new SocialPairInterestObservationService(repository).ObservePairInterestAsync(pair);
        Assert.True(observation.IsValid);
        Assert.Equal(expected, observation.ReciprocalStatus);
        Assert.Equal(expected, SocialReciprocalInterestEvaluator.Evaluate(repository.First, repository.Second));
        Assert.Equal(pair, repository.RequestedPair);
        using var chat = new ChatSyntheticAuthorizationFixture();
        // Do not copy IsMutualMatch (an observation property) into authoritative Chat evidence.
        chat.SetFlag("HasMutualMatch", null);
        Assert.Null(await chat.Policy.AuthorizeConversationAsync(ChatModule.Social, ConversationId));
        Assert.DoesNotContain(pair.FirstProfileId.ToString(), observation.ToString());
    }

    [Fact]
    public async Task RealM5RejectsForeignPairEvidenceRatherThanInferringAMatch()
    {
        var pair = new SocialProfilePairKey(Guid.NewGuid(), Guid.NewGuid());
        var foreign = new SocialProfilePairKey(Guid.NewGuid(), Guid.NewGuid());
        var repository = new SyntheticInterestRepository { First = new(foreign, foreign.FirstProfileId, true, ChatSyntheticAuthorizationFixture.Now.UtcDateTime) };
        var result = await new SocialPairInterestObservationService(repository).ObservePairInterestAsync(pair);
        Assert.Equal(SocialPairInterestObservationStatus.ScopeViolation, result.ObservationStatus);
        Assert.False(result.IsMutualMatch);
        using var chat = new ChatSyntheticAuthorizationFixture();
        chat.RemoveEvidence();
        Assert.Null(await chat.Policy.AuthorizeConversationAsync(ChatModule.Social, ConversationId));
    }

    [Theory]
    [InlineData("underage", SocialDiscoveryIneligibilityReason.CallerUnderage)]
    [InlineData("opt-in", SocialDiscoveryIneligibilityReason.CallerNotOptedIn)]
    [InlineData("caller-block", SocialDiscoveryIneligibilityReason.BlockedByCaller)]
    [InlineData("candidate-block", SocialDiscoveryIneligibilityReason.BlockedByCandidate)]
    [InlineData("unavailable", SocialDiscoveryIneligibilityReason.MissingEvidence)]
    public void RealM5PreFilterEvaluatesSuppliedEvidenceWithoutEstablishingAuthority(string fault, SocialDiscoveryIneligibilityReason reason)
    {
        var caller = new SocialDiscoveryCallerEvidence(Guid.NewGuid(), fault == "underage" ? 17 : 30,
            fault != "opt-in", true, fault != "unavailable");
        var candidate = new SocialDiscoveryCandidateEvidence(OtherId, 30, true, true, true,
            fault == "caller-block", fault == "candidate-block");
        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(caller, candidate);
        Assert.False(result.IsEligible);
        Assert.True(result.Reasons.HasFlag(reason));
        Assert.Equal(fault == "unavailable" ? SocialDiscoveryEligibilityStatus.Indeterminate : SocialDiscoveryEligibilityStatus.Ineligible, result.Status);
    }

    [Fact]
    public async Task CallerDeclaredEligibilityBooleansCannotReplaceMissingChatReader()
    {
        var result = SocialDiscoveryEligibilityPreFilter.Evaluate(
            new(Guid.NewGuid(), 30, true, true), new(OtherId, 30, true, true, true, false, false));
        Assert.True(result.IsEligible); // Pure evaluation of synthetic input, not an authoritative read.
        using var chat = new ChatSyntheticAuthorizationFixture(missingReader: true);
        Assert.Null(await chat.Policy.AuthorizeConversationAsync(ChatModule.Social, ConversationId));
        Assert.Equal(0, chat.Reads);
    }

    [Theory]
    [InlineData("learning", GroupKind.Classroom, ChatModule.Learning)]
    [InlineData("hobbies", GroupKind.Club, ChatModule.Hobbies)]
    [InlineData("social", GroupKind.Zone, ChatModule.Social)]
    public async Task RealM5StructuralMembershipDoesNotSupplyRoleClosureOrChatPermission(string module, GroupKind kind, ChatModule chatModule)
    {
        var scope = new GroupModuleScope(module, kind);
        var repository = new SyntheticMembershipRepository { Membership = new(ConversationId, scope, OtherId) };
        var service = new GroupMembershipScopedLookupService(repository);
        var result = await service.LookupMembershipAsync(ConversationId, scope, OtherId);
        Assert.True(result.HasMembership);
        Assert.True(await service.HasActiveMembershipAsync(ConversationId, scope, OtherId));
        using var chat = new ChatSyntheticAuthorizationFixture();
        // No approved role/closure/operation authority is available. Do not invent group
        // permission semantics or impose Social matching on Learning/Hobbies.
        chat.SetFlag("IsParticipationAuthorized", null);
        chat.SetFlag("IsModuleCommunicationEligible", null);
        Assert.Null(await chat.Policy.AuthorizeConversationAsync(chatModule, ConversationId));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("module")]
    [InlineData("profile")]
    [InlineData("group")]
    public async Task RealM5ScopedLookupRejectsMissingOrSubstitutedMembership(string fault)
    {
        var scope = new GroupModuleScope("hobbies", GroupKind.Club);
        var membership = fault == "missing" ? null : new GroupMembership(
            fault == "group" ? Guid.NewGuid() : ConversationId,
            fault == "module" ? new("learning", GroupKind.Classroom) : scope,
            fault == "profile" ? Guid.NewGuid() : OtherId);
        var service = new GroupMembershipScopedLookupService(new SyntheticMembershipRepository { Membership = membership });
        var result = await service.LookupMembershipAsync(ConversationId, scope, OtherId);
        Assert.False(result.HasMembership);
        Assert.False(await service.HasActiveMembershipAsync(ConversationId, scope, OtherId));
        using var chat = new ChatSyntheticAuthorizationFixture();
        chat.SetFlag("IsParticipationAuthorized", false);
        Assert.Null(await chat.Policy.AuthorizeConversationAsync(ChatModule.Hobbies, ConversationId));
    }

    [Theory]
    [InlineData("IsProfileActive", null)]
    [InlineData("IsProfileEligible", false)]
    [InlineData("IsParticipationAuthorized", null)]
    [InlineData("IsModuleCommunicationEligible", null)]
    [InlineData("HasMutualMatch", null)]
    [InlineData("HasMutualMatch", false)]
    [InlineData("IsAdult", false)]
    [InlineData("HasSocialOptIn", false)]
    [InlineData("IsBlocked", true)]
    [InlineData("IsRevoked", true)]
    [InlineData("IsCurrent", false)]
    public async Task ExistingChatPolicyRejectsSyntheticIncompleteOrRevokedHandoffs(string flag, bool? value)
    {
        // Every remaining flag is fixture-supplied, not owner authority. These are
        // negative policy characterization cases, never production authorization cases.
        using var chat = new ChatSyntheticAuthorizationFixture();
        chat.SetFlag(flag, value);
        Assert.Null(await chat.Policy.AuthorizeConversationAsync(ChatModule.Social, ConversationId));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("module")]
    [InlineData("participant")]
    [InlineData("stale")]
    [InlineData("request")]
    public async Task ExistingChatPolicyRejectsSyntheticIdentityScopeAndFreshnessConflicts(string fault)
    {
        using var chat = new ChatSyntheticAuthorizationFixture();
        chat.Transform = evidence =>
        {
            if (fault == "owner") ChatSyntheticAuthorizationFixture.Set(evidence, "ProfileAccountId", OtherId);
            if (fault == "module") ChatSyntheticAuthorizationFixture.Set(evidence, "ProfileModule", ChatModule.Learning);
            if (fault == "participant") ChatSyntheticAuthorizationFixture.Set(evidence, "Conversation", ChatConversation.Create(ChatModule.Social, ConversationId,
                [ChatParticipantReference.Create(ChatModule.Social, OtherId, Guid.NewGuid())]));
            if (fault == "stale") ChatSyntheticAuthorizationFixture.Set(evidence, "ObservedAtUtc", ChatSyntheticAuthorizationFixture.Now.AddTicks(-1));
            if (fault == "request") ChatSyntheticAuthorizationFixture.Set(evidence, "RequestId", Guid.Empty);
            return evidence;
        };
        Assert.Null(await chat.Policy.AuthorizeConversationAsync(ChatModule.Social, ConversationId));
    }

    [Fact]
    public async Task EarlierOfflineInterestObservationCannotSurviveSyntheticUnmatchAsChatPermission()
    {
        Assert.Equal(SocialReciprocalInterestStatus.MutualInterestObserved, SocialReciprocalInterestEvaluator.Evaluate(true, true));
        using var chat = new ChatSyntheticAuthorizationFixture();
        chat.Unmatch(); // Synthetic revocation only; no persisted M5 unmatch provider exists here.
        Assert.Null(await chat.Policy.AuthorizeConversationAsync(ChatModule.Social, ConversationId));
        Assert.Equal(1, chat.Reads);
    }

    [Fact]
    public async Task CancellationDuringEvidenceAcquisitionNeverPublishesActorContext()
    {
        using var chat = new ChatSyntheticAuthorizationFixture();
        using var cancellation = new CancellationTokenSource();
        var held = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.HoldNext = held;
        var request = new object();
        var transaction = new object();
        var slot = chat.Slot();
        Assert.True(slot.BeginSimulatedTransaction(request, transaction));
        var pending = slot.BindOperationAsync(request, transaction, ChatModule.Social, ConversationId, cancellation.Token);
        Assert.Equal(1, chat.Reads);
        cancellation.Cancel();
        held.SetResult(chat.LastEvidence);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(slot.HasSimulatedContext(request, transaction, ChatModule.Social, ConversationId));
        Assert.False(slot.MayUseDatabase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DependencyFailureOrMissingEvidenceFailsClosedWithoutDiagnosticLeak(bool missing)
    {
        using var chat = new ChatSyntheticAuthorizationFixture();
        chat.Transform = _ => missing ? null : throw new InvalidOperationException("Synthetic private dependency marker " + OtherId);
        Assert.Null(await chat.Policy.AuthorizeConversationAsync(ChatModule.Social, ConversationId));
        Assert.Equal("ChatConversationAccessEvidence", chat.LastEvidence!.ToString());
        Assert.Equal(nameof(ChatSyntheticAuthorizationFixture), chat.ToString());
    }

    // Controlled owner-interface doubles only. No EF context, SQL, live identity
    // provider or production repository is instantiated by this file.
    private sealed class SyntheticM2Boundary : TimeProvider, IOnboardingIdentity, IAccountRepository,
        IPrivateProfileRepository, IPolicyCatalogProvider
    {
        internal Account Account { get; }
        internal PrivateModuleProfile? Profile;
        internal StarterModuleState State;
        internal DefaultHttpContext Context { get; } = new();
        internal HttpCurrentUser User { get; }
        internal bool Confirmed = true, AccountAvailable = true;
        internal int RepositoryReads;
        internal Guid? LastSubject, LastAccount;
        internal string? LastModule;
        internal Action? BeforeValidation;
        internal PrivateProfileService Service => new(User, this, this, this, this, this);

        internal SyntheticM2Boundary(string module)
        {
            var subject = Guid.Parse("30000000-0000-4000-8000-000000000091");
            Account = Account.Create(subject, GetUtcNow());
            Context.User = new(new ClaimsIdentity([new Claim("sub", subject.ToString()),
                new Claim("role", "authenticated"), new Claim("is_anonymous", "false")], "SyntheticValidatedPrincipal"));
            User = new(new HttpContextAccessor { HttpContext = Context });
            Profile = PrivateModuleProfile.Create(Account.Id, module, GetUtcNow());
            State = new(AccountDemographics.Create(Account.Id, new(2000, 1, 1), "other", GetUtcNow()),
                [AccountPolicyRecord.Create(Account.Id, PolicyActions.TermsAgreed, "synthetic-v1", GetUtcNow()),
                 AccountPolicyRecord.Create(Account.Id, PolicyActions.PrivacyAcknowledged, "synthetic-v1", GetUtcNow())],
                StarterMembership.Create(Account.Id, module, module == "social", GetUtcNow()));
        }

        public override DateTimeOffset GetUtcNow() => ChatSyntheticAuthorizationFixture.Now;
        public bool IsConfirmedFor(Guid subject) => Confirmed && subject == Account.SupabaseUserId;
        public Task<Account?> FindBySubjectAsync(Guid subject, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSubject = subject;
            return Task.FromResult(AccountAvailable && subject == Account.SupabaseUserId ? Account : null);
        }
        public Task<Account> GetOrCreateAsync(Guid subject, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public PolicyCatalog GetCurrent() => new(new("synthetic-v1", "https://example.test/terms", "en"),
            new("synthetic-v1", "https://example.test/privacy", "en"), new("synthetic-v1", "https://example.test/community", "en"));
        public Task<PrivateModuleProfile?> ReadAsync(Guid accountId, string module, Action<StarterModuleState> validate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastAccount = accountId;
            LastModule = module;
            RepositoryReads++;
            Assert.Equal(Account.Id, accountId);
            BeforeValidation?.Invoke();
            validate(State);
            return Task.FromResult(Profile);
        }
        public Task<PrivateModuleProfile> GetOrCreateAsync(Guid accountId, string module, Action<StarterModuleState> validate, TimeProvider clock, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task<T> WithAuthorizedProfileAsync<T>(Guid accountId, string module, Action<StarterModuleState> validate,
            Func<PrivateModuleProfile, Task<T>> action, CancellationToken cancellationToken)
        {
            var profile = await ReadAsync(accountId, module, validate, cancellationToken) ?? throw new ProfileMissingException();
            cancellationToken.ThrowIfCancellationRequested();
            return await action(profile);
        }
    }

    private sealed class SyntheticInterestRepository : ISocialInterestRepository
    {
        internal SocialDirectionalInterest? First, Second;
        internal SocialProfilePairKey? RequestedPair;
        public Task<(SocialDirectionalInterest? FirstInterest, SocialDirectionalInterest? SecondInterest)> GetPairInterestsAsync(SocialProfilePairKey pairKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedPair = pairKey;
            return Task.FromResult((First, Second));
        }
        public Task<SocialDirectionalInterest?> GetDirectionalInterestAsync(SocialProfilePairKey pairKey, Guid actingProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> HasRecordedInterestAsync(SocialProfilePairKey pairKey, Guid actingProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveDirectionalInterestAsync(SocialDirectionalInterest interest, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SyntheticMembershipRepository : IGroupMembershipRepository
    {
        internal GroupMembership? Membership;
        public Task<GroupMembership?> GetMembershipAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Membership);
        }
        public Task<bool> ExistsAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveMembershipAsync(GroupMembership membership, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveMembershipAsync(Guid groupId, GroupModuleScope scope, Guid memberProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> GetMemberCountAsync(Guid groupId, GroupModuleScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
