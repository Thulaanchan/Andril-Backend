using System.Text.RegularExpressions;
using Npgsql;

namespace Andril.Persistence.DbContexts;

/// <summary>
/// Explicit scripts for a separately provisioned maintenance role. Never apply to the
/// ordinary runtime role. No role creation, capability changes, RLS bypass or Learning
/// authority grants. An operator must verify the worker and retention authorities first.
/// </summary>
public static class LearningAiCleanupPermissions
{
    public static string GrantSql(string roleName)
    {
        var role = Identifier(roleName);
        return $"""
DO $cleanup$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{roleName}' AND rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolbypassrls) THEN
        RAISE EXCEPTION 'AI maintenance role must already exist with restricted capabilities';
    END IF;
END $cleanup$;
GRANT USAGE ON SCHEMA andril_ai TO {role};
GRANT SELECT, DELETE ON andril_ai.conversations, andril_ai.purge_work TO {role};
GRANT SELECT(id,conversation_id,account_id,module_profile_id,module), DELETE ON andril_ai.messages TO {role};
""";
    }

    public static string RevokeSql(string roleName)
    {
        var role = Identifier(roleName);
        return $"""
REVOKE SELECT(id,conversation_id,account_id,module_profile_id,module) ON andril_ai.messages FROM {role};
REVOKE ALL ON andril_ai.conversations, andril_ai.messages, andril_ai.purge_work FROM {role};
REVOKE ALL ON SCHEMA andril_ai FROM {role};
""";
    }

    private static string Identifier(string value)
    {
        if (value is null || !Regex.IsMatch(value, "\\A[a-z_][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("A restricted PostgreSQL maintenance role identifier is required.", nameof(value));
        using var builder = new NpgsqlCommandBuilder();
        return builder.QuoteIdentifier(value);
    }
}
