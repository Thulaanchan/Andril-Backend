using System.Collections.Immutable;
using System.Data;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andril.IntegrationTests.Communication.Chat;

public sealed class ChatPostgresSecurityContractTests
{
    [Fact]
    public void Closed_proposal_has_no_execution_or_grant_approval_and_matches_the_existing_ef_table_set()
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        var result = ChatPostgresSecurityContract.Validate(design);
        Assert.True(result.ClosedBaselineValid);
        Assert.Equal(ChatSecurityContractError.None, result.Error);
        AssertNoAuthority(result);
        // Reuse the existing connection-rejecting private metadata fixture, not a second EF mapping.
        var type = typeof(ChatPersistenceModelTests).GetNestedType("CandidateChatContext", BindingFlags.NonPublic)!;
        using var reference = Assert.IsAssignableFrom<DbContext>(Activator.CreateInstance(type, nonPublic: true));
        Assert.Equal(reference.Model.GetEntityTypes().Select(entity => $"{entity.GetSchema()}.{entity.GetTableName()}").Order(),
            design.Tables.Select(table => $"{table.Schema}.{table.Table}").Order());
        Assert.Equal(ConnectionState.Closed, reference.Database.GetDbConnection().State);
        var connections = type.GetProperty("Connections", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reference)!;
        Assert.Equal(0, connections.GetType().GetProperty("OpenAttempts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connections));
    }

    [Fact]
    public void Review_sql_requires_enable_force_and_restrictive_false_on_each_actual_table_without_grants()
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        var sql = ChatPostgresSecurityContract.ProposedReviewSql(design)!;
        Assert.Equal(3, Regex.Matches(sql, @"ALTER TABLE andril_chat\.\w+ ENABLE ROW LEVEL SECURITY;").Count);
        Assert.Equal(3, Regex.Matches(sql, @"ALTER TABLE andril_chat\.\w+ FORCE ROW LEVEL SECURITY;").Count);
        Assert.Equal(3, Regex.Matches(sql, @"CREATE POLICY chat_contract_closed ON andril_chat\.\w+").Count);
        foreach (var table in design.Tables)
        {
            Assert.Contains($"ALTER TABLE andril_chat.{table.Table} ENABLE ROW LEVEL SECURITY;", sql, StringComparison.Ordinal);
            Assert.Contains($"ALTER TABLE andril_chat.{table.Table} FORCE ROW LEVEL SECURITY;", sql, StringComparison.Ordinal);
            Assert.Contains($"REVOKE ALL ON TABLE andril_chat.{table.Table} FROM PUBLIC;", sql, StringComparison.Ordinal);
            Assert.Contains($"CREATE POLICY chat_contract_closed ON andril_chat.{table.Table}\n"
                + "AS RESTRICTIVE FOR ALL TO \"<CHAT_RUNTIME>\" USING (false) WITH CHECK (false);", sql, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("AS PERMISSIVE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("USING (true)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("WITH CHECK (true)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\nGRANT ", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(design.Runtime!.Identity!, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_design_is_rejected() => Rejected(null, ChatSecurityContractError.MissingDesign);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Omitting_any_table_duplicating_it_or_disabling_its_rls_or_force_fails(int index)
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        Rejected(design with { Tables = design.Tables.RemoveAt(index) }, ChatSecurityContractError.InvalidTableSet);
        Rejected(design with { Tables = design.Tables.SetItem(index, design.Tables[(index + 1) % 3]) }, ChatSecurityContractError.InvalidTableSet);
        Rejected(ChangeTable(design, index, design.Tables[index] with { EnableRls = false }), ChatSecurityContractError.RlsNotEnabled);
        Rejected(ChangeTable(design, index, design.Tables[index] with { ForceRls = false }), ChatSecurityContractError.RlsNotForced);
        Rejected(ChangeTable(design, index, design.Tables[index] with { PublicPrivileges = ChatDeclaredPrivilege.ReadChat }), ChatSecurityContractError.PublicAccess);
        Rejected(ChangeTable(design, index, design.Tables[index] with { Policies = [] }), ChatSecurityContractError.InvalidPolicy);
        Rejected(ChangeTable(design, index, design.Tables[index] with { Policies = default }), ChatSecurityContractError.InvalidPolicy);
    }

    [Theory]
    [InlineData("andril_identity", "private_module_profiles")]
    [InlineData("andril_hobbies", "synthetic_table")]
    [InlineData("andril_chat", "notifications")]
    [InlineData("public", "messages")]
    [InlineData("andril_chat", "messages;DROP")]
    [InlineData(null, "messages")]
    [InlineData("andril_chat", null)]
    public void Unrelated_ambiguous_or_missing_table_identity_cannot_enter_the_contract_or_sql(string? schema, string? table)
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        Rejected(ChangeTable(design, 0, design.Tables[0] with { Schema = schema, Table = table }), ChatSecurityContractError.InvalidTableSet);
        Rejected(design with { Tables = design.Tables.Add(design.Tables[0] with { Schema = schema, Table = table }) }, ChatSecurityContractError.InvalidTableSet);
    }

    [Fact]
    public void Default_empty_or_null_table_collections_fail_closed()
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        Rejected(design with { Tables = default }, ChatSecurityContractError.InvalidTableSet);
        Rejected(design with { Tables = [] }, ChatSecurityContractError.InvalidTableSet);
        Rejected(design with { Tables = design.Tables.SetItem(0, null!) }, ChatSecurityContractError.InvalidTableSet);
    }

    public static IEnumerable<object[]> RuntimePrivileges()
    {
        foreach (var privilege in Enum.GetValues<ChatDeclaredPrivilege>().Where(value => value != ChatDeclaredPrivilege.None)
            .Append((ChatDeclaredPrivilege)(1L << 60)))
            foreach (var inherited in new[] { false, true })
                yield return [(long)privilege, inherited];
    }

    [Theory]
    [MemberData(nameof(RuntimePrivileges))]
    public void Every_direct_or_inherited_runtime_privilege_including_owner_bypass_ddl_history_and_cross_workstream_access_fails(long privilege, bool inherited)
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        var role = inherited ? design.Runtime! with { Inherited = (ChatDeclaredPrivilege)privilege }
            : design.Runtime! with { Direct = (ChatDeclaredPrivilege)privilege };
        Rejected(design with { Runtime = role }, ChatSecurityContractError.InvalidRuntimePrivileges);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Every_role_pair_must_be_separated_and_each_responsibility_must_exist(int pair)
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        var shared = pair switch
        {
            0 => design with { Runtime = design.Runtime! with { Identity = design.Migration!.Identity } },
            1 => design with { Provisioner = design.Provisioner! with { Identity = design.Runtime!.Identity } },
            _ => design with { Provisioner = design.Provisioner! with { Identity = design.Migration!.Identity } }
        };
        Rejected(shared, ChatSecurityContractError.RolesNotSeparated);
        Rejected(pair switch { 0 => design with { Migration = null }, 1 => design with { Runtime = null }, _ => design with { Provisioner = null } },
            ChatSecurityContractError.InvalidRoleIdentity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("MixedCase")]
    [InlineData("one,two")]
    [InlineData("name\0tail")]
    [InlineData("synthetic_password_value")]
    [InlineData("Host=sensitive.invalid;Username=private_identity;Password=synthetic_secret")]
    public void Unsafe_role_metadata_is_rejected_without_diagnostic_disclosure(string? identity)
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        Rejected(design with { Migration = design.Migration! with { Identity = identity } }, ChatSecurityContractError.InvalidRoleIdentity);
        Rejected(design with { Runtime = design.Runtime! with { Identity = identity } }, ChatSecurityContractError.InvalidRoleIdentity);
        Rejected(design with { Provisioner = design.Provisioner! with { Identity = identity } }, ChatSecurityContractError.InvalidRoleIdentity);
    }

    [Theory]
    [MemberData(nameof(RuntimePrivileges))]
    public void Migration_and_provisioning_cannot_acquire_unrelated_or_implicit_privileges(long privilege, bool inherited)
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        var value = (ChatDeclaredPrivilege)privilege;
        if (value is not (ChatDeclaredPrivilege.MigrateChat or ChatDeclaredPrivilege.WriteMigrationHistory))
        {
            var migration = inherited ? design.Migration! with { Inherited = value } : design.Migration! with { Direct = value };
            Rejected(design with { Migration = migration }, ChatSecurityContractError.InvalidMigrationBoundary);
        }
        var provisioner = inherited ? design.Provisioner! with { Inherited = value } : design.Provisioner! with { Direct = value };
        Rejected(design with { Provisioner = provisioner }, ChatSecurityContractError.InvalidProvisioningBoundary);
    }

    [Fact]
    public void Migration_runtime_use_and_unverified_or_non_disposable_provisioning_are_rejected()
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        Rejected(design with { MigrationRestrictedToChat = false }, ChatSecurityContractError.InvalidMigrationBoundary);
        Rejected(design with { MigrationUsedForRuntime = true }, ChatSecurityContractError.InvalidMigrationBoundary);
        Rejected(design with { ProvisionerDisposableOnly = false }, ChatSecurityContractError.InvalidProvisioningBoundary);
        Rejected(design with { IndependentTargetVerificationRequired = false }, ChatSecurityContractError.InvalidProvisioningBoundary);
        Rejected(design with { GrantsLifecycleAuthority = true }, ChatSecurityContractError.LifecycleAuthorityDeclared);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Any_permissive_policy_including_additional_false_policies_fails_for_every_command(int command)
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        for (var index = 0; index < design.Tables.Length; index++)
        {
            var table = design.Tables[index];
            var policy = table.Policies[0] with { Permissive = true, Command = (ChatPolicyCommand)command };
            Rejected(ChangeTable(design, index, table with { Policies = [policy] }), ChatSecurityContractError.InvalidPolicy);
            Rejected(ChangeTable(design, index, table with { Policies = table.Policies.Add(policy) }), ChatSecurityContractError.InvalidPolicy);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(99)]
    public void Allowing_membership_client_claim_or_cross_module_predicates_fail_both_using_and_with_check(int predicate)
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        for (var index = 0; index < design.Tables.Length; index++)
        {
            var table = design.Tables[index];
            Rejected(ChangeTable(design, index, table with { Policies = [table.Policies[0] with { Using = (ChatPolicyPredicate)predicate }] }),
                ChatSecurityContractError.InvalidPolicy);
            Rejected(ChangeTable(design, index, table with { Policies = [table.Policies[0] with { WithCheck = (ChatPolicyPredicate)predicate }] }),
                ChatSecurityContractError.InvalidPolicy);
        }
    }

