using System;
using System.Collections.Generic;
using System.Linq;

using Andril.Economy.FlightPoints;
using Andril.Hobbies.Map.Contributions;
using Andril.Hobbies.Map.Places;
using Andril.Persistence.Configurations;
using Andril.Persistence.Repositories.Hobbies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// Canonical Map attraction records.
    /// </summary>
    public DbSet<MapAttraction> MapAttractions => Set<MapAttraction>();

    /// <summary>
    /// Private Map contribution records.
    /// </summary>
    public DbSet<MapContribution> MapContributions => Set<MapContribution>();

    /// <summary>
    /// Hobbies Flight Points ledger entries.
    /// </summary>
    public DbSet<FlightPointsLedgerEntry> FlightPointsLedgerEntries => Set<FlightPointsLedgerEntry>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(
            new MapContributionCaptureSessionConfiguration());

        modelBuilder.ApplyConfiguration(
            new MapAttractionConfiguration());

        modelBuilder.ApplyConfiguration(
            new MapContributionConfiguration());

        modelBuilder.ApplyConfiguration(
            new FlightPointsLedgerEntryConfiguration());

        modelBuilder.ApplyConfiguration(new MapAttractionLifecycleConfiguration());
        modelBuilder.ApplyConfiguration(new MapAttractionLifecycleReceiptConfiguration());
        modelBuilder.ApplyConfiguration(new MapAttractionLifecycleAuditConfiguration());
        modelBuilder.ApplyConfiguration(new MapPrivateMediaConfiguration());
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Conventions.Replace<ForeignKeyIndexConvention>(
            serviceProvider => new MapLifecycleForeignKeyIndexConvention(
                serviceProvider.GetRequiredService<ProviderConventionSetBuilderDependencies>()));
    }

    private sealed class MapLifecycleForeignKeyIndexConvention(
        ProviderConventionSetBuilderDependencies dependencies)
        : ForeignKeyIndexConvention(dependencies)
    {
        private static readonly string[] CurrentReceiptProperties =
        [
            nameof(MapAttractionLifecycleRow.LastReceiptId),
            nameof(MapAttractionLifecycleRow.AttractionId),
            nameof(MapAttractionLifecycleRow.IsActive),
            nameof(MapAttractionLifecycleRow.Version),
            nameof(MapAttractionLifecycleRow.OperationSequence)
        ];

        private static readonly string[] ReceiptAuditProperties =
        [
            "ReceiptId",
            "ReviewerSubjectId",
            "RequestId",
            "AttractionId",
            "RequestedIsActive",
            "ExpectedVersion",
            "PreviousIsActive",
            "PreviousVersion",
            "ResultIsActive",
            "ResultVersion",
            "PreviousOperationSequence",
            "ResultOperationSequence",
            "Outcome",
            "AcceptedAtUtc"
        ];

        protected override IConventionIndex? CreateIndex(
            IReadOnlyList<IConventionProperty> properties,
            bool unique,
            IConventionEntityTypeBuilder entityTypeBuilder)
        {
            if (!unique && IsExactSignature(
                    entityTypeBuilder,
                    properties,
                    typeof(MapAttractionLifecycleRow),
                    CurrentReceiptProperties)
                || !unique && IsExactSignature(
                    entityTypeBuilder,
                    properties,
                    typeof(MapAttractionLifecycleReceiptRow),
                    ReceiptAuditProperties))
            {
                return null;
            }

            return base.CreateIndex(properties, unique, entityTypeBuilder);
        }

        private static bool IsExactSignature(
            IConventionEntityTypeBuilder entityTypeBuilder,
            IReadOnlyList<IConventionProperty> properties,
            Type entityClrType,
            IReadOnlyList<string> propertyNames) =>
            entityTypeBuilder.Metadata.ClrType == entityClrType
            && properties.Select(property => property.Name)
                .SequenceEqual(propertyNames, StringComparer.Ordinal);
    }
}
