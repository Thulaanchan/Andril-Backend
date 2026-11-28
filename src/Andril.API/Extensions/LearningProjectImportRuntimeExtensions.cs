using Andril.API.Controllers.Learning;
using Andril.Common.Accounts.Profiles;
using Andril.Learning.CV;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Learning;
using Andril.Shared.Authentication;

namespace Andril.API.Extensions;

public static class LearningProjectImportRuntimeExtensions
{
    private sealed record Enabled;
    public static IServiceCollection AddVerifiedLearningProjectImports(this IServiceCollection services,IConfiguration configuration)
    {
        if(!configuration.GetValue<bool>("Learning:ProjectImports:Enabled"))return services;
        var role=configuration["Learning:ProjectImports:RuntimeRole"];
        if(string.IsNullOrWhiteSpace(role)||!configuration.GetValue<bool>("Learning:PrivateCv:Enabled")||!configuration.GetValue<bool>("Learning:PrivatePortfolios:Enabled"))
            throw new InvalidOperationException("Private project imports require opted-in CV and Portfolio persistence and a restricted import role.");
        services.AddSingleton(new Enabled());
        services.AddScoped<ILearningProjectImportRepository>(sp=>LearningProjectImportPersistence.Create(sp.GetRequiredService<AccountsDbContext>(),
            sp.GetRequiredService<ICurrentUser>(),sp.GetRequiredService<IPrivateProfileService>(),role));
        services.AddScoped<LearningProjectImportApplicationService>();return services;
    }
    public static WebApplication MapVerifiedLearningProjectImports(this WebApplication app)
    {
        if(app.Services.GetService<Enabled>() is not null)ControlledLearningProjectImportEndpoints.MapLearningProjectImportRoutes(app);
        return app;
    }
}
