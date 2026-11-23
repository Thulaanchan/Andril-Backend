using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Reflection;
using Andril.API.Extensions;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.IntegrationTests.Communication.Chat;

/// <summary>Offline provider/source evidence and separately labelled synthetic declarations, never deployment approval.</summary>
public sealed class ChatMigrationHistoryReadinessTests
{
    private const string HistoryTable = "__EFMigrationsHistory_Chat";
    private const string ChatSchema = "andril_chat";
    private const string InertTarget = "Host=127.0.0.1;Database=chat_history_metadata_only;Username=unused;Pooling=false";
    private const string Dependency = "20261120152336_AddPrivateProfilePhotos";
    private const string PreviousMigration = "20261116000200_AddPrivateProfileFields";
    private static Type ChatType => typeof(AccountsDbContext).Assembly.GetType(
        "Andril.Persistence.Repositories.Communication.ChatDbContext", throwOnError: true)!;

    [Fact]
    public void Real_npgsql_options_explicitly_configure_the_chat_history_without_creating_a_history_entity_or_migration()
    {
        using var fixture = new OfflineModels();
        var extension = Relational(fixture.Chat.GetService<IDbContextOptions>())!;
        Assert.True(ApprovedOfflineOptions(fixture.Chat.GetService<IDbContextOptions>()));
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", fixture.Chat.Database.ProviderName);
        Assert.Equal(HistoryTable, extension.MigrationsHistoryTableName);
        Assert.Equal(ChatSchema, extension.MigrationsHistoryTableSchema);
        Assert.Equal(InertTarget, extension.ConnectionString);
        Assert.Null(extension.Connection);
        var model = fixture.Chat.GetService<IDesignTimeModel>().Model;
        Assert.Equal(new[] { "conversation_participants", "conversations", "messages" },
            model.GetRelationalModel().Tables.Select(table => table.Name).Order());
        Assert.All(model.GetEntityTypes(), entity => Assert.Equal(ChatSchema, entity.GetSchema()));
        Assert.DoesNotContain(model.GetEntityTypes(), entity => entity.GetTableName() == HistoryTable);
        var migrations = fixture.Chat.GetService<IMigrationsAssembly>();
        Assert.Empty(migrations.Migrations);
        Assert.Null(migrations.ModelSnapshot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("public")]
    [InlineData("andril_identity")]
    [InlineData("andril_hobbies")]
    [InlineData("andril_notifications")]
    [InlineData("andril_chat ")]
    public void Wrong_or_missing_history_schema_fails_the_offline_contract(string? schema) =>
        Assert.False(ApprovedOfflineOptions(Configured(HistoryTable, schema).Options));

    [Theory]
    [InlineData("__EFMigrationsHistory")]
    [InlineData("__EFMigrationsHistory_Accounts")]
    [InlineData("__EFMigrationsHistory_Hobbies")]
    [InlineData("__EFMigrationsHistory_Notifications")]
    [InlineData("__EFMigrationsHistory_Chat ")]
    public void Wrong_or_shared_history_table_fails_the_offline_contract(string table) =>
        Assert.False(ApprovedOfflineOptions(Configured(table, ChatSchema).Options));

    [Fact]
    public void Missing_provider_or_explicit_history_cannot_use_shared_defaults()
    {
        Assert.False(ApprovedOfflineOptions(null));
        Assert.False(ApprovedOfflineOptions(new DbContextOptionsBuilder().Options));
        var defaults = new DbContextOptionsBuilder().UseNpgsql(InertTarget).Options;
        Assert.Null(Relational(defaults)!.MigrationsHistoryTableName);
        Assert.Null(Relational(defaults)!.MigrationsHistoryTableSchema);
        Assert.False(ApprovedOfflineOptions(defaults));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Host=127.0.0.1;Database=postgres;Username=unused")]
    [InlineData("Host=synthetic-production.invalid;Database=synthetic;Username=unused")]
    public void Missing_or_different_targets_are_rejected_without_loading_defaults_or_resolving_hosts(string? target)
    {
        var options = new DbContextOptionsBuilder().UseNpgsql(target,
            postgres => postgres.MigrationsHistoryTable(HistoryTable, ChatSchema)).Options;
        Assert.False(ApprovedOfflineOptions(options));
    }

    [Fact]
    public void Conflicting_provider_calls_are_checked_by_effective_options_not_assumed_declaration_provenance()
    {
        var builder = Configured(HistoryTable, ChatSchema);
        builder.UseNpgsql(InertTarget, postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "andril_identity"));
        Assert.False(ApprovedOfflineOptions(builder.Options));
        builder.UseNpgsql(InertTarget, postgres => postgres.MigrationsHistoryTable(HistoryTable, ChatSchema));
        Assert.True(ApprovedOfflineOptions(builder.Options));
        // The provider retains effective settings, not a security audit trail of previous calls.
        Assert.Equal(HistoryTable, Relational(builder.Options)!.MigrationsHistoryTableName);
    }

    [Fact]
    public void Chat_options_do_not_mutate_other_context_models_or_their_separately_supplied_history_options()
    {
        using var fixture = new OfflineModels();
        using var gallery = new HobbiesGalleryDbContext(new DbContextOptionsBuilder<HobbiesGalleryDbContext>()
            .UseNpgsql(InertTarget).AddInterceptors(fixture.Connections).Options);
        using var map = new HobbiesMapDbContext(new DbContextOptionsBuilder<HobbiesMapDbContext>()
            .UseNpgsql(InertTarget).AddInterceptors(fixture.Connections).Options);
        using var notifications = new NotificationsDbContext(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(InertTarget).AddInterceptors(fixture.Connections).Options);
        var accountsHistory = Relational(fixture.Accounts.GetService<IDbContextOptions>())!;
        Assert.Equal("__EFMigrationsHistory", accountsHistory.MigrationsHistoryTableName);
        Assert.Equal("andril_identity", accountsHistory.MigrationsHistoryTableSchema);
        foreach (var context in new DbContext[] { fixture.Accounts, gallery, map, notifications })
        {
            Assert.DoesNotContain(context.Model.GetEntityTypes(), entity => entity.GetSchema() == ChatSchema);
            Assert.False(ApprovedOfflineOptions(context.GetService<IDbContextOptions>()));
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        }
        foreach (var context in new DbContext[] { gallery, map, notifications })
        {
            var options = Relational(context.GetService<IDbContextOptions>())!;
            Assert.Null(options.MigrationsHistoryTableName);
            Assert.Null(options.MigrationsHistoryTableSchema);
        }
        Assert.True(ApprovedOfflineOptions(fixture.Chat.GetService<IDbContextOptions>()));
        // These non-Chat options are test inputs, not claims about deployed Hobbies/Notification history.
    }

    [Fact]
    public void Production_service_collection_still_has_no_chat_context_or_options_registration()
    {
        var services = new ServiceCollection();
        services.AddAccounts(new ConfigurationBuilder().Build());
        services.AddNotifications();
        Assert.DoesNotContain(services, descriptor => ReferencesChat(descriptor.ServiceType)
            || ReferencesChat(descriptor.ImplementationType) || ReferencesChat(descriptor.ImplementationInstance?.GetType()));
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService(ChatType));
        Assert.Null(provider.GetService(typeof(DbContextOptions<>).MakeGenericType(ChatType)));
    }

