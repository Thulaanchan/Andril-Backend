namespace Andril.Common.Accounts;

/// <summary>Private account root. Future onboarding records reference Id separately.</summary>
public sealed class Account
{
    private Account() { }

    public Guid Id { get; private set; }
    public Guid SupabaseUserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static Account Create(Guid supabaseUserId, DateTimeOffset now)
    {
        if (supabaseUserId == Guid.Empty)
            throw new ArgumentException("A user identity is required.", nameof(supabaseUserId));
        return new Account
        {
            Id = Guid.NewGuid(), SupabaseUserId = supabaseUserId,
            CreatedAtUtc = now.ToUniversalTime(), UpdatedAtUtc = now.ToUniversalTime()
        };
    }
}
