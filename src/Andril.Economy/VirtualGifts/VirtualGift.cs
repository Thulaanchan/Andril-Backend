namespace Andril.Economy.VirtualGifts;

/// <summary>
/// Definition of a virtual gift item available in the catalog (CF-14).
/// </summary>
public sealed record VirtualGift(
    string GiftId,
    string Name,
    string Description,
    long CoinCost,
    string Category,
    string IconKey,
    bool IsActive
);
