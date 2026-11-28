using Andril.Common.Accounts.Profiles;
using Andril.Common.Accounts.Policies;
using Andril.Learning.Activities;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;

namespace Andril.API.Extensions;

/// <summary>Explicit private owner foundations; unsupported multi-party/public-discovery paths stay unavailable.</summary>
public static class LearningOwnerActivitiesRuntimeExtensions
{
    private sealed record Enabled;
    public static IServiceCollection AddVerifiedLearningOwnerActivities(this IServiceCollection services,IConfiguration configuration)
    {
        bool owners=configuration.GetValue<bool>("Learning:OwnerActivities:Enabled"),peers=configuration.GetValue<bool>("Learning:PeerActivities:Enabled");
        if(!owners&&!peers)return services;
        var role=configuration["Learning:OwnerActivities:RuntimeRole"];
        var peerRole=configuration["Learning:PeerActivities:RuntimeRole"];
        if(owners&&string.IsNullOrWhiteSpace(role)||peers&&string.IsNullOrWhiteSpace(peerRole))throw new InvalidOperationException("Learning activities require explicit restricted runtime roles.");
        services.AddSingleton(new Enabled());
        services.AddScoped<ILearningActivitiesRepository>(sp=>
        {
            var db=sp.GetRequiredService<AccountsDbContext>();var user=sp.GetRequiredService<ICurrentUser>();var profiles=sp.GetRequiredService<IPrivateProfileService>();
            var owner=owners?LearningOwnerActivityPersistence.Create(db,user,profiles,role!):null;
            var peer=peers?LearningPeerActivityPersistence.Create(db,user,profiles,sp.GetRequiredService<IPolicyCatalogProvider>(),peerRole!):null;
            return new RoutedActivities(owner,peer);
        });
        return services;
    }
    public static WebApplication MapVerifiedLearningOwnerActivities(this WebApplication app)
    {
        if(app.Services.GetService<Enabled>() is not null)app.MapControlledLearningActivities();
        return app;
    }
    private sealed class RoutedActivities(ILearningActivitiesRepository? owners,ILearningActivitiesRepository? peers):ILearningActivitiesRepository
    {
        private ILearningActivitiesRepository? For(LearningActivityKind kind)=>kind is LearningActivityKind.GrowthSelfReport or LearningActivityKind.MentorshipOffer?owners:peers;
        private static Task<LearningActivityResult> Missing(CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(new LearningActivityResult(LearningActivityOutcome.Unavailable));}
        public Task<LearningActivityResult> CreateAsync(LearningActivityKind kind,CreateLearningActivityRequest request,CancellationToken cancellationToken=default)=>For(kind)?.CreateAsync(kind,request,cancellationToken)??Missing(cancellationToken);
        public Task<LearningActivityResult> ReadAsync(LearningActivityKind kind,Guid id,CancellationToken cancellationToken=default)=>For(kind)?.ReadAsync(kind,id,cancellationToken)??Missing(cancellationToken);
        public Task<LearningActivityResult> ListAsync(LearningActivityKind kind,Guid? after=null,int limit=50,CancellationToken cancellationToken=default)=>For(kind)?.ListAsync(kind,after,limit,cancellationToken)??Missing(cancellationToken);
        public Task<LearningActivityResult> ChangeAsync(LearningActivityKind kind,Guid id,ChangeLearningActivityRequest request,CancellationToken cancellationToken=default)=>For(kind)?.ChangeAsync(kind,id,request,cancellationToken)??Missing(cancellationToken);
    }
}
