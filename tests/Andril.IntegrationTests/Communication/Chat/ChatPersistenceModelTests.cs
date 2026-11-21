using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using Andril.API.Extensions;
using Andril.Communication.Interfaces;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.IntegrationTests.Communication.Chat;

/// <summary>Inspects candidate Npgsql metadata only; never executes PostgreSQL.</summary>
public sealed class ChatPersistenceModelTests
{
    private const string RecordNamespace = "Andril.Persistence.Repositories.Communication.";
    private const string ModelConnection = "Host=127.0.0.1;Database=andril_chat_model_only;Username=unused;Pooling=false";
    private static readonly Assembly PersistenceAssembly = typeof(AccountsDbContext).Assembly;
    private static readonly string[] RecordNames = ["ChatConversationRecord", "ChatParticipantRecord", "ChatMessageRecord"];

    [Fact]
    public void Private_context_contains_only_three_internal_chat_records_and_never_connects()
    {
        using var context = new CandidateChatContext();
        var model = Model(context);
        Assert.True(typeof(CandidateChatContext).IsNestedPrivate);
        Assert.Equal(3, model.GetEntityTypes().Count());
        Assert.Equal(3, model.GetRelationalModel().Tables.Count());
        Assert.Empty(model.GetSequences());
        foreach (var entity in model.GetEntityTypes())
        {
            Assert.Contains(entity.ClrType.Name, RecordNames);
            Assert.False(entity.ClrType.IsPublic);
            Assert.Empty(entity.ClrType.GetConstructors());
            Assert.Empty(entity.GetNavigations());
            Assert.DoesNotContain(entity.GetProperties(), property => property.IsShadowProperty());
        }
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Equal(0, context.Connections.OpenAttempts);
    }

    [Theory]
    [InlineData("ChatConversationRecord", "conversations", "Module,ConversationId", "module,conversation_id")]
    [InlineData("ChatParticipantRecord", "conversation_participants", "Module,ConversationId,ModuleProfileId", "module,conversation_id,module_profile_id")]
    [InlineData("ChatMessageRecord", "messages", "MessageId,Module,ConversationId,SenderModuleProfileId,Text,MoodTone,CreatedAtUtc", "message_id,module,conversation_id,sender_module_profile_id,text,mood_tone,created_at_utc")]
    public void Tables_columns_nullability_and_generation_are_explicit(string record, string table, string fields, string columns)
    {
        using var context = new CandidateChatContext();
        var entity = Entity(Model(context), record);
        Assert.Equal("andril_chat", entity.GetSchema());
        Assert.Equal(table, entity.GetTableName());
        var names = fields.Split(',');
        Assert.Equal(names.Order(), entity.GetProperties().Select(property => property.Name).Order());
        var store = StoreObjectIdentifier.Table(table, "andril_chat");
        var columnNames = columns.Split(',');
        for (var i = 0; i < names.Length; i++)
        {
            var property = entity.FindProperty(names[i])!;
            Assert.Equal(columnNames[i], property.GetColumnName(store));
            Assert.Equal(names[i] == "MoodTone", property.IsNullable);
            Assert.Null(property.GetDefaultValueSql());
            Assert.Null(property.GetComputedColumnSql());
            if (property.ClrType == typeof(Guid))
            {
                Assert.Equal("uuid", property.GetColumnType());
                Assert.Equal(ValueGenerated.Never, property.ValueGenerated);
            }
        }
    }

    [Theory]
    [InlineData("ChatConversationRecord", "Module,ConversationId", "pk_chat_conversations")]
    [InlineData("ChatParticipantRecord", "Module,ConversationId,ModuleProfileId", "pk_chat_conversation_participants")]
    [InlineData("ChatMessageRecord", "MessageId", "pk_chat_messages")]
    public void Primary_keys_have_exact_candidate_shapes(string record, string fields, string name)
    {
        using var context = new CandidateChatContext();
        var entity = Entity(Model(context), record);
        var key = Assert.Single(entity.GetKeys());
        Assert.Same(key, entity.FindPrimaryKey());
        Assert.Equal(fields.Split(','), key.Properties.Select(property => property.Name));
        Assert.Equal(name, key.GetName());
    }

