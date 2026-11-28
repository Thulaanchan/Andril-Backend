
using Andril.Hobbies.Entities;
using Andril.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

public sealed class HobbiesGalleryDbContext : DbContext
{
    public HobbiesGalleryDbContext(
        DbContextOptions<HobbiesGalleryDbContext> options)
        : base(options)
    {
    }

    public DbSet<GalleryMedia> GalleryMedia =>
        Set<GalleryMedia>();

    public DbSet<GalleryPreferences> GalleryPreferences =>
        Set<GalleryPreferences>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(
            new GalleryMediaConfiguration());

        modelBuilder.ApplyConfiguration(
            new GalleryPreferencesConfiguration());
        modelBuilder.ApplyConfiguration(new GalleryUploadOperationConfiguration());
    }
}
