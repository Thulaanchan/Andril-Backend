using System.Data;
using System.Data.Common;
using System.Reflection;
using Andril.Common.Accounts.Profiles;
using Andril.Communication.Chat;
using Andril.Communication.Interfaces;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.IntegrationTests.Communication.Chat;

/// <summary>
/// Actual EF/source compatibility only. No proposed relationship is added to either model.
/// Migration operations/target models are inspected as metadata, never applied or scripted.
/// Database deployment, deletion/retention decisions and authorization remain unverified.
/// </summary>
public sealed class ChatProfileForeignKeyCompatibilityTests
{
    private const string ProfileName = "Andril.Common.Accounts.Profiles.PrivateModuleProfile";
    private const string ChatNamespace = "Andril.Persistence.Repositories.Communication.";
    private const string PhotoMigration = "20261120152336_AddPrivateProfilePhotos";

    [Fact]
    public void Actual_accounts_profile_maps_required_uuid_identity_owner_and_canonical_module_storage()
    {
        using var models = new OfflineModels();
        var profile = Profile(models.AccountsModel);
        Assert.Equal("private_module_profiles", profile.GetTableName());
        Assert.Equal("andril_identity", profile.GetSchema());
        AssertColumn(profile.FindProperty("Id")!, "id", "uuid", typeof(Guid));
        AssertColumn(profile.FindProperty("AccountId")!, "account_id", "uuid", typeof(Guid));
        var module = profile.FindProperty("Module")!;
        AssertColumn(module, "module", "character varying(16)", typeof(string));
        Assert.Equal(16, module.GetMaxLength());
        Assert.Null(module.GetValueConverter());
        Assert.Equal(new[] { "Id" }, profile.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal("pk_private_module_profiles", profile.FindPrimaryKey()!.GetName());
        Assert.Contains(profile.GetCheckConstraints(), check => check.Name == "ck_profile_module"
            && check.Sql == "module IN ('learning', 'hobbies', 'social')");
    }

    [Fact]
    public void Actual_accounts_model_and_snapshot_have_the_id_module_principal_key_used_by_photos()
    {
        using var models = new OfflineModels();
        var profile = Profile(models.AccountsModel);
        var key = CompositeKey(profile);
        Assert.NotNull(key);
        Assert.Equal(new[] { "Id", "Module" }, key.Properties.Select(property => property.Name));
        Assert.Equal(new[] { "id", "module" }, key.Properties.Select(Column));
        Assert.False(key.IsPrimaryKey());
        var photo = models.AccountsModel.GetEntityTypes().Single(entity => entity.GetTableName() == "private_profile_photos");
        var reference = Assert.Single(photo.GetForeignKeys(), foreignKey => foreignKey.PrincipalEntityType == profile);
        Assert.Same(key, reference.PrincipalKey);
        var snapshot = models.Migrations.ModelSnapshot;
        Assert.NotNull(snapshot);
        Assert.NotNull(CompositeKey(Profile(snapshot.Model)));
        // HasPrincipalKey creates alternate-key metadata even without an explicit HasAlternateKey line.
    }

    [Fact]
    public void First_seven_migrations_lack_the_composite_constraint_and_eighth_adds_it_in_exact_column_order()
    {
        using var models = new OfflineModels();
        var entries = models.Migrations.Migrations.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(8, entries.Length);
        Assert.Equal("20261116000200_AddPrivateProfileFields", entries[6].Key);
        Assert.Equal(PhotoMigration, entries[7].Key);
        foreach (var entry in entries.Take(7))
        {
            var migration = models.Migrations.CreateMigration(entry.Value, models.Accounts.Database.ProviderName!);
            Assert.DoesNotContain(migration.UpOperations.OfType<AddUniqueConstraintOperation>(), ProfileCompositeConstraint);
            Assert.DoesNotContain(migration.UpOperations.OfType<CreateTableOperation>(), table =>
                table.Schema == "andril_identity" && table.Name == "private_module_profiles"
                && table.UniqueConstraints.Any(constraint => constraint.Columns.SequenceEqual(new[] { "id", "module" })));
            var earlierProfile = migration.TargetModel.FindEntityType(ProfileName);
            Assert.Null(earlierProfile is null ? null : CompositeKey(earlierProfile));
        }
        var eighth = models.Migrations.CreateMigration(entries[7].Value, models.Accounts.Database.ProviderName!);
        var operation = Assert.Single(eighth.UpOperations.OfType<AddUniqueConstraintOperation>(), ProfileCompositeConstraint);
        Assert.Equal("AK_private_module_profiles_id_module", operation.Name);
        Assert.NotNull(CompositeKey(Profile(eighth.TargetModel)));
        Assert.Contains(eighth.DownOperations.OfType<DropUniqueConstraintOperation>(), operation =>
            operation.Schema == "andril_identity" && operation.Table == "private_module_profiles"
            && operation.Name == "AK_private_module_profiles_id_module");
        // This is source operation evidence, not GetAppliedMigrations or live catalog inspection.
    }

    [Theory]
    [InlineData("ChatParticipantRecord", "ModuleProfileId", "module_profile_id")]
    [InlineData("ChatMessageRecord", "SenderModuleProfileId", "sender_module_profile_id")]
    public void Actual_chat_candidate_columns_match_the_actual_m2_key_but_deployment_remains_unverified(
        string record, string identifier, string column)
    {
        using var models = new OfflineModels();
        var entity = models.ChatModel.FindEntityType(ChatNamespace + record)!;
        var dependent = new[] { entity.FindProperty(identifier)!, entity.FindProperty("Module")! };
        AssertColumn(dependent[0], column, "uuid", typeof(Guid));
        AssertColumn(dependent[1], "module", "character varying(16)", typeof(ChatModule));
        var key = CompositeKey(Profile(models.AccountsModel))!;
        Assert.Equal(CandidateVerdict.SourceCompatibleDeploymentUnverified,
            CompareCandidate(dependent, key, key.Properties));
        Assert.Equal(16, dependent[1].GetMaxLength());
    }

    [Theory]
    [InlineData(ChatModule.Learning, ModuleNames.Learning)]
    [InlineData(ChatModule.Hobbies, ModuleNames.Hobbies)]
    [InlineData(ChatModule.Social, ModuleNames.Social)]
    public void Actual_chat_converters_match_m2_canonical_values_and_preserve_distinct_module_key_components(ChatModule module, string stored)
    {
        using var models = new OfflineModels();
        Assert.True(ModuleNames.IsValid(stored));
        Assert.False(ModuleNames.IsValid(stored.ToUpperInvariant()));
        Assert.Equal(typeof(string), ProviderType(Profile(models.AccountsModel).FindProperty("Module")!));
        foreach (var entity in models.ChatModel.GetEntityTypes())
        {
            var property = entity.FindProperty("Module")!;
            Assert.False(property.IsNullable);
            var converter = property.GetValueConverter()!;
            Assert.Equal(stored, converter.ConvertToProvider(module));
            Assert.Equal(module, converter.ConvertFromProvider(stored));
            foreach (var other in Enum.GetValues<ChatModule>().Where(other => other != module))
                Assert.NotEqual(converter.ConvertToProvider(module), converter.ConvertToProvider(other));
        }
        // Distinct module columns/conversions are structural prerequisites, not proof of profile access.
    }

    [Fact]
    public void Existing_chat_keys_relationships_and_no_action_rules_remain_chat_only()
    {
        using var models = new OfflineModels();
        var conversation = models.ChatModel.FindEntityType(ChatNamespace + "ChatConversationRecord")!;
        var participant = models.ChatModel.FindEntityType(ChatNamespace + "ChatParticipantRecord")!;
        var message = models.ChatModel.FindEntityType(ChatNamespace + "ChatMessageRecord")!;
        Assert.Equal(3, models.ChatModel.GetEntityTypes().Count());
        Assert.Equal(new[] { "Module", "ConversationId" }, conversation.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(new[] { "Module", "ConversationId", "ModuleProfileId" }, participant.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(new[] { "MessageId" }, message.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Empty(conversation.GetForeignKeys());
        var participantConversation = Assert.Single(participant.GetForeignKeys());
        AssertRelationship(participantConversation, conversation, ["Module", "ConversationId"], ["Module", "ConversationId"]);
        Assert.Equal(2, message.GetForeignKeys().Count());
        AssertRelationship(Assert.Single(message.GetForeignKeys(), key => key.PrincipalEntityType == conversation),
            conversation, ["Module", "ConversationId"], ["Module", "ConversationId"]);
        AssertRelationship(Assert.Single(message.GetForeignKeys(), key => key.PrincipalEntityType == participant),
            participant, ["Module", "ConversationId", "SenderModuleProfileId"], ["Module", "ConversationId", "ModuleProfileId"]);
        Assert.All(models.ChatModel.GetEntityTypes(), entity =>
        {
            Assert.Equal("andril_chat", entity.GetSchema());
            Assert.DoesNotContain(entity.GetForeignKeys(), key => key.PrincipalEntityType.GetSchema() == "andril_identity");
        });
    }

    [Fact]
    public void Existing_sender_path_could_make_a_direct_m2_sender_fk_redundant_only_after_a_participant_fk_is_approved()
    {
        using var models = new OfflineModels();
        var message = models.ChatModel.FindEntityType(ChatNamespace + "ChatMessageRecord")!;
        var sender = Assert.Single(message.GetForeignKeys(), key => key.PrincipalEntityType.GetTableName() == "conversation_participants");
        var participant = sender.PrincipalEntityType;
        var candidate = new[] { participant.FindProperty("ModuleProfileId")!, participant.FindProperty("Module")! };
        var key = CompositeKey(Profile(models.AccountsModel))!;
        Assert.Equal(CandidateVerdict.SourceCompatibleDeploymentUnverified, CompareCandidate(candidate, key, key.Properties));
        Assert.Same(message.FindProperty("SenderModuleProfileId"), sender.Properties[2]);
        Assert.Same(participant.FindProperty("ModuleProfileId"), sender.PrincipalKey.Properties[2]);
        Assert.Equal("Module", sender.Properties[0].Name);
        Assert.Equal("Module", sender.PrincipalKey.Properties[0].Name);
        Assert.DoesNotContain(participant.GetForeignKeys(), foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(PrivateModuleProfile));
        // No transitive M2 integrity exists yet. Adding a redundant direct FK and its lifecycle is an owner decision.
    }

    [Fact]
    public void M2_cascades_are_observed_source_behavior_not_an_approved_chat_deletion_or_retention_policy()
    {
        using var models = new OfflineModels();
        var profile = Profile(models.AccountsModel);
        var account = Assert.Single(profile.GetForeignKeys());
        Assert.Equal("accounts", account.PrincipalEntityType.GetTableName());
        Assert.Equal(new[] { "AccountId" }, account.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Cascade, account.DeleteBehavior);
        var photo = models.AccountsModel.GetEntityTypes().Single(entity => entity.GetTableName() == "private_profile_photos");
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(photo.GetForeignKeys(), key => key.PrincipalEntityType == profile).DeleteBehavior);
        Assert.All(models.ChatModel.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()), key => Assert.Equal(DeleteBehavior.NoAction, key.DeleteBehavior));
    }

    [Theory]
    [InlineData("ChatParticipantRecord", "ModuleProfileId")]
    [InlineData("ChatMessageRecord", "SenderModuleProfileId")]
    public void Missing_reordered_wrong_owner_or_incompatible_actual_metadata_never_produces_compatibility_evidence(string record, string identifier)
    {
        using var models = new OfflineModels();
        var entity = models.ChatModel.FindEntityType(ChatNamespace + record)!;
        var dependent = new[] { entity.FindProperty(identifier)!, entity.FindProperty("Module")! };
        var profile = Profile(models.AccountsModel);
        var key = CompositeKey(profile)!;
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate(null, key, key.Properties));
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate(dependent, null, key.Properties));
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate(dependent, key, null));
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate([], key, key.Properties));
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate([dependent[0]], key, key.Properties));
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate(dependent.Reverse().ToArray(), key, key.Properties));
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate(dependent, key, key.Properties.Reverse().ToArray()));
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate(dependent, profile.FindPrimaryKey(), profile.FindPrimaryKey()!.Properties));
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate([entity.FindProperty("ConversationId")!, dependent[1]], key, key.Properties));
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate(dependent, key, [profile.FindProperty("AccountId")!, profile.FindProperty("Module")!]));
        var text = models.ChatModel.FindEntityType(ChatNamespace + "ChatMessageRecord")!.FindProperty("Text")!;
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate([text, dependent[1]], key, key.Properties));
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate([dependent[0], text], key, key.Properties));
        Assert.Equal(CandidateVerdict.Unverified, CompareCandidate([null!, dependent[1]], key, key.Properties));
    }

    [Fact]
    public async Task Structural_foreign_key_evidence_does_not_replace_the_existing_deny_all_authorizer()
    {
        foreach (var module in Enum.GetValues<ChatModule>())
            Assert.Null(await new DenyAllChatAccessAuthorizer().AuthorizeConversationAsync(module,
                Guid.Parse("11111111-1111-4111-8111-111111111111")));
    }

    private enum CandidateVerdict { Unverified, SourceCompatibleDeploymentUnverified }

    private static CandidateVerdict CompareCandidate(IReadOnlyList<IProperty>? dependent, IKey? principal, IReadOnlyList<IProperty>? referenced)
    {
        // No alternative model is built: negative cases select/reorder/omit properties of the actual models.
        if (dependent is not { Count: 2 } || principal is null || referenced is not { Count: 2 }
            || !principal.Properties.SequenceEqual(referenced) || principal.DeclaringEntityType.GetSchema() != "andril_identity"
            || principal.DeclaringEntityType.GetTableName() != "private_module_profiles")
            return CandidateVerdict.Unverified;
        var id = dependent[0];
        var module = dependent[1];
        if (id is null || module is null || referenced[0] is null || referenced[1] is null
            || id.Name is not ("ModuleProfileId" or "SenderModuleProfileId") || module.Name != "Module"
            || referenced[0].Name != "Id" || referenced[1].Name != "Module"
            || Column(referenced[0]) != "id" || Column(referenced[1]) != "module"
            || dependent.Concat(referenced).Any(property => property.IsNullable)
            || ProviderType(id) != typeof(Guid) || ProviderType(referenced[0]) != typeof(Guid)
            || id.GetColumnType() != "uuid" || referenced[0].GetColumnType() != "uuid"
            || ProviderType(module) != typeof(string) || ProviderType(referenced[1]) != typeof(string)
            || module.GetColumnType() != "character varying(16)" || referenced[1].GetColumnType() != "character varying(16)"
            || module.GetMaxLength() != 16 || referenced[1].GetMaxLength() != 16)
            return CandidateVerdict.Unverified;
        return CandidateVerdict.SourceCompatibleDeploymentUnverified;
    }

    private static IEntityType Profile(IModel model) => model.FindEntityType(ProfileName)
        ?? throw new InvalidOperationException("Authoritative profile metadata is missing.");

    private static IKey? CompositeKey(IEntityType entity) => entity.GetKeys().SingleOrDefault(key =>
        key.Properties.Select(property => property.Name).SequenceEqual(new[] { "Id", "Module" }));

    private static bool ProfileCompositeConstraint(AddUniqueConstraintOperation operation) =>
        operation.Schema == "andril_identity" && operation.Table == "private_module_profiles"
        && operation.Columns.SequenceEqual(new[] { "id", "module" });

    private static Type ProviderType(IProperty property) => property.GetValueConverter()?.ProviderClrType ?? property.ClrType;
    private static string? Column(IProperty property) => property.GetColumnName(StoreObjectIdentifier.Table(
        ((IEntityType)property.DeclaringType).GetTableName()!, ((IEntityType)property.DeclaringType).GetSchema()));

    private static void AssertColumn(IProperty property, string column, string storageType, Type clrType)
    {
        Assert.Equal(column, Column(property));
        Assert.Equal(storageType, property.GetColumnType());
        Assert.Equal(clrType, property.ClrType);
        Assert.False(property.IsNullable);
    }

    private static void AssertRelationship(IForeignKey key, IEntityType principal, string[] dependentNames, string[] principalNames)
    {
        Assert.Same(principal, key.PrincipalEntityType);
        Assert.Equal(dependentNames, key.Properties.Select(property => property.Name));
        Assert.Equal(principalNames, key.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.NoAction, key.DeleteBehavior);
        Assert.All(key.Properties, property => Assert.False(property.IsNullable));
    }

    private sealed class OfflineModels : IDisposable
    {
        private const string ModelOnly = "Host=127.0.0.1;Database=chat_fk_metadata_only;Username=unused;Pooling=false";
        private readonly RejectConnections connections = new();
        internal AccountsDbContext Accounts { get; }
        private DbContext Chat { get; }
        internal IModel AccountsModel => Accounts.GetService<IDesignTimeModel>().Model;
        internal IModel ChatModel => Chat.GetService<IDesignTimeModel>().Model;
        internal IMigrationsAssembly Migrations => Accounts.GetService<IMigrationsAssembly>();

        internal OfflineModels()
        {
            Accounts = new AccountsDbContext(new DbContextOptionsBuilder<AccountsDbContext>()
                .UseNpgsql(ModelOnly, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "andril_identity"))
                .AddInterceptors(connections).Options);
            var type = typeof(AccountsDbContext).Assembly.GetType(ChatNamespace + "ChatDbContext", throwOnError: true)!;
            var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(type))!;
            builder.UseNpgsql(ModelOnly).AddInterceptors(connections);
            Chat = Assert.IsAssignableFrom<DbContext>(Activator.CreateInstance(type,
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null, args: [builder.Options], culture: null));
        }

        public void Dispose()
        {
            try
            {
                Assert.Equal(0, connections.OpenAttempts);
                Assert.Equal(ConnectionState.Closed, Accounts.Database.GetDbConnection().State);
                Assert.Equal(ConnectionState.Closed, Chat.Database.GetDbConnection().State);
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
            throw new InvalidOperationException("Database connections are forbidden in offline FK compatibility tests.");
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            OpenAttempts++;
            throw new InvalidOperationException("Database connections are forbidden in offline FK compatibility tests.");
        }
    }
}
