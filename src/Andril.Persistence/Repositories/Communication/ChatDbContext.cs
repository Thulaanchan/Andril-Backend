using Andril.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.Repositories.Communication;

/// <summary>
/// Isolated candidate Chat model only. Not registered for runtime use or deployment.
/// Provider and migration-history options belong to the caller; no connection is configured here.
/// </summary>
internal sealed class ChatDbContext : DbContext
{
    internal ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new ChatConversationRecordConfiguration());
        modelBuilder.ApplyConfiguration(new ChatParticipantRecordConfiguration());
        modelBuilder.ApplyConfiguration(new ChatMessageRecordConfiguration());
    }
}
