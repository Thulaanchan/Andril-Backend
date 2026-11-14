using Andril.Common.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Andril.Persistence.Configurations;

public sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("accounts", "andril_identity", table =>
        {
            table.HasCheckConstraint("ck_accounts_id_nonempty", "id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_accounts_subject_nonempty", "supabase_user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
            table.HasCheckConstraint("ck_accounts_timestamps", "updated_at_utc >= created_at_utc");
        });
        builder.HasKey(x => x.Id).HasName("pk_accounts");
        builder.HasAlternateKey(x => x.SupabaseUserId).HasName("uq_accounts_supabase_user_id");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(x => x.SupabaseUserId).HasColumnName("supabase_user_id").HasColumnType("uuid").ValueGeneratedNever();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").HasColumnType("timestamp with time zone").IsRequired();
    }
}
