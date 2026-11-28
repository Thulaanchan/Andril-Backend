using Npgsql;

namespace Andril.Persistence.DbContexts;

public static class LearningPeerRuntimePermissions
{
    public static string GrantSql(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);using var quoting=new NpgsqlCommandBuilder();var name=quoting.QuoteIdentifier(role);
        return $"""
            GRANT USAGE ON SCHEMA andril_learning TO {name};
            GRANT SELECT,INSERT ON andril_learning.peer_activities TO {name};
            GRANT UPDATE(content,status,version) ON andril_learning.peer_activities TO {name};
            GRANT EXECUTE ON FUNCTION andril_learning.scope_visible(uuid,text),andril_learning.lock_cv_owner(uuid,uuid),
                andril_learning.peer_account(uuid,uuid,integer,text,text),andril_learning.peer_row_visible(jsonb,integer),
                andril_learning.mentorship_members(uuid,uuid,uuid,uuid,text,text),andril_learning.peer_content_visible(jsonb,integer,jsonb) TO {name};
            """;
    }
}
