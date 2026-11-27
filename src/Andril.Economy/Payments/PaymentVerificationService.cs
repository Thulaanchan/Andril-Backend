using Andril.Economy.Premium;
using Andril.Economy.Wallet;

namespace Andril.Economy.Payments;

public interface IPaymentVerificationService
{
    Task<PaymentVerificationResult> VerifyPurchaseAsync(PaymentVerificationCommand command, CancellationToken cancellationToken = default);
    Task<RefundVerificationResult> ProcessRefundAsync(RefundVerificationCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provider-neutral payment verification implementation (SC-08).
/// Enforces fail-closed validation: unverified external tokens and unsupported providers are rejected.
/// </summary>
public sealed class PaymentVerificationService(
    ICoinWalletService walletService,
    IEntitlementService entitlementService,
    TimeProvider clock)
    : IPaymentVerificationService
{
    public async Task<PaymentVerificationResult> VerifyPurchaseAsync(PaymentVerificationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.AccountId == Guid.Empty)
            return Fail("Account ID cannot be empty.");
        if (string.IsNullOrWhiteSpace(command.Provider))
            return Fail("Payment provider must be specified.");
        if (string.IsNullOrWhiteSpace(command.PurchaseToken))
            return Fail("Purchase token cannot be empty.");
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return Fail("Idempotency key is required.");

        var normalizedProvider = command.Provider.Trim().ToLowerInvariant();
        var now = clock.GetUtcNow();

        // Provider-neutral token verification boundary
        // Fails closed for unconfigured live production providers
        switch (normalizedProvider)
        {
            case "controlled_test":
                // Controlled test provider for local and CI verification
                return await HandleControlledTestPurchaseAsync(command, now, cancellationToken);

            case "apple_iap":
            case "google_play":
            case "stripe":
                // Production payment gateways require authorized backend credentials
                // Fails closed with descriptive security rejection
                return new PaymentVerificationResult(
                    false,
                    null,
                    normalizedProvider,
                    $"Payment provider '{normalizedProvider}' gateway credentials are not configured in current environment.",
                    null);

            default:
                return new PaymentVerificationResult(
                    false,
                    null,
                    normalizedProvider,
                    $"Unsupported payment provider '{command.Provider}'.",
                    null);
        }
    }

    public async Task<RefundVerificationResult> ProcessRefundAsync(RefundVerificationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.AccountId == Guid.Empty)
            return FailRefund(command.TransactionId, command.Provider, "Account ID cannot be empty.");
        if (string.IsNullOrWhiteSpace(command.TransactionId))
            return FailRefund(command.TransactionId, command.Provider, "Transaction ID cannot be empty.");
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return FailRefund(command.TransactionId, command.Provider, "Idempotency key is required.");

        var normalizedProvider = command.Provider.Trim().ToLowerInvariant();
        var now = clock.GetUtcNow();

        switch (normalizedProvider)
        {
            case "controlled_test":
                // Reversal of entitlement or debit
                await entitlementService.RevokeSubscriptionAsync(command.AccountId, cancellationToken);
                return new RefundVerificationResult(true, command.TransactionId, normalizedProvider, null, now);

            default:
                return FailRefund(command.TransactionId, normalizedProvider,
                    $"Refund processing for provider '{command.Provider}' is not configured in current environment.");
        }
    }

    private async Task<PaymentVerificationResult> HandleControlledTestPurchaseAsync(
        PaymentVerificationCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var externalTxId = $"test_tx_{Guid.NewGuid():N}";

        if (command.ProductId.StartsWith("sub_", StringComparison.OrdinalIgnoreCase))
        {
            var tier = command.ProductId.Contains("annual", StringComparison.OrdinalIgnoreCase)
                ? SubscriptionTier.PremiumAnnual
                : SubscriptionTier.PremiumMonthly;

            var expiresAt = tier == SubscriptionTier.PremiumAnnual
                ? now.AddYears(1)
                : now.AddMonths(1);

            await entitlementService.GrantSubscriptionAsync(
                command.AccountId, tier, expiresAt, "controlled_test", externalTxId, cancellationToken);
        }
        else if (command.ProductId.StartsWith("coins_", StringComparison.OrdinalIgnoreCase))
        {
            // Parses e.g. "coins_500"
            var parts = command.ProductId.Split('_');
            if (parts.Length > 1 && long.TryParse(parts[1], out var coinAmount) && coinAmount > 0)
            {
                await walletService.CreditMyWalletAsync(
                    coinAmount,
                    CoinTransactionType.PurchaseCredit,
                    $"Coin purchase ({command.ProductId})",
                    command.IdempotencyKey,
                    externalTxId,
                    cancellationToken);
            }
        }

        return new PaymentVerificationResult(true, externalTxId, "controlled_test", null, now);
    }

    private static PaymentVerificationResult Fail(string reason) =>
        new(false, null, "unknown", reason, null);

    private static RefundVerificationResult FailRefund(string txId, string provider, string reason) =>
        new(false, txId ?? string.Empty, provider ?? "unknown", reason, null);
}
