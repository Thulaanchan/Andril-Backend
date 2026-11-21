namespace Andril.IntegrationTests.Communication.Chat;

internal enum ChatTestEnvironment
{
    IsolatedDisposable = 1,
    Production,
    Staging,
    SharedSupabase
}

internal enum ChatTestLifecycle
{
    ExternallyManagedDisposable = 1,
    SharedPersistent,
    ProductionPersistent
}

internal enum ChatTestLifecycleApproval
{
    Approved = 1,
    Pending,
    Rejected
}

/// <summary>
/// Explicit non-secret assertions only, never connection settings or proof of an environment.
/// DatabaseEnvironment independently declares the database classification for consistency checking.
/// No field has a default. Identifiers use a deliberately narrow canonical ASCII vocabulary.
/// </summary>
internal sealed record ChatPostgresTestMetadata(
    ChatTestEnvironment? Environment,
    string? Host,
    int? Port,
    string? Database,
    ChatTestEnvironment? DatabaseEnvironment,
    string? MigrationRole,
    string? RuntimeRole,
    string? ProvisioningAuthority,
    ChatTestLifecycle? Lifecycle,
    ChatTestLifecycleApproval? ExternalLifecycleApproval)
{
    public override string ToString() => nameof(ChatPostgresTestMetadata);
}

internal enum ChatTestReadinessError
{
    None,
    MissingMetadata,
    InvalidEnvironment,
    InvalidEndpoint,
    InvalidDatabaseIdentity,
    ConflictingDatabaseClassification,
    InvalidRoleIdentity,
    RolesNotSeparated,
    InvalidProvisioningAuthority,
    InvalidLifecycle,
    InvalidExternalApprovalStatus,
    ExternalApprovalNotDeclared,
    ExternalApprovalRejected
}

/// <summary>No result contains input values or grants any execution capability.</summary>
internal sealed record ChatPostgresTestReadinessResult(
    bool StructuralMetadataValid,
    bool ExternalLifecycleApprovalDeclared,
    ChatTestReadinessError Error)
{
    internal bool HasCompleteDeclaredMetadata => StructuralMetadataValid && ExternalLifecycleApprovalDeclared
        && Error == ChatTestReadinessError.None;
    internal bool RequiresIndependentEnvironmentVerification => true;
    internal bool ExternalLifecycleApprovalVerified => false;
    internal bool MayConnect => false;
    internal bool MayProvision => false;
    internal bool MayMigrate => false;
    internal bool MayCleanup => false;
    internal bool MayExecuteSql => false;
    internal bool MayChangePrivileges => false;
    internal bool MayDeleteTestData => false;

    public override string ToString() => nameof(ChatPostgresTestReadinessResult);
}

/// <summary>
/// Pure, deterministic metadata validation in the test assembly only. No configuration reads,
/// DNS, connection-string parsing, SQL, I/O or execution hooks. Loopback and declared approval
/// cannot establish instance identity, privileges, disposability or create/drop permission.
/// </summary>
internal static class ChatPostgresTestReadinessPolicy
{
    internal static ChatPostgresTestReadinessResult Validate(ChatPostgresTestMetadata? metadata)
    {
        var declared = metadata?.ExternalLifecycleApproval == ChatTestLifecycleApproval.Approved;
        ChatPostgresTestReadinessResult Invalid(ChatTestReadinessError error) => new(false, declared, error);

        if (metadata is null)
            return Invalid(ChatTestReadinessError.MissingMetadata);
        if (metadata.Environment != ChatTestEnvironment.IsolatedDisposable)
            return Invalid(ChatTestReadinessError.InvalidEnvironment);

        // Canonical literals only: reject hostnames, multiple hosts, URLs, sockets and embedded ports.
        // This is a conservative structural boundary, not a safety assertion about loopback.
        if (metadata.Host is not ("127.0.0.1" or "::1") || metadata.Port is not (>= 1 and <= 65535))
            return Invalid(ChatTestReadinessError.InvalidEndpoint);
        if (!SafeIdentifier(metadata.Database))
            return Invalid(ChatTestReadinessError.InvalidDatabaseIdentity);
        if (metadata.DatabaseEnvironment != ChatTestEnvironment.IsolatedDisposable)
            return Invalid(ChatTestReadinessError.ConflictingDatabaseClassification);
        if (!SafeIdentifier(metadata.MigrationRole) || !SafeIdentifier(metadata.RuntimeRole))
            return Invalid(ChatTestReadinessError.InvalidRoleIdentity);
        if (string.Equals(metadata.MigrationRole, metadata.RuntimeRole, StringComparison.Ordinal))
            return Invalid(ChatTestReadinessError.RolesNotSeparated);
        if (!SafeIdentifier(metadata.ProvisioningAuthority))
            return Invalid(ChatTestReadinessError.InvalidProvisioningAuthority);
        if (metadata.Lifecycle != ChatTestLifecycle.ExternallyManagedDisposable)
            return Invalid(ChatTestReadinessError.InvalidLifecycle);

        return metadata.ExternalLifecycleApproval switch
        {
            ChatTestLifecycleApproval.Approved => new(true, true, ChatTestReadinessError.None),
            null or ChatTestLifecycleApproval.Pending => new(true, false, ChatTestReadinessError.ExternalApprovalNotDeclared),
            ChatTestLifecycleApproval.Rejected => new(true, false, ChatTestReadinessError.ExternalApprovalRejected),
            _ => Invalid(ChatTestReadinessError.InvalidExternalApprovalStatus)
        };
    }

    private static bool SafeIdentifier(string? value)
    {
        // Reject ambiguous/quoted/noncanonical identifiers, delimiters, controls and labelled secrets.
        // Arbitrary unlabelled secrets cannot be identified offline; callers must supply non-secret metadata.
        if (value is null || value.Length is < 1 or > 63 || value[0] is not (>= 'a' and <= 'z'))
            return false;
        if (value.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            return false;

        return !value.Split('_').Any(part => part is "prod" or "production" or "stage" or "staging"
            or "shared" or "supabase" or "postgres" or "template0" or "template1"
            or "password" or "pwd" or "secret" or "token" or "credentials" or "credential"
            or "connection" or "connectionstring" or "host" or "server" or "username");
    }
}
