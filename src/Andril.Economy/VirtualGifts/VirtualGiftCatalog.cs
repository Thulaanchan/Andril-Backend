namespace Andril.Economy.VirtualGifts;

public interface IVirtualGiftCatalog
{
    IReadOnlyList<VirtualGift> GetAllActiveGifts();
    VirtualGift? FindGift(string giftId);
    bool IsValidGift(string giftId);
}

public sealed class ConfiguredVirtualGiftCatalog : IVirtualGiftCatalog
{
    private static readonly Dictionary<string, VirtualGift> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gift_spark"] = new("gift_spark", "Spark", "A bright spark of appreciation", 10, "Social", "icon_spark", true),
        ["gift_coffee"] = new("gift_coffee", "Coffee", "Warm coffee cup", 25, "Social", "icon_coffee", true),
        ["gift_flower"] = new("gift_flower", "Bouquet", "A beautiful bouquet of flowers", 50, "Social", "icon_flower", true),
        ["gift_star"] = new("gift_star", "Gold Star", "Award of excellence", 100, "Learning", "icon_star", true),
        ["gift_trophy"] = new("gift_trophy", "Champion Trophy", "Recognizing outstanding achievement", 250, "Hobbies", "icon_trophy", true),
        ["gift_diamond"] = new("gift_diamond", "Diamond", "A brilliant precious diamond", 500, "Social", "icon_diamond", true)
    };

    public IReadOnlyList<VirtualGift> GetAllActiveGifts() =>
        Catalog.Values.Where(g => g.IsActive).ToList();

    public VirtualGift? FindGift(string giftId) =>
        !string.IsNullOrWhiteSpace(giftId) && Catalog.TryGetValue(giftId, out var gift) && gift.IsActive ? gift : null;

    public bool IsValidGift(string giftId) =>
        FindGift(giftId) is not null;
}
