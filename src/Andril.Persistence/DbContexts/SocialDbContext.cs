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
