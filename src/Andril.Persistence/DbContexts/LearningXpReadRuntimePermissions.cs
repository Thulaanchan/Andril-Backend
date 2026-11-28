using Npgsql;

namespace Andril.Persistence.DbContexts;

public static class LearningXpReadRuntimePermissions
{
    /// <summary>Deployment-time current-owner reads only; no client or runtime earning/adjustment grants.</summary>
    public static string GrantSql(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);using var quoting=new NpgsqlCommandBuilder();var target=quoting.QuoteIdentifier(role);
        return $"""
            GRANT USAGE ON SCHEMA andril_learning TO {target};
            GRANT SELECT ON andril_learning.xp_credits TO {target};
            GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_cv_owner(uuid,uuid) TO {target};
            """;
    }
}
