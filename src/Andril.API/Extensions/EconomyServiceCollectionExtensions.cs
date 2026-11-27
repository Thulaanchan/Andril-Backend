using Andril.API.Middleware;
using Andril.Common.Accounts;
using Andril.Economy.Payments;
using Andril.Economy.Premium;
using Andril.Economy.VirtualGifts;
using Andril.Economy.Wallet;
using Andril.Persistence.DbContexts;
using Andril.Persistence.Configurations;
using Andril.Persistence.Repositories.Economy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Andril.API.Extensions;

public static class EconomyServiceCollectionExtensions
{
    public static IServiceCollection AddEconomy(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);

        // Virtual Gift Catalog
        services.AddSingleton<IVirtualGiftCatalog, ConfiguredVirtualGiftCatalog>();
        services.AddScoped<IVirtualGiftService, VirtualGiftService>();

        // Wallet & Ledger
        services.AddScoped<ICoinWalletRepository, PostgresCoinWalletRepository>();
        services.AddScoped<ICoinWalletService, CoinWalletService>();

        // Entitlements & Payments
        services.AddScoped<IEntitlementRepository, PostgresEntitlementRepository>();
        services.AddScoped<IEntitlementService, EntitlementService>();
        services.AddScoped<IPaymentVerificationService, PaymentVerificationService>();

        // Exception Handling
        services.AddExceptionHandler<EconomyExceptionHandler>();

        // Persistence DbContext (optional connection string fallback to main Accounts connection string if not explicitly set)
        services.AddDbContext<EconomyDbContext>(options =>
        {
            var connection = configuration.GetConnectionString("AndrilEconomy")
                             ?? configuration.GetConnectionString("AndrilAccounts");

            if (string.IsNullOrWhiteSpace(connection))
                throw new AccountStoreUnavailableException();

            NpgsqlConnectionStringBuilder settings;
            try
            {
                settings = new NpgsqlConnectionStringBuilder(connection) { IncludeErrorDetail = false };
                if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database))
                    throw new AccountStoreUnavailableException();
            }
            catch (ArgumentException)
            {
                throw new AccountStoreUnavailableException();
            }

            options.UseNpgsql(settings.ConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable(EconomyDatabaseOptions.MigrationsHistoryTableName,
                    EconomyDatabaseOptions.Schema));
        });

        return services;
    }
}
