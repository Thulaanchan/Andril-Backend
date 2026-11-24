using System.Text.Json;
using System.Text.Json.Serialization;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.LocationValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

/// <summary>
/// Defines PostgreSQL mappings and integrity constraints for private Map contributions.
/// </summary>
public sealed class MapContributionConfiguration : IEntityTypeConfiguration<MapContribution>
{
    private const string EmptyUuid = "'00000000-0000-0000-0000-000000000000'::uuid";

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        // All fields must be present, including fields whose values are explicitly null.
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public void Configure(EntityTypeBuilder<MapContribution> builder)
    {
        builder.ToTable("map_contributions", HobbiesDatabaseOptions.Schema, table =>
        {
            table.HasCheckConstraint("ck_map_contribution_id", $"contribution_id <> {EmptyUuid}");
            table.HasCheckConstraint("ck_map_contribution_owner_id", $"owner_hobbies_module_profile_id <> {EmptyUuid}");
            table.HasCheckConstraint("ck_map_contribution_attraction_id", $"attraction_id <> {EmptyUuid}");
            table.HasCheckConstraint("ck_map_contribution_capture_session_id", $"capture_session_id <> {EmptyUuid}");

            // PostgreSQL and .NET classify whitespace differently.
            table.HasCheckConstraint(
                "ck_map_contribution_photo_key",
                "private_photo_storage_object_key !~ '^[[:space:]]*$'");
            table.HasCheckConstraint(
                "ck_map_contribution_description",
                "description !~ '^[[:space:]]*$'");

            // Structural protection only. The submission service validates location evidence.
            table.HasCheckConstraint(
                "ck_map_contribution_location_evidence",
                "jsonb_typeof(location_evidence) = 'object'");

            // PendingReview is the only supported persisted moderation state.
            table.HasCheckConstraint(
                "ck_map_contribution_moderation_status",
                "moderation_status = 1");
        });

        builder.HasKey(contribution => contribution.ContributionId)
            .HasName("pk_map_contributions");

        builder.Property(contribution => contribution.ContributionId)
            .HasColumnName("contribution_id")
            .HasColumnType("uuid")
            .ValueGeneratedNever()
            .IsRequired();

        builder.Property(contribution => contribution.OwnerHobbiesModuleProfileId)
            .HasColumnName("owner_hobbies_module_profile_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(contribution => contribution.AttractionId)
            .HasColumnName("attraction_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(contribution => contribution.CaptureSessionId)
            .HasColumnName("capture_session_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.HasIndex(contribution => contribution.CaptureSessionId)
            .IsUnique()
            .HasDatabaseName("ux_map_contributions_capture_session_id");

        builder.HasOne<MapContributionCaptureSession>()
            .WithMany()
            .HasForeignKey(contribution => new
            {
                contribution.CaptureSessionId,
                contribution.OwnerHobbiesModuleProfileId,
                contribution.AttractionId
            })
            .HasPrincipalKey(session => new
            {
                session.SessionId,
                session.OwnerHobbiesModuleProfileId,
                session.AttractionId
            })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("fk_map_contributions_capture_session_scope");

        builder.Property(contribution => contribution.PrivatePhotoStorageObjectKey)
            .HasColumnName("private_photo_storage_object_key")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(contribution => contribution.Description)
            .HasColumnName("description")
            .HasColumnType("text")
            .IsRequired();

        // The immutable record's value equality and reference snapshot suffice for EF tracking.
        builder.Property(contribution => contribution.LocationEvidence)
            .HasColumnName("location_evidence")
            .HasColumnType("jsonb")
            .HasConversion(evidence => SerializeEvidence(evidence), json => DeserializeEvidence(json))
            .IsRequired();

        builder.Property(contribution => contribution.SubmittedAtUtc)
            .HasColumnName("submitted_at_utc")
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(contribution => contribution.ModerationStatus)
            .HasColumnName("moderation_status")
            .HasColumnType("integer")
            .HasConversion<int>()
            .IsRequired();
    }

    private static string SerializeEvidence(MapContributionLocationEvidence evidence)
    {
        EnsureFiniteNumbers(evidence);
        return JsonSerializer.Serialize(evidence, EvidenceJsonOptions);
    }

    private static MapContributionLocationEvidence DeserializeEvidence(string json)
    {
        MapContributionLocationEvidence? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<MapContributionLocationEvidence>(json, EvidenceJsonOptions);
        }
        catch (JsonException)
        {
            // Keep stored evidence and parser details out of the exception.
            throw new JsonException("Stored Map contribution location evidence has an invalid JSON structure.");
        }

        if (evidence is null)
            throw new JsonException("Stored Map contribution location evidence must be a JSON object.");

        EnsureFiniteNumbers(evidence);
        return evidence;
    }

    private static void EnsureFiniteNumbers(MapContributionLocationEvidence evidence)
    {
        // Reject NaN and infinities on both paths, including overflow while reading JSON numbers.
        if (!IsFiniteOrNull(evidence.UserLatitude)
            || !IsFiniteOrNull(evidence.UserLongitude)
            || !IsFiniteOrNull(evidence.AttractionLatitude)
            || !IsFiniteOrNull(evidence.AttractionLongitude)
            || !IsFiniteOrNull(evidence.HorizontalAccuracyMetres))
        {
            throw new JsonException("Map contribution location evidence requires finite numeric values.");
        }
    }

    private static bool IsFiniteOrNull(double? value) => value is null || double.IsFinite(value.Value);
}
