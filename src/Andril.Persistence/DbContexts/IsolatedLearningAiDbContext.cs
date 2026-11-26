using Andril.AI.ChatBox;
using Andril.AI.Persistence;
using Andril.Persistence.Repositories.AI;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

/// <summary>M1-only disposable model, NOT a production schema/history decision. Created only
/// by isolated PostgreSQL fixtures; no migration or runtime registration. Uses existing AI
/// entities and policies without adopting Learning preference revision or retention semantics.</summary>
internal sealed class IsolatedLearningAiDbContext(DbContextOptions<IsolatedLearningAiDbContext> options) : DbContext(options)
{
    internal const string Schema = "andril_ai_isolated";
    protected override void OnModelCreating(ModelBuilder m)
    {
        m.HasDefaultSchema(Schema);
        var acknowledgement = m.Entity<IsolatedAiAcknowledgementRow>();
        acknowledgement.ToTable("privacy_acknowledgements", t =>
        {
            t.HasCheckConstraint("ck_ai_ack_scope", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid AND module_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND module = 'Learning'");
            t.HasCheckConstraint("ck_ai_ack_revision", "generation <> '00000000-0000-0000-0000-000000000000'::uuid AND version > 0 AND last_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_ai_ack_policy", "NOT acknowledged OR length(trim(policy_version)) > 0");
        });
        acknowledgement.HasKey(x => new { x.AccountId, x.ModuleProfileId, x.Module });
        acknowledgement.Property(x => x.Module).HasConversion<string>();
        acknowledgement.Property(x => x.Version).IsConcurrencyToken();
        acknowledgement.Property(x => x.Generation).IsConcurrencyToken();
        var operation = m.Entity<IsolatedAiDispatchOperationRow>();
        operation.ToTable("dispatch_operations", t =>
        {
            t.HasCheckConstraint("ck_ai_dispatch_scope", "module = 'Learning' AND operation_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_ai_dispatch_state", "state IN ('Prepared','Claimed','Cancelled','Indeterminate')");
            t.HasCheckConstraint("ck_ai_dispatch_ack", "acknowledgement_generation <> '00000000-0000-0000-0000-000000000000'::uuid AND acknowledgement_version > 0 AND length(trim(policy_version)) > 0");
        });
        operation.HasKey(x => new { x.AccountId, x.ModuleProfileId, x.Module, x.OperationId });
        operation.Property(x => x.Module).HasConversion<string>();
        operation.Property(x => x.State).HasConversion<string>().IsConcurrencyToken();
        operation.HasOne<IsolatedAiAcknowledgementRow>().WithMany()
            .HasForeignKey(x => new { x.AccountId, x.ModuleProfileId, x.Module }).OnDelete(DeleteBehavior.Restrict);
        var c = m.Entity<AiConversationEntity>();
        c.ToTable("conversations", t =>
        {
            t.HasCheckConstraint("ck_ai_conversation_scope", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid AND module_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND module = 'Learning'");
            t.HasCheckConstraint("ck_ai_conversation_expiry", "expires_at_utc = last_saved_user_message_at_utc + interval '336 hours'");
        });
        c.HasKey(x => x.Id); c.HasAlternateKey(x => new { x.Id, x.AccountId, x.ModuleProfileId, x.Module });
        c.HasIndex(x => new { x.AccountId, x.ModuleProfileId, x.Module, x.CreateOperationId }).IsUnique().HasDatabaseName("uq_ai_conversation_operation");
        c.Property(x => x.Module).HasConversion<string>().HasMaxLength(16);
        var message = m.Entity<AiMessageEntity>();
        message.ToTable("messages", t =>
        {
            t.HasCheckConstraint("ck_ai_message_scope", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid AND module_profile_id <> '00000000-0000-0000-0000-000000000000'::uuid AND module = 'Learning'");
            t.HasCheckConstraint("ck_ai_message_role", "role IN ('User','Assistant')");
            t.HasCheckConstraint("ck_ai_message_text", "length(text) > 0");
        });
        message.HasKey(x => x.Id);
        message.Property(x => x.Module).HasConversion<string>().HasMaxLength(16);
        message.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
        message.Property<long>("StoreSequence").UseIdentityAlwaysColumn();
        message.HasIndex(x => new { x.AccountId, x.ModuleProfileId, x.Module, x.OperationId, x.Role }).IsUnique().HasDatabaseName("uq_ai_message_operation_role");
        message.HasOne<AiConversationEntity>().WithMany().HasForeignKey(x => new { x.ConversationId, x.AccountId, x.ModuleProfileId, x.Module })
            .HasPrincipalKey(x => new { x.Id, x.AccountId, x.ModuleProfileId, x.Module }).OnDelete(DeleteBehavior.Restrict);
        var work = m.Entity<IsolatedAiPurgeWork>();
        work.ToTable("purge_work", t => t.HasCheckConstraint("ck_ai_purge_target", "purge_by_utc = requested_at_utc + interval '24 hours' AND module = 'Learning'"));
        work.HasKey(x => x.ConversationId); work.Property(x => x.Module).HasConversion<string>().HasMaxLength(16);
        work.HasOne<AiConversationEntity>().WithMany().HasForeignKey(x => new { x.ConversationId, x.AccountId, x.ModuleProfileId, x.Module })
            .HasPrincipalKey(x => new { x.Id, x.AccountId, x.ModuleProfileId, x.Module }).OnDelete(DeleteBehavior.Restrict);
        foreach (var entity in m.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            property.SetColumnName(System.Text.RegularExpressions.Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
            if (property.ClrType == typeof(Guid)) property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }
    }
}

/// <summary>Durable scheduling data only. Deployment/worker/backup coordination is not activated.</summary>
internal sealed class IsolatedAiPurgeWork
{
    public Guid ConversationId { get; set; }
    public Guid AccountId { get; set; }
    public Guid ModuleProfileId { get; set; }
    public AiModule Module { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset PurgeByUtc { get; set; }
    public override string ToString() => "Private AI purge work [bindings omitted]";
}
