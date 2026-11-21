using System.Collections.Immutable;

namespace Andril.IntegrationTests.Communication.Chat;

[Flags]
internal enum ChatDeclaredPrivilege : long
{
    None = 0,
    ReadChat = 1L << 0,
    InsertMessages = 1L << 1,
    UpdateChat = 1L << 2,
    DeleteChat = 1L << 3,
    TruncateChat = 1L << 4,
    AllPrivileges = 1L << 5,
    CreateChatObjects = 1L << 6,
    CreateSchemas = 1L << 7,
    MigrateChat = 1L << 8,
    WriteMigrationHistory = 1L << 9,
    OwnChatTables = 1L << 10,
    Superuser = 1L << 11,
    BypassRls = 1L << 12,
    AdministerRoles = 1L << 13,
    AssumeOwner = 1L << 14,
    AssumeMigration = 1L << 15,
    AssumeProvisioner = 1L << 16,
    AccountsAccess = 1L << 17,
    HobbiesAccess = 1L << 18,
    NotificationAccess = 1L << 19,
    ModifyOutsideChat = 1L << 20,
    ServeRuntimeRequests = 1L << 21,
    ProvisionOrCleanup = 1L << 22
}

internal enum ChatPolicyPredicate { DenyAll = 1, AllowAll, MembershipOnly, ClientClaims, CrossModuleException }
internal enum ChatPolicyCommand { All = 1, Select, Insert, Update, Delete }
internal enum ChatPolicyAudience { Runtime = 1, Public, Other }

/// <summary>Declared direct and transitive/inherited capabilities, not a server privilege inspection.</summary>
internal sealed record ChatSecurityRole(string? Identity, ChatDeclaredPrivilege Direct, ChatDeclaredPrivilege Inherited)
{
    public override string ToString() => nameof(ChatSecurityRole);
}

internal sealed record ChatRlsPolicy(bool Permissive, ChatPolicyCommand Command, ChatPolicyAudience Audience,
    ChatPolicyPredicate Using, ChatPolicyPredicate WithCheck)
{
    public override string ToString() => nameof(ChatRlsPolicy);
}

internal sealed record ChatProtectedTable(string? Schema, string? Table, bool EnableRls, bool ForceRls,
    ChatDeclaredPrivilege PublicPrivileges, ImmutableArray<ChatRlsPolicy> Policies)
{
    public override string ToString() => nameof(ChatProtectedTable);
}

/// <summary>
/// Immutable, non-secret test design only. ProposedRuntimeDml is a proposal, never an approved grant.
/// Role names and inherited privileges are assertions requiring independent owner verification.
/// </summary>
internal sealed record ChatPostgresSecurityDesign(ChatSecurityRole? Migration, ChatSecurityRole? Runtime,
    ChatSecurityRole? Provisioner, bool MigrationRestrictedToChat, bool MigrationUsedForRuntime,
    bool ProvisionerDisposableOnly, bool IndependentTargetVerificationRequired, bool GrantsLifecycleAuthority,
    ChatDeclaredPrivilege ProposedRuntimeDml, ImmutableArray<ChatProtectedTable> Tables)
{
    public override string ToString() => nameof(ChatPostgresSecurityDesign);
}

internal enum ChatSecurityContractError
{
    None, MissingDesign, InvalidRoleIdentity, RolesNotSeparated, InvalidMigrationBoundary,
    InvalidRuntimePrivileges, InvalidProvisioningBoundary, LifecycleAuthorityDeclared,
    InvalidDmlProposal, InvalidTableSet, RlsNotEnabled, RlsNotForced, PublicAccess, InvalidPolicy
}

internal sealed record ChatSecurityContractResult(bool ClosedBaselineValid, ChatSecurityContractError Error)
{
    internal bool RequiresIndependentVerification => true;
    internal bool LiveEnforcementVerified => false;
    internal bool SpecificGrantsApproved => false;
    internal bool MayConnect => false;
    internal bool MayExecuteSql => false;
    internal bool MayMigrate => false;
    internal bool MayProvision => false;
    internal bool MayCleanup => false;

    public override string ToString() => nameof(ChatSecurityContractResult);
}

/// <summary>
/// Pure offline contract checks and non-deployed review text. No SQL parser/executor, connection,
/// configuration, credential, environment, file, DNS or network access. Not a runtime security layer.
/// Exact roles, GRANT SQL, ownership, PostgreSQL version and live enforcement remain pending M2.
/// </summary>
internal static class ChatPostgresSecurityContract
{
    internal static ChatPostgresSecurityDesign ClosedProposal() => new(
        new("abstract_migration", ChatDeclaredPrivilege.MigrateChat | ChatDeclaredPrivilege.WriteMigrationHistory, ChatDeclaredPrivilege.None),
        new("abstract_runtime", ChatDeclaredPrivilege.None, ChatDeclaredPrivilege.None),
        new("abstract_provisioner", ChatDeclaredPrivilege.None, ChatDeclaredPrivilege.None),
        MigrationRestrictedToChat: true, MigrationUsedForRuntime: false, ProvisionerDisposableOnly: true,
        IndependentTargetVerificationRequired: true, GrantsLifecycleAuthority: false,
        ProposedRuntimeDml: ChatDeclaredPrivilege.ReadChat | ChatDeclaredPrivilege.InsertMessages,
        Tables: [ClosedTable("conversations"), ClosedTable("conversation_participants"), ClosedTable("messages")]);

