using Andril.Economy.Wallet;
using Andril.Shared.Authentication;

namespace Andril.Economy.VirtualGifts;

public interface IVirtualGiftService
{
    IReadOnlyList<VirtualGift> GetCatalog();
    Task<GiftTransactionReceipt> SendGiftAsync(SendVirtualGiftRequest request, CancellationToken cancellationToken = default);
}

public sealed class VirtualGiftService(
    ICurrentUser currentUser,
    IVirtualGiftCatalog catalog,
    ICoinWalletService walletService,
    TimeProvider clock)
    : IVirtualGiftService
{
    public IReadOnlyList<VirtualGift> GetCatalog() => catalog.GetAllActiveGifts();

    public async Task<GiftTransactionReceipt> SendGiftAsync(SendVirtualGiftRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!currentUser.IsAuthenticated || currentUser.UserId is not Guid senderId || senderId == Guid.Empty)
            throw new InvalidGiftOperationException("Authenticated user identity required.");

        if (request.RecipientAccountId == Guid.Empty)
            throw new InvalidGiftOperationException("Recipient account identifier cannot be empty.");

        if (senderId == request.RecipientAccountId)
            throw new InvalidGiftOperationException("Sending gifts to oneself is not permitted.");

        var gift = catalog.FindGift(request.GiftId);
        if (gift is null)
            throw new InvalidGiftOperationException($"Gift '{request.GiftId}' was not found in catalog or is inactive.");

        var giftRecord = GiftTransactionRecord.Create(
            senderId,
            request.RecipientAccountId,
            gift.GiftId,
            gift.CoinCost,
            request.Module,
            request.IdempotencyKey,
            request.Message,
            clock.GetUtcNow());

        // Atomically debit sender's wallet for the gift
        await walletService.DebitMyWalletAsync(
            gift.CoinCost,
            CoinTransactionType.VirtualGiftSentDebit,
            $"Sent virtual gift '{gift.Name}' to {request.RecipientAccountId}",
            request.IdempotencyKey,
            giftRecord.Id.ToString(),
            cancellationToken);

        return new GiftTransactionReceipt(
            giftRecord.Id,
            giftRecord.SenderAccountId,
            giftRecord.RecipientAccountId,
            giftRecord.GiftId,
            giftRecord.CoinCost,
            giftRecord.Module,
            giftRecord.Message,
            giftRecord.CreatedAtUtc);
    }
}