    [Theory]
    [InlineData("ChatParticipantRecord", "Module,ConversationId", "ChatConversationRecord", "Module,ConversationId", "fk_chat_participants_conversation")]
    [InlineData("ChatMessageRecord", "Module,ConversationId", "ChatConversationRecord", "Module,ConversationId", "fk_chat_messages_conversation")]
    [InlineData("ChatMessageRecord", "Module,ConversationId,SenderModuleProfileId", "ChatParticipantRecord", "Module,ConversationId,ModuleProfileId", "fk_chat_messages_sender")]
    public void Foreign_keys_include_module_and_conversation_without_cascades(string record, string fields, string principal, string principalFields, string name)
    {
        using var context = new CandidateChatContext();
        var model = Model(context);
        var foreignKey = Assert.Single(Entity(model, record).GetForeignKeys(), key => key.GetConstraintName() == name);
        Assert.Equal(fields.Split(','), foreignKey.Properties.Select(property => property.Name));
        Assert.Equal(RecordNamespace + principal, foreignKey.PrincipalEntityType.Name);
        Assert.Equal(principalFields.Split(','), foreignKey.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.NoAction, foreignKey.DeleteBehavior);
        Assert.True(foreignKey.IsRequired);
        Assert.False(foreignKey.IsUnique);
        Assert.Equal(3, model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()).Count());
        Assert.All(model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()), key => Assert.Equal("Module", key.Properties[0].Name));
    }

    [Theory]
    [InlineData(ChatModule.Learning, "learning")]
    [InlineData(ChatModule.Hobbies, "hobbies")]
    [InlineData(ChatModule.Social, "social")]
    public void Module_converter_round_trips_exact_canonical_values(ChatModule module, string stored)
    {
        using var context = new CandidateChatContext();
        foreach (var entity in Model(context).GetEntityTypes())
        {
            var property = entity.FindProperty("Module")!;
            var converter = property.GetValueConverter()!;
            Assert.Equal(typeof(string), converter.ProviderClrType);
            Assert.Equal("character varying(16)", property.GetColumnType());
            Assert.Equal(16, property.GetMaxLength());
            Assert.Equal(stored, converter.ConvertToProvider(module));
            Assert.Equal(module, converter.ConvertFromProvider(stored));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void Module_converter_rejects_undefined_enums(int value)
    {
        using var context = new CandidateChatContext();
        foreach (var entity in Model(context).GetEntityTypes())
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => entity.FindProperty("Module")!.GetValueConverter()!.ConvertToProvider((ChatModule)value));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Learning")]
    [InlineData("HOBBIES")]
    [InlineData("Social")]
    [InlineData("social ")]
    [InlineData(" learning")]
    [InlineData("unapproved-private-module")]
    public void Module_converter_rejects_noncanonical_storage_without_echoing_input(string value)
    {
        using var context = new CandidateChatContext();
        foreach (var entity in Model(context).GetEntityTypes())
        {
            var error = Assert.Throws<ArgumentException>(() => entity.FindProperty("Module")!.GetValueConverter()!.ConvertFromProvider(value));
            if (!string.IsNullOrWhiteSpace(value))
            {
                Assert.DoesNotContain(value, error.Message);
            }
        }
    }

    [Fact]
    public void Required_module_metadata_handles_null_which_ef_does_not_pass_to_converter()
    {
        using var context = new CandidateChatContext();
        foreach (var entity in Model(context).GetEntityTypes())
        {
            var property = entity.FindProperty("Module")!;
            Assert.False(property.IsNullable);
            Assert.Null(property.GetValueConverter()!.ConvertFromProvider(null));
        }
    }

    [Fact]
    public void Indexes_preserve_scoped_history_order_and_profile_lookup_without_retry_uniqueness()
    {
        using var context = new CandidateChatContext();
        var model = Model(context);
        var history = Assert.Single(Entity(model, "ChatMessageRecord").GetIndexes(), index => index.GetDatabaseName() == "ix_chat_messages_history");
        Assert.Equal(new[] { "Module", "ConversationId", "CreatedAtUtc", "MessageId" }, history.Properties.Select(property => property.Name));
        Assert.Equal(new[] { false, false, true, true }, history.IsDescending);
        var lookup = Assert.Single(Entity(model, "ChatParticipantRecord").GetIndexes(), index => index.GetDatabaseName() == "ix_chat_participants_profile_conversation");
        Assert.Equal(new[] { "Module", "ModuleProfileId", "ConversationId" }, lookup.Properties.Select(property => property.Name));
        Assert.True(lookup.IsDescending is null || (lookup.IsDescending.Count == 3 && lookup.IsDescending.All(descending => !descending)));
        foreach (var index in model.GetEntityTypes().SelectMany(entity => entity.GetIndexes()))
        {
            Assert.False(index.IsUnique);
            Assert.Equal("Module", index.Properties[0].Name);
            Assert.DoesNotContain(index.Properties, property => property.Name is "Text" or "MoodTone");
        }
    }

    [Fact]
    public void Only_module_vocabulary_and_current_all_null_tone_checks_are_declared()
    {
        using var context = new CandidateChatContext();
        var model = Model(context);
        var checks = model.GetEntityTypes().SelectMany(entity => entity.GetCheckConstraints()).ToArray();
        Assert.Equal(4, checks.Length);
        foreach (var name in new[] { "ck_chat_conversation_module", "ck_chat_participant_module", "ck_chat_message_module" })
        {
            Assert.Equal("module IN ('learning', 'hobbies', 'social')", Assert.Single(checks, check => check.Name == name).Sql);
        }
        Assert.Equal("mood_tone IS NULL", Assert.Single(checks, check => check.Name == "ck_chat_message_mood_tone_pending").Sql);
        var message = Entity(model, "ChatMessageRecord");
        Assert.Null(message.FindProperty("Text")!.GetMaxLength());
        Assert.Null(message.FindProperty("MoodTone")!.GetMaxLength());
        Assert.Equal("text", message.FindProperty("Text")!.GetColumnType());
        Assert.Equal("timestamp with time zone", message.FindProperty("CreatedAtUtc")!.GetColumnType());
        Assert.Equal(ValueGenerated.Never, message.FindProperty("CreatedAtUtc")!.ValueGenerated);
    }

    [Fact]
    public void Existing_contexts_do_not_include_candidate_chat_records()
    {
        using var accounts = new AccountsDbContext(Options<AccountsDbContext>());
        using var gallery = new HobbiesGalleryDbContext(Options<HobbiesGalleryDbContext>());
        using var map = new HobbiesMapDbContext(Options<HobbiesMapDbContext>());
        using var notifications = new NotificationsDbContext(Options<NotificationsDbContext>());
        foreach (var context in new DbContext[] { accounts, gallery, map, notifications })
        {
            Assert.DoesNotContain(context.Model.GetEntityTypes(), entity => RecordNames.Contains(entity.ClrType.Name));
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        }
        Assert.DoesNotContain(PersistenceAssembly.GetTypes(), type => type.Name == "ChatDbContext");
    }

    [Fact]
    public void Existing_service_registration_does_not_register_candidate_chat_persistence()
    {
        var services = new ServiceCollection();
        services.AddAccounts(new ConfigurationBuilder().Build());
        services.AddNotifications();
        Assert.DoesNotContain(services, descriptor => IsCandidate(descriptor.ServiceType) || IsCandidate(descriptor.ImplementationType));
    }

    [Fact]
    public void Generated_ddl_contains_exactly_the_three_scoped_candidate_tables()
    {
        using var context = new CandidateChatContext();
        var sql = GenerateDdl(context);
        var tables = DdlTables(sql);
        var identifiers = context.GetService<ISqlGenerationHelper>();
        var entities = Model(context).GetEntityTypes().ToArray();

        Assert.Equal(3, tables.Count);
        Assert.Equal(new[] { "conversation_participants", "conversations", "messages" }, entities.Select(entity => entity.GetTableName()!).Order());
        Assert.All(entities, entity => Assert.Equal("andril_chat", entity.GetSchema()));
        Assert.Equal(entities.Select(entity => TableIdentifier(identifiers, entity)).Order(), tables.Keys.Order());
    }

    [Fact]
    public void Generated_ddl_primary_keys_match_each_tables_candidate_identity()
    {
        using var context = new CandidateChatContext();
        var tables = DdlTables(GenerateDdl(context));
        var identifiers = context.GetService<ISqlGenerationHelper>();
        foreach (var entity in Model(context).GetEntityTypes())
        {
            var body = tables[TableIdentifier(identifiers, entity)];
            var key = entity.FindPrimaryKey()!;
            var actual = Assert.Single(DdlLines(body), line => line.Contains(" PRIMARY KEY (", StringComparison.Ordinal));
            Assert.Equal($"CONSTRAINT {identifiers.DelimitIdentifier(key.GetName()!)} PRIMARY KEY ({ColumnIdentifiers(identifiers, entity, key.Properties)})", actual);
            Assert.DoesNotMatch(@"\bUNIQUE\b", body);
        }
    }

    [Fact]
    public void Generated_ddl_foreign_keys_preserve_scoped_principals_without_cascades()
    {
        using var context = new CandidateChatContext();
        var tables = DdlTables(GenerateDdl(context));
        var identifiers = context.GetService<ISqlGenerationHelper>();
        foreach (var entity in Model(context).GetEntityTypes())
        {
            var actual = DdlLines(tables[TableIdentifier(identifiers, entity)])
                .Where(line => line.Contains(" FOREIGN KEY (", StringComparison.Ordinal)).ToArray();
            var foreignKeys = entity.GetForeignKeys().ToArray();
            Assert.Equal(foreignKeys.Length, actual.Length);
            foreach (var foreignKey in foreignKeys)
            {
                Assert.Equal(DeleteBehavior.NoAction, foreignKey.DeleteBehavior);
                var constraint = identifiers.DelimitIdentifier(foreignKey.GetConstraintName()!);
                var statement = Assert.Single(actual, line => line.StartsWith($"CONSTRAINT {constraint} FOREIGN KEY (", StringComparison.Ordinal));
                var expected = $"CONSTRAINT {constraint} FOREIGN KEY ({ColumnIdentifiers(identifiers, entity, foreignKey.Properties)}) "
                    + $"REFERENCES {TableIdentifier(identifiers, foreignKey.PrincipalEntityType)} "
                    + $"({ColumnIdentifiers(identifiers, foreignKey.PrincipalEntityType, foreignKey.PrincipalKey.Properties)})";
                // PostgreSQL's default is NO ACTION; the provider may omit that clause.
                Assert.True(statement == expected || statement == expected + " ON DELETE NO ACTION",
                    "Generated foreign key must match its scoped principal and preserve NO ACTION deletion.");
            }
        }
    }

    [Fact]
    public void Generated_ddl_columns_preserve_candidate_types_required_fields_and_nullable_tone()
    {
        using var context = new CandidateChatContext();
        var tables = DdlTables(GenerateDdl(context));
        var identifiers = context.GetService<ISqlGenerationHelper>();
        foreach (var entity in Model(context).GetEntityTypes())
        {
            var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            var expected = entity.GetProperties().Select(property =>
                $"{identifiers.DelimitIdentifier(property.GetColumnName(store)!)} {property.GetColumnType()}"
                + (property.IsNullable ? "" : " NOT NULL"));
            var actual = DdlLines(tables[TableIdentifier(identifiers, entity)])
                .Where(line => !line.StartsWith("CONSTRAINT ", StringComparison.Ordinal));
            Assert.Equal(expected.Order(), actual.Order());
        }
    }

    [Fact]
    public void Generated_ddl_checks_remain_on_their_tables_with_canonical_modules_and_null_only_tone()
    {
        using var context = new CandidateChatContext();
        var tables = DdlTables(GenerateDdl(context));
        var identifiers = context.GetService<ISqlGenerationHelper>();
        foreach (var entity in Model(context).GetEntityTypes())
        {
            var expected = entity.GetCheckConstraints().Select(check =>
                $"CONSTRAINT {identifiers.DelimitIdentifier(check.Name!)} CHECK ({check.Sql})");
            var actual = DdlLines(tables[TableIdentifier(identifiers, entity)])
                .Where(line => line.Contains(" CHECK (", StringComparison.Ordinal));
            Assert.Equal(expected.Order(), actual.Order());
        }
    }

    [Fact]
    public void Generated_ddl_indexes_preserve_scoped_ordering_without_retry_uniqueness()
    {
        using var context = new CandidateChatContext();
        var sql = GenerateDdl(context);
        var identifiers = context.GetService<ISqlGenerationHelper>();
        var expected = Model(context).GetEntityTypes().SelectMany(entity => entity.GetIndexes().Select(index =>
        {
            Assert.False(index.IsUnique);
            var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            var columns = index.Properties.Select((property, position) =>
                identifiers.DelimitIdentifier(property.GetColumnName(store)!)
                + (index.IsDescending is { } descending && (descending.Count == 0 || descending[position]) ? " DESC" : ""));
            return $"CREATE INDEX {identifiers.DelimitIdentifier(index.GetDatabaseName()!)} ON {TableIdentifier(identifiers, entity)} ({string.Join(", ", columns)});";
        }));
        var actual = Regex.Matches(sql, @"^CREATE (?:UNIQUE )?INDEX [^\r\n]+;(?=\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => match.Value).ToArray();
        Assert.Equal(Regex.Matches(sql, @"\bCREATE\s+(?:UNIQUE\s+)?INDEX\b", RegexOptions.CultureInvariant).Count, actual.Length);
        Assert.Equal(expected.Order(), actual.Order());
    }

    private static string GenerateDdl(CandidateChatContext context)
    {
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
        try
        {
            // Generate text in memory only. Do not execute or export deployment SQL.
            return context.Database.GenerateCreateScript();
        }
        finally
        {
            Assert.Equal(0, context.Connections.OpenAttempts);
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        }
    }

    private static Dictionary<string, string> DdlTables(string sql)
    {
        var matches = Regex.Matches(sql, @"^CREATE TABLE (?<table>[^\r\n(]+)\s*\((?<body>.*?)^\);",
            RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.Equal(Regex.Matches(sql, @"\bCREATE\s+TABLE\b", RegexOptions.CultureInvariant).Count, matches.Count);
        return matches.ToDictionary(match => match.Groups["table"].Value.Trim(), match => match.Groups["body"].Value, StringComparer.Ordinal);
    }

    private static string[] DdlLines(string body) => body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim().TrimEnd(',')).Where(line => line.Length != 0).ToArray();

    private static string TableIdentifier(ISqlGenerationHelper identifiers, IEntityType entity) =>
        identifiers.DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());

    private static string ColumnIdentifiers(ISqlGenerationHelper identifiers, IEntityType entity, IReadOnlyList<IProperty> properties)
    {
        var store = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        return string.Join(", ", properties.Select(property => identifiers.DelimitIdentifier(property.GetColumnName(store)!)));
    }

    private static bool IsCandidate(Type? type) => type is not null && (RecordNames.Contains(type.Name) || type == typeof(CandidateChatContext));
    private static DbContextOptions<T> Options<T>() where T : DbContext => new DbContextOptionsBuilder<T>()
        .UseNpgsql(ModelConnection).AddInterceptors(new RejectConnections()).Options;
    private static IModel Model(DbContext context) => context.GetService<IDesignTimeModel>().Model;
    private static IEntityType Entity(IModel model, string record) => model.FindEntityType(RecordNamespace + record)!;

    private sealed class CandidateChatContext : DbContext
    {
        internal RejectConnections Connections { get; }

        internal CandidateChatContext() : this(new RejectConnections()) { }

        private CandidateChatContext(RejectConnections interceptor)
            : base(new DbContextOptionsBuilder<CandidateChatContext>().UseNpgsql(ModelConnection).AddInterceptors(interceptor).Options)
        {
            Connections = interceptor;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Explicit allowlist, matching existing reflection tests for internal records.
            foreach (var record in RecordNames)
            {
                var recordType = PersistenceAssembly.GetType(RecordNamespace + record, throwOnError: true)!;
                var configurationType = PersistenceAssembly.GetType("Andril.Persistence.Configurations." + record + "Configuration", throwOnError: true)!;
                var configuration = Activator.CreateInstance(configurationType, nonPublic: true)!;
                typeof(ModelBuilder).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Single(method => method.Name == nameof(ModelBuilder.ApplyConfiguration) && method.IsGenericMethodDefinition
                        && method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 1)
                    .MakeGenericMethod(recordType).Invoke(modelBuilder, [configuration]);
            }
        }
    }

    private sealed class RejectConnections : DbConnectionInterceptor
    {
        internal int OpenAttempts { get; private set; }

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            OpenAttempts++;
            throw new InvalidOperationException("Database connections are forbidden in candidate metadata tests.");
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            OpenAttempts++;
            throw new InvalidOperationException("Database connections are forbidden in candidate metadata tests.");
        }
    }
}
