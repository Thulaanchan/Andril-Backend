using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

internal sealed class GalleryUploadOperation
{
    public Guid OwnerId { get; set; }
    public string Module { get; set; } = "Hobbies";
    public short Kind { get; set; }
    public Guid OperationId { get; set; }
    public string Fingerprint { get; set; } = "";
    public Guid MediaId { get; set; }
    public string PrivateObjectKey { get; set; } = "";
    public string? ProviderGeneration { get; set; }
    public short State { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class GalleryUploadOperationConfiguration : IEntityTypeConfiguration<GalleryUploadOperation>
{
    internal const string Table = "GalleryUploadOperations";

    public void Configure(EntityTypeBuilder<GalleryUploadOperation> builder)
    {
        builder.ToTable(Table, HobbiesDatabaseOptions.Schema, table =>
        {
            table.HasCheckConstraint("CK_GalleryUploadOperations_scope",
                "module = 'Hobbies' AND operation_kind = 0 AND owner_module_profile_id <> '00000000-0000-0000-0000-000000000000' AND operation_id <> '00000000-0000-0000-0000-000000000000' AND media_id <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_GalleryUploadOperations_evidence",
                "fingerprint_sha256 ~ '^[0-9A-F]{64}$' AND private_object_key ~ '[^[:space:]]' AND ((state = 0 AND provider_generation IS NULL) OR (state IN (1, 2) AND provider_generation IS NOT NULL AND provider_generation ~ '[^[:space:]]'))");
            table.HasCheckConstraint("CK_GalleryUploadOperations_revision", "revision = state AND updated_at_utc >= created_at_utc");
        });
        builder.HasKey(row => new { row.OwnerId, row.Module, row.Kind, row.OperationId });
        builder.HasIndex(row => row.MediaId).IsUnique();
        builder.HasIndex(row => row.PrivateObjectKey).IsUnique();
        builder.Property(row => row.OwnerId).HasColumnName("owner_module_profile_id");
        builder.Property(row => row.Module).HasColumnName("module");
        builder.Property(row => row.Kind).HasColumnName("operation_kind");
        builder.Property(row => row.OperationId).HasColumnName("operation_id");
        builder.Property(row => row.Fingerprint).HasColumnName("fingerprint_sha256").HasColumnType("character(64)").IsFixedLength();
        builder.Property(row => row.MediaId).HasColumnName("media_id");
        builder.Property(row => row.PrivateObjectKey).HasColumnName("private_object_key");
        builder.Property(row => row.ProviderGeneration).HasColumnName("provider_generation");
        builder.Property(row => row.State).HasColumnName("state");
        builder.Property(row => row.Revision).HasColumnName("revision").IsConcurrencyToken();
        builder.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc");
        builder.Property(row => row.UpdatedAtUtc).HasColumnName("updated_at_utc");
    }
}
