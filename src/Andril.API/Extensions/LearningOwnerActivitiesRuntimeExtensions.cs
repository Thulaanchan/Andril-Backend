using Andril.Common.Accounts.Profiles;
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
        if(!configuration.GetValue<bool>("Learning:OwnerActivities:Enabled"))return services;
        var role=configuration["Learning:OwnerActivities:RuntimeRole"];
        if(string.IsNullOrWhiteSpace(role))throw new InvalidOperationException("Private Learning activities require a restricted runtime role.");
        services.AddSingleton(new Enabled());
        services.AddScoped<ILearningActivitiesRepository>(sp=>LearningOwnerActivityPersistence.Create(sp.GetRequiredService<AccountsDbContext>(),
            sp.GetRequiredService<ICurrentUser>(),sp.GetRequiredService<IPrivateProfileService>(),role));
        return services;
    }
    public static WebApplication MapVerifiedLearningOwnerActivities(this WebApplication app)
    {
        if(app.Services.GetService<Enabled>() is not null)app.MapControlledLearningActivities();
        return app;
    }
}
