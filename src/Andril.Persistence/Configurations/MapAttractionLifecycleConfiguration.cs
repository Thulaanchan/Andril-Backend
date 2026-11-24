using Andril.Persistence.Repositories.Hobbies;
using Andril.Hobbies.Map.Places;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace Andril.Persistence.Configurations;

// Deliberately NOT applied by HobbiesMapDbContext. Deferral and trigger DDL belong to a
// separately reviewed future migration; ordinary EF metadata does not activate that contract.
internal sealed class MapAttractionLifecycleConfiguration : IEntityTypeConfiguration<MapAttractionLifecycleRow>
{
    public void Configure(EntityTypeBuilder<MapAttractionLifecycleRow> b)
    {
        b.ToTable("map_attraction_lifecycle", HobbiesDatabaseOptions.Schema,t =>
        {
            t.HasCheckConstraint("ck_map_lifecycle_state", "version >= 0 AND operation_sequence >= 0 AND ((operation_sequence=0 AND version=0 AND last_receipt_id IS NULL) OR (operation_sequence>0 AND last_receipt_id IS NOT NULL))");
        });
        b.HasOne<MapAttraction>().WithMany().HasForeignKey(x => x.AttractionId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_map_lifecycle_attraction");
        b.HasKey(x => x.AttractionId).HasName("pk_map_attraction_lifecycle");
        b.Property(x => x.AttractionId).HasColumnName("attraction_id").HasColumnType("uuid").ValueGeneratedNever();
        b.Property(x => x.IsActive).HasColumnName("is_active").HasColumnType("boolean");
        b.Property(x => x.Version).HasColumnName("version").HasColumnType("bigint").IsConcurrencyToken();
        b.Property(x => x.OperationSequence).HasColumnName("operation_sequence").HasColumnType("bigint").IsConcurrencyToken();
        b.Property(x => x.LastReceiptId).HasColumnName("last_receipt_id").HasColumnType("uuid");
        b.HasOne<MapAttractionLifecycleReceiptRow>().WithMany()
            .HasForeignKey("LastReceiptId","AttractionId","IsActive","Version","OperationSequence")
            .HasPrincipalKey("ReceiptId","AttractionId","ResultIsActive","ResultVersion","ResultOperationSequence")
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_map_lifecycle_current_receipt");
    }
}
internal static class MapLifecycleEvidenceMapping
{
    internal static readonly string[] Evidence = ["ReceiptId","ReviewerSubjectId","RequestId","AttractionId","RequestedIsActive","ExpectedVersion","PreviousIsActive","PreviousVersion","ResultIsActive","ResultVersion","PreviousOperationSequence","ResultOperationSequence","Outcome","AcceptedAtUtc"];
    internal static void Configure<T>(EntityTypeBuilder<T> b) where T : class
    {
        b.Property<Guid>("ReceiptId").HasColumnName("receipt_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
        b.Property<Guid>("ReviewerSubjectId").HasColumnName("reviewer_subject_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
        b.Property<Guid>("RequestId").HasColumnName("request_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
        b.Property<Guid>("AttractionId").HasColumnName("attraction_id").HasColumnType("uuid").IsRequired().ValueGeneratedNever();
        b.Property<bool>("RequestedIsActive").HasColumnName("requested_is_active").HasColumnType("boolean").IsRequired().ValueGeneratedNever();
        b.Property<long>("ExpectedVersion").HasColumnName("expected_version").HasColumnType("bigint").IsRequired().ValueGeneratedNever();
        b.Property<bool>("PreviousIsActive").HasColumnName("previous_is_active").HasColumnType("boolean").IsRequired().ValueGeneratedNever();
        b.Property<long>("PreviousVersion").HasColumnName("previous_version").HasColumnType("bigint").IsRequired().ValueGeneratedNever();
        b.Property<bool>("ResultIsActive").HasColumnName("result_is_active").HasColumnType("boolean").IsRequired().ValueGeneratedNever();
        b.Property<long>("ResultVersion").HasColumnName("result_version").HasColumnType("bigint").IsRequired().ValueGeneratedNever();
        b.Property<long>("PreviousOperationSequence").HasColumnName("previous_operation_sequence").HasColumnType("bigint").IsRequired().ValueGeneratedNever();
        b.Property<long>("ResultOperationSequence").HasColumnName("result_operation_sequence").HasColumnType("bigint").IsRequired().ValueGeneratedNever();
        b.Property<short>("Outcome").HasColumnName("outcome").HasColumnType("smallint").IsRequired().ValueGeneratedNever();
        b.Property<DateTimeOffset>("AcceptedAtUtc").HasColumnName("accepted_at_utc").HasColumnType("timestamp(6) with time zone").IsRequired().ValueGeneratedNever();
    }
}
internal sealed class MapAttractionLifecycleReceiptConfiguration : IEntityTypeConfiguration<MapAttractionLifecycleReceiptRow>
{
    public void Configure(EntityTypeBuilder<MapAttractionLifecycleReceiptRow> b)
    {
        b.ToTable("map_attraction_lifecycle_receipts",HobbiesDatabaseOptions.Schema,t =>
        {
            t.HasCheckConstraint("ck_map_lifecycle_receipt_transition", "expected_version=previous_version AND requested_is_active=result_is_active AND previous_version>=0 AND result_version>=0 AND previous_operation_sequence>=0 AND result_operation_sequence::numeric=previous_operation_sequence::numeric+1 AND ((outcome=1 AND previous_is_active<>result_is_active AND result_version::numeric=previous_version::numeric+1) OR (outcome=2 AND previous_is_active=result_is_active AND result_version=previous_version))");
            t.HasCheckConstraint("ck_map_lifecycle_receipt_ids", "receipt_id<>'00000000-0000-0000-0000-000000000000'::uuid AND reviewer_subject_id<>'00000000-0000-0000-0000-000000000000'::uuid AND request_id<>'00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_map_lifecycle_receipt_time", "isfinite(accepted_at_utc)");
        });
        b.HasOne<MapAttraction>().WithMany().HasForeignKey(x => x.AttractionId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_map_lifecycle_receipt_attraction");
        MapLifecycleEvidenceMapping.Configure(b);
        b.HasKey(x => x.ReceiptId).HasName("pk_map_lifecycle_receipts");
        b.HasIndex(x => new { x.ReviewerSubjectId,x.RequestId }).IsUnique().HasDatabaseName("ux_map_lifecycle_reviewer_request");
        b.HasIndex(x => new { x.AttractionId,x.ResultOperationSequence }).IsUnique().HasDatabaseName("ux_map_lifecycle_operation");
        b.HasAlternateKey("ReceiptId","AttractionId","ResultIsActive","ResultVersion","ResultOperationSequence").HasName("ak_map_lifecycle_receipt_result");
        b.HasOne<MapAttractionLifecycleAuditRow>().WithMany().HasForeignKey(MapLifecycleEvidenceMapping.Evidence)
            .HasPrincipalKey(MapLifecycleEvidenceMapping.Evidence).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_map_lifecycle_receipt_audit");
    }
}
internal sealed class MapAttractionLifecycleAuditConfiguration : IEntityTypeConfiguration<MapAttractionLifecycleAuditRow>
{
    public void Configure(EntityTypeBuilder<MapAttractionLifecycleAuditRow> b)
    {
        b.ToTable("map_attraction_lifecycle_audit",HobbiesDatabaseOptions.Schema);
        MapLifecycleEvidenceMapping.Configure(b);
        b.HasKey(x => x.ReceiptId).HasName("pk_map_lifecycle_audit");
        b.HasAlternateKey(MapLifecycleEvidenceMapping.Evidence).HasName("ak_map_lifecycle_audit_evidence");
        b.HasOne<MapAttractionLifecycleReceiptRow>().WithMany().HasForeignKey(x => x.ReceiptId)
            .OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_map_lifecycle_audit_receipt");
    }
}
