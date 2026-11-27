using Andril.Common.Accounts;
using Andril.Common.Accounts.Privacy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class UserPrivacySettingsConfiguration : IEntityTypeConfiguration<UserPrivacySettings>
{
    public void Configure(EntityTypeBuilder<UserPrivacySettings> builder)
    {
        builder.ToTable("account_privacy_settings", "andril_identity", table =>
        {
            table.HasCheckConstraint("ck_privacy_account_id_nonempty", "account_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_privacy_discoverability", "profile_discoverability IN ('public', 'contacts_only', 'private')");
            table.HasCheckConstraint("ck_privacy_messaging", "direct_messaging_policy IN ('everyone', 'friends_only', 'disabled')");
            table.HasCheckConstraint("ck_privacy_timestamps", "updated_at_utc >= created_at_utc");
        });

        builder.HasKey(x => x.AccountId).HasName("pk_account_privacy_settings");

        builder.Property(x => x.AccountId).HasColumnName("account_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(x => x.ProfileDiscoverability).HasColumnName("profile_discoverability").HasMaxLength(32).HasColumnType("character varying(32)").IsRequired();
        builder.Property(x => x.GhostModeEnabled).HasColumnName("ghost_mode_enabled").HasColumnType("boolean").IsRequired();
        builder.Property(x => x.DirectMessagingPolicy).HasColumnName("direct_messaging_policy").HasMaxLength(32).HasColumnType("character varying(32)").IsRequired();
        builder.Property(x => x.AllowFriendRequests).HasColumnName("allow_friend_requests").HasColumnType("boolean").IsRequired();
        builder.Property(x => x.ShowActiveModule).HasColumnName("show_active_module").HasColumnType("boolean").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone").IsRequired();

        builder.HasOne<Account>().WithOne().HasForeignKey<UserPrivacySettings>(x => x.AccountId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_account_privacy_settings_accounts");
    }
}
