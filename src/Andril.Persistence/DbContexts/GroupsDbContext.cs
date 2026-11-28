using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

public sealed class GroupsDbContext(DbContextOptions<GroupsDbContext> options) : DbContext(options)
{
    public const string Schema = "andril_groups";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<GroupRecord>(entry =>
        {
            entry.ToTable("groups", table =>
            {
                table.HasCheckConstraint("ck_groups_module_kind", "(module, kind) IN (('learning', 1), ('hobbies', 2), ('social', 3))");
                table.HasCheckConstraint("ck_groups_visibility", "visibility IN ('private', 'public')");
                table.HasCheckConstraint("ck_groups_revision", "revision > 0");
                table.HasCheckConstraint("ck_groups_name", "length(btrim(name)) > 0");
                table.HasCheckConstraint("ck_groups_identity", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            });
            entry.HasKey(row => new { row.Id, row.Module });
            entry.Property(row => row.Id).HasColumnName("id");
            entry.Property(row => row.Module).HasColumnName("module").HasMaxLength(16);
            entry.Property(row => row.Kind).HasColumnName("kind");
            entry.Property(row => row.Name).HasColumnName("name").HasMaxLength(160);
            entry.Property(row => row.Description).HasColumnName("description").HasMaxLength(2000);
            entry.Property(row => row.CreatorProfileId).HasColumnName("creator_profile_id");
            entry.Property(row => row.Visibility).HasColumnName("visibility").HasMaxLength(16);
            entry.Property(row => row.IsDiscoverable).HasColumnName("is_discoverable");
            entry.Property(row => row.IsActive).HasColumnName("is_active");
            entry.Property(row => row.IsOpen).HasColumnName("is_open");
            entry.Property(row => row.Revision).HasColumnName("revision").IsConcurrencyToken();
            entry.HasIndex(row => new { row.Module, row.CreatorProfileId });
            entry.HasIndex(row => new { row.Module, row.Id }).HasFilter("is_discoverable AND is_active");
        });

        model.Entity<GroupMembershipRecord>(entry =>
        {
            entry.ToTable("memberships", table => table.HasCheckConstraint("ck_groups_membership_dates",
                "isfinite(joined_at_utc) AND (departed_at_utc IS NULL OR (isfinite(departed_at_utc) AND departed_at_utc >= joined_at_utc))"));
            entry.HasKey(row => new { row.GroupId, row.Module, row.ProfileId });
            entry.Property(row => row.GroupId).HasColumnName("group_id");
            entry.Property(row => row.Module).HasColumnName("module").HasMaxLength(16);
            entry.Property(row => row.ProfileId).HasColumnName("profile_id");
            entry.Property(row => row.JoinedAtUtc).HasColumnName("joined_at_utc");
            entry.Property(row => row.DepartedAtUtc).HasColumnName("departed_at_utc");
            entry.HasOne<GroupRecord>().WithMany().HasForeignKey(row => new { row.GroupId, row.Module }).OnDelete(DeleteBehavior.Restrict);
            entry.HasIndex(row => new { row.ProfileId, row.Module });
        });

        model.Entity<GroupJoinRequestRecord>(entry =>
        {
            entry.ToTable("join_requests", table =>
            {
                table.HasCheckConstraint("ck_groups_request_state", "(state = 'pending' AND resolved_at_utc IS NULL) OR (state IN ('cancelled', 'approved', 'rejected') AND resolved_at_utc IS NOT NULL)");
                table.HasCheckConstraint("ck_groups_request_dates", "isfinite(requested_at_utc) AND (resolved_at_utc IS NULL OR (isfinite(resolved_at_utc) AND resolved_at_utc >= requested_at_utc))");
            });
            entry.HasKey(row => row.Id);
            entry.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
            entry.Property(row => row.GroupId).HasColumnName("group_id");
            entry.Property(row => row.Module).HasColumnName("module").HasMaxLength(16);
            entry.Property(row => row.ProfileId).HasColumnName("profile_id");
            entry.Property(row => row.State).HasColumnName("state").HasMaxLength(16);
            entry.Property(row => row.RequestedAtUtc).HasColumnName("requested_at_utc");
            entry.Property(row => row.ResolvedAtUtc).HasColumnName("resolved_at_utc");
            entry.HasOne<GroupRecord>().WithMany().HasForeignKey(row => new { row.GroupId, row.Module }).OnDelete(DeleteBehavior.Restrict);
            entry.HasIndex(row => new { row.GroupId, row.Module, row.ProfileId }).IsUnique().HasFilter("state = 'pending'");
            entry.HasIndex(row => new { row.ProfileId, row.Module });
        });

        model.Entity<GroupCapabilityRecord>(entry =>
        {
            entry.ToTable("capabilities", table => table.HasCheckConstraint("ck_groups_capability", "capability IN (1, 2, 4)"));
            entry.HasKey(row => new { row.GroupId, row.Module, row.ProfileId, row.Capability });
            entry.Property(row => row.GroupId).HasColumnName("group_id");
            entry.Property(row => row.Module).HasColumnName("module").HasMaxLength(16);
            entry.Property(row => row.ProfileId).HasColumnName("profile_id");
            entry.Property(row => row.Capability).HasColumnName("capability");
            entry.HasOne<GroupMembershipRecord>().WithMany().HasForeignKey(row => new { row.GroupId, row.Module, row.ProfileId }).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<GroupAuditRecord>(entry =>
        {
            entry.ToTable("audit", table =>
            {
                table.HasCheckConstraint("ck_groups_audit_dates", "isfinite(recorded_at_utc)");
                table.HasCheckConstraint("ck_groups_audit_revision", "revision > 0");
            });
            entry.HasKey(row => row.Sequence);
            entry.Property(row => row.Sequence).HasColumnName("sequence").UseIdentityAlwaysColumn();
            entry.Property(row => row.GroupId).HasColumnName("group_id");
            entry.Property(row => row.Module).HasColumnName("module").HasMaxLength(16);
            entry.Property(row => row.ActorProfileId).HasColumnName("actor_profile_id");
            entry.Property(row => row.Action).HasColumnName("action").HasMaxLength(32);
            entry.Property(row => row.Revision).HasColumnName("revision");
            entry.Property(row => row.RecordedAtUtc).HasColumnName("recorded_at_utc");
            entry.HasOne<GroupRecord>().WithMany().HasForeignKey(row => new { row.GroupId, row.Module }).OnDelete(DeleteBehavior.Restrict);
            entry.HasIndex(row => new { row.GroupId, row.Module, row.Revision }).IsUnique();
        });
    }
}

internal sealed class GroupRecord
{
    public Guid Id { get; init; }
    public string Module { get; init; } = null!;
    public int Kind { get; init; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public Guid CreatorProfileId { get; init; }
    public string Visibility { get; set; } = null!;
    public bool IsDiscoverable { get; set; }
    public bool IsActive { get; set; }
    public bool IsOpen { get; set; }
    public long Revision { get; set; }
}

internal sealed class GroupMembershipRecord
{
    public Guid GroupId { get; init; }
    public string Module { get; init; } = null!;
    public Guid ProfileId { get; init; }
    public DateTimeOffset JoinedAtUtc { get; set; }
    public DateTimeOffset? DepartedAtUtc { get; set; }
}

internal sealed class GroupJoinRequestRecord
{
    public Guid Id { get; init; }
    public Guid GroupId { get; init; }
    public string Module { get; init; } = null!;
    public Guid ProfileId { get; init; }
    public string State { get; set; } = null!;
    public DateTimeOffset RequestedAtUtc { get; init; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}

internal sealed class GroupCapabilityRecord
{
    public Guid GroupId { get; init; }
    public string Module { get; init; } = null!;
    public Guid ProfileId { get; init; }
    public int Capability { get; init; }
}

internal sealed class GroupAuditRecord
{
    public long Sequence { get; init; }
    public Guid GroupId { get; init; }
    public string Module { get; init; } = null!;
    public Guid ActorProfileId { get; init; }
    public string Action { get; init; } = null!;
    public long Revision { get; init; }
    public DateTimeOffset RecordedAtUtc { get; init; }
}