    [Fact]
    public void Partial_command_other_audience_and_null_policy_cannot_replace_the_all_command_runtime_guard()
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        var table = design.Tables[0];
        foreach (var command in new[] { 0, 2, 3, 4, 5, 99 })
            Rejected(ChangeTable(design, 0, table with { Policies = [table.Policies[0] with { Command = (ChatPolicyCommand)command }] }), ChatSecurityContractError.InvalidPolicy);
        foreach (var audience in new[] { 0, 2, 3, 99 })
            Rejected(ChangeTable(design, 0, table with { Policies = [table.Policies[0] with { Audience = (ChatPolicyAudience)audience }] }), ChatSecurityContractError.InvalidPolicy);
        Rejected(ChangeTable(design, 0, table with { Policies = [null!] }), ChatSecurityContractError.InvalidPolicy);
    }

    [Fact]
    public void Proposed_reads_and_message_inserts_are_not_granted_and_cannot_expand_to_updates_deletes_or_unknown_privileges()
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        foreach (var proposal in new[] { ChatDeclaredPrivilege.None, ChatDeclaredPrivilege.ReadChat, ChatDeclaredPrivilege.InsertMessages })
        {
            var result = ChatPostgresSecurityContract.Validate(design with { ProposedRuntimeDml = proposal });
            Assert.True(result.ClosedBaselineValid);
            AssertNoAuthority(result);
        }
        foreach (var proposal in new[] { ChatDeclaredPrivilege.UpdateChat, ChatDeclaredPrivilege.DeleteChat, ChatDeclaredPrivilege.AllPrivileges, (ChatDeclaredPrivilege)(1L << 60) })
            Rejected(design with { ProposedRuntimeDml = proposal }, ChatSecurityContractError.InvalidDmlProposal);
    }

    [Fact]
    public void Diagnostics_are_fixed_and_review_sql_does_not_echo_abstract_role_identities()
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        Assert.Equal(nameof(ChatPostgresSecurityDesign), design.ToString());
        Assert.Equal(nameof(ChatSecurityRole), design.Runtime!.ToString());
        Assert.Equal(nameof(ChatProtectedTable), design.Tables[0].ToString());
        Assert.Equal(nameof(ChatRlsPolicy), design.Tables[0].Policies[0].ToString());
        Assert.Equal(nameof(ChatSecurityContractResult), ChatPostgresSecurityContract.Validate(design).ToString());
        var renamed = design with { Runtime = design.Runtime! with { Identity = "synthetic_private_identity" } };
        Assert.Equal(ChatPostgresSecurityContract.ProposedReviewSql(design), ChatPostgresSecurityContract.ProposedReviewSql(renamed));
        Assert.DoesNotContain("synthetic_private_identity", ChatPostgresSecurityContract.ProposedReviewSql(renamed)!, StringComparison.Ordinal);
    }

    [Fact]
    public void Immutable_input_and_repeated_concurrent_validation_remain_deterministic_and_test_only()
    {
        var design = ChatPostgresSecurityContract.ClosedProposal();
        var copy = design with { };
        var result = ChatPostgresSecurityContract.Validate(design);
        var sql = ChatPostgresSecurityContract.ProposedReviewSql(design);
        Parallel.For(0, 64, _ =>
        {
            Assert.Equal(result, ChatPostgresSecurityContract.Validate(design));
            Assert.Equal(sql, ChatPostgresSecurityContract.ProposedReviewSql(design));
        });
        Assert.Equal(copy, design);
        Assert.True(typeof(ChatPostgresSecurityContract).IsNotPublic);
        Assert.Equal(typeof(ChatPostgresSecurityContractTests).Assembly, typeof(ChatPostgresSecurityContract).Assembly);
        Assert.Equal(typeof(ImmutableArray<ChatProtectedTable>), typeof(ChatPostgresSecurityDesign).GetProperty("Tables")!.PropertyType);
        // No syscall interception is claimed. Source review establishes that validation/review-text
        // generation uses only supplied immutable metadata, strings and enum/collection operations.
    }

    private static ChatPostgresSecurityDesign ChangeTable(ChatPostgresSecurityDesign design, int index, ChatProtectedTable table) =>
        design with { Tables = design.Tables.SetItem(index, table) };

    private static void Rejected(ChatPostgresSecurityDesign? design, ChatSecurityContractError error)
    {
        var result = ChatPostgresSecurityContract.Validate(design);
        Assert.False(result.ClosedBaselineValid);
        Assert.Equal(error, result.Error);
        Assert.Null(ChatPostgresSecurityContract.ProposedReviewSql(design));
        AssertNoAuthority(result);
    }

    private static void AssertNoAuthority(ChatSecurityContractResult result)
    {
        Assert.True(result.RequiresIndependentVerification);
        Assert.False(result.LiveEnforcementVerified);
        Assert.False(result.SpecificGrantsApproved);
        Assert.False(result.MayConnect);
        Assert.False(result.MayExecuteSql);
        Assert.False(result.MayMigrate);
        Assert.False(result.MayProvision);
        Assert.False(result.MayCleanup);
    }
}