    [Fact]
    public void Actual_eighth_accounts_dependency_is_source_evidence_while_first_seven_missing_or_reordered_lists_fail()
    {
        using var fixture = new OfflineModels();
        var ids = MigrationIds(fixture);
        Assert.True(HasAccountsDependency(fixture, ids));
        Assert.Equal(Dependency, ids[7]);
        Assert.False(HasAccountsDependency(fixture, ids.Take(7).ToArray()));
        Assert.False(HasAccountsDependency(fixture, null));
        Assert.False(HasAccountsDependency(fixture, ids.Reverse().ToArray()));
        Assert.False(HasAccountsDependency(fixture, ids.Append(Dependency).ToArray()));
        var assembly = fixture.Accounts.GetService<IMigrationsAssembly>();
        foreach (var id in ids.Take(7))
        {
            var migration = assembly.CreateMigration(assembly.Migrations[id], fixture.Accounts.Database.ProviderName!);
            Assert.DoesNotContain(migration.UpOperations.OfType<AddUniqueConstraintOperation>(), CompositeConstraint);
        }
        // Only existing operation metadata is constructed/read. No migrator, scripts or database history queries.
    }

    [Fact]
    public void Synthetic_prerequisite_report_keeps_actual_source_facts_separate_from_unresolved_owner_decisions()
    {
        using var fixture = new OfflineModels();
        var report = Assess(fixture, MigrationIds(fixture), SyntheticTarget(), ChatPostgresSecurityContract.ClosedProposal(), PendingDecisions());
        Assert.Equal(Evidence.VerifiedFromSource, report.Findings[Gate.HistoryOptions]);
        Assert.Equal(Evidence.VerifiedFromSource, report.Findings[Gate.AccountsSource]);
        Assert.Equal(Evidence.VerifiedFromSource, report.Findings[Gate.OfflineRoleRlsContract]);
        Assert.Equal(Evidence.DeclaredButUnverified, report.Findings[Gate.TargetMetadata]);
        Assert.All(ExternalGates(), gate => Assert.Equal(Evidence.PendingOwnerDecision, report.Findings[gate]));
        AssertBlocked(report);
    }

