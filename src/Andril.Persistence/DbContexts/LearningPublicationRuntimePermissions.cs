using Npgsql;

namespace Andril.Persistence.DbContexts;

public static class LearningPublicationRuntimePermissions
{
    public static string GrantSql(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);using var quote=new NpgsqlCommandBuilder();var target=quote.QuoteIdentifier(role);
        return $"""
            GRANT USAGE ON SCHEMA andril_learning TO {target};
            GRANT SELECT,INSERT ON andril_learning.publications TO {target};
            GRANT UPDATE(published,name_disclosure,version,reviewed_name,lifecycle_revision) ON andril_learning.publications TO {target};
            GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_cv_owner(uuid,uuid),
                andril_learning.publication_owned(integer,uuid,uuid),andril_learning.publication_owner_facts(uuid),andril_learning.read_disclosed_people(uuid,text,integer,text,text),
                andril_learning.published_target_account(uuid,uuid,integer,uuid,text,text) TO {target};
            """;
    }
}
