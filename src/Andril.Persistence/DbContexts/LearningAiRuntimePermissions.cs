using System.Text.RegularExpressions;
using Npgsql;

namespace Andril.Persistence.DbContexts;

/// <summary>
/// Explicit operator-owned role provisioning scripts; never executed at application startup.
/// Does not create roles, alter role capabilities or grant cleanup/DELETE authority.
/// Credentials, existing shared-role privileges and hosted execution remain separate gates.
/// </summary>
public static class LearningAiRuntimePermissions
{
    public static string GrantSql(string roleName)
    {
        var role = Identifier(roleName);
        // roleName has a strict ASCII identifier allowlist before use as a SQL value or identifier.
        return $"""
DO $runtime$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{roleName}' AND rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolbypassrls) THEN
        RAISE EXCEPTION 'AI runtime role must already exist with restricted capabilities';
    END IF;
END $runtime$;
GRANT USAGE ON SCHEMA andril_ai TO {role};
GRANT SELECT, INSERT ON andril_ai.conversations, andril_ai.messages, andril_ai.purge_work, andril_ai.privacy_acknowledgements, andril_ai.dispatch_operations TO {role};
GRANT UPDATE(last_saved_user_message_at_utc,expires_at_utc,deleted_at_utc) ON andril_ai.conversations TO {role};
GRANT UPDATE(version,policy_version,acknowledged,last_operation_id,updated_at_utc) ON andril_ai.privacy_acknowledgements TO {role};
GRANT UPDATE(state,updated_at_utc) ON andril_ai.dispatch_operations TO {role};
GRANT USAGE ON ALL SEQUENCES IN SCHEMA andril_ai TO {role};
""";
    }

    public static string RevokeSql(string roleName)
    {
        var role = Identifier(roleName);
        return $"""
REVOKE UPDATE(last_saved_user_message_at_utc,expires_at_utc,deleted_at_utc) ON andril_ai.conversations FROM {role};
REVOKE UPDATE(version,policy_version,acknowledged,last_operation_id,updated_at_utc) ON andril_ai.privacy_acknowledgements FROM {role};
REVOKE UPDATE(state,updated_at_utc) ON andril_ai.dispatch_operations FROM {role};
REVOKE ALL ON ALL TABLES IN SCHEMA andril_ai FROM {role};
REVOKE ALL ON ALL SEQUENCES IN SCHEMA andril_ai FROM {role};
REVOKE ALL ON SCHEMA andril_ai FROM {role};
""";
    }

    private static string Identifier(string value)
    {
        if (value is null || !Regex.IsMatch(value, "\\A[a-z_][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("A restricted PostgreSQL role identifier is required.", nameof(value));
        using var builder = new NpgsqlCommandBuilder();
        return builder.QuoteIdentifier(value);
    }
}