    public static IEnumerable<object[]> UnresolvedEvidence()
    {
        foreach (var gate in ExternalGates())
            foreach (var status in new[] { Evidence.Blocked, Evidence.PendingOwnerDecision, Evidence.DeclaredButUnverified, Evidence.VerifiedFromSource })
                yield return [(int)gate, (int)status];
    }

    [Theory]
    [MemberData(nameof(UnresolvedEvidence))]
    public void Synthetic_missing_or_claimed_target_deployment_roles_rls_and_lifecycle_evidence_never_authorizes_execution(int gateValue, int statusValue)
    {
        using var fixture = new OfflineModels();
        var gate = (Gate)gateValue;
        var report = Assess(fixture, MigrationIds(fixture), SyntheticTarget(), ChatPostgresSecurityContract.ClosedProposal(),
            PendingDecisions().SetItem(gate, (Evidence)statusValue));
        var expected = (Evidence)statusValue == Evidence.VerifiedFromSource ? Evidence.DeclaredButUnverified : (Evidence)statusValue;
        Assert.Equal(expected, report.Findings[gate]);
        AssertBlocked(report);
        var missing = Assess(fixture, MigrationIds(fixture), SyntheticTarget(), ChatPostgresSecurityContract.ClosedProposal(), PendingDecisions().Remove(gate));
        Assert.Equal(Evidence.Blocked, missing.Findings[gate]);
        AssertBlocked(missing);
    }

    [Fact]
    public void Synthetic_disposable_and_approval_assertions_cannot_be_promoted_to_independent_verification()
    {
        using var fixture = new OfflineModels();
        var claims = ExternalGates().ToImmutableDictionary(gate => gate, _ => Evidence.VerifiedFromSource);
        var report = Assess(fixture, MigrationIds(fixture), SyntheticTarget(), ChatPostgresSecurityContract.ClosedProposal(), claims);
        Assert.All(ExternalGates(), gate => Assert.Equal(Evidence.DeclaredButUnverified, report.Findings[gate]));
        AssertBlocked(report);
        foreach (var target in new[] { SyntheticTarget() with { ProvisioningAuthority = null },
            SyntheticTarget() with { ExternalLifecycleApproval = null },
            SyntheticTarget() with { Environment = ChatTestEnvironment.SharedSupabase },
            SyntheticTarget() with { Environment = ChatTestEnvironment.Production } })
        {
            var invalid = Assess(fixture, MigrationIds(fixture), target, ChatPostgresSecurityContract.ClosedProposal(), claims);
            Assert.Equal(Evidence.Blocked, invalid.Findings[Gate.TargetMetadata]);
            AssertBlocked(invalid);
        }
    }

    [Fact]
    public void Synthetic_source_dependency_or_role_rls_failures_are_reported_as_blocked()
    {
        using var fixture = new OfflineModels();
        var ids = MigrationIds(fixture);
        var missing = Assess(fixture, ids.Take(7).ToArray(), SyntheticTarget(), ChatPostgresSecurityContract.ClosedProposal(), PendingDecisions());
        Assert.Equal(Evidence.Blocked, missing.Findings[Gate.AccountsSource]);
        AssertBlocked(missing);
        var design = ChatPostgresSecurityContract.ClosedProposal();
        foreach (var invalid in new[] { design with { Runtime = design.Runtime! with { Identity = design.Migration!.Identity } },
            design with { Tables = design.Tables.SetItem(0, design.Tables[0] with { ForceRls = false }) } })
        {
            var report = Assess(fixture, ids, SyntheticTarget(), invalid, PendingDecisions());
            Assert.Equal(Evidence.Blocked, report.Findings[Gate.OfflineRoleRlsContract]);
            AssertBlocked(report);
        }
    }

