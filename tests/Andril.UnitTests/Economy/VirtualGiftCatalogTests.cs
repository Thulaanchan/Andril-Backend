using Andril.Economy.VirtualGifts;
using Xunit;

namespace Andril.UnitTests.Economy;

public sealed class VirtualGiftCatalogTests
{
    private readonly IVirtualGiftCatalog _catalog = new ConfiguredVirtualGiftCatalog();

    [Fact]
    public void Catalog_ContainsStandardGifts()
    {
        var gifts = _catalog.GetAllActiveGifts();

        Assert.NotEmpty(gifts);
        Assert.All(gifts, g =>
        {
            Assert.False(string.IsNullOrWhiteSpace(g.GiftId));
            Assert.False(string.IsNullOrWhiteSpace(g.Name));
            Assert.True(g.CoinCost > 0);
            Assert.True(g.IsActive);
        });
    }

    [Fact]
    public void FindGift_ExistingGiftId_ReturnsActiveGift()
    {
        var spark = _catalog.FindGift("gift_spark");

        Assert.NotNull(spark);
        Assert.Equal("Spark", spark.Name);
        Assert.Equal(10, spark.CoinCost);
    }

    [Fact]
    public void FindGift_NonExistentGift_ReturnsNull()
    {
        var nonexistent = _catalog.FindGift("gift_unicorn");
        Assert.Null(nonexistent);
    }

    [Fact]
    public void GiftRecord_SelfGift_ThrowsInvalidGiftOperationException()
    {
        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<InvalidGiftOperationException>(() =>
            GiftTransactionRecord.Create(
                accountId,
                accountId, // Self gift
                "gift_spark",
                10,
                "social",
                "idemp_self",
                "To myself",
                now));
    }
}
