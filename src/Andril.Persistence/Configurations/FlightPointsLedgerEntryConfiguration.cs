using System;
using Andril.Economy.FlightPoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

/// <summary>
/// Defines PostgreSQL mappings and row-level integrity checks for the Flight Points ledger.
/// </summary>
public sealed class FlightPointsLedgerEntryConfiguration : IEntityTypeConfiguration<FlightPointsLedgerEntry>
{
    private const string EmptyUuid = "'00000000-0000-0000-0000-000000000000'::uuid";

    public void Configure(EntityTypeBuilder<FlightPointsLedgerEntry> builder)
    {
        const int grant = (int)FlightPointsLedgerEntryType.Grant;
        const int reversal = (int)FlightPointsLedgerEntryType.Reversal;
        const int firstContribution = (int)FlightPointsRewardType.FirstEligibleContribution;
        const int firstExplorer = (int)FlightPointsRewardType.FirstExplorer;
        const int contributionPoints = FlightPointsRewardPolicy.FirstEligibleContributionPoints;
        const int explorerPoints = FlightPointsRewardPolicy.FirstExplorerBonusPoints;

        builder.ToTable("flight_point_ledger", HobbiesDatabaseOptions.Schema, table =>
        {
            table.HasCheckConstraint("ck_flight_point_ledger_id", $"ledger_entry_id <> {EmptyUuid}");
            table.HasCheckConstraint("ck_flight_point_ledger_owner_id", $"owner_hobbies_module_profile_id <> {EmptyUuid}");
            table.HasCheckConstraint("ck_flight_point_ledger_contribution_id", $"contribution_id <> {EmptyUuid}");
            table.HasCheckConstraint("ck_flight_point_ledger_attraction_id", $"attraction_id <> {EmptyUuid}");

            // PostgreSQL and .NET classify whitespace differently. The original key is stored unchanged.
            table.HasCheckConstraint("ck_flight_point_ledger_idempotency_key", "idempotency_key !~ '^[[:space:]]*$'");
            table.HasCheckConstraint("ck_flight_point_ledger_entry_type",
                FormattableString.Invariant($"entry_type IN ({grant}, {reversal})"));
            table.HasCheckConstraint("ck_flight_point_ledger_reward_type",
                FormattableString.Invariant($"reward_type IN ({firstContribution}, {firstExplorer})"));
            table.HasCheckConstraint("ck_flight_point_ledger_points_delta", FormattableString.Invariant($"""
                (entry_type = {grant} AND reward_type = {firstContribution} AND points_delta = {contributionPoints})
                OR (entry_type = {grant} AND reward_type = {firstExplorer} AND points_delta = {explorerPoints})
                OR (entry_type = {reversal} AND reward_type = {firstContribution} AND points_delta = {-contributionPoints})
                OR (entry_type = {reversal} AND reward_type = {firstExplorer} AND points_delta = {-explorerPoints})
                """));
            table.HasCheckConstraint("ck_flight_point_ledger_reversal_reference", FormattableString.Invariant($"""
                (entry_type = {grant} AND reversed_entry_id IS NULL)
                OR (entry_type = {reversal} AND reversed_entry_id IS NOT NULL
                    AND reversed_entry_id <> {EmptyUuid} AND reversed_entry_id <> ledger_entry_id)
                """));
        });

        builder.HasKey(entry => entry.LedgerEntryId).HasName("pk_flight_point_ledger");
        builder.Property(entry => entry.LedgerEntryId)
            .HasColumnName("ledger_entry_id").HasColumnType("uuid").ValueGeneratedNever().IsRequired();
        builder.Property(entry => entry.OwnerHobbiesModuleProfileId)
            .HasColumnName("owner_hobbies_module_profile_id").HasColumnType("uuid").IsRequired();
        builder.Property(entry => entry.ContributionId)
            .HasColumnName("contribution_id").HasColumnType("uuid").IsRequired();
        builder.Property(entry => entry.AttractionId)
            .HasColumnName("attraction_id").HasColumnType("uuid").IsRequired();
        builder.Property(entry => entry.EntryType)
            .HasColumnName("entry_type").HasColumnType("integer").HasConversion<int>().IsRequired();
        builder.Property(entry => entry.RewardType)
            .HasColumnName("reward_type").HasColumnType("integer").HasConversion<int>().IsRequired();
        builder.Property(entry => entry.PointsDelta)
            .HasColumnName("points_delta").HasColumnType("integer").IsRequired();
        builder.Property(entry => entry.IdempotencyKey)
            .HasColumnName("idempotency_key").HasColumnType("text").IsRequired();
        builder.Property(entry => entry.ReversedEntryId)
            .HasColumnName("reversed_entry_id").HasColumnType("uuid").IsRequired(false);
        builder.Property(entry => entry.CreatedAtUtc)
            .HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        // The repository verifies the referenced row is a matching grant.
        builder.HasOne<FlightPointsLedgerEntry>()
            .WithMany()
            .HasForeignKey(entry => entry.ReversedEntryId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_flight_point_ledger_reversed_entry");

        builder.HasIndex(entry => entry.ReversedEntryId)
            .HasDatabaseName("ux_flight_point_ledger_reversed_entry")
            .IsUnique()
            .HasFilter(FormattableString.Invariant($"entry_type = {reversal}"));
    }
}