    internal static ChatSecurityContractResult Validate(ChatPostgresSecurityDesign? design)
    {
        static ChatSecurityContractResult Invalid(ChatSecurityContractError error) => new(false, error);
        if (design is null)
            return Invalid(ChatSecurityContractError.MissingDesign);
        if (!SafeRole(design.Migration?.Identity) || !SafeRole(design.Runtime?.Identity) || !SafeRole(design.Provisioner?.Identity))
            return Invalid(ChatSecurityContractError.InvalidRoleIdentity);

        var migration = design.Migration!;
        var runtime = design.Runtime!;
        var provisioner = design.Provisioner!;
        if (migration.Identity == runtime.Identity || migration.Identity == provisioner.Identity || runtime.Identity == provisioner.Identity)
            return Invalid(ChatSecurityContractError.RolesNotSeparated);
        if (!design.MigrationRestrictedToChat || design.MigrationUsedForRuntime
            || ((migration.Direct | migration.Inherited) & ~(ChatDeclaredPrivilege.MigrateChat | ChatDeclaredPrivilege.WriteMigrationHistory)) != 0)
            return Invalid(ChatSecurityContractError.InvalidMigrationBoundary);
        // The closed baseline approves no runtime grants. DML proposals below cannot change this.
        if ((runtime.Direct | runtime.Inherited) != ChatDeclaredPrivilege.None)
            return Invalid(ChatSecurityContractError.InvalidRuntimePrivileges);
        if (!design.ProvisionerDisposableOnly || !design.IndependentTargetVerificationRequired
            || (provisioner.Direct | provisioner.Inherited) != ChatDeclaredPrivilege.None)
            return Invalid(ChatSecurityContractError.InvalidProvisioningBoundary);
        if (design.GrantsLifecycleAuthority)
            return Invalid(ChatSecurityContractError.LifecycleAuthorityDeclared);
        if ((design.ProposedRuntimeDml & ~(ChatDeclaredPrivilege.ReadChat | ChatDeclaredPrivilege.InsertMessages)) != 0)
            return Invalid(ChatSecurityContractError.InvalidDmlProposal);

        if (design.Tables.IsDefault || design.Tables.Length != 3
            || design.Tables.Any(table => table is null || table.Schema != "andril_chat"
                || table.Table is not ("conversations" or "conversation_participants" or "messages"))
            || design.Tables.Select(table => table.Table).Distinct(StringComparer.Ordinal).Count() != 3)
            return Invalid(ChatSecurityContractError.InvalidTableSet);
        foreach (var table in design.Tables)
        {
            if (!table.EnableRls)
                return Invalid(ChatSecurityContractError.RlsNotEnabled);
            if (!table.ForceRls)
                return Invalid(ChatSecurityContractError.RlsNotForced);
            if (table.PublicPrivileges != ChatDeclaredPrivilege.None)
                return Invalid(ChatSecurityContractError.PublicAccess);
            // Require one explicit restrictive false guard. No extra policy, even permissive false,
            // belongs to this closed baseline. Never simulate this as live PostgreSQL enforcement.
            if (table.Policies.IsDefault || table.Policies.Length != 1 || table.Policies[0] is not
                {
                    Permissive: false,
                    Command: ChatPolicyCommand.All, Audience: ChatPolicyAudience.Runtime,
                    Using: ChatPolicyPredicate.DenyAll, WithCheck: ChatPolicyPredicate.DenyAll
                })
                return Invalid(ChatSecurityContractError.InvalidPolicy);
        }
        return new(true, ChatSecurityContractError.None);
    }

    internal static string? ProposedReviewSql(ChatPostgresSecurityDesign? design)
    {
        if (!Validate(design).ClosedBaselineValid)
            return null;
        // Emit only allowlisted object names and an unresolved role placeholder, never input identities.
        // These statements assume future approved objects exist. No deployment script is saved.
        return "-- TEST-ONLY REVIEW; NOT DEPLOYED. Role, ownership, target version and GRANT SQL pending M2.\n"
            + "REVOKE ALL ON SCHEMA andril_chat FROM PUBLIC;\n"
            + string.Join("\n", design!.Tables.OrderBy(table => table.Table, StringComparer.Ordinal).Select(table =>
                $"ALTER TABLE andril_chat.{table.Table} ENABLE ROW LEVEL SECURITY;\n"
                + $"ALTER TABLE andril_chat.{table.Table} FORCE ROW LEVEL SECURITY;\n"
                + $"REVOKE ALL ON TABLE andril_chat.{table.Table} FROM PUBLIC;\n"
                + $"CREATE POLICY chat_contract_closed ON andril_chat.{table.Table}\n"
                + "AS RESTRICTIVE FOR ALL TO \"<CHAT_RUNTIME>\" USING (false) WITH CHECK (false);"));
    }

    private static ChatProtectedTable ClosedTable(string table) => new("andril_chat", table, true, true,
        ChatDeclaredPrivilege.None, [new(false, ChatPolicyCommand.All, ChatPolicyAudience.Runtime,
            ChatPolicyPredicate.DenyAll, ChatPolicyPredicate.DenyAll)]);

    private static bool SafeRole(string? identity) => identity is { Length: >= 1 and <= 63 }
        && identity[0] is >= 'a' and <= 'z'
        && identity.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
        && !identity.Split('_').Any(part => part is "password" or "pwd" or "secret" or "token" or "credentials");
}
