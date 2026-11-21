using Andril.API.Middleware;
using Andril.API.Configuration;
using Andril.API.Integrations;
using Andril.Common.Accounts.Profiles;
using Andril.Common.Interfaces;
using Andril.Common.Accounts;
using Andril.Common.Accounts.Preferences;
using Andril.Common.Accounts.Eligibility;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Repositories.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Andril.API.Extensions;

public static class AccountServiceCollectionExtensions
{
    public static IServiceCollection AddAccounts(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<ProfilePhotoOptions>(configuration.GetSection("ProfilePhotos"));
        services.AddScoped<IProfilePhotoService, ProfilePhotoService>();
        services.AddScoped<IPrivateProfilePhotoStore, PrivateProfilePhotoStore>();
        services.AddSingleton<IPhotoUploadPolicy, ConfiguredPhotoUploadPolicy>();
        services.AddSingleton<IProfileImageInspector, UnavailableProfilePhotos>();
        services.AddHttpClient<IPrivateProfilePhotoObjectStorage, SupabaseProfilePhotoObjectStorage>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                MaxResponseHeadersLength = 16
            })
            .RedactLoggedHeaders(_ => true)
            .RemoveAllLoggers();
        services.AddScoped<Andril.Common.Accounts.Profiles.IPrivateProfileRepository, PrivateProfileRepository>();
        services.AddScoped<Andril.Common.Accounts.Profiles.IPrivateProfileService, Andril.Common.Accounts.Profiles.PrivateProfileService>();
        services.AddScoped<Andril.Common.Accounts.Profiles.IPrivateProfileCompletionReader, Andril.Common.Accounts.Profiles.PrivateProfileCompletionReader>();
        services.AddScoped<Andril.Common.Accounts.Profiles.IPrivateModuleAccess, Andril.Common.Accounts.Profiles.PrivateModuleAccess>();
        services.AddExceptionHandler<PrivateProfileExceptionHandler>();
        services.AddScoped<Andril.Common.Accounts.Onboarding.IOnboardingIdentity, Andril.API.Authentication.OnboardingIdentity>();
        services.AddScoped<Andril.Common.Accounts.Onboarding.IOnboardingStatusService, Andril.Common.Accounts.Onboarding.OnboardingStatusService>();
        services.AddExceptionHandler<OnboardingExceptionHandler>();
        services.AddScoped<Andril.Common.Accounts.StarterModules.IStarterModuleService, Andril.Common.Accounts.StarterModules.StarterModuleService>();
        services.AddScoped<Andril.Common.Accounts.StarterModules.IStarterModuleRepository, StarterModuleRepository>();
        services.AddExceptionHandler<StarterModuleExceptionHandler>();
        services.AddScoped<Andril.Common.Accounts.Policies.IPolicyCatalogProvider, Andril.API.Configuration.ConfiguredPolicyCatalog>();
        services.AddScoped<Andril.Common.Accounts.Policies.IAccountPolicyService, Andril.Common.Accounts.Policies.AccountPolicyService>();
        services.AddScoped<Andril.Common.Accounts.Policies.IAccountPolicyRepository, AccountPolicyRepository>();
        services.AddExceptionHandler<PolicyExceptionHandler>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<ILanguagePreferenceService, LanguagePreferenceService>();
        services.AddScoped<ILanguagePreferenceRepository, LanguagePreferenceRepository>();
        services.AddExceptionHandler<LanguagePreferenceExceptionHandler>();
        services.AddScoped<IAccountEligibilityService, AccountEligibilityService>();
        services.AddScoped<IAccountDemographicsRepository, AccountDemographicsRepository>();
        services.AddExceptionHandler<EligibilityExceptionHandler>();
        services.AddDbContext<AccountsDbContext>(options =>
        {
            var connection = configuration.GetConnectionString("AndrilAccounts");
            if (string.IsNullOrWhiteSpace(connection)) throw new AccountStoreUnavailableException();
            NpgsqlConnectionStringBuilder settings;
            try
            {
                settings = new NpgsqlConnectionStringBuilder(connection) { IncludeErrorDetail = false };
                if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database))
                    throw new AccountStoreUnavailableException();
            }
            catch (ArgumentException) { throw new AccountStoreUnavailableException(); }
            options.UseNpgsql(settings.ConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "andril_identity"));
            // No startup migration, database creation, sensitive logging, or credentials in source.
        });
        services.AddExceptionHandler<AccountExceptionHandler>();
        return services;
    }
}
