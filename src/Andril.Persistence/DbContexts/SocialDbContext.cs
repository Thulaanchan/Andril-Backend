using Andril.Social.Entities;
using Microsoft.EntityFrameworkCore;

namespace Andril.Persistence.DbContexts;

/// <summary>
/// Dedicated Entity Framework Core DbContext for the isolated Social module.
/// Encapsulates relational persistence for personality questionnaire responses and answers
/// within the 'andril_social' PostgreSQL database schema under M2 delegation.
/// </summary>
public sealed class SocialDbContext : DbContext
{
    public const string Schema = "andril_social";

    public SocialDbContext(DbContextOptions<SocialDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Questionnaire response snapshots belonging to private Social profiles.
    /// </summary>
    public DbSet<QuestionnaireResponseEntity> QuestionnaireResponses => Set<QuestionnaireResponseEntity>();

    /// <summary>
    /// Answer entities associated with parent questionnaire responses.
    /// </summary>
    public DbSet<QuestionnaireAnswerEntity> QuestionnaireAnswers => Set<QuestionnaireAnswerEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<QuestionnaireResponseEntity>(b =>
        {
            b.ToTable("social_questionnaire_responses", Schema, t =>
            {
                t.HasCheckConstraint("ck_questionnaire_response_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_questionnaire_profile_id", "profile_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_questionnaire_timestamp", "isfinite(created_at_utc)");
            });

            b.HasKey(x => x.Id).HasName("pk_social_questionnaire_responses");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.ProfileId).HasColumnName("profile_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.Version).HasColumnName("version").HasColumnType("character varying(32)").HasMaxLength(32).IsRequired();
            b.Property(x => x.Status).HasColumnName("status").HasColumnType("character varying(32)").HasMaxLength(32)
                .HasConversion<string>().IsRequired();
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();

            b.Property<byte[]>("RowVersion")
                .HasColumnName("row_version")
                .IsRowVersion();

            b.HasIndex(x => new { x.ProfileId, x.Version }).HasDatabaseName("ix_social_questionnaire_profile_version");

            b.HasMany(x => x.Answers)
                .WithOne()
                .HasForeignKey(x => x.ResponseId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("fk_social_questionnaire_answers_response");

            b.Navigation(x => x.Answers)
                .HasField("_answers")
                .UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<SocialQuestionnaireCurrentResponse>(b =>
        {
            b.ToTable("social_questionnaire_current_responses", Schema, table =>
                table.HasCheckConstraint("ck_social_questionnaire_current_module", "module = 'social'"));
            b.HasKey(row => row.ProfileId).HasName("pk_social_questionnaire_current_responses");
            b.Property(row => row.ProfileId).HasColumnName("profile_id").ValueGeneratedNever();
            b.Property(row => row.ResponseId).HasColumnName("response_id");
            b.Property(row => row.Module).HasColumnName("module").HasMaxLength(16).IsRequired();
            b.Property<uint>("RowVersion").HasColumnName("xmin").IsRowVersion();
            b.HasOne<QuestionnaireResponseEntity>().WithMany()
                .HasPrincipalKey(response => new { response.ProfileId, response.Id })
                .HasForeignKey(row => new { row.ProfileId, row.ResponseId })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_social_questionnaire_current_response");
        });

        modelBuilder.Entity<SocialDirectionalInterestRow>(b =>
        {
            b.ToTable("social_directional_interests", Schema, table =>
            {
                table.HasCheckConstraint("ck_social_interest_module", "module = 'social'");
                table.HasCheckConstraint("ck_social_interest_distinct", "actor_profile_id <> target_profile_id");
                table.HasCheckConstraint("ck_social_interest_timestamp", "isfinite(recorded_at_utc)");
            });
            b.HasKey(row => new { row.ActorProfileId, row.TargetProfileId }).HasName("pk_social_directional_interests");
            b.HasIndex(row => row.TargetProfileId).HasDatabaseName("ix_social_interests_target");
            b.Property(row => row.ActorProfileId).HasColumnName("actor_profile_id");
            b.Property(row => row.TargetProfileId).HasColumnName("target_profile_id");
            b.Property(row => row.Module).HasColumnName("module").HasMaxLength(16).IsRequired();
            b.Property(row => row.IsPositive).HasColumnName("is_positive");
            b.Property(row => row.RecordedAtUtc).HasColumnName("recorded_at_utc");
            b.Property<uint>("RowVersion").HasColumnName("xmin").IsRowVersion();
        });

        modelBuilder.Entity<SocialMatchRow>(b =>
        {
            b.ToTable("social_matches", Schema, table =>
            {
                table.HasCheckConstraint("ck_social_match_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("ck_social_match_module", "module = 'social'");
                table.HasCheckConstraint("ck_social_match_canonical_pair", "first_profile_id < second_profile_id");
                table.HasCheckConstraint("ck_social_match_state", """
                    isfinite(created_at_utc) AND (
                        (state = 'active' AND revoked_at_utc IS NULL) OR
                        (state = 'revoked' AND revoked_at_utc IS NOT NULL AND isfinite(revoked_at_utc) AND revoked_at_utc >= created_at_utc)
                    )
                    """.ReplaceLineEndings("\n"));
            });
            b.HasKey(row => row.Id).HasName("pk_social_matches");
            b.HasIndex(row => new { row.FirstProfileId, row.SecondProfileId })
                .HasDatabaseName("ix_social_matches_pair");
            b.HasIndex(row => row.SecondProfileId).HasDatabaseName("ix_social_matches_second");
            b.HasIndex(row => new { row.FirstProfileId, row.SecondProfileId }, "UX_SocialMatchActivePair")
                .IsUnique().HasFilter("state = 'active'").HasDatabaseName("ux_social_matches_active_pair");
            b.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(row => row.FirstProfileId).HasColumnName("first_profile_id");
            b.Property(row => row.SecondProfileId).HasColumnName("second_profile_id");
            b.Property(row => row.Module).HasColumnName("module").HasMaxLength(16).IsRequired();
            b.Property(row => row.State).HasColumnName("state").HasMaxLength(16).IsRequired();
            b.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc");
            b.Property(row => row.RevokedAtUtc).HasColumnName("revoked_at_utc");
            b.Property<uint>("RowVersion").HasColumnName("xmin").IsRowVersion();
        });

        modelBuilder.Entity<SocialMatchingPreferencesRow>(b =>
        {
            b.ToTable("social_matching_preferences", Schema, table =>
            {
                table.HasCheckConstraint("ck_social_matching_preferences_module", "module = 'social'");
                table.HasCheckConstraint("ck_social_matching_preferences_age", "min_age >= 18 AND max_age <= 99 AND min_age <= max_age");
                table.HasCheckConstraint("ck_social_matching_preferences_distance", "min_distance_km >= 0 AND max_distance_km <= 999 AND min_distance_km <= max_distance_km");
                table.HasCheckConstraint("ck_social_matching_preferences_gender", "show_me IN ('All', 'Men', 'Women')");
                table.HasCheckConstraint("ck_social_matching_preferences_revision", "revision > 0");
                table.HasCheckConstraint("ck_social_matching_preferences_partner", """
                    num_nonnulls(partner_social, partner_communication, partner_planning) = 0 OR
                    (num_nonnulls(partner_social, partner_communication, partner_planning) = 3
                        AND partner_social IN ('Either', 'Reserved', 'Outgoing')
                        AND partner_communication IN ('Either', 'LessExpressive', 'Expressive')
                        AND partner_planning IN ('Either', 'Spontaneous', 'Structured'))
                    """.ReplaceLineEndings("\n"));
            });
            b.HasKey(row => row.ProfileId).HasName("pk_social_matching_preferences");
            b.Property(row => row.ProfileId).HasColumnName("profile_id").ValueGeneratedNever();
            b.Property(row => row.Module).HasColumnName("module").HasMaxLength(16).IsRequired();
            b.Property(row => row.Discoverable).HasColumnName("discoverable");
            b.Property(row => row.ShowMe).HasColumnName("show_me").HasMaxLength(16).IsRequired();
            b.Property(row => row.MinAge).HasColumnName("min_age");
            b.Property(row => row.MaxAge).HasColumnName("max_age");
            b.Property(row => row.MinDistanceKm).HasColumnName("min_distance_km");
            b.Property(row => row.MaxDistanceKm).HasColumnName("max_distance_km");
            b.Property(row => row.Languages).HasColumnName("languages").IsRequired();
            b.Property(row => row.ConnectionGoals).HasColumnName("connection_goals").IsRequired();
            b.Property(row => row.PartnerSocial).HasColumnName("partner_social").HasMaxLength(16);
            b.Property(row => row.PartnerCommunication).HasColumnName("partner_communication").HasMaxLength(16);
            b.Property(row => row.PartnerPlanning).HasColumnName("partner_planning").HasMaxLength(16);
            b.Property(row => row.Revision).HasColumnName("revision").IsConcurrencyToken();
        });

        modelBuilder.Entity<SocialLocationEvidenceRow>(b =>
        {
            b.ToTable("social_location_evidence", Schema, table =>
            {
                table.HasCheckConstraint("ck_social_location_module", "module = 'social'");
                table.HasCheckConstraint("ck_social_location_revision", "revision > 0 AND isfinite(observed_at_utc)");
                table.HasCheckConstraint("ck_social_location_cells", """
                    (latitude_cell IS NULL AND longitude_cell IS NULL) OR
                    (latitude_cell IS NOT NULL AND longitude_cell IS NOT NULL
                        AND latitude_cell BETWEEN -90 AND 90 AND longitude_cell >= -180 AND longitude_cell < 180)
                    """.ReplaceLineEndings("\n"));
            });
            b.HasKey(row => row.ProfileId).HasName("pk_social_location_evidence");
            b.Property(row => row.ProfileId).HasColumnName("profile_id").ValueGeneratedNever();
            b.Property(row => row.Module).HasColumnName("module").HasMaxLength(16).IsRequired();
            b.Property(row => row.LatitudeCell).HasColumnName("latitude_cell").HasPrecision(5, 2);
            b.Property(row => row.LongitudeCell).HasColumnName("longitude_cell").HasPrecision(5, 2);
            b.Property(row => row.ObservedAtUtc).HasColumnName("observed_at_utc");
            b.Property(row => row.Revision).HasColumnName("revision").IsConcurrencyToken();
        });

        modelBuilder.Entity<QuestionnaireAnswerEntity>(b =>
        {
            b.ToTable("social_questionnaire_answers", Schema, t =>
            {
                t.HasCheckConstraint("ck_questionnaire_answer_id", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_questionnaire_answer_response_id", "response_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                t.HasCheckConstraint("ck_questionnaire_answer_timestamp", "isfinite(answered_at_utc)");
            });

            b.HasKey(x => x.Id).HasName("pk_social_questionnaire_answers");
            b.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
            b.Property(x => x.ResponseId).HasColumnName("response_id").HasColumnType("uuid").IsRequired();
            b.Property(x => x.QuestionId).HasColumnName("question_id").HasColumnType("character varying(128)").HasMaxLength(128).IsRequired();
            b.Property(x => x.OptionId).HasColumnName("option_id").HasColumnType("character varying(128)").HasMaxLength(128).IsRequired();
            b.Property(x => x.AnsweredAtUtc).HasColumnName("answered_at_utc").HasColumnType("timestamp with time zone").IsRequired();

            b.HasIndex(x => x.ResponseId).HasDatabaseName("ix_social_questionnaire_answers_response");
        });
    }
}

// A separate pointer keeps immutable response snapshots independent of the current response.
internal sealed class SocialQuestionnaireCurrentResponse(Guid profileId, Guid responseId)
{
    public Guid ProfileId { get; private set; } = profileId;
    public Guid ResponseId { get; private set; } = responseId;
    public string Module { get; private set; } = "social";
}

internal sealed class SocialDirectionalInterestRow(Guid actorProfileId, Guid targetProfileId, bool isPositive, DateTime recordedAtUtc)
{
    public Guid ActorProfileId { get; private set; } = actorProfileId;
    public Guid TargetProfileId { get; private set; } = targetProfileId;
    public string Module { get; private set; } = "social";
    public bool IsPositive { get; private set; } = isPositive;
    public DateTime RecordedAtUtc { get; private set; } = recordedAtUtc;
}

internal sealed class SocialMatchRow(Guid id, Guid firstProfileId, Guid secondProfileId, DateTime createdAtUtc)
{
    public Guid Id { get; private set; } = id;
    public Guid FirstProfileId { get; private set; } = firstProfileId;
    public Guid SecondProfileId { get; private set; } = secondProfileId;
    public string Module { get; private set; } = "social";
    public string State { get; private set; } = "active";
    public DateTime CreatedAtUtc { get; private set; } = createdAtUtc;
    public DateTime? RevokedAtUtc { get; private set; }
}

internal sealed class SocialLocationEvidenceRow
{
    public Guid ProfileId { get; private set; }
    public string Module { get; private set; } = null!;
    public decimal? LatitudeCell { get; private set; }
    public decimal? LongitudeCell { get; private set; }
    public DateTimeOffset ObservedAtUtc { get; private set; }
    public long Revision { get; private set; }
}