    [Fact]
    public void Configuration_and_classification_are_deterministic_and_diagnostics_do_not_echo_target_metadata()
    {
        Assert.Equal(Relational(Configured(HistoryTable, ChatSchema).Options)!.MigrationsHistoryTableName,
            Relational(Configured(HistoryTable, ChatSchema).Options)!.MigrationsHistoryTableName);
        using var fixture = new OfflineModels();
        var first = Assess(fixture, MigrationIds(fixture), SyntheticTarget(), ChatPostgresSecurityContract.ClosedProposal(), PendingDecisions());
        var second = Assess(fixture, MigrationIds(fixture), SyntheticTarget(), ChatPostgresSecurityContract.ClosedProposal(), PendingDecisions());
        Assert.Equal(first.Findings.OrderBy(pair => pair.Key), second.Findings.OrderBy(pair => pair.Key));
        Assert.Equal(nameof(ReadinessReport), first.ToString());
        AssertBlocked(first);
    }

    private enum Evidence { VerifiedFromSource = 1, DeclaredButUnverified, PendingOwnerDecision, Blocked }
    private enum Gate
    {
        HistoryOptions, AccountsSource, TargetMetadata, OfflineRoleRlsContract,
        TargetIdentityVerified, TargetOwnerApproval, DeployedCompositeConstraint, MigrationOwnerApproval,
        RoleGrantVerification, LiveRlsVerification, ProfileDeletion, AccountDeletion, MessageRetention,
        EvidencePreservation, CrossSchemaDeleteAction, RollbackOrder
    }

    private sealed record ReadinessReport(ImmutableDictionary<Gate, Evidence> Findings)
    {
        internal bool DeploymentBlocked => Findings.Values.Any(status => status != Evidence.VerifiedFromSource);
        internal bool MayConnect => false;
        internal bool MayExecuteMigration => false;
        internal bool MayProvision => false;
        internal bool MayCleanup => false;
        public override string ToString() => nameof(ReadinessReport);
    }

    // This private classifier is report/test evidence only, never an authorization service or lifecycle engine.
    private static ReadinessReport Assess(OfflineModels fixture, string[]? migrationIds, ChatPostgresTestMetadata? target,
        ChatPostgresSecurityDesign? security, ImmutableDictionary<Gate, Evidence> declarations)
    {
        var findings = ImmutableDictionary.CreateBuilder<Gate, Evidence>();
        findings[Gate.HistoryOptions] = ApprovedOfflineOptions(fixture.Chat.GetService<IDbContextOptions>()) ? Evidence.VerifiedFromSource : Evidence.Blocked;
        findings[Gate.AccountsSource] = HasAccountsDependency(fixture, migrationIds) ? Evidence.VerifiedFromSource : Evidence.Blocked;
        findings[Gate.TargetMetadata] = ChatPostgresTestReadinessPolicy.Validate(target).HasCompleteDeclaredMetadata ? Evidence.DeclaredButUnverified : Evidence.Blocked;
        findings[Gate.OfflineRoleRlsContract] = ChatPostgresSecurityContract.Validate(security).ClosedBaselineValid ? Evidence.VerifiedFromSource : Evidence.Blocked;
        foreach (var gate in ExternalGates())
        {
            findings[gate] = declarations.TryGetValue(gate, out var status) ? status switch
            {
                Evidence.PendingOwnerDecision => Evidence.PendingOwnerDecision,
                Evidence.VerifiedFromSource or Evidence.DeclaredButUnverified => Evidence.DeclaredButUnverified,
                _ => Evidence.Blocked
            } : Evidence.Blocked;
        }
        return new(findings.ToImmutable());
    }

    private static IEnumerable<Gate> ExternalGates() => Enum.GetValues<Gate>().Where(gate => (int)gate >= (int)Gate.TargetIdentityVerified);
    private static ImmutableDictionary<Gate, Evidence> PendingDecisions() => ExternalGates().ToImmutableDictionary(gate => gate, _ => Evidence.PendingOwnerDecision);
    private static ChatPostgresTestMetadata SyntheticTarget() => new(ChatTestEnvironment.IsolatedDisposable, "127.0.0.1", 15432,
        "synthetic_history_test", ChatTestEnvironment.IsolatedDisposable, "synthetic_migration", "synthetic_runtime",
        "synthetic_owner", ChatTestLifecycle.ExternallyManagedDisposable, ChatTestLifecycleApproval.Approved);

