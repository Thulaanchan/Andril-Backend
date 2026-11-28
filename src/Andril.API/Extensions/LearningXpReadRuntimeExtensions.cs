using Andril.Common.Accounts.Profiles;
using Andril.Learning.Progress;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;

namespace Andril.API.Extensions;

/// <summary>Explicit current-owner XP reads. No earning, adjustment, policy publication or conversion activation.</summary>
public static class LearningXpReadRuntimeExtensions
{
    private sealed record Enabled;
    public static IServiceCollection AddVerifiedLearningXpRead(this IServiceCollection services,IConfiguration configuration)
    {
        if(!configuration.GetValue<bool>("Learning:XpRead:Enabled"))return services;
        var role=configuration["Learning:XpRead:RuntimeRole"];
        if(string.IsNullOrWhiteSpace(role))throw new InvalidOperationException("Learning XP reads require a restricted runtime role.");
        services.AddSingleton(new Enabled());
        services.AddScoped<ILearningXpRepository>(sp=>LearningXpReadPersistence.Create(sp.GetRequiredService<AccountsDbContext>(),sp.GetRequiredService<ICurrentUser>(),sp.GetRequiredService<IPrivateProfileService>(),role));
        return services;
    }
    public static WebApplication MapVerifiedLearningXpRead(this WebApplication app)
    {if(app.Services.GetService<Enabled>() is not null)app.MapControlledLearningXp();return app;}
}
