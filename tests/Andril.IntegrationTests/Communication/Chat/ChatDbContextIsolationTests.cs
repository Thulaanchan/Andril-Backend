using System.Data;
using System.Data.Common;
using System.Reflection;
using Andril.API.Extensions;
using Andril.Communication.Interfaces;
using Andril.Persistence.DbContexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Andril.IntegrationTests.Communication.Chat;

/// <summary>Offline model/options checks, not permission to deploy or use runtime Chat persistence.</summary>
public sealed class ChatDbContextIsolationTests
{
    private const string ContextName = "Andril.Persistence.Repositories.Communication.ChatDbContext";
    private const string RecordNamespace = "Andril.Persistence.Repositories.Communication.";
    private const string ModelConnection = "Host=127.0.0.1;Database=andril_chat_model_only;Username=unused;Pooling=false";
    private static readonly Assembly PersistenceAssembly = typeof(AccountsDbContext).Assembly;
    private static readonly string[] RecordNames = ["ChatConversationRecord", "ChatParticipantRecord", "ChatMessageRecord"];
    private static Type ContextType => PersistenceAssembly.GetType(ContextName, throwOnError: true)!;

    [Fact]
    public void Approved_context_is_internal_sealed_and_the_only_chat_context()
    {
        var context = Assert.Single(PersistenceAssembly.GetTypes(), type =>
            typeof(DbContext).IsAssignableFrom(type) && type.Name.Contains("Chat", StringComparison.Ordinal));
        Assert.Equal(ContextType, context);
        Assert.True(context.IsNotPublic);
        Assert.False(context.IsVisible);
        Assert.True(context.IsSealed);
        Assert.Empty(context.GetConstructors());
    }

    [Fact]
    public void Model_contains_only_the_three_existing_internal_chat_records()
    {
        using var fixture = new OfflineContext();
        var model = Model(fixture.Context);
        Assert.Equal(RecordNames.Order(), model.GetEntityTypes().Select(entity => entity.ClrType.Name).Order());
        Assert.Equal(3, model.GetRelationalModel().Tables.Count());
        Assert.Equal(new[] { "conversation_participants", "conversations", "messages" },
            model.GetRelationalModel().Tables.Select(table => table.Name).Order());
        Assert.All(model.GetEntityTypes(), entity =>
        {
            Assert.Equal(RecordNamespace, entity.ClrType.Namespace + ".");
            Assert.True(entity.ClrType.IsNotPublic);
            Assert.Equal("andril_chat", entity.GetSchema());
            Assert.All(entity.GetForeignKeys(), key => Assert.Contains(key.PrincipalEntityType.ClrType.Name, RecordNames));
        });
    }