    private static bool HasAccountsDependency(OfflineModels fixture, string[]? ids)
    {
        if (ids is null || ids.Length < 8 || ids[6] != PreviousMigration || ids[7] != Dependency
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || !ids.SequenceEqual(ids.Order(StringComparer.Ordinal)))
            return false;
        var assembly = fixture.Accounts.GetService<IMigrationsAssembly>();
        if (!assembly.Migrations.TryGetValue(Dependency, out var type))
            return false;
        var migration = assembly.CreateMigration(type, fixture.Accounts.Database.ProviderName!);
        return migration.UpOperations.OfType<AddUniqueConstraintOperation>().Any(CompositeConstraint);
    }

    private static bool CompositeConstraint(AddUniqueConstraintOperation operation) => operation.Schema == "andril_identity"
        && operation.Table == "private_module_profiles" && operation.Name == "AK_private_module_profiles_id_module"
        && operation.Columns.SequenceEqual(new[] { "id", "module" });
    private static string[] MigrationIds(OfflineModels fixture) => fixture.Accounts.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).ToArray();
    private static bool ReferencesChat(Type? type) => type is not null && (type == ChatType || type.IsGenericType && type.GetGenericArguments().Any(ReferencesChat));
    private static DbContextOptionsBuilder Configured(string table, string? schema) => new DbContextOptionsBuilder().UseNpgsql(InertTarget,
        postgres => postgres.MigrationsHistoryTable(table, schema));
    private static RelationalOptionsExtension? Relational(IDbContextOptions options) => options.Extensions.OfType<RelationalOptionsExtension>().SingleOrDefault();

    private static bool ApprovedOfflineOptions(IDbContextOptions? options)
    {
        if (options is null)
            return false;
        var providers = options.Extensions.Where(extension => extension.Info.IsDatabaseProvider).ToArray();
        var relational = Relational(options);
        return providers.Length == 1 && providers[0].GetType().Assembly.GetName().Name == "Npgsql.EntityFrameworkCore.PostgreSQL"
            && relational is not null && relational.MigrationsHistoryTableName == HistoryTable
            && relational.MigrationsHistoryTableSchema == ChatSchema && relational.ConnectionString == InertTarget
            && relational.Connection is null;
    }

    private static void AssertBlocked(ReadinessReport report)
    {
        Assert.True(report.DeploymentBlocked);
        Assert.False(report.MayConnect);
        Assert.False(report.MayExecuteMigration);
        Assert.False(report.MayProvision);
        Assert.False(report.MayCleanup);
    }

    private sealed class OfflineModels : IDisposable
    {
        internal RejectConnections Connections { get; } = new();
        internal DbContext Chat { get; }
        internal AccountsDbContext Accounts { get; }

        internal OfflineModels()
        {
            var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(ChatType))!;
            builder.UseNpgsql(InertTarget, postgres => postgres.MigrationsHistoryTable(HistoryTable, ChatSchema)).AddInterceptors(Connections);
            Chat = Assert.IsAssignableFrom<DbContext>(Activator.CreateInstance(ChatType,
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null, args: [builder.Options], culture: null));
            Accounts = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>().UseNpgsql(InertTarget,
                postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "andril_identity")).AddInterceptors(Connections).Options);
        }

        public void Dispose()
        {
            try
            {
                Assert.Equal(0, Connections.OpenAttempts);
                Assert.Equal(ConnectionState.Closed, Chat.Database.GetDbConnection().State);
                Assert.Equal(ConnectionState.Closed, Accounts.Database.GetDbConnection().State);
            }
            finally
            {
                Chat.Dispose();
                Accounts.Dispose();
            }
        }
    }

    private sealed class RejectConnections : DbConnectionInterceptor
    {
        internal int OpenAttempts { get; private set; }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            OpenAttempts++;
            throw new InvalidOperationException("Connections are forbidden in offline history-readiness tests.");
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            OpenAttempts++;
            throw new InvalidOperationException("Connections are forbidden in offline history-readiness tests.");
        }
    }
}
