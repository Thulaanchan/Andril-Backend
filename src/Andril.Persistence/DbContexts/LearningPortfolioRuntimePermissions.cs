using Npgsql;

namespace Andril.Persistence.DbContexts;

/// <summary>Explicit deployment-time private Portfolio grants; never applied at startup.</summary>
public static class LearningPortfolioRuntimePermissions
{
    public static string GrantSql(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        using var quoting=new NpgsqlCommandBuilder();var target=quoting.QuoteIdentifier(role);
        return $"""
            GRANT USAGE ON SCHEMA andril_learning TO {target};
            GRANT SELECT,INSERT,DELETE ON andril_learning.learning_portfolios TO {target};
            GRANT UPDATE(template,projects,content_version) ON andril_learning.learning_portfolios TO {target};
            GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_cv_owner(uuid,uuid) TO {target};
            """;
    }
}
