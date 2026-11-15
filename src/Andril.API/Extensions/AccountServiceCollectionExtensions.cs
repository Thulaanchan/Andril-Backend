using Andril.API.Middleware;
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
