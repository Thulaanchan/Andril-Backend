using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Andril.Persistence.Configurations;

public static class HobbiesDatabaseOptions
{
    public const string Schema = "andril_hobbies";

    public static void ConfigureGalleryHistory(NpgsqlDbContextOptionsBuilder options) =>
        options.MigrationsHistoryTable("__EFMigrationsHistory_Gallery", Schema);

    public static void ConfigureMapHistory(NpgsqlDbContextOptionsBuilder options) =>
        options.MigrationsHistoryTable("__EFMigrationsHistory_Map", Schema);
}
