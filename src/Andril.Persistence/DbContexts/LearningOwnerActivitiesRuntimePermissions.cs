using Npgsql;

namespace Andril.Persistence.DbContexts;

/// <summary>Deployment-time private owner read/insert only. No peer, mutation, worker or discovery authority.</summary>
public static class LearningOwnerActivitiesRuntimePermissions
{
    public static string GrantSql(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);using var quoting=new NpgsqlCommandBuilder();var target=quoting.QuoteIdentifier(role);
        return $"""
            GRANT USAGE ON SCHEMA andril_learning TO {target};
            GRANT SELECT,INSERT ON andril_learning.private_owner_activities TO {target};
            GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_cv_owner(uuid,uuid) TO {target};
            """;
    }
}