    [Fact]
    public void Relational_model_and_generated_sql_match_the_existing_candidate_reference()
    {
        using var fixture = new OfflineContext();
        // Reuse the existing reference model; do not duplicate its configuration setup.
        var referenceType = typeof(ChatPersistenceModelTests).GetNestedType("CandidateChatContext", BindingFlags.NonPublic)!;
        using var reference = Assert.IsAssignableFrom<DbContext>(Activator.CreateInstance(referenceType, nonPublic: true));
        var actual = Model(fixture.Context);
        var expected = Model(reference);
        Assert.Empty(fixture.Context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(expected.GetRelationalModel(), actual.GetRelationalModel()));
        foreach (var entity in actual.GetEntityTypes())
        {
            var original = expected.FindEntityType(entity.Name)!;
            foreach (var property in entity.GetProperties())
            {
                Assert.Equal(original.FindProperty(property.Name)!.GetValueConverter()?.GetType(), property.GetValueConverter()?.GetType());
            }
        }
        // Compare provider output in memory, never an exported or executed deployment script.
        Assert.Equal(reference.Database.GenerateCreateScript(), fixture.Context.Database.GenerateCreateScript());
        Assert.Equal(ConnectionState.Closed, reference.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData(ChatModule.Learning, "learning")]
    [InlineData(ChatModule.Hobbies, "hobbies")]
    [InlineData(ChatModule.Social, "social")]
    public void Context_preserves_the_existing_canonical_module_converter(ChatModule module, string stored)
    {
        using var fixture = new OfflineContext();
        foreach (var entity in Model(fixture.Context).GetEntityTypes())
        {
            var property = entity.FindProperty("Module")!;
            Assert.False(property.IsNullable);
            var converter = property.GetValueConverter()!;
            Assert.Equal(stored, converter.ConvertToProvider(module));
            Assert.Equal(module, converter.ConvertFromProvider(stored));
            Assert.Throws<ArgumentOutOfRangeException>(() => converter.ConvertToProvider((ChatModule)0));
            Assert.Throws<ArgumentException>(() => converter.ConvertFromProvider("Learning"));
        }
    }

    [Fact]
    public void Proposed_history_location_is_test_provider_configuration_not_an_entity_mapping()
    {
        using var proposed = new OfflineContext();
        var options = HistoryOptions(proposed.Context);
        Assert.Equal("__EFMigrationsHistory_Chat", options.MigrationsHistoryTableName);
        Assert.Equal("andril_chat", options.MigrationsHistoryTableSchema);
        Assert.DoesNotContain(Model(proposed.Context).GetRelationalModel().Tables, table => table.Name == options.MigrationsHistoryTableName);

        using var defaults = new OfflineContext(configureProposedHistory: false);
        Assert.Null(HistoryOptions(defaults.Context).MigrationsHistoryTableName);
        Assert.Null(HistoryOptions(defaults.Context).MigrationsHistoryTableSchema);
        Assert.Equal(defaults.Context.Database.GenerateCreateScript(), proposed.Context.Database.GenerateCreateScript());
        // This demonstrates options only; no production factory or deployment consistency is certified.
    }

    [Fact]
    public void Context_has_no_migrations_snapshot_or_design_time_factory()
    {
        using var fixture = new OfflineContext();
        var migrations = fixture.Context.GetService<IMigrationsAssembly>();
        Assert.Empty(migrations.Migrations);
        Assert.Null(migrations.ModelSnapshot);
        var factoryType = typeof(IDesignTimeDbContextFactory<>).MakeGenericType(ContextType);
        Assert.DoesNotContain(PersistenceAssembly.GetTypes(), factoryType.IsAssignableFrom);
    }

    [Fact]
    public void Production_services_do_not_register_the_context_or_its_options()
    {
        var services = new ServiceCollection();
        services.AddAccounts(new ConfigurationBuilder().Build());
        services.AddNotifications();
        Assert.DoesNotContain(services, descriptor => ReferencesContext(descriptor.ServiceType)
            || ReferencesContext(descriptor.ImplementationType) || ReferencesContext(descriptor.ImplementationInstance?.GetType()));
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService(ContextType));
        Assert.Null(provider.GetService(typeof(DbContextOptions<>).MakeGenericType(ContextType)));
    }

    private static bool ReferencesContext(Type? type) => type is not null && (type == ContextType
        || (type.IsGenericType && type.GetGenericArguments().Any(ReferencesContext)));

    private static IModel Model(DbContext context) => context.GetService<IDesignTimeModel>().Model;

    private static RelationalOptionsExtension HistoryOptions(DbContext context) =>
        Assert.Single(context.GetService<IDbContextOptions>().Extensions.OfType<RelationalOptionsExtension>());

    private sealed class OfflineContext : IDisposable
    {
        private readonly RejectConnections connections = new();
        internal DbContext Context { get; }

        internal OfflineContext(bool configureProposedHistory = true)
        {
            var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(ContextType))!;
            builder.UseNpgsql(ModelConnection, postgres =>
            {
                if (configureProposedHistory)
                    postgres.MigrationsHistoryTable("__EFMigrationsHistory_Chat", "andril_chat");
            }).AddInterceptors(connections);
            Context = Assert.IsAssignableFrom<DbContext>(Activator.CreateInstance(ContextType,
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null, args: [builder.Options], culture: null));
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", Context.Database.ProviderName);
        }

        public void Dispose()
        {
            try
            {
                Assert.Equal(0, connections.OpenAttempts);
                Assert.Equal(ConnectionState.Closed, Context.Database.GetDbConnection().State);
            }
            finally
            {
                Context.Dispose();
            }
        }
    }

    private sealed class RejectConnections : DbConnectionInterceptor
    {
        internal int OpenAttempts { get; private set; }

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            OpenAttempts++;
            throw new InvalidOperationException("Connections are forbidden in offline Chat context tests.");
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            OpenAttempts++;
            throw new InvalidOperationException("Connections are forbidden in offline Chat context tests.");
        }
    }
}
