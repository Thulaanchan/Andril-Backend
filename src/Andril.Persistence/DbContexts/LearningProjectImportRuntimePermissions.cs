using Npgsql;

namespace Andril.Persistence.DbContexts;

/// <summary>Deployment-time grants only: no source Portfolio mutation, owner rewrite, delete or identity reads.</summary>
public static class LearningProjectImportRuntimePermissions
{
    public static string GrantSql(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);using var quoting=new NpgsqlCommandBuilder();var name=quoting.QuoteIdentifier(role);
        return $"""
            GRANT USAGE ON SCHEMA andril_learning TO {name};
            GRANT SELECT ON andril_learning.learning_cvs,andril_learning.learning_portfolios TO {name};
            GRANT UPDATE(sections) ON andril_learning.learning_cvs TO {name};
            GRANT SELECT,INSERT ON andril_learning.learning_project_import_reviews,andril_learning.learning_project_import_origins TO {name};
            GRANT UPDATE(confirmed) ON andril_learning.learning_project_import_reviews TO {name};
            GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_cv_owner(uuid,uuid) TO {name};
            """;
    }
}
