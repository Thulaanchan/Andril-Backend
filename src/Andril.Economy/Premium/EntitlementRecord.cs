using System.Text.Json.Serialization;

namespace Andril.Economy.Premium;

/// <summary>
/// Domain entity representing an account's premium subscription entitlement (SC-07).
/// </summary>
public sealed class EntitlementRecord
{
    private EntitlementRecord() { }

    public Guid AccountId { get; private set; }
    public SubscriptionTier Tier { get; private set; } = SubscriptionTier.Free;
    public bool IsActive { get; private set; }
    public DateTimeOffset? ExpiresAtUtc { get; private set; }
    public string Provider { get; private set; } = "none";
    public string? ExternalSubscriptionId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static EntitlementRecord CreateDefault(Guid accountId, DateTimeOffset now)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("Account identifier cannot be empty.", nameof(accountId));

        var utc = now.ToUniversalTime();
        return new EntitlementRecord
        {
            AccountId = accountId,
            Tier = SubscriptionTier.Free,
            IsActive = false,
            ExpiresAtUtc = null,
            Provider = "none",
            ExternalSubscriptionId = null,
            CreatedAtUtc = utc,
            UpdatedAtUtc = utc
        };
    }

    public void ActivateSubscription(
        SubscriptionTier tier,
        DateTimeOffset? expiresAtUtc,
        string provider,
        string? externalSubscriptionId,
        DateTimeOffset now)
    {
        if (tier == SubscriptionTier.Free)
            throw new ArgumentException("Cannot activate Free tier as premium subscription.", nameof(tier));
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Payment provider is required.", nameof(provider));

        Tier = tier;
        IsActive = true;
        ExpiresAtUtc = expiresAtUtc?.ToUniversalTime();
        Provider = provider.Trim().ToLowerInvariant();
        ExternalSubscriptionId = externalSubscriptionId?.Trim();
        UpdatedAtUtc = now.ToUniversalTime();
    }

    public void Revoke(DateTimeOffset now)
    {
        Tier = SubscriptionTier.Free;
        IsActive = false;
        ExpiresAtUtc = null;
        UpdatedAtUtc = now.ToUniversalTime();
    }
}

public sealed record UserEntitlementDto(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("tier")] SubscriptionTier Tier,
    [property: JsonPropertyName("isPremium")] bool IsPremium,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset? ExpiresAtUtc,
    [property: JsonPropertyName("provider")] string Provider
);
