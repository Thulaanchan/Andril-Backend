
using Andril.Hobbies.Map.Contributions;
using Andril.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

/// <summary>
/// Provides the EF Core persistence context
/// for the ANDRIL Hobbies Map feature.
/// </summary>
public sealed class HobbiesMapDbContext : DbContext
{
    public HobbiesMapDbContext(
        DbContextOptions<HobbiesMapDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Server-managed Map contribution capture sessions.
    /// </summary>
    public DbSet<MapContributionCaptureSession>
        CaptureSessions =>
            Set<MapContributionCaptureSession>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(
            new MapContributionCaptureSessionConfiguration());
    }
}
