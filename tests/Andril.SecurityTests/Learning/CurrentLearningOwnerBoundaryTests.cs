using Andril.API.Extensions;
using Andril.API.Integrations.Learning;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.Interfaces;
using Andril.Learning.Progress;
using Andril.Learning.Activities;
using Andril.Shared.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.SecurityTests.Learning;

public sealed class CurrentLearningOwnerBoundaryTests
{
    [Fact]
    public void OptInCompositionRequiresRealSharedDependenciesAndDoesNotInstallWorkersOrProviders()
    {
        var services=new ServiceCollection();services.AddLearningOwnerAuthority();services.AddLearningOwnerAuthority();
        Assert.Single(services,x=>x.ServiceType==typeof(ICurrentLearningOwnerBoundary));
        Assert.Single(services,x=>x.ServiceType==typeof(ILearningActivityAuthority));
        Assert.Single(services,x=>x.ServiceType==typeof(ILearningXpAuthority));
        Assert.DoesNotContain(services,x=>x.ServiceType==typeof(Microsoft.Extensions.Hosting.IHostedService));
        Assert.DoesNotContain(services,x=>x.ServiceType==typeof(Andril.AI.Interfaces.IAiProviderDispatchBoundary));
        Assert.DoesNotContain(services,x=>x.ServiceType==typeof(Andril.AI.Interfaces.IAiPrivacyNoticeGate));
        Assert.Throws<AggregateException>(()=>services.BuildServiceProvider(new ServiceProviderOptions{ValidateOnBuild=true,ValidateScopes=true}));
    }
    [Fact]
    public async Task SharedOwnerAdapterAllowsOnlyOwnXpReadsAndNeverConfiguresProductionRewards()
    {
        var f=new Fixture();var authority=new CurrentLearningXpReadAuthority(f.Boundary);var scope=new LearningSetupScope(f.Account.Id,f.Profile.ProfileId);
        Assert.True(await authority.MayReadAsync(scope));
        Assert.False(await authority.MayReadAsync(new(Guid.NewGuid(),scope.LearningProfileId)));
        Assert.False(await authority.MayReadAsync(new(scope.AccountId,Guid.NewGuid())));
        Assert.Null(await authority.ResolveAwardAsync(scope,new("like",Guid.NewGuid())));
        Assert.False((await authority.ReadCurrentPolicyAsync())!.IsConfigured);
        f.Denied=true;Assert.False(await authority.MayReadAsync(scope));
    }
    [Fact]
    public async Task SharedActivityAdapterKeepsMultiPartyAndCirclePermissionsUnavailable()
    {
        var f=new Fixture();var authority=new CurrentLearningOwnerActivityAuthority(f.Boundary);var actor=new LearningActivityMember(f.Account.Id,f.Profile.ProfileId);
        Assert.Equal(LearningActivityPermissionOutcome.Allowed,(await authority.AuthorizeAsync(actor,LearningActivityKind.GrowthSelfReport,LearningActivityAction.Read,Guid.NewGuid(),null)).Outcome);
        foreach(var kind in new[]{LearningActivityKind.SkillExchange,LearningActivityKind.Pact,LearningActivityKind.MentorshipRequest,LearningActivityKind.Circle,LearningActivityKind.Connection})
            Assert.Equal(LearningActivityPermissionOutcome.Unavailable,(await authority.AuthorizeAsync(actor,kind,LearningActivityAction.Create,null,new())).Outcome);
        f.Denied=true;Assert.Equal(LearningActivityPermissionOutcome.Denied,(await authority.AuthorizeAsync(actor,LearningActivityKind.GrowthSelfReport,LearningActivityAction.Read,Guid.NewGuid(),null)).Outcome);
    }
    [Fact]
    public void OptInCompositionIsScopedIdempotentAndCannotCreateAuthorityWithoutRealDependencies()
    {
        var services=new ServiceCollection();services.AddLearningOwnerAuthority();services.AddLearningOwnerAuthority();
        Assert.Equal(3,services.Count);Assert.All(services,x=>Assert.Equal(ServiceLifetime.Scoped,x.Lifetime));
        Assert.Single(services,x=>x.ServiceType==typeof(ICurrentLearningOwnerBoundary));
        Assert.Single(services,x=>x.ServiceType==typeof(ILearningActivityAuthority));Assert.Single(services,x=>x.ServiceType==typeof(ILearningXpAuthority));
        Assert.Throws<AggregateException>(()=>services.BuildServiceProvider(new ServiceProviderOptions{ValidateOnBuild=true,ValidateScopes=true}));
    }
    private sealed class Fixture:ICurrentUser,IAccountRepository,IPrivateProfileService,IPrivateModuleAccess
    {
        public Guid? UserId{get;set;}=Guid.NewGuid();public bool IsAuthenticated{get;set;}=true;
        internal Account Account;internal PrivateProfileDto Profile=new(Guid.NewGuid(),"learning",DateTimeOffset.UnixEpoch);
        internal int Reads,Callbacks;internal bool Denied,Unavailable;internal Action? BeforeRelease;
        internal Fixture()=>Account=Account.Create(UserId!.Value,DateTimeOffset.UnixEpoch);
        internal CurrentLearningOwnerBoundary Boundary=>new(this,this,this,this);
        public Task<Account?> FindBySubjectAsync(Guid subject,CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();Reads++;if(Unavailable)throw new IOException("PRIVATE_IDENTITY_FAILURE");return Task.FromResult<Account?>(Account);}
        public Task<Account> GetOrCreateAsync(Guid subject,DateTimeOffset now,CancellationToken cancellationToken=default)=>throw new InvalidOperationException("No provisioning.");
        public Task RequireAsync(string module,CancellationToken cancellationToken=default)
        {Assert.Equal("learning",module);cancellationToken.ThrowIfCancellationRequested();if(Denied)throw new ProfileAccessDeniedException();return Task.CompletedTask;}
        public async Task<T> WithAuthorizedProfileAsync<T>(string module,Func<PrivateProfileDto,Task<T>> action,CancellationToken cancellationToken=default)
        {Assert.Equal("learning",module);await RequireAsync(module,cancellationToken);var result=await action(Profile);BeforeRelease?.Invoke();cancellationToken.ThrowIfCancellationRequested();if(Denied)throw new ProfileAccessDeniedException();return result;}
        public Task<PrivateProfileDto> GetAsync(string module,CancellationToken cancellationToken=default)=>throw new InvalidOperationException("No uncoordinated read.");
        public Task<PrivateProfileDto> CreateAsync(string module,CancellationToken cancellationToken=default)=>throw new InvalidOperationException("No provisioning.");
    }
    [Fact]
    public async Task ActualCurrentAccountAndProfileAreResolvedFreshlyAndNotSerializedAsPermission()
    {
        var f=new Fixture();for(var i=0;i<2;i++)
        {var owner=await f.Boundary.WithCurrentOwnerAsync<CurrentLearningOwner>((o,_)=>{f.Callbacks++;return Task.FromResult<CurrentLearningOwner?>(o);});Assert.Equal(f.Account.Id,owner!.AccountId);Assert.Equal(f.Profile.ProfileId,owner.ProfileId);Assert.Equal("{}",System.Text.Json.JsonSerializer.Serialize(owner));}
        Assert.Equal(2,f.Reads);Assert.Equal(2,f.Callbacks);
    }
    [Theory] [InlineData("foreign-account")] [InlineData("foreign-subject")] [InlineData("foreign-module")] [InlineData("inactive")] [InlineData("anonymous")]
    public async Task InvalidIdentityModuleOrLifecycleNeverInvokesProtectedCallback(string defect)
    {
        var f=new Fixture();Guid? expected=null;
        if(defect=="foreign-account")expected=Guid.NewGuid();if(defect=="foreign-subject")f.UserId=Guid.NewGuid();
        if(defect=="foreign-module")f.Profile=f.Profile with{Module="hobbies"};if(defect=="inactive")f.Denied=true;if(defect=="anonymous")f.IsAuthenticated=false;
        var result=await f.Boundary.WithCurrentOwnerAsync<object>((_,_)=>{f.Callbacks++;return Task.FromResult<object?>(new());},expectedAccountId:expected);
        Assert.Null(result);Assert.Equal(0,f.Callbacks);
    }
    [Fact]
    public async Task SubjectChangesAndLateRevocationWithholdCallbackResults()
    {
        var f=new Fixture();f.BeforeRelease=()=>f.UserId=Guid.NewGuid();Assert.Null(await f.Boundary.WithCurrentOwnerAsync<object>((_,_)=>Task.FromResult<object?>(new())));
        var second=new Fixture();second.BeforeRelease=()=>second.Denied=true;Assert.Null(await second.Boundary.WithCurrentOwnerAsync<object>((_,_)=>Task.FromResult<object?>(new())));
    }
    [Fact]
    public async Task InfrastructureAndCancellationAreNotTurnedIntoPositiveAuthority()
    {
        var f=new Fixture{Unavailable=true};await Assert.ThrowsAsync<IOException>(()=>f.Boundary.WithCurrentOwnerAsync<object>((_,_)=>Task.FromResult<object?>(new())));
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Boundary.WithCurrentOwnerAsync<object>((_,_)=>Task.FromResult<object?>(new()),cancel.Token));
    }
    [Fact]
    public void OptInRegistrationInstallsOnlyTheScopedSharedBoundaryAndNoPermissiveDependencies()
    {
        var services=new ServiceCollection();services.AddLearningOwnerAuthority();services.AddLearningOwnerAuthority();
        var expected=new[]{typeof(ICurrentLearningOwnerBoundary),typeof(ILearningActivityAuthority),typeof(ILearningXpAuthority)};
        Assert.Equal(expected.Length,services.Count);
        foreach(var type in expected)
        {
            var registration=Assert.Single(services,x=>x.ServiceType==type);
            Assert.Equal(ServiceLifetime.Scoped,registration.Lifetime);
        }
        using var provider=services.BuildServiceProvider();using var scope=provider.CreateScope();
        Assert.Throws<InvalidOperationException>(()=>scope.ServiceProvider.GetRequiredService<ICurrentLearningOwnerBoundary>());
        Assert.Throws<InvalidOperationException>(()=>scope.ServiceProvider.GetRequiredService<ILearningActivityAuthority>());
        Assert.Throws<InvalidOperationException>(()=>scope.ServiceProvider.GetRequiredService<ILearningXpAuthority>());
    }
}
