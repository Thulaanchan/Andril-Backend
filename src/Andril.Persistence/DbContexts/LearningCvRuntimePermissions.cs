using Npgsql;

namespace Andril.Persistence.DbContexts;

/// <summary>Deployment-time least-privilege grants for private CV commands only.
/// Never executed at startup. No identity-table access or cross-purpose authority.</summary>
public static class LearningCvRuntimePermissions
{
    public static string GrantSql(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        using var quoting = new NpgsqlCommandBuilder();
        var target = quoting.QuoteIdentifier(role);
        return $"""
            GRANT USAGE ON SCHEMA andril_learning TO {target};
            GRANT SELECT,INSERT ON andril_learning.learning_cvs TO {target};
            GRANT UPDATE(template,include_contact_information,sections) ON andril_learning.learning_cvs TO {target};
            GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_cv_owner(uuid,uuid) TO {target};
            """;
    }
}
