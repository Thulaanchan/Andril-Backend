using Andril.Shared.Authentication;

namespace Andril.Economy.Premium;

public interface IEntitlementRepository
{
    Task<EntitlementRecord?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<EntitlementRecord> UpsertAsync(EntitlementRecord record, CancellationToken cancellationToken = default);
}

public interface IEntitlementService
{
    Task<UserEntitlementDto> GetMyEntitlementAsync(CancellationToken cancellationToken = default);
    Task<UserEntitlementDto> GetEntitlementByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<UserEntitlementDto> GrantSubscriptionAsync(Guid accountId, SubscriptionTier tier, DateTimeOffset? expiresAt, string provider, string? externalId, CancellationToken cancellationToken = default);
    Task<UserEntitlementDto> RevokeSubscriptionAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public sealed class EntitlementService(
    ICurrentUser currentUser,
    IEntitlementRepository repository,
    TimeProvider clock)
    : IEntitlementService
{
    public async Task<UserEntitlementDto> GetMyEntitlementAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid userId || userId == Guid.Empty)
            throw new InvalidOperationException("Authenticated user identity required.");

        return await GetEntitlementByAccountIdAsync(userId, cancellationToken);
    }

    public async Task<UserEntitlementDto> GetEntitlementByAccountIdAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("Account ID cannot be empty.", nameof(accountId));

        var record = await repository.GetByAccountIdAsync(accountId, cancellationToken);
        if (record is null)
        {
            var defaultRecord = EntitlementRecord.CreateDefault(accountId, clock.GetUtcNow());
            return ToDto(defaultRecord);
        }

        // Auto-check expiry
        var now = clock.GetUtcNow();
        if (record.IsActive && record.ExpiresAtUtc.HasValue && record.ExpiresAtUtc.Value <= now)
        {
            record.Revoke(now);
            await repository.UpsertAsync(record, cancellationToken);
        }

        return ToDto(record);
    }

    public async Task<UserEntitlementDto> GrantSubscriptionAsync(
        Guid accountId,
        SubscriptionTier tier,
        DateTimeOffset? expiresAt,
        string provider,
        string? externalId,
        CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("Account ID cannot be empty.", nameof(accountId));
        if (tier == SubscriptionTier.Lifetime)
            throw new InvalidOperationException("Lifetime subscriptions are not an approved commercial plan under BRD v2.4-R1.");

        var now = clock.GetUtcNow();
        var record = await repository.GetByAccountIdAsync(accountId, cancellationToken)
                     ?? EntitlementRecord.CreateDefault(accountId, now);

        record.ActivateSubscription(tier, expiresAt, provider, externalId, now);
        var saved = await repository.UpsertAsync(record, cancellationToken);
        return ToDto(saved);
    }

    public async Task<UserEntitlementDto> RevokeSubscriptionAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("Account ID cannot be empty.", nameof(accountId));

        var now = clock.GetUtcNow();
        var record = await repository.GetByAccountIdAsync(accountId, cancellationToken)
                     ?? EntitlementRecord.CreateDefault(accountId, now);

        record.Revoke(now);
        var saved = await repository.UpsertAsync(record, cancellationToken);
        return ToDto(saved);
    }

    private static UserEntitlementDto ToDto(EntitlementRecord r) =>
        new(r.AccountId, r.Tier, r.Tier.IsPremium(), r.IsActive, r.ExpiresAtUtc, r.Provider);
}
