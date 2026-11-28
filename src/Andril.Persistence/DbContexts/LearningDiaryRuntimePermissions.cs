using Npgsql;

namespace Andril.Persistence.DbContexts;

/// <summary>Explicit deployment grants for private Learning Diary commands. No worker/retention grant.</summary>
public static class LearningDiaryRuntimePermissions
{
    public static string GrantSql(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);using var quoting=new NpgsqlCommandBuilder();var target=quoting.QuoteIdentifier(role);
        return $"""
            GRANT USAGE ON SCHEMA andril_diary,andril_learning TO {target};
            GRANT SELECT,INSERT,DELETE ON andril_diary.module_diary_entries TO {target};
            GRANT UPDATE(reflection_title,reflection_body,plans) ON andril_diary.module_diary_entries TO {target};
            GRANT EXECUTE ON FUNCTION andril_diary.scope_visible(uuid,text),andril_learning.lock_cv_owner(uuid,uuid) TO {target};
            """;
    }
}
