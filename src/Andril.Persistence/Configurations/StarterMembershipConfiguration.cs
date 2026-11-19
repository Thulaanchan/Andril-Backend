using Andril.Common.Accounts;
using Andril.Common.Accounts.StarterModules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class StarterMembershipConfiguration : IEntityTypeConfiguration<StarterMembership>
{
    public void Configure(EntityTypeBuilder<StarterMembership> b)
    {
        b.ToTable("account_starter_memberships", "andril_identity", t =>
        {
            t.HasCheckConstraint("ck_starter_module", "module IN ('learning', 'hobbies', 'social')");
            t.HasCheckConstraint("ck_starter_timestamp", "isfinite(activated_at_utc)");
            t.HasCheckConstraint("ck_starter_social_opt_in", "(module = 'social' AND social_opt_in_at_utc IS NOT NULL AND social_opt_in_at_utc = activated_at_utc) OR (module <> 'social' AND social_opt_in_at_utc IS NULL)");
        });
        b.HasKey(x => x.AccountId).HasName("pk_account_starter_memberships");
        b.Property(x => x.AccountId).HasColumnName("account_id").HasColumnType("uuid").ValueGeneratedNever();
        b.Property(x => x.Module).HasColumnName("module").HasColumnType("character varying(16)").HasMaxLength(16).IsRequired();
        b.Property(x => x.ActivatedAtUtc).HasColumnName("activated_at_utc").HasColumnType("timestamp with time zone");
        b.Property(x => x.SocialOptInAtUtc).HasColumnName("social_opt_in_at_utc").HasColumnType("timestamp with time zone");
        b.HasOne<Account>().WithOne().HasForeignKey<StarterMembership>(x => x.AccountId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_starter_memberships_accounts");
    }
}
