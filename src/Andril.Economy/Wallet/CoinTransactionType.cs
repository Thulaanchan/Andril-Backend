namespace Andril.Economy.Wallet;

public enum CoinTransactionType
{
    PurchaseCredit = 1,
    TransferCredit = 2,
    TransferDebit = 3,
    VirtualGiftSentDebit = 4,
    VirtualGiftReceivedCredit = 5,
    AdminAdjustment = 6,
    RefundReversalDebit = 7
}

public static class CoinTransactionTypeExtensions
{
    public static bool IsCredit(this CoinTransactionType type) =>
        type is CoinTransactionType.PurchaseCredit
             or CoinTransactionType.TransferCredit
             or CoinTransactionType.VirtualGiftReceivedCredit
             or (CoinTransactionType.AdminAdjustment); // Context-dependent, checked by amount sign
}
